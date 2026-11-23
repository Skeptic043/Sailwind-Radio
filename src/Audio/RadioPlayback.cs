using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SailwindRadio.Audio
{
    /// <summary>One playback timeline shared by the built-in emitter and speaker endpoints. Call from the Unity main thread.</summary>
    public sealed partial class RadioPlayback : IDisposable
    {
        private const double ScheduleLeadSeconds = 0.05;
        private const double LoadTimeoutSeconds = 60;
        private const int MusicAheadSeconds = 4, MusicBufferSeconds = 5;
        private const int MusicUploadFrames = 4096, MusicUploadsPerTick = 16;
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
        private bool listenerKnown, flat;
        private AudioClip clip;
        private BufferedAudioClip musicBuffer, musicWooferBuffer;
        private BufferedAudioClip.ReadFrames readMusicFrames, readMusicWooferFrames;
        private readonly RadioBassFilter musicWooferBass = new RadioBassFilter();
        private readonly PeakLimiter musicWooferLimiter = new PeakLimiter();
        private StreamedAudio streamed;
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
        public bool TrackEnded { get; private set; }
        public bool LoadFailed { get { return failure != null; } }
        public string FailedPath { get; private set; }
        // Diagnostic count of corrective seeks. Checks and the editor probe observe churn with it.
        internal int DriftRepairs { get; private set; }

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
            // An application keeps playing. The new track opens when music returns.
            if (application != null) return;
            ReleaseTrack();
            failure = null;
            FailedPath = null;
            TrackEnded = false;
            requestedPath = null; // Explicit selection also retries a previously failed file.
            KeepOrAdoptPreload(path);
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
        /// <param name="flat">The listening point is not the native listener, such as the player's head in the boat camera.</param>
        public void SetAcoustics(Vector3 listenerPosition, bool listenerKnown, float obstruction, bool flat = false)
        {
            if (disposed) return;
            this.flat = flat;
            this.listenerPosition = listenerPosition;
            this.listenerKnown = listenerKnown && IsFinite(listenerPosition.x) && IsFinite(listenerPosition.y) && IsFinite(listenerPosition.z);
            builtIn.Obstruction = obstruction;
        }

        public void Tick(Vector3 worldPosition, bool suspended) => Tick(worldPosition, suspended, suspended);

        // A suspended radio is silent. Only a paused game also pauses its application.
        public void Tick(Vector3 worldPosition, bool suspended, bool paused)
        {
            if (disposed) return;
            if (host == null) { Dispose(); return; }
            EnsureOwnedOutput();
            builtIn.Position = worldPosition;
            builtIn.LocalVolume = state.LocalVolume;
            state.Volume = Single.IsNaN(state.Volume) ? 0.5f : Mathf.Clamp01(state.Volume);
            // The saved knob value stays as set. Outputs square it for useful quiet tabletop settings.
            builtIn.Flat = flat;
            builtIn.Update(state.Volume, listenerPosition, listenerKnown);
            foreach (var endpoint in endpoints.Values)
            {
                endpoint.Flat = flat;
                endpoint.Update(state.Volume, listenerPosition, listenerKnown);
            }
            double dsp = AudioSettings.dspTime;
            if (resetRequested || dsp < lastDsp)
            {
                // A device reset can invalidate source sample position. Resume from the last
                // position captured before the reset, not the newly reset source cursor.
                StopOutputs();
                StopBufferedApplication();
                StopBufferedInternetStream();
                resetRequested = false;
            }
            lastDsp = dsp;
            if (application != null)
            {
                TickApplication(WantsToPlay(suspended), paused || AudioListener.pause);
                return;
            }

            if (!String.Equals(requestedPath, state.TrackPath ?? "", StringComparison.Ordinal))
            {
                ReleaseTrack();
                requestedPath = state.TrackPath ?? "";
                failure = null;
                FailedPath = null;
                TrackEnded = false;
                TrackLabel = SafeLabel(requestedPath);
                if (!IsStreamUrl(requestedPath) && !KeepOrAdoptPreload(requestedPath) && state.Powered) BeginLoad();
            }
            else if (state.Powered && !wasPowered && clip == null && streamed == null && internetStream == null)
            {
                failure = null;
                FailedPath = null;
                if (!IsStreamUrl(requestedPath)) BeginLoad();
            }
            wasPowered = state.Powered;
            if (IsStreamUrl(requestedPath))
            {
                TickInternetStream(WantsToPlay(suspended));
                return;
            }
            AttachStream();
            if (streamed != null)
            {
                Exception fault = streamed.Fault;
                if (fault != null) FailStream(fault);
                else if (clip != null) streamed.Request(running ? SampleAt(dsp) : SafeSavedFrame());
            }
            // An open stream still decoding its opening falls under the buffer deadline below instead.
            if (Loading && !streamed.IsOpen && Time.realtimeSinceStartup - loadStartedAt >= LoadTimeoutSeconds)
                Fail("Unable to load music", "Audio loading timed out after 60 seconds");

            FinishIfEnded();
            FinishIfDamaged(dsp, WantsToPlay(suspended));
            bool shouldPlay = WantsToPlay(suspended) && !TrackEnded;
            if (clip != null && !TrackEnded) UpdateMusicBuffer(dsp);
            bool streamReady = streamed == null || (clip != null && (running || MusicReady(SafeSavedFrame())));
            if (streamed != null && shouldPlay && !streamReady)
            {
                if (streamWaitingSince < 0) streamWaitingSince = Time.realtimeSinceStartup;
                else if (Time.realtimeSinceStartup - streamWaitingSince >= LoadTimeoutSeconds)
                {
                    Fail("Unable to decode music", "Streamed audio buffer did not recover");
                    shouldPlay = false;
                }
            }
            else streamWaitingSince = -1;
            if (!shouldPlay && running)
            {
                CapturePosition();
                PauseOutputs();
            }
            if (shouldPlay && clip != null && !running && streamReady)
                StartAtSavedPosition();
            if (running) CapturePosition();
            if (running) JoinEndpoints();
            if (running) CheckOutputHealth();

            bool loading = Loading || (shouldPlay && streamed != null && !running && !streamReady);
            Status = !state.Powered ? "Off" : failure != null ? failure : loading ? "Loading" : clip == null ? "No track" :
                TrackEnded ? "Ended" : state.Paused ? "Paused" : suspended || AudioListener.pause ? "Suspended" : "Playing";
            // The selected track keeps priority. The next one opens only once this one can play.
            PollPreload(state.Powered && failure == null && clip != null && !loading);
        }

        // The player wants sound from this radio now: on, not paused, and the game neither suspended nor paused.
        private bool WantsToPlay(bool suspended) => state.Powered && !state.Paused && !suspended && !AudioListener.pause;

        // Whether this radio's outputs are making sound or about to: an open track that hasn't ended or
        // failed, an internet radio stream, or an application. Only then do they need cabin muffling.
        internal bool Audible(bool suspended) =>
            !disposed && WantsToPlay(suspended) && (application != null ||
                (internetStream != null && failure == null && streamScheduled) ||
                (clip != null && failure == null && !TrackEnded));

        // True while the current track's stream is still opening.
        private bool Loading { get { return streamed != null && clip == null; } }

        // The current song stays named while opening, paused or suspended, but not once it ends or fails.
        public bool NamesTrack => !disposed && state.Powered && failure == null && !TrackEnded && (clip != null || Loading || internetStream != null);

        public void CapturePosition()
        {
            if (disposed || !running || clip == null || internetStream != null || resetRequested || AudioSettings.dspTime < lastDsp) return;
            if (FinishIfEnded()) return;
            state.PositionSeconds = SampleAt(AudioSettings.dspTime) / (double)clip.frequency;
        }

        private int SafeSavedFrame()
        { return clip == null ? 0 : SavedFrame(streamed.Frames, streamed.SampleRate); }

        private int SavedFrame(int samples, int frequency)
        {
            if (samples <= 0 || frequency <= 0) return 0;
            double seconds = state.PositionSeconds;
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) return 0;
            return (int)Math.Min(samples - 1, Math.Floor(seconds * frequency));
        }

        private int SampleAt(double dsp)
        {
            long sample = scheduledSample + (long)Math.Round(Math.Max(0, dsp - scheduledDsp) * clip.frequency);
            return (int)Math.Min(musicBuffer.WrittenThrough, sample);
        }

        internal static float DistanceGain(float distance)
        { return RadioSpeakerOutput.DistanceGain(distance, RadioSpeakerProfile.BuiltIn.Radius); }

        private static bool IsFinite(float value) { return RadioSpeakerOutput.Finite(value); }
        private void BeginLoad()
        {
            if (String.IsNullOrWhiteSpace(requestedPath)) return;
            try
            {
                string fullPath = LocalPath(requestedPath);
                loadStartedAt = Time.realtimeSinceStartup;
                if (!PcmDecoders.Supports(fullPath))
                    throw new IOException(PcmDecoders.UnsupportedMessage(fullPath) ?? PcmDecoders.ChooseFormatMessage);
                // The stream's own worker opens and decodes the file, never this thread.
                streamed = StreamedAudio.Start(fullPath, state.PositionSeconds);
            }
            catch (Exception ex) { Fail("Unable to load music", ex.Message); }
        }

        // Only absolute local filesystem paths. A URL or UNC share must never turn
        // a music setting into an HTTP request or a network filesystem operation.
        private static string LocalPath(string path)
        {
            if (!Path.IsPathRooted(path)) throw new IOException("Choose an absolute local music file path");
            string fullPath = Path.GetFullPath(path);
            var uri = new Uri(fullPath);
            if (!uri.IsFile || uri.IsUnc) throw new IOException("Choose a local music file");
            return fullPath;
        }

        // The decoder and the native clip have separate bounded windows. Only the active track owns a clip.
        private void AttachStream()
        {
            if (streamed == null || clip != null || !streamed.IsOpen || streamed.Fault != null) return;
            int frame = SavedFrame(streamed.Frames, streamed.SampleRate);
            streamed.Request(frame);
            if (!streamed.Ready(frame)) return;
            try
            {
                int capacity = Math.Min(streamed.Frames, streamed.SampleRate * MusicBufferSeconds);
                musicBuffer = new BufferedAudioClip("Sailwind Radio buffered music", streamed.SampleRate,
                    streamed.Channels, capacity, Math.Min(capacity, MusicUploadFrames));
                musicBuffer.Reset(frame);
                readMusicFrames = streamed.TryCopy;
                readMusicWooferFrames = ReadMusicWooferFrames;
                musicWooferBass.SetProfile(streamed.SampleRate, true);
                musicWooferLimiter.Reset();
                clip = musicBuffer.Clip;
                source.clip = clip;
            }
            catch (Exception ex) { Fail("Unable to decode music", ex.Message); }
        }

        private bool HasWooferEndpoint()
        {
            foreach (var endpoint in endpoints.Values)
                if (endpoint.Profile != null && endpoint.Profile.IsWoofer) return true;
            return false;
        }

        private bool ReadMusicWooferFrames(long firstFrame, float[] data, int frames)
        {
            if (streamed == null || !streamed.TryCopy(firstFrame, data, frames)) return false;
            musicWooferBass.Process(data, streamed.Channels, frames);
            musicWooferLimiter.Process(data, streamed.Channels, frames, 1f, streamed.SampleRate);
            return true;
        }

        private BufferedAudioClip MusicBufferFor(RadioSpeakerOutput output) =>
            output.Profile != null && output.Profile.IsWoofer && musicWooferBuffer != null ? musicWooferBuffer : musicBuffer;

        private int MusicEndFrame => Math.Min(streamed.Frames, streamed.DamageFrame);

        private bool MusicReady(int frame)
        {
            long required = Math.Min((long)MusicEndFrame, (long)frame + streamed.SampleRate);
            return frame >= musicBuffer.FirstFrame && frame < musicBuffer.WrittenThrough &&
                musicBuffer.WrittenThrough >= required;
        }

        private bool LoopMusicBuffer => streamed.Frames > musicBuffer.CapacityFrames;

        private double BufferedMusicEndDsp(BufferedAudioClip buffer = null) =>
            scheduledDsp + (Math.Min((buffer ?? musicBuffer).WrittenThrough, MusicEndFrame) - scheduledSample) / (double)clip.frequency;

        private void UpdateMusicBuffer(double dsp)
        {
            try
            {
                if (running && musicBuffer.WrittenThrough < MusicEndFrame && dsp >= BufferedMusicEndDsp())
                {
                    // The scheduled end stopped native playback. Resume at the first frame it could not play.
                    state.PositionSeconds = musicBuffer.WrittenThrough / (double)clip.frequency;
                    StopOutputs();
                }

                int frame = running ? SampleAt(dsp) : SafeSavedFrame();
                if (frame < musicBuffer.FirstFrame || frame > musicBuffer.WrittenThrough)
                {
                    StopOutputs();
                    musicBuffer.Reset(frame);
                    if (musicWooferBuffer != null)
                    {
                        musicWooferBuffer.Reset(frame);
                        musicWooferBass.Reset();
                        musicWooferLimiter.Reset();
                    }
                }
                long lastWritten = musicBuffer.WrittenThrough;
                int end = MusicEndFrame;
                long target = Math.Min((long)end, (long)frame + streamed.SampleRate * MusicAheadSeconds);
                long retainFrom = Math.Max(0, frame - streamed.SampleRate / 2);
                for (int block = 0; block < MusicUploadsPerTick && musicBuffer.WrittenThrough < target; block++)
                {
                    int count = (int)Math.Min(musicBuffer.BlockFrames, end - musicBuffer.WrittenThrough);
                    if (!musicBuffer.TryAppend(retainFrom, count, readMusicFrames)) break;
                }

                bool needWoofer = HasWooferEndpoint();
                if (needWoofer && musicWooferBuffer == null)
                {
                    musicWooferBuffer = new BufferedAudioClip("Sailwind Radio buffered music woofer", streamed.SampleRate,
                        streamed.Channels, musicBuffer.CapacityFrames, musicBuffer.BlockFrames);
                    musicWooferBuffer.Reset(frame);
                    musicWooferBass.SetProfile(streamed.SampleRate, true);
                    musicWooferLimiter.Reset();
                }
                else if (!needWoofer && musicWooferBuffer != null)
                {
                    musicWooferBuffer.Dispose();
                    musicWooferBuffer = null;
                }

                long lastWooferWritten = musicWooferBuffer == null ? 0 : musicWooferBuffer.WrittenThrough;
                if (musicWooferBuffer != null)
                {
                    if (frame < musicWooferBuffer.FirstFrame || frame > musicWooferBuffer.WrittenThrough)
                    {
                        StopWooferOutputs(musicWooferBuffer);
                        musicWooferBuffer.Reset(frame);
                        musicWooferBass.Reset();
                        musicWooferLimiter.Reset();
                    }
                    for (int block = 0; block < MusicUploadsPerTick && musicWooferBuffer.WrittenThrough < target; block++)
                    {
                        int count = (int)Math.Min(musicWooferBuffer.BlockFrames, end - musicWooferBuffer.WrittenThrough);
                        if (!musicWooferBuffer.TryAppend(retainFrom, count, readMusicWooferFrames)) break;
                    }
                }

                if (running && (musicBuffer.WrittenThrough != lastWritten ||
                    (musicWooferBuffer != null && musicWooferBuffer.WrittenThrough != lastWooferWritten)))
                {
                    double horizon = BufferedMusicEndDsp(musicBuffer);
                    double wooferHorizon = musicWooferBuffer == null ? horizon : BufferedMusicEndDsp(musicWooferBuffer);
                    builtIn.ExtendBuffered(horizon);
                    foreach (var endpoint in endpoints.Values)
                        endpoint.ExtendBuffered(endpoint.Profile.IsWoofer && musicWooferBuffer != null ? wooferHorizon : horizon);
                }
            }
            catch (InvalidOperationException ex) { Fail("Unable to play buffered music", ex.Message); }
        }

        private void StartAtSavedPosition()
        {
            double position = state.PositionSeconds;
            if (Double.IsNaN(position) || Double.IsInfinity(position) || position < 0) position = 0;
            double duration = streamed.Frames / (double)clip.frequency;
            if (position >= duration)
            {
                state.PositionSeconds = duration;
                TrackEnded = true;
                return;
            }
            scheduledSample = SafeSavedFrame();
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
            BufferedAudioClip buffer = MusicBufferFor(output);
            if (output.Paused && output.Source != null && buffer != null && output.Source.clip == buffer.Clip)
            {
                output.Resume(buffer.ClipFrame(scheduledSample), now);
                output.ExtendBuffered(BufferedMusicEndDsp(buffer));
            }
            else ScheduleEndpoint(output, now + ScheduleLeadSeconds, SampleAt(now + ScheduleLeadSeconds));
        }

        private double EndDspTime()
        { return clip == null ? Double.PositiveInfinity : scheduledDsp + (streamed.Frames - scheduledSample) / (double)clip.frequency; }

        private bool FinishIfEnded()
        {
            if (!running || clip == null || musicBuffer.WrittenThrough < streamed.Frames ||
                AudioSettings.dspTime < EndDspTime()) return false;
            state.PositionSeconds = streamed.Frames / (double)clip.frequency;
            TrackEnded = true;
            StopOutputs();
            return true;
        }

        // A decode failure after audio played ends the track where the damage starts, like a normal
        // end, so the queue advances and keeps the song. A paused radio ends only once it would play.
        private void FinishIfDamaged(double dsp, bool wantsToPlay)
        {
            if (streamed == null || clip == null || TrackEnded) return;
            int damage = streamed.DamageFrame;
            bool reached = running ? SampleAt(dsp) >= damage : wantsToPlay && SafeSavedFrame() >= damage;
            if (!reached) return;
            Exception reason = streamed.Damage;
            state.PositionSeconds = streamed.Frames / (double)clip.frequency;
            TrackEnded = true;
            StopOutputs();
            warning("Music ended early at damaged audio: " + (reason == null ? "The decoder failed" : reason.Message) + NamedFile(requestedPath));
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
            if (start >= EndDspTime()) return;
            ScheduleEndpoint(output, start, SampleAt(start));
        }

        private void ScheduleEndpoint(RadioSpeakerOutput endpoint, double start, int sample)
        {
            BufferedAudioClip buffer = MusicBufferFor(endpoint);
            if (clip == null || buffer == null || sample >= MusicEndFrame) return;
            endpoint.PlayBuffered(buffer, sample, start, BufferedMusicEndDsp(buffer), LoopMusicBuffer);
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
            // Unity reports a stopped voice as virtual, so only a playing one is skipped as unmeasurable.
            if (output.Source.isPlaying && (output.Source.isVirtual || output.Source.volume <= 0)) { output.DriftObservations = 0; return; }
            BufferedAudioClip buffer = MusicBufferFor(output);
            if (buffer == null) return;
            int expected = buffer.ClipFrame(SampleAt(now));
            int difference = Math.Abs(output.Source.timeSamples - expected);
            if (LoopMusicBuffer) difference = Math.Min(difference, buffer.CapacityFrames - difference);
            int bufferLength, bufferCount;
            AudioSettings.GetDSPBufferSize(out bufferLength, out bufferCount);
            double tolerance = Math.Max(.05, 2d * bufferLength / Math.Max(8000, AudioSettings.outputSampleRate) + .01);
            if (output.Source.isPlaying)
            {
                if (difference <= clip.frequency * tolerance) { output.DriftObservations = 0; return; }
                if (++output.DriftObservations < 2) return;
            }
            output.DriftObservations = 0;
            DriftRepairs++;
            // Native cursors are observations, never the radio's master clock. Repair
            // a revived/lagging output without restarting healthy neighboring voices.
            if (output.Source.isPlaying)
            {
                output.Pause();
                output.Resume(expected, now);
                output.ExtendBuffered(BufferedMusicEndDsp(buffer));
            }
            else ScheduleEndpoint(output, now + ScheduleLeadSeconds, SampleAt(now + ScheduleLeadSeconds));
        }

        // A missing or unreadable file keeps its saved position for when it returns. A decode failure
        // happens before any audio, possibly because the saved position lies in damaged data, so the
        // position resets and a later selection starts from the beginning.
        private void FailStream(Exception fault)
        {
            if (fault is FileNotFoundException || fault is DirectoryNotFoundException || fault is DriveNotFoundException ||
                fault is UnauthorizedAccessException)
            {
                Fail("Unable to load music", fault.Message);
                return;
            }
            state.PositionSeconds = 0;
            Fail("Unable to decode music", fault.Message);
        }

        private void Fail(string message, string detail)
        {
            ReleaseTrack();
            failure = message;
            FailedPath = requestedPath;
            warning(message + ": " + detail + NamedFile(requestedPath));
        }

        // The file name only. Players post logs, so a full path or stream URL never appears in a warning.
        private static string NamedFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
            if (IsStreamUrl(path)) return " (internet stream)";
            try
            {
                string name = Path.GetFileName(path);
                return String.IsNullOrEmpty(name) ? "" : " (" + name + ")";
            }
            catch (ArgumentException) { return ""; }
        }

        private static string SafeLabel(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "No track";
            if (IsStreamUrl(path))
            {
                try { return new Uri(path.Trim()).Host; }
                catch { return "Internet radio"; }
            }
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
            ReleaseInternetStream();
            musicBuffer?.Dispose();
            musicBuffer = null;
            musicWooferBuffer?.Dispose();
            musicWooferBuffer = null;
            readMusicFrames = null;
            readMusicWooferFrames = null;
            clip = null;
            // Disposal never waits for the stream worker. A late open result dies with it.
            if (streamed != null) { streamed.Dispose(); streamed = null; }
            streamWaitingSince = -1;
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
                    try { DisposeApplication(); }
                    finally
                    {
                        try { ReleaseTrack(); }
                        finally { ReleasePreload(); }
                    }
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
