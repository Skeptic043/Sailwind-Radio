#if UNITY_EDITOR
using System;
using System.Collections;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Reflection;
using System.Threading;
using UnityEditor;
using UnityEngine;
using UnityEngine.Profiling;
using SailwindRadio;

// Run only inside the isolated project created by Run-UnityEditorProbe.ps1.
public sealed class RadioUnityEditorProbe : MonoBehaviour
{
    private static readonly int[] ProbeRates = { 44100, 88200, 96000, 176400, 192000 };
    private RadioPlayback playback;
    private RadioState state;
    private bool suspended;
    private int checks, failures;
    private string report = "";
    private string output;
    private int readerFrames;
    private double readerPhase;
    private int readerRate;
    private double maxTickMs;
    private int setPositionCalls, lastSetPosition = -1, setPositionThread, mainThread;
    private readonly Stopwatch tickWatch = new Stopwatch();
    // The sync phase drives speaker endpoints and the preload the way Plugin.TickPlayback does.
    private bool driveEndpoints;
    private readonly List<int> activeEndpoints = new List<int>();
    private string preloadPath = "";
    private int syncPeriod = 1;
    private readonly List<int> readSizes = new List<int>();

    [InitializeOnLoadMethod]
    private static void Register()
    {
        EditorApplication.playModeStateChanged += delegate(PlayModeStateChange mode)
        {
            if (mode == PlayModeStateChange.EnteredPlayMode && SessionState.GetBool("RadioAudioProbe", false))
                new GameObject("Radio audio probe").AddComponent<RadioUnityEditorProbe>();
        };
    }

    public static void Begin()
    {
        SessionState.SetBool("RadioAudioProbe", true);
        EditorApplication.isPlaying = true;
    }

    private IEnumerator Start()
    {
        output = Path.GetFullPath(Path.Combine(Application.dataPath, "../probe-result.txt"));
        report = "Unity editor audio probe " + Application.unityVersion + Environment.NewLine;
        int bufferLength, bufferCount;
        AudioSettings.GetDSPBufferSize(out bufferLength, out bufferCount);
        Info("outputSampleRate=" + AudioSettings.outputSampleRate + " dspBuffer=" + bufferLength + "x" + bufferCount +
            " speakerMode=" + AudioSettings.speakerMode + " driverCaps=" + AudioSettings.driverCapabilities);
        // The default scene's camera brings a second listener, and Unity then mixes silence.
        foreach (var other in FindObjectsOfType<AudioListener>()) Destroy(other);
        new GameObject("Probe listener").AddComponent<AudioListener>();
        // Master output is silent. Sources keep real gain so drift repair runs as in play.
        AudioListener.volume = 0;
        string root = Path.GetFullPath(Path.Combine(Application.dataPath, ".."));
        MediaFoundationSupport.Detect();
        string only = ReadSetting(Path.Combine(root, "probe-only.txt"));
        string music = ReadSetting(Path.Combine(root, "probe-music.txt"));
        if (only != "")
        {
            Info("Phases limited to " + only);
            if (only.Contains("callback")) yield return CallbackPhase();
            if (only.Contains("stutter")) yield return StutterPhase(root, music);
            if (only.Contains("sync")) yield return SyncPhase(root);
            Finish();
            yield break;
        }
        // Real files first, so their reads meet the coldest disk cache of the run.
        yield return StutterPhase(root, music);
        yield return TonePhase();
        yield return RatePhase();
        yield return CallbackPhase();
        yield return SyncPhase(root);
        Info(MediaFoundationSupport.Summary + " in the editor process x64=" + Environment.Is64BitProcess);
        // Every in-source format streams at every length. These tracks are shorter than the 20 s window.
        foreach (string extension in new[] { ".wav", ".mp3", ".flac", ".ogg", ".m4a" })
        {
            if (extension == ".m4a" && !Check(MediaFoundationSupport.Enabled, "Media Foundation decision enables M4A in the editor")) continue;
            yield return LongTrackPhase(Path.Combine(root, "short-tone" + extension), 3, "short streamed " + extension.Substring(1).ToUpperInvariant(),
                15 * 44100, false, true, extension == ".mp3" ? 44100 / 5 : 0);
        }
        yield return LoopPhase(Path.Combine(root, "loop-tone.wav"));
        yield return TinyPhase(Path.Combine(root, "tiny-tone.wav"));
        yield return LongTrackPhase(Path.Combine(root, "long-tone.wav"), 500, "streamed long WAV", 780 * 44100);
        // OGG streams like every other format. The removed Unity loader refused 27 minutes.
        yield return LongTrackPhase(Path.Combine(root, "ogg-20min.ogg"), 600, "streamed 20 minute OGG", 1200 * 44100);
        yield return LongTrackPhase(Path.Combine(root, "ogg-27min.ogg"), 1500, "streamed 27 minute OGG", 1620 * 44100);
        if (MediaFoundationSupport.Enabled)
            yield return LongTrackPhase(Path.Combine(root, "long-tone.m4a"), 500, "streamed long M4A", 800 * 44100);
        Finish();
    }

