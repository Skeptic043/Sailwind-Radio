using System;
using System.Collections.Generic;
using System.IO;
using UnityEngine;

namespace SailwindRadio
{
    /// <summary>One local track streamed to the built-in emitter and any speaker endpoints. Call only from the Unity main thread.</summary>
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
        // Playhead at the previous running Tick, so loop damage ends the track only where it is crossed.
        private int lastSample;
        private double loadStartedAt;
        private double streamWaitingSince = -1;

        public string Status { get; private set; }
        public string TrackLabel { get; private set; }
        public bool RepeatTrack { get; set; } = true;
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
            // The saved knob value stays as set. Outputs square it for useful quiet tabletop settings.
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

            if (!String.Equals(requestedPath, state.TrackPath ?? "", StringComparison.Ordinal))
            {
                ReleaseTrack();
                requestedPath = state.TrackPath ?? "";
                failure = null;
                FailedPath = null;
                TrackEnded = false;
                TrackLabel = SafeLabel(requestedPath);
                if (!KeepOrAdoptPreload(requestedPath) && state.Powered) BeginLoad();
            }
            else if (state.Powered && !wasPowered && clip == null && streamed == null)
            {
                failure = null;
                FailedPath = null;
                BeginLoad();
            }
            wasPowered = state.Powered;
            AttachStream();
            if (streamed != null)
            {
                streamed.Loop = RepeatTrack;
                Exception fault = streamed.Fault;
                if (fault != null) FailStream(fault);
                else if (clip != null) streamed.Request(running ? SampleAt(dsp) : SafeSavedFrame());
            }
            // An open stream still decoding its opening falls under the buffer deadline below instead.
            if (Loading && !streamed.IsOpen && Time.realtimeSinceStartup - loadStartedAt >= LoadTimeoutSeconds)
                Fail("Unable to load music", "Audio loading timed out after 60 seconds");

            FinishIfEnded();
            FinishIfDamaged(dsp, state.Powered && !state.Paused && !suspended && !AudioListener.pause);
            bool shouldPlay = state.Powered && !state.Paused && !suspended && !AudioListener.pause && !TrackEnded;
            int wantedFrame = SafeSavedFrame();
            bool streamReady = streamed == null || (clip != null && streamed.Ready(running ? SampleAt(dsp) : wantedFrame));
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

            bool loading = Loading || (streamed != null && !running && !streamReady);
            Status = !state.Powered ? "Off" : failure != null ? failure : loading ? "Loading" : clip == null ? "No track" :
                TrackEnded ? "Ended" : state.Paused ? "Paused" : suspended || AudioListener.pause ? "Suspended" : "Playing";
            // The selected track keeps priority. The next one opens only once this one can play.
            PollPreload(state.Powered && failure == null && clip != null && !loading);
        }

        // True while the current track's stream is still opening.
        private bool Loading { get { return streamed != null && clip == null; } }

        public void CapturePosition()
        {
            if (disposed || !running || clip == null || resetRequested || AudioSettings.dspTime < lastDsp) return;
            if (FinishIfEnded()) return;
            state.PositionSeconds = SampleAt(AudioSettings.dspTime) / (double)clip.frequency;
        }

        private int SafeSavedFrame()
        { return clip == null ? 0 : SavedFrame(clip.samples, clip.frequency); }

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

        // Creating a PCM-reader clip allocates no sample data. Each output gets its own reader. Unity pre-reads
        // the opening of a new clip and plays it unchanged when the track starts at frame 0, so the clip waits
        // until the stream holds the start. Otherwise it would begin with silence for frames still decoding.
        private void AttachStream()
        {
            if (streamed == null || clip != null || !streamed.IsOpen || streamed.Fault != null) return;
            int frame = SavedFrame(streamed.Frames, streamed.SampleRate);
            streamed.Request(frame);
            if (!streamed.Ready(frame)) return;
            try
            {
                clip = streamed.CreateClip(streamed.CreateReader());
                if (clip == null) throw new IOException("Unable to create streamed audio clip");
                builtIn.StreamClip = clip;
                source.clip = clip;
            }
            catch (Exception ex) { Fail("Unable to decode music", ex.Message); }
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
            lastSample = scheduledSample;
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
            if (output.Paused && output.Source != null && output.StreamClip != null && output.Source.clip == output.StreamClip)
            {
                Want(scheduledSample);
                output.Resume(scheduledSample, now);
            }
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

        // A decode failure after audio played ends the track where the damage starts, like a normal
        // end, so the queue advances and keeps the song. A paused radio ends only once it would play.
        // Damage found by the looping look-ahead lies behind the playhead, so it ends the track only
        // once the wrapped playhead crosses it, and never from a saved position.
        private void FinishIfDamaged(double dsp, bool wantsToPlay)
        {
            if (streamed == null || clip == null || TrackEnded) return;
            int damage = streamed.DamageFrame, loopDamage = streamed.LoopDamageFrame;
            bool reached;
            if (running)
            {
                int sample = SampleAt(dsp), previous = lastSample;
                lastSample = sample;
                reached = sample >= damage || (loopDamage < clip.samples && (sample >= previous
                    ? previous <= loopDamage && loopDamage <= sample
                    : previous <= loopDamage || sample >= loopDamage));
            }
            else reached = wantsToPlay && SafeSavedFrame() >= damage;
            if (!reached) return;
            Exception reason = streamed.Damage;
            state.PositionSeconds = clip.samples / (double)clip.frequency;
            TrackEnded = true;
            StopOutputs();
            warning("Music ended early at damaged audio: " + (reason == null ? "The decoder failed" : reason.Message) + NamedFile(requestedPath));
        }

        // The main thread alone moves the stream worker. Every native seek asks for its frame first.
        private void Want(int frame) { if (streamed != null) streamed.Request(frame); }

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
            Want(sample);
            if (endpoint.StreamClip == null)
            {
                endpoint.StreamClip = streamed.CreateClip(streamed.CreateReader());
                // A refused clip leaves this output silent. Sharing another output's clip would share its cursor.
                if (endpoint.StreamClip == null) return;
            }
            endpoint.Source.clip = endpoint.StreamClip;
            endpoint.Source.loop = RepeatTrack;
            // Unity alone positions the reader. It seeks it on PlayScheduled to a nonzero sample and, at sample 0,
            // plays the opening it pre-read. Moving the reader here would desynchronize it from that buffered audio.
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
            // Unity reports a stopped voice as virtual, so only a playing one is skipped as unmeasurable.
            if (output.Source.isPlaying && (output.Source.isVirtual || output.Source.volume <= 0)) { output.DriftObservations = 0; return; }
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
            DriftRepairs++;
            // Native cursors are observations, never the radio's master clock. Repair
            // a revived/lagging output without restarting healthy neighboring voices.
            if (output.Source.isPlaying)
            {
                Want(expected);
                output.Pause();
                output.Resume(expected, now);
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

        // The file name only. Players post logs, so a full path never appears in a warning.
        private static string NamedFile(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return "";
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
            foreach (var endpoint in endpoints.Values) endpoint.ReleaseStreamClip();
            if (builtIn != null)
            {
                if (builtIn.StreamClip == clip) builtIn.StreamClip = null;
                else builtIn.ReleaseStreamClip();
            }
            if (clip != null) UnityEngine.Object.Destroy(clip);
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
