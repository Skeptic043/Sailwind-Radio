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
    public sealed class RadioPlayback : IDisposable
    {
        private const double ScheduleLeadSeconds = 0.05;
        private const double LoadTimeoutSeconds = 60;
        private readonly RadioState state;
        private readonly Action<string> warning;
        private GameObject emitter;
        private AudioSource source;
        private AudioClip clip;
        private UnityWebRequest request;
        private Task<DecodedMp3> mp3Task;
        private CancellationTokenSource mp3Cancellation;
        private string requestedPath;
        private string failure;
        private bool disposed;
        private bool running;
        private bool wasPowered;
        private volatile bool resetRequested;
        private double scheduledDsp;
        private double lastDsp;
        private int scheduledSample;
        private double loadStartedAt;

        public string Status { get; private set; }
        public string TrackLabel { get; private set; }

        public RadioPlayback(MonoBehaviour coroutineHost, RadioState state, Action<string> warning)
        {
            if (coroutineHost == null) throw new ArgumentNullException("coroutineHost");
            if (state == null) throw new ArgumentNullException("state");
            this.state = state;
            this.warning = warning ?? delegate { };
            emitter = new GameObject("Sailwind Radio audio");
            // The plugin host owns lifetime, not whichever additive scene happened to
            // be active when this endpoint was created. Item/session removal disposes it.
            UnityEngine.Object.DontDestroyOnLoad(emitter);
            source = emitter.AddComponent<AudioSource>();
            source.playOnAwake = false;
            source.loop = true; // Milestone A repeats its single configured track.
            source.spatialBlend = 1f;
            source.rolloffMode = AudioRolloffMode.Logarithmic;
            source.minDistance = 2f;
            source.maxDistance = 35f;
            source.pitch = 1f;
            source.dopplerLevel = 0f;
            source.ignoreListenerPause = false;
            var lifetime = emitter.AddComponent<RadioAudioLifetime>();
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
            requestedPath = null; // Explicit selection also retries a previously failed file.
        }

        public void Tick(Vector3 worldPosition, bool suspended)
        {
            if (disposed) return;
            if (emitter == null || source == null) { Dispose(); return; }
            emitter.transform.position = worldPosition;
            state.Volume = Single.IsNaN(state.Volume) ? 0.5f : Mathf.Clamp01(state.Volume);
            source.volume = state.Volume;
            double dsp = AudioSettings.dspTime;
            if (resetRequested || dsp < lastDsp)
            {
                // A device reset can invalidate source sample position. Resume from the last
                // position captured before the reset, not the newly reset source cursor.
                source.Stop();
                running = false;
                resetRequested = false;
            }
            lastDsp = dsp;

            if (!String.Equals(requestedPath, state.TrackPath ?? "", StringComparison.Ordinal))
            {
                ReleaseTrack();
                requestedPath = state.TrackPath ?? "";
                failure = null;
                TrackLabel = SafeLabel(requestedPath);
                if (state.Powered) BeginLoad();
            }
            else if (state.Powered && !wasPowered && clip == null && request == null && mp3Task == null)
            {
                failure = null;
                BeginLoad();
            }
            wasPowered = state.Powered;
            CompleteLoad();
            CompleteMp3Load();
            if (request != null || mp3Task != null || (clip != null && clip.loadState != AudioDataLoadState.Loaded))
            {
                if (clip != null && clip.loadState == AudioDataLoadState.Failed)
                    Fail("Unable to decode music", "The decoder failed to load audio data");
                else if (Time.realtimeSinceStartup - loadStartedAt >= LoadTimeoutSeconds)
                    Fail("Unable to load music", "Audio loading timed out after 60 seconds");
            }

            bool shouldPlay = state.Powered && !state.Paused && !suspended && !AudioListener.pause;
            if (!shouldPlay && running)
            {
                CapturePosition();
                source.Stop();
                running = false;
            }
            if (shouldPlay && clip != null && clip.loadState == AudioDataLoadState.Loaded && !running)
                StartAtSavedPosition();
            if (running) CapturePosition();

            Status = !state.Powered ? "Off" : failure != null ? failure :
                request != null || mp3Task != null || (clip != null && clip.loadState != AudioDataLoadState.Loaded) ? "Loading" : clip == null ? "No track" :
                state.Paused ? "Paused" : suspended || AudioListener.pause ? "Suspended" : "Playing";
        }

        public void CapturePosition()
        {
            if (disposed || !running || source == null || clip == null || resetRequested) return;
            int sample = AudioSettings.dspTime < scheduledDsp ? scheduledSample : source.timeSamples;
            state.PositionSeconds = Math.Max(0, sample) / (double)clip.frequency;
        }

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
                loadStartedAt = Time.realtimeSinceStartup;
                if (type == AudioType.MPEG)
                {
                    // Sailwind's Windows Unity 2019 player rejects MPEG in its runtime
                    // audio loader. Decode locally on a worker, then upload PCM on Tick.
                    mp3Cancellation = new CancellationTokenSource();
                    mp3Cancellation.CancelAfter((int)(LoadTimeoutSeconds * 1000));
                    CancellationToken token = mp3Cancellation.Token;
                    mp3Task = Task.Run(() => DecodeMp3(fullPath, token), token);
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
                    clip.samples <= 0 || clip.frequency <= 0)
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
                DecodedMp3 decoded = mp3Task.Result;
                mp3Task = null;
                mp3Cancellation.Dispose();
                mp3Cancellation = null;
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

        // 256 MiB of float PCM per clip. Managed blocks are released after upload.
        // Native clip allocation temporarily doubles that footprint during handoff.
        internal const int MaximumMp3Samples = 64 * 1024 * 1024;
        internal const long MaximumMp3FileBytes = 128L * 1024 * 1024;
        private static readonly SemaphoreSlim Mp3DecodeSlot = new SemaphoreSlim(1, 1);

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

        private sealed class CancellableFileStream : Stream
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
            scheduledSample = (int)Math.Min(clip.samples - 1, Math.Floor((position % duration) * clip.frequency));
            source.timeSamples = scheduledSample;
            scheduledDsp = AudioSettings.dspTime + ScheduleLeadSeconds;
            source.PlayScheduled(scheduledDsp);
            running = true;
        }

        private void Fail(string message, string detail)
        {
            ReleaseTrack();
            failure = message;
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
            running = false;
            if (mp3Cancellation != null)
            {
                Task<DecodedMp3> abandoned = mp3Task;
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
                    cancellation.Dispose();
                }, TaskScheduler.Default);
            }
            if (request != null)
            {
                if (!request.isDone) request.Abort();
                request.Dispose();
                request = null;
            }
            if (source != null)
            {
                source.Stop();
                source.clip = null;
            }
            if (clip != null) UnityEngine.Object.Destroy(clip);
            clip = null;
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
                try { ReleaseTrack(); }
                finally
                {
                    if (emitter != null) UnityEngine.Object.Destroy(emitter);
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
        private void OnDestroy() { if (Playback != null) Playback.Dispose(); }
    }
}