    private IEnumerator TonePhase()
    {
        string fixture = Path.GetFullPath(Path.Combine(Application.dataPath, "../original-tone.wav"));
        WriteTone(fixture);
        state = new RadioState { TrackPath = fixture, Powered = true, Volume = 0 };
        playback = new RadioPlayback(this, state, message => report += "WARNING: " + message + Environment.NewLine);
        playback.SetAcoustics(Vector3.zero, true, 0);
        float deadline = Time.realtimeSinceStartup + 20;
        while (playback.Status != "Playing" && Time.realtimeSinceStartup < deadline)
            yield return null;
        if (!Check(playback.Status == "Playing", "original PCM WAV decoded by the in-source decoder")) yield break;
        foreach (float scale in new[] { 1f, 8f, 0.5f })
        {
            Time.timeScale = scale;
            yield return new WaitForSecondsRealtime(0.15f);
            double before = state.PositionSeconds;
            float started = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(0.5f);
            playback.CapturePosition();
            double elapsed = Time.realtimeSinceStartup - started;
            double moved = state.PositionSeconds - before;
            Check(Math.Abs(moved - elapsed) < 0.18, "timeScale=" + scale + " real=" + elapsed + " samples=" + moved);
        }
        Time.timeScale = 1;
        state.Paused = true;
        yield return null;
        double paused = state.PositionSeconds;
        yield return new WaitForSecondsRealtime(0.3f);
        Check(Math.Abs(state.PositionSeconds - paused) < 1.0 / 48000, "manual pause preserves sample position");
        suspended = true;
        yield return null;
        suspended = false;
        yield return new WaitForSecondsRealtime(0.2f);
        Check(playback.Status == "Paused" && state.PositionSeconds == paused, "suspension cannot undo manual pause");
        state.Paused = false;
        yield return new WaitForSecondsRealtime(0.3f);
        Check(state.PositionSeconds > paused + 0.1, "resume advances from retained position");
        suspended = true;
        yield return null;
        paused = state.PositionSeconds;
        yield return new WaitForSecondsRealtime(0.2f);
        Check(state.PositionSeconds == paused, "game suspension retains position");
        suspended = false;
        yield return new WaitForSecondsRealtime(0.3f);
        Check(state.PositionSeconds > paused + 0.1, "game suspension resumes active radio");
        playback.Dispose();
        playback = null;
    }

    // Plays each rate briefly as a PCM-reader stream, the only way production plays in-source PCM.
    private IEnumerator RatePhase()
    {
        var host = new GameObject("Rate probe");
        var source = host.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0;
        source.volume = 1;
        foreach (int rate in ProbeRates)
        {
            AudioClip clip = null;
            string label = rate + " Hz PCM-reader stream";
            try
            {
                readerFrames = 0;
                readerPhase = 0;
                readerRate = rate;
                clip = AudioClip.Create("rate " + rate, rate * 20, 2, rate, true, ReadTone);
            }
            catch (Exception ex) { Check(false, "RATE " + label + " creation threw " + ex.GetType().Name + ": " + ex.Message); continue; }
            if (clip == null) { Check(false, "RATE " + label + " creation returned null"); continue; }
            source.clip = clip;
            source.timeSamples = rate / 2;
            source.PlayScheduled(AudioSettings.dspTime + 0.1);
            yield return new WaitForSecondsRealtime(0.4f);
            int first = source.timeSamples;
            double dspFirst = AudioSettings.dspTime;
            float started = Time.realtimeSinceStartup;
            yield return new WaitForSecondsRealtime(1.5f);
            int last = source.timeSamples;
            double real = Time.realtimeSinceStartup - started, dsp = AudioSettings.dspTime - dspFirst;
            double measured = (last - first) / dsp;
            bool ok = source.isPlaying && clip.frequency == rate && clip.samples > 0 && Math.Abs(measured / rate - 1) < 0.05 &&
                readerFrames > rate;
            Check(ok, "RATE " + label + " clipFrequency=" + clip.frequency + " clipSamples=" + clip.samples +
                " advanced=" + (last - first) + " over dsp=" + dsp.ToString("F3") + "s real=" + real.ToString("F3") +
                "s rate=" + measured.ToString("F0") + " playing=" + source.isPlaying + " virtual=" + source.isVirtual +
                " readerFrames=" + readerFrames + " loadType=" + clip.loadType);
            source.Stop();
            source.clip = null;
            Destroy(clip);
        }
        Destroy(host);
    }

    // Records which AudioSource operations make Unity call a streamed clip's PCMSetPositionCallback,
    // so the test doubles can follow the real engine. Reported as a table of INFO lines.
    private IEnumerator CallbackPhase()
    {
        mainThread = Thread.CurrentThread.ManagedThreadId;
        var host = new GameObject("Callback probe");
        var source = host.AddComponent<AudioSource>();
        source.playOnAwake = false;
        source.spatialBlend = 0;
        source.volume = 1;
        readerFrames = 0;
        readerPhase = 0;
        readerRate = 44100;
        AudioClip clip = null;
        lock (readSizes) readSizes.Clear();
        yield return Observe("AudioClip.Create stream=true", () => clip = AudioClip.Create("callback probe", 44100 * 20, 2, 44100, true, ReadTone, OnSetPosition));
        if (!Check(clip != null, "CALLBACK probe clip created")) { Destroy(host); yield break; }
        yield return Observe("clip assignment", () => source.clip = clip);
        yield return Observe("timeSamples set while stopped", () => source.timeSamples = 44100);
        yield return Observe("PlayScheduled", () => source.PlayScheduled(AudioSettings.dspTime + 0.1));
        // Frames per reader call from creation through the first scheduled play show Unity's own stream buffer.
        lock (readSizes) Info("CALLBACK reader frames per call from creation through PlayScheduled: " + String.Join(",", readSizes.ConvertAll(n => n.ToString()).ToArray()));
        yield return Observe("timeSamples set while playing", () => source.timeSamples = 66150);
        yield return Observe("Pause", () => source.Pause());
        yield return Observe("timeSamples set while paused", () => source.timeSamples = 88200);
        yield return Observe("UnPause", () => source.UnPause());
        yield return Observe("Stop while playing", () => source.Stop());
        yield return Observe("Stop while stopped", () => source.Stop());
        yield return Observe("clip cleared", () => source.clip = null);
        yield return Observe("clip assignment again", () => source.clip = clip);
        yield return Observe("PlayScheduled again", () => source.PlayScheduled(AudioSettings.dspTime + 0.1));
        yield return Observe("Stop after playing again", () => source.Stop());
        // A stopped source that already played, scheduled again at sample 0, as a device reset or repair can do.
        yield return Observe("PlayScheduled at sample 0 after playing and Stop", () => { source.timeSamples = 0; source.PlayScheduled(AudioSettings.dspTime + 0.1); });
        yield return Observe("Stop after the sample 0 replay", () => source.Stop());
        source.clip = null;
        Destroy(clip);
        // A half-second looping stream shows whether Unity seeks the reader when it wraps.
        AudioClip loop = null;
        yield return Observe("AudioClip.Create looping", () => loop = AudioClip.Create("callback loop", 22050, 2, 44100, true, ReadTone, OnSetPosition));
        source.clip = loop;
        source.loop = true;
        yield return Observe("looping play over 1.3 s", () => source.PlayScheduled(AudioSettings.dspTime + 0.05), 1.3f);
        source.Stop();
        source.clip = null;
        Destroy(loop);
        Destroy(host);
        Check(Interlocked.CompareExchange(ref setPositionCalls, 0, 0) > 0, "CALLBACK phase observed set-position callbacks");
    }

