using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SailwindRadio
{
    /// <summary>One local clip and one positional emitter. Call only from the Unity main thread.</summary>
    public sealed partial class RadioPlayback : IDisposable
    {
        private const double ScheduleLeadSeconds = 0.05;
        private const double LoadTimeoutSeconds = 60;
        private readonly RadioState state;
        private readonly Action<string> warning;
        private readonly MonoBehaviour host;
        private RadioAudioLifetime lifetime;
        private GameObject emitter;
        private AudioSource source;
        private RadioSpeakerOutput builtIn;
        private readonly Dictionary<int, RadioSpeakerOutput> endpoints = new Dictionary<int, RadioSpeakerOutput>();
        private readonly List<int> lostEndpoints = new List<int>();
        private Vector3 listenerPosition;
        private bool listenerKnown;
        private AudioClip clip;
        private UnityWebRequest request;
        private Task<Mp3LoadResult> mp3Task;
        private CancellationTokenSource mp3Cancellation;
        private StreamedMp3 streamedMp3;
        private string requestedPath;
        private string failure;
        private bool disposed;
        private bool running;
        private bool voicesPaused;
        private bool recoveryWarned;
        private double nextHealthCheck;
        private bool wasPowered;
        private volatile bool resetRequested;
        private double scheduledDsp;
        private double lastDsp;
        private int scheduledSample;
        private double loadStartedAt;
        private double streamWaitingSince = -1;

        public string Status { get; private set; }
        public string TrackLabel { get; private set; }
        public bool RepeatTrack { get; set; } = true;
        public bool TrackEnded { get; private set; }
        public bool LoadFailed { get { return failure != null; } }
        public string FailedPath { get; private set; }

        public RadioPlayback(MonoBehaviour coroutineHost, RadioState state, Action<string> warning)
        {
            if (coroutineHost == null) throw new ArgumentNullException("coroutineHost");
            if (state == null) throw new ArgumentNullException("state");
            this.state = state;
            host = coroutineHost;
            this.warning = warning ?? delegate { };
            builtIn = new RadioSpeakerOutput("Sailwind Radio audio", RadioSpeakerProfile.BuiltIn, this.warning);
            emitter = builtIn.Emitter;
            source = builtIn.Source;
            lifetime = emitter.AddComponent<RadioAudioLifetime>();
            lifetime.Host = coroutineHost;
            lifetime.Playback = this;
            AudioSettings.OnAudioConfigurationChanged += OnAudioConfigurationChanged;
            Status = "Off";
            TrackLabel = "No track";
            lastDsp = AudioSettings.dspTime;
        }

        public void SetTrack(string path)
        {
            if (disposed) return;
            path = path ?? "";
            if (!String.Equals(state.TrackPath, path, StringComparison.Ordinal))
                state.PositionSeconds = 0;
            state.TrackPath = path;
            ReleaseTrack();
            failure = null;
            FailedPath = null;
            TrackEnded = false;
            requestedPath = null; // Explicit selection also retries a previously failed file.
            if (!AdoptPreload(path)) ReleasePreload();
        }

        public void SetCarried(bool carried) { if (!disposed) builtIn.Carried = carried; }

        public void BeginEndpoints()
        {
            if (disposed) return;
            foreach (var endpoint in endpoints.Values) endpoint.Seen = false;
        }

        public void SetEndpoint(int id, Vector3 position, bool carried, int kind, float localVolume, float bass, float obstruction)
        {
            if (disposed) return;
            RadioSpeakerProfile profile = RadioSpeakerProfile.ForKind(kind);
            if (profile == null) return;
            RadioSpeakerOutput endpoint;
            if (!endpoints.TryGetValue(id, out endpoint) || endpoint.Emitter == null || endpoint.Source == null || endpoint.Profile != profile)
            {
                if (endpoint != null) endpoint.Dispose();
                endpoint = new RadioSpeakerOutput("Sailwind Radio speaker " + id, profile, warning);
                endpoints[id] = endpoint;
            }
            endpoint.Profile = profile;
            endpoint.Position = position;
            endpoint.Carried = carried;
            endpoint.LocalVolume = localVolume;
            endpoint.Bass = bass;
            endpoint.Obstruction = obstruction;
            endpoint.Seen = true;
        }

        public void EndEndpoints()
        {
            if (disposed) return;
            lostEndpoints.Clear();
            foreach (var pair in endpoints) if (!pair.Value.Seen) lostEndpoints.Add(pair.Key);
            foreach (int id in lostEndpoints) { endpoints[id].Dispose(); endpoints.Remove(id); }
        }

        /// <summary>Set current listener and obstruction from native world geometry. Main thread only.</summary>
        public void SetAcoustics(Vector3 listenerPosition, bool listenerKnown, float obstruction)
        {
            if (disposed) return;
            this.listenerPosition = listenerPosition;
            this.listenerKnown = listenerKnown && IsFinite(listenerPosition.x) && IsFinite(listenerPosition.y) && IsFinite(listenerPosition.z);
            builtIn.Obstruction = obstruction;
        }

        public void Tick(Vector3 worldPosition, bool suspended)
        {
            if (disposed) return;
            if (host == null) { Dispose(); return; }
            EnsureOwnedOutput();
            builtIn.Position = worldPosition;
            builtIn.LocalVolume = state.LocalVolume;
            state.Volume = Single.IsNaN(state.Volume) ? 0.5f : Mathf.Clamp01(state.Volume);
            // Preserve the saved knob value. A lower ceiling and squared gain provide
            // useful quiet settings for a tabletop radio without changing playback time.
            builtIn.Update(state.Volume, listenerPosition, listenerKnown);
            source.loop = RepeatTrack;
            foreach (var endpoint in endpoints.Values)
            {
                endpoint.Update(state.Volume, listenerPosition, listenerKnown);
                if (endpoint.Source != null) endpoint.Source.loop = RepeatTrack;
            }
            double dsp = AudioSettings.dspTime;
            if (resetRequested || dsp < lastDsp)
            {
                // A device reset can invalidate source sample position. Resume from the last
                // position captured before the reset, not the newly reset source cursor.
                StopOutputs();
                resetRequested = false;
            }
            lastDsp = dsp;

            PollPreload();

            if (!String.Equals(requestedPath, state.TrackPath ?? "", StringComparison.Ordinal))
            {
                ReleaseTrack();
                requestedPath = state.TrackPath ?? "";
                failure = null;
                FailedPath = null;
                TrackEnded = false;
                TrackLabel = SafeLabel(requestedPath);
                if (!AdoptPreload(requestedPath))
                {
                    ReleasePreload();
                    if (state.Powered) BeginLoad();
                }
            }
            else if (state.Powered && !wasPowered && clip == null && request == null && mp3Task == null)
            {
                failure = null;
                FailedPath = null;
                BeginLoad();
            }
            wasPowered = state.Powered;
            CompleteLoad();
            CompleteMp3Load();
            if (streamedMp3 != null)
            {
                if (streamedMp3.Fault != null) Fail("Unable to decode music", streamedMp3.Fault.Message);
                else
                {
                    int frame = SafeSavedFrame();
                    streamedMp3.Request(running ? SampleAt(dsp) : frame);
                }
            }
            if (request != null || mp3Task != null || (clip != null && clip.loadState != AudioDataLoadState.Loaded))
            {
                if (clip != null && clip.loadState == AudioDataLoadState.Failed)
                    Fail("Unable to decode music", "The decoder failed to load audio data");
                else if (Time.realtimeSinceStartup - loadStartedAt >= LoadTimeoutSeconds)
                    Fail("Unable to load music", "Audio loading timed out after 60 seconds");
            }

            FinishIfEnded();
            bool shouldPlay = state.Powered && !state.Paused && !suspended && !AudioListener.pause && !TrackEnded;
            int wantedFrame = SafeSavedFrame();
            bool streamReady = streamedMp3 == null || streamedMp3.Ready(running ? SampleAt(dsp) : wantedFrame);
            if (streamedMp3 != null && shouldPlay && !streamReady)
            {
                if (streamWaitingSince < 0) streamWaitingSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - streamWaitingSince >= LoadTimeoutSeconds)
                {
                    Fail("Unable to decode music", "Streamed MP3 buffer did not recover");
                    shouldPlay = false;
                }
            }
            else streamWaitingSince = -1;
            if (!shouldPlay && running)
            {
                CapturePosition();
                PauseOutputs();
            }
            if (shouldPlay && clip != null && clip.loadState == AudioDataLoadState.Loaded && !running && streamReady)
                StartAtSavedPosition();
            if (running) CapturePosition();
            if (running) JoinEndpoints();
            if (running) CheckOutputHealth();

            Status = !state.Powered ? "Off" : failure != null ? failure :
                request != null || mp3Task != null || (clip != null && clip.loadState != AudioDataLoadState.Loaded) ||
                (streamedMp3 != null && !running && !streamReady) ? "Loading" : clip == null ? "No track" :
                TrackEnded ? "Ended" : state.Paused ? "Paused" : suspended || AudioListener.pause ? "Suspended" : "Playing";
        }

        public void CapturePosition()
        {
            if (disposed || !running || clip == null || resetRequested || AudioSettings.dspTime < lastDsp) return;
            if (FinishIfEnded()) return;
            state.PositionSeconds = SampleAt(AudioSettings.dspTime) / (double)clip.frequency;
        }

        private int SafeSavedFrame()
        {
            if (clip == null || clip.samples <= 0 || clip.frequency <= 0) return 0;
            double seconds = state.PositionSeconds;
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) return 0;
            return (int)Math.Min(clip.samples - 1, Math.Floor(seconds * clip.frequency));
        }

        private int SampleAt(double dsp)
        {
            long sample = scheduledSample + (long)Math.Round(Math.Max(0, dsp - scheduledDsp) * clip.frequency);
            return RepeatTrack ? (int)(sample % clip.samples) : (int)Math.Min(clip.samples, sample);
        }

        internal static float DistanceGain(float distance)
        { return RadioSpeakerOutput.DistanceGain(distance, RadioSpeakerProfile.BuiltIn.Radius); }

        private static bool IsFinite(float value) { return RadioSpeakerOutput.Finite(value); }
        private void BeginLoad()
        {
            if (String.IsNullOrWhiteSpace(requestedPath)) return;
            try
            {
                // Only absolute local filesystem paths. A URL or UNC share must never turn
                // a music setting into an HTTP request or a network filesystem operation.
                if (!Path.IsPathRooted(requestedPath))
                    throw new IOException("Choose an absolute local music file path");
                string fullPath = Path.GetFullPath(requestedPath);
                var uri = new Uri(fullPath);
                if (!uri.IsFile || uri.IsUnc) throw new IOException("Choose a local music file");
                AudioType type;
                switch (Path.GetExtension(fullPath).ToLowerInvariant())
                {
                    case ".mp3": type = AudioType.MPEG; break;
                    case ".ogg": type = AudioType.OGGVORBIS; break;
                    case ".wav": type = AudioType.WAV; break;
                    default: throw new IOException("Choose an MP3, OGG or WAV file");
                }
                if (!File.Exists(fullPath)) throw new IOException("Music file is missing or inaccessible");
                if (new FileInfo(fullPath).Length > (type == AudioType.MPEG ? MaximumStreamedMp3FileBytes : MaximumMp3FileBytes))
                    throw new IOException("Audio file exceeds the size limit");
                loadStartedAt = Time.realtimeSinceStartup;
                if (type == AudioType.MPEG)
                {
                    // Sailwind's Windows Unity 2019 player rejects MPEG in its runtime
                    // audio loader. Decode locally on a worker, then upload PCM on Tick.
                    mp3Cancellation = new CancellationTokenSource();
                    mp3Cancellation.CancelAfter((int)(LoadTimeoutSeconds * 1000));
                    CancellationToken token = mp3Cancellation.Token;
                    double saved = state.PositionSeconds;
                    mp3Task = Task.Run(() => PrepareMp3(fullPath, saved, token), token);
                    return;
                }
                request = UnityWebRequestMultimedia.GetAudioClip(uri.AbsoluteUri, type);
                // Fully decode this one clip. Seeking and repeat must not race an unfinished stream.
                ((DownloadHandlerAudioClip)request.downloadHandler).streamAudio = false;
                request.SendWebRequest();
            }
            catch (Exception ex) { Fail("Unable to load music", ex.Message); }
        }

        private void CompleteLoad()
        {
            if (request == null || !request.isDone) return;
            try
            {
                if (request.isNetworkError || request.isHttpError)
                    throw new IOException(request.error ?? "Audio request failed");
                clip = DownloadHandlerAudioClip.GetContent(request);
                if (clip == null || clip.loadState == AudioDataLoadState.Failed ||
                    clip.samples <= 0 || clip.frequency <= 0 || (long)clip.samples * clip.channels > MaximumMp3Samples)
                    throw new IOException("The file could not be decoded as audio");
                source.clip = clip;
                request.Dispose();
                request = null;
            }
            catch (Exception ex) { Fail("Unable to decode music", ex.Message); }
        }

        private void CompleteMp3Load()
        {
            if (mp3Task == null || !mp3Task.IsCompleted) return;
            try
            {
                if (mp3Task.IsCanceled) throw new IOException("MP3 decoding timed out");
                if (mp3Task.IsFaulted)
                    throw new IOException("MP3 decoding failed: " + mp3Task.Exception.GetBaseException().Message);
                Mp3LoadResult result = mp3Task.Result;
                mp3Task = null;
                mp3Cancellation.Dispose();
                mp3Cancellation = null;
                if (result.Stream != null)
                {
                    streamedMp3 = result.Stream;
                    var reader = streamedMp3.CreateReader();
                    clip = streamedMp3.CreateClip(reader);
                    if (clip == null) throw new IOException("Unable to create streamed audio clip");
                    builtIn.StreamClip = clip;
                    source.clip = clip;
                    ReleasePreload();
                    return;
                }
                DecodedMp3 decoded = result.Decoded;
                // Unity counts sample frames per channel, while decoder buffers contain
                // interleaved floats. Dividing by channels avoids double-length stereo.
                clip = AudioClip.Create("Sailwind Radio MP3", decoded.SampleCount / decoded.Channels,
                    decoded.Channels, decoded.SampleRate, false);
                if (clip == null) throw new IOException("Unable to allocate the decoded audio clip");
                int offset = 0;
                foreach (float[] block in decoded.Blocks)
                {
                    if (!clip.SetData(block, offset)) throw new IOException("Unable to upload decoded audio");
                    offset += block.Length / decoded.Channels;
                }
                source.clip = clip;
            }
            catch (Exception ex) { Fail("Unable to decode music", ex.Message); }
        }

        internal sealed class DecodedMp3
        {
            internal readonly List<float[]> Blocks = new List<float[]>();
            internal int Channels, SampleRate, SampleCount;
        }

        internal sealed class Mp3LoadResult
        {
            internal DecodedMp3 Decoded;
            internal StreamedMp3 Stream;
        }

        // 256 MiB of float PCM per clip. Managed blocks are released after upload.
        // Native clip allocation temporarily doubles that footprint during handoff.
        internal const int MaximumMp3Samples = 64 * 1024 * 1024;
        internal const long MaximumMp3FileBytes = 128L * 1024 * 1024;
        internal const long MaximumStreamedMp3FileBytes = 1024L * 1024 * 1024;
        private static readonly SemaphoreSlim Mp3DecodeSlot = new SemaphoreSlim(1, 1);

        internal static Mp3LoadResult PrepareMp3(string path, double initialSeconds, CancellationToken token,
            int maximumSamples = MaximumMp3Samples)
        {
            token.ThrowIfCancellationRequested();
            using (var input = new CancellableFileStream(path, token))
            {
                if (input.Length > MaximumStreamedMp3FileBytes)
                    throw new IOException("MP3 exceeds the streamed file size limit");
                using (var decoder = new NLayer.MpegFile(input))
                {
                    int channels = decoder.Channels;
                    if (channels < 1 || channels > 2 || decoder.SampleRate < 8000 || decoder.SampleRate > 96000)
                        throw new IOException("Unsupported MP3 channel count or sample rate");
                    long samples = decoder.Length < 0 ? -1 : decoder.Length / sizeof(float);
                    if (samples > maximumSamples || input.Length > MaximumMp3FileBytes)
                    {
                        if (decoder.Length % (channels * sizeof(float)) != 0)
                            throw new IOException("Invalid MP3 sample length");
                        if (Double.IsNaN(initialSeconds) || Double.IsInfinity(initialSeconds) || initialSeconds < 0)
                            initialSeconds = 0;
                        int requested = (int)Math.Min(Int32.MaxValue - 1, initialSeconds * decoder.SampleRate);
                        return new Mp3LoadResult { Stream = StreamedMp3.Open(path, requested, token) };
                    }
                }
            }
            return new Mp3LoadResult { Decoded = DecodeMp3(path, token, maximumSamples) };
        }

        internal static bool IsLongMp3(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            using (var input = new CancellableFileStream(path, token))
            {
                if (input.Length > MaximumMp3FileBytes) return true;
                using (var decoder = new NLayer.MpegFile(input))
                    return decoder.Length > (long)MaximumMp3Samples * sizeof(float);
            }
        }

        internal static DecodedMp3 DecodeMp3(string path, CancellationToken token, int maximumSamples = MaximumMp3Samples,
            long maximumFileBytes = MaximumMp3FileBytes)
        {
            Mp3DecodeSlot.Wait(token);
            try { return DecodeMp3Core(path, token, maximumSamples, maximumFileBytes); }
            finally { Mp3DecodeSlot.Release(); }
        }

        private static DecodedMp3 DecodeMp3Core(string path, CancellationToken token, int maximumSamples, long maximumFileBytes)
        {
            token.ThrowIfCancellationRequested();
            // Own the stream outside MpegFile so a malformed-header constructor failure
            // still closes the file. Read/seek cancellation covers header scanning too.
            using (var input = new CancellableFileStream(path, token))
            {
              if (input.Length > maximumFileBytes) throw new IOException("MP3 exceeds the 128 MiB file size limit");
              using (var decoder = new NLayer.MpegFile(input))
              {
                var decoded = new DecodedMp3 { Channels = decoder.Channels, SampleRate = decoder.SampleRate };
                if (decoded.Channels < 1 || decoded.Channels > 2 || decoded.SampleRate < 8000 || decoded.SampleRate > 96000)
                    throw new IOException("Unsupported MP3 channel count or sample rate");
                while (true)
                {
                    token.ThrowIfCancellationRequested();
                    var block = new float[16384];
                    int count = decoder.ReadSamples(block, 0, block.Length);
                    if (count == 0) break;
                    if (count < 0 || count % decoded.Channels != 0)
                        throw new IOException("MP3 decoder returned incomplete sample frames");
                    if (count > maximumSamples - decoded.SampleCount)
                        throw new IOException("MP3 exceeds the 256 MiB decoded audio limit");
                    if (count != block.Length) Array.Resize(ref block, count);
                    for (int i = 0; i < block.Length; i++)
                        block[i] = Single.IsNaN(block[i]) ? 0f : Math.Max(-1f, Math.Min(1f, block[i]));
                    decoded.Blocks.Add(block);
                    decoded.SampleCount += count;
                }
                token.ThrowIfCancellationRequested();
                if (decoded.SampleCount == 0) throw new IOException("The MP3 contains no decodable audio");
                return decoded;
              }
            }
        }

        internal sealed class CancellableFileStream : Stream
        {
            private readonly FileStream input;
            private readonly CancellationToken token;
            internal CancellableFileStream(string path, CancellationToken token)
            {
                this.token = token;
                input = File.Open(path, FileMode.Open, FileAccess.Read, FileShare.Read);
            }
            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return true; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { return input.Length; } }
            public override long Position { get { return input.Position; } set { token.ThrowIfCancellationRequested(); input.Position = value; } }
            public override int Read(byte[] buffer, int offset, int count) { token.ThrowIfCancellationRequested(); return input.Read(buffer, offset, count); }
            public override long Seek(long offset, SeekOrigin origin) { token.ThrowIfCancellationRequested(); return input.Seek(offset, origin); }
            public override void Flush() { }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
            protected override void Dispose(bool disposing) { if (disposing) input.Dispose(); base.Dispose(disposing); }
        }

        private void StartAtSavedPosition()
        {
            double position = state.PositionSeconds;
            if (Double.IsNaN(position) || Double.IsInfinity(position) || position < 0) position = 0;
            double duration = clip.samples / (double)clip.frequency;
            if (!RepeatTrack && position >= duration)
            {
                state.PositionSeconds = duration;
                TrackEnded = true;
                return;
            }
            scheduledSample = (int)Math.Min(clip.samples - 1, Math.Floor((position % duration) * clip.frequency));
            double now = AudioSettings.dspTime;
            scheduledDsp = now + (voicesPaused ? 0 : ScheduleLeadSeconds);
            if (voicesPaused)
            {
                ResumeOutput(builtIn, now);
                foreach (var endpoint in endpoints.Values) ResumeOutput(endpoint, now);
            }
            else
            {
                ScheduleEndpoint(builtIn, scheduledDsp, scheduledSample);
                foreach (var endpoint in endpoints.Values) ScheduleEndpoint(endpoint, scheduledDsp, scheduledSample);
            }
            running = true;
            voicesPaused = false;
            nextHealthCheck = now + .5;
        }

        private void ResumeOutput(RadioSpeakerOutput output, double now)
        {
            if (output.Paused && output.Source != null && output.Source.clip ==
                (output.StreamClip != null ? output.StreamClip : clip)) output.Resume(scheduledSample, now);
            else ScheduleEndpoint(output, now + ScheduleLeadSeconds, SampleAt(now + ScheduleLeadSeconds));
        }

        private double EndDspTime()
        { return clip == null ? Double.PositiveInfinity : scheduledDsp + (clip.samples - scheduledSample) / (double)clip.frequency; }

        private bool FinishIfEnded()
        {
            if (!running || RepeatTrack || clip == null || AudioSettings.dspTime < EndDspTime()) return false;
            state.PositionSeconds = clip.samples / (double)clip.frequency;
            TrackEnded = true;
            StopOutputs();
            return true;
        }

        private void JoinEndpoints()
        {
            double now = AudioSettings.dspTime;
            bool waitingForStart = now < scheduledDsp;
            JoinOutput(builtIn, now, waitingForStart);
            foreach (var endpoint in endpoints.Values)
                JoinOutput(endpoint, now, waitingForStart);
        }

        private void JoinOutput(RadioSpeakerOutput output, double now, bool waitingForStart)
        {
            if (output.Running || output.Source == null) return;
            double start = waitingForStart ? scheduledDsp : now + ScheduleLeadSeconds;
            if (!RepeatTrack && start >= EndDspTime()) return;
            ScheduleEndpoint(output, start, SampleAt(start));
        }

        private void ScheduleEndpoint(RadioSpeakerOutput endpoint, double start, int sample)
        {
            if (endpoint.Source == null) return;
            if (clip == null || sample < 0 || sample >= clip.samples || (!RepeatTrack && start >= EndDspTime())) return;
            if (streamedMp3 != null && endpoint.StreamClip == null)
                endpoint.StreamClip = streamedMp3.CreateClip(streamedMp3.CreateReader());
            endpoint.Source.clip = endpoint.StreamClip != null ? endpoint.StreamClip : clip;
            endpoint.Source.loop = RepeatTrack;
            endpoint.Source.timeSamples = sample;
            endpoint.Source.PlayScheduled(start);
            endpoint.Running = true;
            endpoint.Paused = false;
            endpoint.SettlesAt = start + .3;
            endpoint.StartsAt = start;
        }

        private void PauseOutputs()
        {
            // Cancel an initial scheduled start explicitly. Otherwise retain native voices:
            // Unity 2019.1 has known filtered PlayScheduled re-trigger timing defects.
            if (AudioSettings.dspTime < scheduledDsp) { StopOutputs(); return; }
            running = false;
            voicesPaused = true;
            PauseOutput(builtIn);
            foreach (var endpoint in endpoints.Values) PauseOutput(endpoint);
        }

        private static void PauseOutput(RadioSpeakerOutput output)
        {
            if (AudioSettings.dspTime < output.StartsAt) output.Stop();
            else output.Pause();
        }

        private void StopOutputs(bool clearClip = false)
        {
            running = false;
            voicesPaused = false;
            if (builtIn != null) builtIn.Stop(clearClip);
            foreach (var endpoint in endpoints.Values) endpoint.Stop(clearClip);
        }

        private void EnsureOwnedOutput()
        {
            if (emitter == null || source == null)
            {
                var previous = builtIn;
                if (lifetime != null) lifetime.Playback = null;
                builtIn = new RadioSpeakerOutput("Sailwind Radio audio", RadioSpeakerProfile.BuiltIn, warning);
                builtIn.Carried = previous.Carried;
                builtIn.Obstruction = previous.Obstruction;
                // The timeline clip outlives a lost built-in emitter. Its old output
                // must not destroy the clip while healthy speakers still use it.
                if (previous.StreamClip == clip) previous.StreamClip = null;
                previous.Dispose();
                emitter = builtIn.Emitter;
                source = builtIn.Source;
                lifetime = emitter.AddComponent<RadioAudioLifetime>();
                lifetime.Host = host;
                lifetime.Playback = this;
                WarnRecovery();
            }
            EnableOwnedOutput(builtIn);
            foreach (var endpoint in endpoints.Values) EnableOwnedOutput(endpoint);
        }

        private void EnableOwnedOutput(RadioSpeakerOutput output)
        {
            if (output.Emitter == null || output.Source == null) return;
            if (!output.Emitter.activeSelf) { output.Emitter.SetActive(true); WarnRecovery(); }
            if (!output.Source.enabled) { output.Source.enabled = true; WarnRecovery(); }
        }

        private void WarnRecovery()
        {
            if (recoveryWarned) return;
            recoveryWarned = true;
            warning("Recovered an unavailable radio audio output");
        }

        private void CheckOutputHealth()
        {
            double now = AudioSettings.dspTime;
            if (now < nextHealthCheck) return;
            nextHealthCheck = now + .25;
            RepairOutput(builtIn, now);
            foreach (var endpoint in endpoints.Values) RepairOutput(endpoint, now);
        }

        private void RepairOutput(RadioSpeakerOutput output, double now)
        {
            if (!output.Running || output.Source == null || now < output.SettlesAt) return;
            if (output.Source.isVirtual || output.Source.volume <= 0) { output.DriftObservations = 0; return; }
            int expected = SampleAt(now);
            int difference = Math.Abs(output.Source.timeSamples - expected);
            if (RepeatTrack) difference = Math.Min(difference, clip.samples - difference);
            int bufferLength, bufferCount;
            AudioSettings.GetDSPBufferSize(out bufferLength, out bufferCount);
            double tolerance = Math.Max(.05, 2d * bufferLength / Math.Max(8000, AudioSettings.outputSampleRate) + .01);
            if (output.Source.isPlaying)
            {
                if (difference <= clip.frequency * tolerance) { output.DriftObservations = 0; return; }
                if (++output.DriftObservations < 2) return;
            }
            output.DriftObservations = 0;
            // Native cursors are observations, never the radio's master clock. Repair
            // a revived/lagging output without restarting healthy neighboring voices.
            if (output.Source.isPlaying) { output.Pause(); output.Resume(expected, now); }
            else ScheduleEndpoint(output, now + ScheduleLeadSeconds, SampleAt(now + ScheduleLeadSeconds));
        }

        private void Fail(string message, string detail)
        {
            ReleaseTrack();
            failure = message;
            FailedPath = requestedPath;
            warning(message + ": " + detail);
        }

        private static string SafeLabel(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "No track";
            try { return Path.GetFileNameWithoutExtension(path); }
            catch { return "Music file"; }
        }

        private void ReleaseTrack()
        {
            try { StopOutputs(true); }
            finally { ReleaseTrackResources(); }
        }

        private void ReleaseTrackResources()
        {
            if (mp3Cancellation != null)
            {
                Task<Mp3LoadResult> abandoned = mp3Task;
                CancellationTokenSource cancellation = mp3Cancellation;
                mp3Task = null;
                mp3Cancellation = null;
                cancellation.Cancel();
                // Never block the Unity thread waiting for file IO. Observe faults and
                // release the token after the worker exits. No continuation touches Unity.
                if (abandoned == null) cancellation.Dispose();
                else abandoned.ContinueWith(completed =>
                {
                    if (completed.IsFaulted) { var observed = completed.Exception; }
                    if (completed.Status == TaskStatus.RanToCompletion && completed.Result.Stream != null)
                        completed.Result.Stream.Dispose();
                    cancellation.Dispose();
                }, TaskScheduler.Default);
            }
            try { DiscardRequest(ref request, clip); }
            finally
            {
                foreach (var endpoint in endpoints.Values) endpoint.ReleaseStreamClip();
                if (builtIn != null)
                {
                    if (builtIn.StreamClip == clip) builtIn.StreamClip = null;
                    else builtIn.ReleaseStreamClip();
                }
                if (clip != null) UnityEngine.Object.Destroy(clip);
                clip = null;
                if (streamedMp3 != null) { streamedMp3.Dispose(); streamedMp3 = null; }
                streamWaitingSince = -1;
            }
        }

        private void OnAudioConfigurationChanged(bool deviceWasChanged) { resetRequested = true; }

        public void Dispose()
        {
            if (disposed) return;
            try { CapturePosition(); }
            finally
            {
                disposed = true;
                AudioSettings.OnAudioConfigurationChanged -= OnAudioConfigurationChanged;
                try
                {
                    try { ReleaseTrack(); }
                    finally { ReleasePreload(); }
                }
                finally
                {
                    foreach (var endpoint in endpoints.Values) endpoint.Dispose();
                    endpoints.Clear();
                    if (builtIn != null) builtIn.Dispose();
                    builtIn = null;
                    emitter = null;
                    source = null;
                }
            }
        }
    }

    /// <summary>Inventory may hide the physical item, so lifetime follows the plugin host.</summary>
    public sealed class RadioAudioLifetime : MonoBehaviour
    {
        internal MonoBehaviour Host;
        internal RadioPlayback Playback;
        private void Update() { if (Host == null && Playback != null) Playback.Dispose(); }
        // An unexpectedly removed emitter can be recreated by its still-live host.
        private void OnDestroy() { if (Host == null && Playback != null) Playback.Dispose(); }
    }
}