    private IEnumerator Observe(string operation, Action action, float seconds = 0.3f)
    {
        int before = Interlocked.CompareExchange(ref setPositionCalls, 0, 0);
        Interlocked.Exchange(ref lastSetPosition, -1);
        Interlocked.Exchange(ref setPositionThread, 0);
        action();
        int immediate = Interlocked.CompareExchange(ref setPositionCalls, 0, 0) - before;
        yield return new WaitForSecondsRealtime(seconds);
        int total = Interlocked.CompareExchange(ref setPositionCalls, 0, 0) - before;
        int thread = Interlocked.CompareExchange(ref setPositionThread, 0, 0);
        Info("CALLBACK " + operation + ": setPosition calls during the call=" + immediate + " within " + seconds.ToString("F1") + " s=" + total +
            " last position=" + Interlocked.CompareExchange(ref lastSetPosition, 0, 0) +
            " thread=" + (total == 0 ? "none" : thread == mainThread ? "main" : "other"));
    }

    private void OnSetPosition(int position)
    {
        Interlocked.Increment(ref setPositionCalls);
        Interlocked.Exchange(ref lastSetPosition, position);
        Interlocked.Exchange(ref setPositionThread, Thread.CurrentThread.ManagedThreadId);
    }

    private void ReadTone(float[] data)
    {
        int frames = data.Length / 2;
        lock (readSizes) if (readSizes.Count < 64) readSizes.Add(frames);
        for (int i = 0; i < frames; i++)
        {
            data[i * 2] = data[i * 2 + 1] = (float)(.2 * Math.Sin(readerPhase));
            readerPhase += 2 * Math.PI * 440 / readerRate;
        }
        readerFrames += frames;
    }

    // MP3 frames add encoder delay and padding, so its length may exceed the source by a fraction of a second.
    private IEnumerator LongTrackPhase(string path, double saved, string label, int expectedSamples, bool repeat = true, bool budget = false,
        int sampleTolerance = 0)
    {
        if (!File.Exists(path)) { Check(false, label + " fixture missing: " + path); yield break; }
        state = new RadioState { TrackPath = path, Powered = true, Volume = 1, PositionSeconds = saved };
        playback = new RadioPlayback(this, state, message => report += "WARNING: " + message + Environment.NewLine) { RepeatTrack = repeat };
        playback.SetAcoustics(Vector3.zero, true, 0);
        maxTickMs = 0;
        float loadStarted = Time.realtimeSinceStartup;
        float deadline = loadStarted + 60;
        while (playback.Status != "Playing" && !playback.LoadFailed && Time.realtimeSinceStartup < deadline)
            yield return null;
        float loadSeconds = Time.realtimeSinceStartup - loadStarted;
        if (!Check(playback.Status == "Playing", label + " reaches Playing in " + loadSeconds.ToString("F2") + "s status=" + playback.Status))
        {
            playback.Dispose(); playback = null; yield break;
        }
        var source = (AudioSource)typeof(RadioPlayback).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(playback);
        var clip = (AudioClip)typeof(RadioPlayback).GetField("clip", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(playback);
        Check(clip != null && clip.samples >= expectedSamples && clip.samples <= expectedSamples + sampleTolerance &&
            clip.frequency == 44100 && clip.channels == 2,
            label + " clip samples=" + clip.samples + " expected=" + expectedSamples + " frequency=" + clip.frequency +
            " channels=" + clip.channels + " loadType=" + clip.loadType + " loadState=" + clip.loadState);
        Info(label + " runtimeMemoryBytes=" + Profiler.GetRuntimeMemorySizeLong(clip) + " fileBytes=" + new FileInfo(path).Length);
        yield return new WaitForSecondsRealtime(0.5f);
        playback.CapturePosition();
        double start = state.PositionSeconds;
        int native = source.timeSamples;
        Check(start >= saved && start < saved + 1.5 && Math.Abs(native / 44100.0 - start) < 0.25,
            label + " starts at saved position: saved=" + saved + " timeline=" + start.ToString("F3") +
            " native=" + (native / 44100.0).ToString("F3") + " playing=" + source.isPlaying + " virtual=" + source.isVirtual);
        int repairsBefore = playback.DriftRepairs;
        double maxDrift = 0;
        float began = Time.realtimeSinceStartup;
        double timelineBegan = start;
        for (int i = 0; i < 16; i++)
        {
            yield return new WaitForSecondsRealtime(0.5f);
            playback.CapturePosition();
            maxDrift = Math.Max(maxDrift, Math.Abs(source.timeSamples / 44100.0 - state.PositionSeconds));
        }
        double real = Time.realtimeSinceStartup - began, moved = state.PositionSeconds - timelineBegan;
        int nativeEnd = source.timeSamples;
        int repairs = playback.DriftRepairs - repairsBefore;
        Check(Math.Abs(moved - real) < 0.25 && Math.Abs((nativeEnd - native) / 44100.0 - (state.PositionSeconds - start)) < 0.3,
            label + " progresses at real time: real=" + real.ToString("F3") + " timeline=" + moved.ToString("F3") +
            " native=" + ((nativeEnd - native) / 44100.0).ToString("F3") + " maxNativeDrift=" + maxDrift.ToString("F4") +
            " playing=" + source.isPlaying + " virtual=" + source.isVirtual + " volume=" + source.volume);
        Check(repairs <= 1, label + " drift repairs during 8 s=" + repairs + " total=" + playback.DriftRepairs);
        // Main-thread cost of every Tick from creation through load, start and steady play.
        if (budget) Check(maxTickMs < 5, label + " max main-thread Tick=" + maxTickMs.ToString("F2") + "ms");
        else Info(label + " max main-thread Tick=" + maxTickMs.ToString("F2") + "ms");
        playback.Dispose();
        playback = null;
        yield return null;
    }

    // A one second track repeating many times, held whole in its ring. The timeline wraps with it.
    private IEnumerator LoopPhase(string path)
    {
        const int rate = 44100;
        WriteStereoTone(path, rate, rate);
        state = new RadioState { TrackPath = path, Powered = true, Volume = 1, PositionSeconds = .5 };
        playback = new RadioPlayback(this, state, message => report += "WARNING: " + message + Environment.NewLine) { RepeatTrack = true };
        playback.SetAcoustics(Vector3.zero, true, 0);
        maxTickMs = 0;
        float started = Time.realtimeSinceStartup;
        while (playback.Status != "Playing" && !playback.LoadFailed && Time.realtimeSinceStartup < started + 20) yield return null;
        if (!Check(playback.Status == "Playing", "repeating 1 s WAV reaches Playing in " + (Time.realtimeSinceStartup - started).ToString("F2") + "s"))
        { playback.Dispose(); playback = null; yield break; }
        var source = (AudioSource)typeof(RadioPlayback).GetField("source", BindingFlags.Instance | BindingFlags.NonPublic).GetValue(playback);
        yield return new WaitForSecondsRealtime(0.5f);
        playback.CapturePosition();
        double previous = state.PositionSeconds, unwrapped = 0, maxDrift = 0;
        int repairs = playback.DriftRepairs;
        float began = Time.realtimeSinceStartup;
        for (int i = 0; i < 24; i++)
        {
            yield return new WaitForSecondsRealtime(0.25f);
            playback.CapturePosition();
            double position = state.PositionSeconds;
            unwrapped += ((position - previous) % 1.0 + 1.0) % 1.0;
            previous = position;
            double native = source.timeSamples / (double)rate, difference = Math.Abs(native - position);
            maxDrift = Math.Max(maxDrift, Math.Min(difference, 1.0 - difference));
        }
        double real = Time.realtimeSinceStartup - began;
        Check(Math.Abs(unwrapped - real) < 0.25 && maxDrift < 0.1 && source.isPlaying,
            "repeating 1 s WAV loops at real time: real=" + real.ToString("F3") + " timeline=" + unwrapped.ToString("F3") +
            " maxNativeDrift=" + maxDrift.ToString("F4") + " playing=" + source.isPlaying);
        Check(playback.DriftRepairs - repairs <= 1, "repeating 1 s WAV drift repairs during 6 s=" + (playback.DriftRepairs - repairs));
        Check(maxTickMs < 5, "repeating 1 s WAV max main-thread Tick=" + maxTickMs.ToString("F2") + "ms");
        playback.Dispose();
        playback = null;
        yield return null;
    }

    // Three sample frames, far shorter than one audio callback.
    private IEnumerator TinyPhase(string path)
    {
        WriteStereoTone(path, 44100, 3);
        string warning = "";
        state = new RadioState { TrackPath = path, Powered = true, Volume = 1 };
        playback = new RadioPlayback(this, state, message => warning += message) { RepeatTrack = false };
        playback.SetAcoustics(Vector3.zero, true, 0);
        bool played = false;
        float started = Time.realtimeSinceStartup;
        while (playback.Status != "Ended" && !playback.LoadFailed && Time.realtimeSinceStartup < started + 20)
        {
            played |= playback.Status == "Playing";
            yield return null;
        }
        Check(playback.Status == "Ended" && warning == "" && state.PositionSeconds == 3 / 44100.0,
            "three-frame WAV streams and ends normally in " + (Time.realtimeSinceStartup - started).ToString("F2") + "s status=" +
            playback.Status + " sawPlaying=" + played + " warning=" + warning);
        playback.Dispose();
        playback = null;
        yield return null;
    }

    // Stereo synthetic track from a file of "rate frames". The left channel encodes the frame position for
    // RadioContentTap and the right channel is its constant reference.
    private sealed class SyncDecoder : IPcmDecoder
    {
        private readonly int period, delay;
        public int Channels { get { return 2; } }
        public int SampleRate { get; private set; }
        public long Frames { get; private set; }
        public long Position { get; private set; }
        private SyncDecoder(int rate, long frames, int delay) { SampleRate = rate; Frames = frames; this.delay = delay; period = SyncPeriod(rate, frames); }
        // An optional third field is a delay in milliseconds per read, like a slow decoder on a cold disk.
        internal static IPcmDecoder Open(string path, CancellationToken token)
        {
            string[] fields = File.ReadAllText(path).Split(' ');
            return new SyncDecoder(Int32.Parse(fields[0]), Int64.Parse(fields[1]), fields.Length > 2 ? Int32.Parse(fields[2]) : 0);
        }
        public void Seek(long frame) { Position = Math.Max(0, Math.Min(Frames, frame)); }
        public int Read(float[] buffer, int offset, int count)
        {
            if (delay > 0) Thread.Sleep(delay);
            int frames = (int)Math.Max(0, Math.Min(count / 2, Frames - Position));
            for (int f = 0; f < frames; f++)
            {
                buffer[offset + f * 2] = RadioContentTap.Left(Position + f, period);
                buffer[offset + f * 2 + 1] = RadioContentTap.Reference;
            }
            Position += frames;
            return frames * 2;
        }
        public void Dispose() { }
    }

    private static int SyncPeriod(int rate, long frames) { return (int)Math.Min(frames, 4L * rate); }

    private static string WriteSync(string root, string name, int rate, long frames, int delay = 0)
    {
        string path = Path.Combine(root, name);
        File.WriteAllText(path, rate + " " + frames + " " + delay);
        return path;
    }

    private double scenarioDsp;
    private readonly List<double> timelineOffsets = new List<double>();
    private static readonly string[] EndpointNames = { "built-in", "small", "normal", "woofer" };

    // Measures every output's decoded content, never its native cursor. All outputs share one timeline,
    // so each speaker must carry the same content as the built-in output in the same DSP buffer.
    private IEnumerator SyncPhase(string root)
    {
        int rate = AudioSettings.outputSampleRate;
        PcmDecoders.Register(".sync", SyncDecoder.Open);
        string a = WriteSync(root, "sync-a.sync", rate, 60L * rate), b = WriteSync(root, "sync-b.sync", rate, 40L * rate),
            c = WriteSync(root, "sync-c.sync", rate, 30L * rate), tiny = WriteSync(root, "sync-short.sync", rate, rate);
        driveEndpoints = true;
        timelineOffsets.Clear();
        bool wasMuted = EditorUtility.audioMasterMute;
        yield return MuteDevice();
        if (silencer == null) yield break;
        syncPeriod = SyncPeriod(rate, 60L * rate);
        SetEndpoints(1, 2, 3);

        NewSyncPlayback(a, 0);
        yield return null;
        BeginScenario();
        state.Powered = true;
        yield return WaitPlaying();
        yield return Measure("powered on at the start with speakers connected");

        activeEndpoints.Remove(2);
        yield return new WaitForSecondsRealtime(.5f);
        BeginScenario();
        activeEndpoints.Add(2);
        yield return Measure("a speaker leaves and rejoins");

        SetEndpoints();
        yield return new WaitForSecondsRealtime(.5f);
        BeginScenario();
        SetEndpoints(1, 2, 3);
        yield return Measure("speakers join a playing radio");

        state.Paused = true;
        yield return new WaitForSecondsRealtime(.5f);
        BeginScenario();
        state.Paused = false;
        yield return Measure("pause then resume");

        state.Powered = false;
        yield return new WaitForSecondsRealtime(.5f);
        BeginScenario();
        state.Powered = true;
        yield return Measure("power off then on");

        int repairs = playback.DriftRepairs;
        BeginScenario();
        var displaced = Endpoint(1).Source;
        displaced.timeSamples = (displaced.timeSamples + rate / 5) % displaced.clip.samples;
        yield return WaitFor(() => playback.DriftRepairs > repairs, 5);
        Check(playback.DriftRepairs > repairs, "SYNC a displaced speaker cursor triggers drift repair: repairs=" + (playback.DriftRepairs - repairs));
        yield return Measure("drift repair after a displaced speaker cursor", 1);

        repairs = playback.DriftRepairs;
        BeginScenario();
        Endpoint(2).Source.Stop();
        yield return WaitFor(() => playback.DriftRepairs > repairs, 5);
        Check(playback.DriftRepairs > repairs, "SYNC a natively stopped speaker triggers drift repair: repairs=" + (playback.DriftRepairs - repairs));
        yield return Measure("drift repair after a native speaker stop", 2);

        preloadPath = b;
        yield return WaitFor(PreloadOpen, 20);
        yield return new WaitForSecondsRealtime(1f);
        Check(PreloadOpen(), "SYNC the upcoming track preloads");
        BeginScenario();
        playback.SetTrack(b);
        preloadPath = "";
        yield return WaitPlaying();
        yield return Measure("next track adopting the preload");

        BeginScenario();
        playback.SetTrack(c);
        yield return WaitPlaying();
        yield return Measure("next track without preload");

        // Decoding at about seven times real time, like a large file on a cold disk.
        string slowA = WriteSync(root, "sync-slow-a.sync", rate, 40L * rate, 12), slowB = WriteSync(root, "sync-slow-b.sync", rate, 30L * rate, 12);
        BeginScenario();
        playback.SetTrack(slowA);
        yield return WaitPlaying();
        yield return Measure("next track without preload on a slow decoder");

        preloadPath = slowB;
        yield return WaitFor(PreloadOpen, 20);
        yield return new WaitForSecondsRealtime(2f);
        BeginScenario();
        playback.SetTrack(slowB);
        preloadPath = "";
        yield return WaitPlaying();
        yield return Measure("next track adopting the preload on a slow decoder");

        syncPeriod = SyncPeriod(rate, rate);
        BeginScenario();
        playback.SetTrack(tiny);
        yield return WaitPlaying();
        yield return Measure("one second repeating track");
        syncPeriod = SyncPeriod(rate, 60L * rate);

        NewSyncPlayback(a, 30.25);
        yield return null;
        BeginScenario();
        state.Powered = true;
        yield return WaitPlaying();
        yield return Measure("powered on mid-track from a saved position");

        // A pause inside the schedule lead cancels the start, and the resume schedules sample 0 again.
        NewSyncPlayback(a, 0);
        yield return null;
        state.Powered = true;
        yield return WaitFor(() => (bool)Private(playback, "running"), 20);
        state.Paused = true;
        yield return new WaitForSecondsRealtime(.3f);
        BeginScenario();
        state.Paused = false;
        yield return WaitPlaying();
        yield return Measure("paused inside the schedule lead then resumed");

        playback.Dispose();
        playback = null;
        driveEndpoints = false;
        SetEndpoints();
        if (timelineOffsets.Count > 0)
        {
            double median = Median(timelineOffsets), worst = 0;
            foreach (double offset in timelineOffsets) worst = Math.Max(worst, Math.Abs(offset - median));
            int bufferLength, bufferCount;
            AudioSettings.GetDSPBufferSize(out bufferLength, out bufferCount);
            Check(worst <= bufferLength, "SYNC the built-in output keeps one content lead over the timeline in every scenario: median=" +
                median.ToString("F1") + " samples worst deviation=" + worst.ToString("F1") + " samples limit=" + bufferLength);
        }
        UnmuteDevice(wasMuted);
        PcmDecoders.Register(".sync", null);
    }

    private RadioContentTap silencer;

    // Content taps need audible gain, because Unity applies listener volume inside each source's chain and
    // FMOD virtualizes inaudible voices. The editor master mute and a listener filter that zeroes the final
    // mix keep that gain away from the device, and the check stops the phase if the mix still reaches it.
    private IEnumerator MuteDevice()
    {
        EditorUtility.audioMasterMute = true;
        var listener = FindObjectOfType<AudioListener>();
        silencer = listener.gameObject.GetComponent<RadioContentTap>();
        if (silencer == null) silencer = listener.gameObject.AddComponent<RadioContentTap>();
        silencer.Silence = true;
        yield return new WaitForSecondsRealtime(.2f);
        if (!Check(silencer.Calls > 0 && EditorUtility.audioMasterMute, "TAP the listener silencer runs and the editor master is muted"))
        {
            silencer = null;
            yield break;
        }
        AudioListener.volume = 1;
    }

    private void UnmuteDevice(bool wasMuted)
    {
        AudioListener.volume = 0;
        EditorUtility.audioMasterMute = wasMuted;
    }

    private void SetEndpoints(params int[] ids) { activeEndpoints.Clear(); activeEndpoints.AddRange(ids); }

    private void NewSyncPlayback(string path, double position)
    {
        if (playback != null) playback.Dispose();
        state = new RadioState { TrackPath = path, Powered = false, Volume = 1, PositionSeconds = position };
        playback = new RadioPlayback(this, state, message => report += "WARNING: " + message + Environment.NewLine) { RepeatTrack = true };
        preloadPath = "";
    }

    private IEnumerator WaitPlaying()
    {
        float deadline = Time.realtimeSinceStartup + 20;
        while (playback.Status != "Playing" && !playback.LoadFailed && Time.realtimeSinceStartup < deadline) yield return null;
    }

    private static IEnumerator WaitFor(Func<bool> condition, float seconds)
    {
        float deadline = Time.realtimeSinceStartup + seconds;
        while (!condition() && Time.realtimeSinceStartup < deadline) yield return null;
    }

    private bool PreloadOpen()
    {
        var stream = Private(playback, "preloadStream") as StreamedAudio;
        return stream != null && stream.IsOpen;
    }

    private RadioSpeakerOutput Endpoint(int id) { return ((Dictionary<int, RadioSpeakerOutput>)Private(playback, "endpoints"))[id]; }

    private static object Private(object target, string name)
    { return target.GetType().GetField(name, BindingFlags.Instance | BindingFlags.NonPublic).GetValue(target); }

    private List<KeyValuePair<int, RadioSpeakerOutput>> Outputs()
    {
        var outputs = new List<KeyValuePair<int, RadioSpeakerOutput>>();
        if (playback == null) return outputs;
        outputs.Add(new KeyValuePair<int, RadioSpeakerOutput>(0, (RadioSpeakerOutput)Private(playback, "builtIn")));
        outputs.AddRange((Dictionary<int, RadioSpeakerOutput>)Private(playback, "endpoints"));
        return outputs;
    }

    private void AttachTaps()
    {
        foreach (var pair in Outputs())
        {
            var output = pair.Value;
            if (output == null || output.Emitter == null) continue;
            var tap = output.Emitter.GetComponent<RadioContentTap>();
            // The filters would reshape the ramp. They add no latency, so bypassing them leaves timing unchanged,
            // and Unity keeps a playing voice's filter order even when its components are reordered.
            foreach (var filter in output.Emitter.GetComponents<Behaviour>())
                if (filter is AudioHighPassFilter || filter is AudioLowPassFilter || filter is RadioBassFilter) filter.enabled = false;
            if (tap == null) tap = output.Emitter.AddComponent<RadioContentTap>();
            tap.Period = syncPeriod;
        }
    }

    private RadioContentTap Tap(RadioSpeakerOutput output)
    { return output == null || output.Emitter == null ? null : output.Emitter.GetComponent<RadioContentTap>(); }

    private void BeginScenario()
    {
        scenarioDsp = AudioSettings.dspTime;
        foreach (var pair in Outputs())
        {
            var tap = Tap(pair.Value);
            if (tap != null) tap.Take();
        }
    }

    private IEnumerator Measure(string scenario, int disturbed = -1)
    {
        yield return new WaitForSecondsRealtime(1f);
        double windowDsp = AudioSettings.dspTime;
        yield return new WaitForSecondsRealtime(1.5f);
        var blocks = new SortedDictionary<int, RadioContentTap.Block[]>();
        foreach (var pair in Outputs())
        {
            var tap = Tap(pair.Value);
            if (tap != null) blocks[pair.Key] = tap.Take();
        }
        Analyze(scenario, blocks, windowDsp, disturbed);
    }

    // Compares content per DSP buffer. Offsets are speaker minus built-in in frames, and timeline is each output's
    // content minus the radio's timeline at the buffer's dspTime, one constant lead when healthy.
    private void Analyze(string scenario, SortedDictionary<int, RadioContentTap.Block[]> blocks, double windowDsp, int disturbed)
    {
        int rate = AudioSettings.outputSampleRate, bufferLength, bufferCount, period = syncPeriod;
        AudioSettings.GetDSPBufferSize(out bufferLength, out bufferCount);
        double blockSeconds = bufferLength / (double)rate;
        double scheduledDsp = (double)Private(playback, "scheduledDsp");
        int scheduledSample = (int)Private(playback, "scheduledSample");
        var clip = (AudioClip)Private(playback, "clip");
        double continuityFrom = Math.Max(scenarioDsp, scheduledDsp - blockSeconds);
        string detail = "SYNC " + scenario + ":";
        bool passed = true;
        RadioContentTap.Block[] reference;
        if (clip == null || !blocks.TryGetValue(0, out reference))
        {
            Check(false, detail + " no clip or no built-in tap");
            return;
        }
        foreach (var pair in blocks)
        {
            int id = pair.Key;
            var offsets = new List<double>();
            var timeline = new List<double>();
            int jumps = 0, gaps = 0, partial = 0;
            bool started = false, chained = false;
            RadioContentTap.Block previous = default(RadioContentTap.Block);
            foreach (var block in pair.Value)
            {
                bool full = block.Valid == block.Frames && block.Outliers == 0;
                if (block.Dsp >= continuityFrom)
                {
                    if (started && block.Valid < block.Frames) gaps++;
                    if (block.Valid > 0 && block.Outliers > 0) jumps++;
                    if (block.Valid > 0 && block.Valid < block.Frames) partial++;
                    if (full && chained && Math.Abs(RadioContentTap.Fold(block.Position - previous.Position - previous.Frames, period)) > 2) jumps++;
                    started |= full;
                    chained = full;
                    previous = block;
                }
                if (!full || block.Dsp < windowDsp) continue;
                long expected = scheduledSample + (long)Math.Round(Math.Max(0, block.Dsp - scheduledDsp) * clip.frequency);
                if (playback.RepeatTrack) expected %= clip.samples;
                timeline.Add(RadioContentTap.Fold(block.Position - expected, period));
                if (id == 0) continue;
                int match = Nearest(reference, block.Dsp);
                if (match < 0 || Math.Abs(reference[match].Dsp - block.Dsp) > blockSeconds / 2) continue;
                var other = reference[match];
                if (other.Valid != other.Frames || other.Outliers != 0) continue;
                offsets.Add(RadioContentTap.Fold(block.Position - other.Position, period));
            }
            string name = id < EndpointNames.Length ? EndpointNames[id] : "speaker " + id;
            detail += " [" + name + " timeline=" + Stats(timeline, rate);
            if (id == 0 && timeline.Count > 0) timelineOffsets.Add(Median(timeline));
            if (id != 0)
            {
                detail += " offset=" + Stats(offsets, rate);
                double worst = 0;
                foreach (double offset in offsets) worst = Math.Max(worst, Math.Abs(offset));
                passed &= offsets.Count >= 10 && worst <= bufferLength;
            }
            else passed &= timeline.Count >= 10;
            detail += " jumps=" + jumps + " gaps=" + gaps + " partial=" + partial + "]";
            if (id != disturbed) passed &= jumps == 0 && gaps == 0;
        }
        foreach (int id in activeEndpoints) if (!blocks.ContainsKey(id)) { passed = false; detail += " [speaker " + id + " missing]"; }
        Check(passed, detail + " limit=" + bufferLength + " samples");
    }

    private static int Nearest(RadioContentTap.Block[] blocks, double dsp)
    {
        int best = -1;
        double distance = Double.MaxValue;
        for (int i = 0; i < blocks.Length; i++)
        {
            double d = Math.Abs(blocks[i].Dsp - dsp);
            if (d < distance) { distance = d; best = i; }
        }
        return best;
    }

    private static double Median(List<double> values)
    {
        var sorted = new List<double>(values);
        sorted.Sort();
        return sorted[sorted.Count / 2];
    }

    private static string Stats(List<double> values, int rate)
    {
        if (values.Count == 0) return "none";
        double min = Double.MaxValue, max = Double.MinValue;
        foreach (double value in values) { min = Math.Min(min, value); max = Math.Max(max, value); }
        double median = Median(values);
        return median.ToString("F1") + " (" + (median * 1000 / rate).ToString("F2") + " ms) range " + min.ToString("F1") + ".." +
            max.ToString("F1") + " n=" + values.Count;
    }

    // Starts each real file as the next track through a fresh open and through an adopted preload. Half the files
    // meet each path on their first touch of the run, when the disk cache is coldest, and every file then meets
    // both paths warm. Counts frames Unity read before their chunk was decoded and digital-silence gaps in each
    // output during the first 2.5 s. Files are only opened for reading.
    private IEnumerator StutterPhase(string root, string folder)
    {
        if (folder != "" && !Check(Directory.Exists(folder), "STUTTER music folder exists")) yield break;
        int rate = AudioSettings.outputSampleRate;
        PcmDecoders.Register(".sync", SyncDecoder.Open);
        string filler = WriteSync(root, "sync-filler.sync", rate, 60L * rate);
        var files = new List<string>();
        foreach (string extension in new[] { ".wav", ".mp3", ".flac", ".ogg", ".m4a" })
        {
            string fixture = Path.Combine(root, "short-tone" + extension);
            if (File.Exists(fixture) && PcmDecoders.Supports(fixture)) files.Add(fixture);
        }
        var real = new List<string>();
        if (folder != "") foreach (string file in Directory.GetFiles(folder)) if (PcmDecoders.Supports(file)) real.Add(file);
        real.Sort(StringComparer.OrdinalIgnoreCase);
        files.AddRange(real);
        if (!Check(files.Count > 0, "STUTTER has tracks to start")) yield break;
        bool wasMuted = EditorUtility.audioMasterMute;
        yield return MuteDevice();
        if (silencer == null) yield break;
        driveEndpoints = true;
        syncPeriod = SyncPeriod(rate, 60L * rate);
        for (int pass = 0; pass < 4; pass++)
            for (int index = 0; index < files.Count; index++)
            {
                string file = files[index];
                bool preload = pass < 2 ? (index + pass) % 2 == 1 : pass == 3;
                string label = "STUTTER " + Path.GetFileName(file) + " " + (preload ? "adopted preload" : "fresh open") + (pass == 0 ? " first touch" : " warm");
                SetEndpoints(1, 2, 3);
                NewSyncPlayback(filler, 0);
                state.Powered = true;
                yield return WaitPlaying();
                if (preload)
                {
                    preloadPath = file;
                    yield return WaitFor(PreloadOpen, 60);
                    yield return new WaitForSecondsRealtime(1f);
                }
                BeginScenario();
                double beganDsp = AudioSettings.dspTime;
                playback.SetTrack(file);
                preloadPath = "";
                yield return WaitFor(() => playback.LoadFailed || (bool)Private(playback, "running"), 60);
                double scheduledDsp = (double)Private(playback, "scheduledDsp"), block = 1024.0 / rate;
                string start = "start after " + ((scheduledDsp - beganDsp) * 1000).ToString("F0") + " ms";
                if (!Check((bool)Private(playback, "running"), label + " starts, status=" + playback.Status + " " + start))
                {
                    playback.Dispose();
                    playback = null;
                    continue;
                }
                object stream = Private(playback, "streamed");
                yield return new WaitForSecondsRealtime(2.7f);
                int missing = MissingFrames(stream);
                var clip = (AudioClip)Private(playback, "clip");
                string gaps = "", late = "";
                int totalGaps = 0;
                double lead = LeadingSilence(file), worstLate = 0;
                foreach (var pair in Outputs())
                {
                    var tap = Tap(pair.Value);
                    if (tap == null) { gaps += " " + pair.Key + "=untapped"; totalGaps++; continue; }
                    var blocks = tap.Take();
                    int gap = RadioContentTap.Gaps(blocks, scheduledDsp - block / 2, scheduledDsp + 2.5);
                    totalGaps += gap;
                    gaps += " " + EndpointNames[pair.Key] + "=" + gap;
                    // The first sound follows the scheduled start by the file's own leading silence.
                    double lateMs = (RadioContentTap.FirstSound(blocks, scheduledDsp - block / 2, rate) - scheduledDsp - lead) * 1000;
                    late += " " + EndpointNames[pair.Key] + "=" + lateMs.ToString("F1");
                    worstLate = Double.IsNaN(lateMs) ? Double.PositiveInfinity : Math.Max(worstLate, Math.Abs(lateMs));
                }
                string counted = missing < 0 ? "unavailable" : missing + " (" + (missing * 1000.0 / clip.frequency).ToString("F1") + " ms over 4 outputs)";
                Check(missing <= 0 && totalGaps == 0 && worstLate <= block * 1000, label + " " + start + " missing-chunk frames=" + counted +
                    " silence gap frames in the first 2.5 s:" + gaps + " first sound late by ms:" + late + " drift repairs=" + playback.DriftRepairs);
                playback.Dispose();
                playback = null;
                yield return null;
            }
        driveEndpoints = false;
        SetEndpoints();
        UnmuteDevice(wasMuted);
        PcmDecoders.Register(".sync", null);
    }

    // Seconds of exact digital silence the file itself opens with, read through the production decoder.
    private static double LeadingSilence(string path)
    {
        using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
        {
            var buffer = new float[4096 * decoder.Channels];
            long frames = 0;
            while (frames < decoder.SampleRate * 10L)
            {
                int got = decoder.Read(buffer, 0, buffer.Length);
                if (got <= 0) break;
                for (int i = 0; i < got; i++)
                    if (buffer[i] != 0f) return (frames + i / decoder.Channels) / (double)decoder.SampleRate;
                frames += got / decoder.Channels;
            }
            return frames / (double)decoder.SampleRate;
        }
    }

    private static int MissingFrames(object stream)
    {
        if (stream == null) return -1;
        var property = stream.GetType().GetProperty("MissingFrames", BindingFlags.Instance | BindingFlags.NonPublic);
        return property == null ? -1 : (int)property.GetValue(stream, null);
    }

    private void Update()
    {
        if (playback == null) return;
        tickWatch.Reset();
        tickWatch.Start();
        if (driveEndpoints)
        {
            // Carried outputs are 2D at full distance gain, so every tap sees the unpanned stereo signal.
            playback.SetAcoustics(Vector3.zero, true, 0);
            playback.SetCarried(true);
            playback.BeginEndpoints();
            if (state.Powered) foreach (int id in activeEndpoints) playback.SetEndpoint(id, Vector3.zero, true, id, 1f, 1f, 0f);
            playback.EndEndpoints();
        }
        playback.Tick(Vector3.zero, suspended);
        if (driveEndpoints) playback.PreloadTrack(state.Powered ? preloadPath : "");
        tickWatch.Stop();
        maxTickMs = Math.Max(maxTickMs, tickWatch.Elapsed.TotalMilliseconds);
        if (driveEndpoints) AttachTaps();
    }
    private void OnDestroy() { if (playback != null) playback.Dispose(); }
    private static string ReadSetting(string path) { return File.Exists(path) ? File.ReadAllText(path).Trim() : ""; }
    private void Info(string detail) { report += "INFO " + detail + Environment.NewLine; }
    private bool Check(bool passed, string detail)
    {
        report += (passed ? "PASS " : "FAIL ") + detail + Environment.NewLine;
        if (passed) checks++;
        else failures++;
        return passed;
    }
    private void Finish()
    {
        Time.timeScale = 1;
        AudioListener.volume = 1;
        SessionState.SetBool("RadioAudioProbe", false);
        File.WriteAllText(output, report + "Checks=" + checks + " Failures=" + failures + Environment.NewLine +
            "Result=" + (failures == 0 ? "PASS" : "FAIL"));
        EditorApplication.Exit(failures == 0 ? 0 : 2);
    }
    private static void WriteStereoTone(string path, int rate, int frames)
    {
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * 4);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)2); writer.Write(rate); writer.Write(rate * 4);
            writer.Write((short)4); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * 4);
            for (int i = 0; i < frames; i++)
            {
                short value = (short)(Math.Sin(i * 330 * Math.PI * 2 / rate) * 1500);
                writer.Write(value); writer.Write(value);
            }
        }
    }
    private static void WriteTone(string path)
    {
        const int rate = 48000, samples = rate * 8;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + samples * 2);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)1); writer.Write(rate); writer.Write(rate * 2);
            writer.Write((short)2); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(samples * 2);
            for (int i=0; i<samples; i++) writer.Write((short)(Math.Sin(i * 220 * Math.PI * 2 / rate) * 1500));
        }
    }
}
#endif
