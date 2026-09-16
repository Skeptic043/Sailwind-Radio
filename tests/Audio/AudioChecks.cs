using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SailwindRadio;
using UnityEngine;
using UnityEngine.Networking;

static class AudioChecks
{
    static int checks;
    static void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
    static UnityWebRequest Request { get { return UnityWebRequest.All[UnityWebRequest.All.Count-1]; } }
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Run()
    {
        string path = Path.Combine(AppContext.BaseDirectory, "fixture # 100%.wav");
        File.WriteAllBytes(path, new byte[] { 0 }); // Existence fixture, deliberately not a decoder test.
        var state = new RadioState { TrackPath = path, PositionSeconds = 2, Powered = true };
        int warnings = 0;
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        var emitter = GameObject.Last;
        Check(emitter.PersistsAcrossScenes, "Emitter persists with plugin host across unrelated additive scene unloads");
        AudioSettings.dspTime = 100;
        player.Tick(new Vector3(1,2,3), false);
        Check(player.Status == "Loading" && source.Schedules == 0, "Async load does not start output");
        Check(!Request.downloadHandler.streamAudio, "Single decoded clip supports exact sample seeking");
        Check(UnityWebRequestMultimedia.LastUri.Contains("%23") && UnityWebRequestMultimedia.LastUri.Contains("%25"), "File URI escapes filename symbols");
        Check(source.pitch == 1 && source.dopplerLevel == 0 && source.spatialBlend == 1 && !source.ignoreListenerPause, "Real time positional source settings");
        Check(emitter.transform.position.x == 1 && emitter.transform.position.z == 3, "Independent emitter follows requested position");
        var firstRequest = Request;
        Request.isDone = true;
        player.Tick(new Vector3(), true);
        var firstClip = source.clip;
        Check(firstRequest.Disposed && source.Schedules == 0 && player.Status == "Suspended", "Load finishing during suspension stays silent and releases request");
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1 && source.timeSamples == 96000 && source.ScheduledAt > AudioSettings.dspTime, "Resume schedules saved sample on DSP clock");
        source.timeSamples = 0;
        player.CapturePosition();
        Check(state.PositionSeconds == 2, "Saving before scheduled start preserves intended sample");
        AudioSettings.dspTime = 101.05;
        source.timeSamples = 144000;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 3, "Position follows shared DSP clock");
        Check(source.volume == 0 && source.Schedules == 1 && player.Status == "Playing" && !state.Paused && state.Powered,
            "Unknown listener mutes output while preserving desired playback and advancing timeline");
        player.SetAcoustics(new Vector3(), true, 0);
        player.Tick(new Vector3(), false);
        Check(source.volume > 0 && source.Schedules == 1 && source.clip == firstClip,
            "Listener recovery restores output without restarting timeline or replacing decoded PCM");
        state.Paused = true;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 3 && player.Status == "Paused", "Manual pause captures shared position while retaining native voice");
        player.Tick(new Vector3(), true);
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1, "Suspend and resume never undo manual pause");
        state.Paused = false;
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 144000 && source.Schedules == 1 && source.UnPauses == 1, "Manual resume restores exact sample");
        AudioSettings.dspTime = 102.05;
        source.timeSamples = 192000;
        state.Powered = false;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 4 && source.clip == firstClip && player.Status == "Off", "Power off preserves cursor and bounded loaded clip");
        state.Powered = true;
        AudioListener.pause = true;
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1, "Global listener pause prevents scheduling");
        AudioListener.pause = false;
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1 && source.UnPauses == 2 && source.timeSamples == 192000, "Power resume restores position");
        AudioSettings.dspTime = 103.05;
        source.timeSamples = 240000;
        player.Tick(new Vector3(), false);
        AudioSettings.Reset(); source.timeSamples = 0;
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 240000 && source.Schedules == 2, "Audio device reset uses saved position rather than reset cursor");
        AudioSettings.dspTime = 0;
        source.timeSamples = 0;
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 240000 && source.Schedules == 3, "DSP clock rollback reschedules from prior position");
        player.SetTrack(path);
        Check(firstClip.Destroyed && source.clip == null, "Track reload releases previous clip");
        player.Tick(new Vector3(), false);
        var pending = Request;
        player.SetTrack("https://example.com/music.mp3");
        Check(pending.Aborted && pending.Disposed, "Replacing pending load aborts and disposes request");
        player.Tick(new Vector3(), false);
        Check(warnings == 1 && player.Status == "Unable to load music", "Remote URI rejected");
        int count = UnityWebRequest.All.Count;
        for (int i=0;i<20;i++) player.Tick(new Vector3(), false);
        Check(warnings == 1 && UnityWebRequest.All.Count == count, "Invalid file is not retried each frame");
        player.SetTrack(path);
        player.Tick(new Vector3(), false);
        Request.isDone = true; Request.isNetworkError = true; Request.error = "decode failed";
        var failed = Request;
        player.Tick(new Vector3(), false);
        Check(failed.Disposed && source.clip == null && warnings == 2, "Request errors release resources without starting playback");
        state.Powered = false; player.Tick(new Vector3(), false);
        state.Powered = true; player.Tick(new Vector3(), false);
        Check(UnityWebRequest.All.Count == count + 2, "Power cycling retries failed file once");
        pending = Request;
        player.Dispose(); player.Dispose();
        Check(pending.Aborted && pending.Disposed && emitter.Destroyed && AudioSettings.Subscribers == 0, "Dispose is idempotent and releases request, emitter and event subscription");
        player.Tick(new Vector3(), false);
        Check(UnityWebRequest.All.Count == count + 2, "Disposed playback cannot restart");
        state = new RadioState { TrackPath = path, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        player.Tick(new Vector3(), false);
        Request.downloadHandler.Content.loadState = AudioDataLoadState.Loading;
        Request.isDone = true;
        player.Tick(new Vector3(), false);
        Check(player.Status == "Loading" && source.Schedules == 0, "Request completion alone does not prove decoded clip readiness");
        Request.downloadHandler.Content.loadState = AudioDataLoadState.Loaded;
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1, "Loaded clip can start after deferred decode completes");
        player.SetTrack(path); player.Tick(new Vector3(), false);
        pending = Request;
        Time.realtimeSinceStartup = 61;
        player.Tick(new Vector3(), true);
        Check(pending.Aborted && pending.Disposed && player.Status == "Unable to load music", "Unscaled timeout cancels stuck request even during suspension");
        player.SetTrack(path); player.Tick(new Vector3(), false);
        Request.downloadHandler.Content.loadState = AudioDataLoadState.Loading;
        Request.isDone = true;
        player.Tick(new Vector3(), false);
        var stuckClip = source.clip;
        Time.realtimeSinceStartup = 122;
        player.Tick(new Vector3(), false);
        Check(stuckClip.Destroyed && player.Status == "Unable to load music", "Stuck decoder releases clip at finite deadline");
        player.Dispose();
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        emitter = GameObject.Last;
        player.Tick(new Vector3(), false);
        Request.isDone = true;
        player.Tick(new Vector3(), false);
        var destroyedSourceClip = source.clip;
        UnityEngine.Object.Destroy(source);
        player.CapturePosition();
        player.Dispose();
        Check(destroyedSourceClip.Destroyed && emitter.Destroyed && AudioSettings.Subscribers == 0, "Source destroyed before lifetime callback cannot interrupt cleanup");
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        player.Tick(new Vector3(), false);
        pending = Request;
        UnityEngine.Object.Destroy(GameObject.Last);
        player.Tick(new Vector3(), false);
        Check(!pending.Aborted && AudioSettings.Subscribers == 1 && player.Status == "Loading", "Missing owned emitter is recreated without abandoning current load");
        player.Dispose();
        CheckMp3(path);
        CheckTabletopVolume();
        CheckEndpointLifecycle(path);
        CheckWeather();
        CheckBassDsp();
        CheckPreload(path);
        CheckPauseTimeline(path);
        File.Delete(path);
        string localMp3 = Environment.GetEnvironmentVariable("SAILWIND_RADIO_MP3_PROBE_FILE");
        if (!String.IsNullOrEmpty(localMp3))
        {
            var result = RadioPlayback.DecodeMp3(localMp3, CancellationToken.None);
            Console.WriteLine("Configured local MP3 decoded: channels=" + result.Channels + ", sampleRate=" + result.SampleRate +
                ", interleavedSamples=" + result.SampleCount + ", nonzeroPCM=" + result.Blocks.Exists(block => Array.Exists(block, value => value != 0)) +
                ", seconds=" + (result.SampleCount / (double)result.Channels / result.SampleRate).ToString("F3"));
        }
        Console.WriteLine("Audio checks passed: " + checks + ". MP3 decoding uses real NLayer. Unity playback uses doubles and does not establish audible behavior.");
    }

    static void CheckTabletopVolume()
    {
        var state = new RadioState();
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
        var source = AudioSource.Last;
        var highPass = AudioHighPassFilter.Last;
        var lowPass = AudioLowPassFilter.Last;
        var curve = source.RolloffCurve;
        Check(source.rolloffMode == AudioRolloffMode.Custom && source.minDistance == 1 && source.maxDistance == 12 &&
            source.RolloffCurveType == AudioSourceCurveType.CustomRolloff && curve.keys[0].value == 1 && curve.keys[1].value == 1 &&
            curve.preWrapMode == WrapMode.ClampForever && curve.postWrapMode == WrapMode.ClampForever,
            "Unity rolloff is flat so explicit listener-distance gain is the only attenuation path");
        state.Volume = 1;
        player.Tick(new Vector3(), false);
        Check(source.volume == 0, "No listener provided defaults to silent output");
        player.SetAcoustics(new Vector3(), true, 0);
        Check(highPass.cutoffFrequency == 650 && highPass.highpassResonanceQ == 1 &&
            lowPass.cutoffFrequency == 8500 && lowPass.lowpassResonanceQ == 1,
            "Built-in speaker uses low-resonance 650 Hz high-pass and 8500 Hz low-pass components");
        float[] settings = { 0f, 0.25f, 0.5f, 0.65f, 1f };
        float[] gains = { 0f, 0.015625f, 0.0625f, 0.105625f, 0.25f };
        bool valid = true;
        for (int i = 0; i < settings.Length; i++)
        {
            state.Volume = settings[i];
            player.Tick(new Vector3(), false);
            valid &= state.Volume == settings[i] && Math.Abs(source.volume - gains[i]) < 0.000001f;
        }
        Check(valid, "Saved knob values remain unchanged while output follows quiet squared gain including 65 percent setting");
        bool monotonic = true;
        float previous = -1;
        for (int i = 0; i <= 100; i++)
        {
            state.Volume = i / 100f;
            player.Tick(new Vector3(), false);
            monotonic &= source.volume >= previous && source.volume >= 0 && source.volume <= 0.25f;
            previous = source.volume;
        }
        Check(monotonic, "Entire knob range is monotonic and bounded by quarter-scale maximum gain");
        float[] distances = { 0, 1, 3.75f, 6.5f, 9.25f, 12, 1000 };
        float[] outputs = { 0.25f, 0.25f, 0.140625f, 0.0625f, 0.015625f, 0, 0 };
        valid = true;
        for (int i = 0; i < distances.Length; i++)
        {
            player.SetAcoustics(new Vector3(distances[i], 0, 0), true, 0);
            player.Tick(new Vector3(), false);
            valid &= Math.Abs(source.volume - outputs[i]) < 0.000001f;
        }
        Check(valid, "Actual production source gain fades throughout 1–12 metres with squared distance law and exact far silence");
        previous = 1;
        monotonic = true;
        for (int i = 0; i <= 100; i++)
        {
            player.SetAcoustics(new Vector3(1 + i * 0.11f, 0, 0), true, 0);
            player.Tick(new Vector3(), false);
            monotonic &= source.volume <= previous && source.volume >= 0;
            previous = source.volume;
        }
        Check(monotonic && source.volume == 0, "Distance gain decreases monotonically across entire fade interval");
        Check(RadioPlayback.DistanceGain(11.99f) < 0.000001f && RadioPlayback.DistanceGain(12) == 0 &&
            RadioPlayback.DistanceGain(Single.NaN) == 0 && RadioPlayback.DistanceGain(Single.PositiveInfinity) == 0,
            "Fade approaches zero gently and rejects invalid distance inputs");
        player.SetAcoustics(new Vector3(3, 4, 0), true, 0);
        player.Tick(new Vector3(3, 3, 0), false);
        Check(source.volume == 0.25f, "Distance uses supplied listener and emitter world positions");
        player.SetAcoustics(new Vector3(), false, 0);
        player.Tick(new Vector3(), false);
        Check(source.volume == 0, "Listener loss immediately mutes flat-rolloff source");
        player.SetAcoustics(new Vector3(Single.NaN, 0, 0), true, 0);
        player.Tick(new Vector3(), false);
        Check(source.volume == 0, "Invalid reported listener position fails closed");
        player.SetAcoustics(new Vector3(), true, 0);
        state.Volume = 0;
        player.Tick(new Vector3(), false);
        Check(source.volume == 0, "Zero knob output remains silent even at emitter position");
        Check(source.pitch == 1 && source.dopplerLevel == 0 && source.spatialBlend == 1,
            "Volume correction retains real-time fully spatial playback settings");
        state.Volume = 1;
        Time.unscaledDeltaTime = 0.25f;
        player.SetAcoustics(new Vector3(), true, 1);
        player.Tick(new Vector3(), false);
        Check(source.volume > 0.0875f && source.volume < 0.25f && lowPass.cutoffFrequency > 1500 && lowPass.cutoffFrequency < 8500,
            "Obstruction gain and tone transition smoothly during one unscaled time constant");
        for (int i = 0; i < 20; i++) player.Tick(new Vector3(), false);
        Check(Math.Abs(source.volume - 0.0875f) < 0.000001f && Math.Abs(lowPass.cutoffFrequency - 1500) < 0.01f,
            "Fully blocked endpoint settles to 0.35 acoustic gain and 1500 Hz low-pass");
        player.SetAcoustics(new Vector3(12, 0, 0), true, 0);
        player.Tick(new Vector3(), false);
        Check(source.volume == 0, "Obstruction smoothing never delays the hard distance cutoff");
        player.SetAcoustics(new Vector3(), true, 0);
        player.Tick(new Vector3(), false);
        Check(source.volume > 0.0875f && source.volume < 0.25f, "Door reopening restores gain gradually");
        for (int i = 0; i < 20; i++) player.Tick(new Vector3(), false);
        Check(Math.Abs(source.volume - 0.25f) < 0.000001f && Math.Abs(lowPass.cutoffFrequency - 8500) < 0.01f,
            "Clear endpoint returns to original speaker profile");
        Time.unscaledDeltaTime = 1f / 60;
        player.Dispose();
        Check(highPass.Destroyed && lowPass.Destroyed, "Emitter destruction also removes its speaker filters");
        int warnings = 0;
        GameObject.RejectFilters = true;
        try
        {
            player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
            source = AudioSource.Last;
            player.SetAcoustics(new Vector3(), true, 0);
            for (int i = 0; i < 20; i++) player.Tick(new Vector3(), false);
            Check(warnings == 2 && source.volume == 0.25f, "Unavailable optional filters warn once each without breaking distance-controlled output");
            player.Dispose();
        }
        finally { GameObject.RejectFilters = false; }
    }

    static void CheckEndpointLifecycle(string path)
    {
        AudioSettings.dspTime = 500;
        var state = new RadioState { TrackPath = path, Powered = true, PositionSeconds = 2, Volume = .5f };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { }) { RepeatTrack = false };
        var radio = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        int requests = UnityWebRequest.All.Count;
        player.BeginEndpoints();
        player.SetEndpoint(1, new Vector3(1, 0, 0), false, 1, .8f, 1, 0);
        var small = AudioSource.Last;
        var smallHigh = AudioHighPassFilter.Last;
        player.SetEndpoint(2, new Vector3(2, 0, 0), false, 2, 1, 1, 0);
        var normal = AudioSource.Last;
        var normalLow = AudioLowPassFilter.Last;
        player.SetEndpoint(3, new Vector3(), false, 3, 1, .7f, 0);
        var woofer = AudioSource.Last;
        var wooferLow = AudioLowPassFilter.Last;
        player.EndEndpoints();
        player.Tick(new Vector3(), false);
        Request.isDone = true;
        player.Tick(new Vector3(), false);
        var shared = radio.clip;
        Check(UnityWebRequest.All.Count == requests + 1 && small.clip == shared && normal.clip == shared && woofer.clip == shared,
            "Three speaker endpoints share one radio clip and one loader");
        Check(small.ScheduledAt == radio.ScheduledAt && normal.ScheduledAt == radio.ScheduledAt && woofer.ScheduledAt == radio.ScheduledAt &&
            small.timeSamples == 96000 && normal.timeSamples == 96000 && woofer.timeSamples == 96000,
            "Initial endpoints schedule identical sample frames on the authoritative DSP start");
        Check(!radio.loop && !small.loop && !normal.loop && !woofer.loop,
            "Playlist non-repeat mode reaches every source");
        Check(smallHigh.cutoffFrequency == 500 && small.maxDistance == 15 && normalLow.cutoffFrequency == 22000 && normal.maxDistance == 20 &&
            wooferLow.cutoffFrequency == 140, "Small, normal and woofer outputs receive distinct native tone and range profiles");
        Check(Math.Abs(woofer.volume - .25f * (float)Math.Sqrt(.7f)) < .000001f,
            "Woofer bass strength scales only its local bass output");

        AudioSettings.dspTime = 501.05;
        radio.timeSamples = 144000;
        player.SetCarried(true);
        player.SetAcoustics(new Vector3(), true, 1);
        player.Tick(new Vector3(1000, 0, 0), false);
        Check(radio.spatialBlend == 0 && radio.volume == .0625f && radio.Schedules == 1 && state.PositionSeconds == 3,
            "Carried radio bypasses moving spatial geometry and obstruction without rescheduling playback");
        player.SetCarried(false);
        player.Tick(new Vector3(1000, 0, 0), false);
        Check(radio.spatialBlend == 1 && radio.volume == 0 && radio.Schedules == 1 && state.PositionSeconds == 3,
            "Dropping radio restores positional output without restarting its cursor");

        player.BeginEndpoints();
        player.SetEndpoint(2, new Vector3(), true, 2, .5f, 1, 1);
        player.EndEndpoints();
        Check(small.Destroyed && woofer.Destroyed && !normal.Destroyed && !shared.Destroyed,
            "Lost endpoints release their sources without destroying the shared radio clip");
        player.Tick(new Vector3(), false);
        Check(normal.spatialBlend == 0 && Math.Abs(normal.volume - .65f * .25f * .25f) < .000001f && normal.Schedules == 1,
            "Carried external speaker also bypasses obstruction and keeps continuous playback");
        player.BeginEndpoints();
        player.SetEndpoint(2, new Vector3(), true, 2, .5f, 1, 0);
        player.SetEndpoint(4, new Vector3(), false, 1, 1, 1, 0);
        var joined = AudioSource.Last;
        player.EndEndpoints();
        player.Tick(new Vector3(), false);
        int expectedJoin = 96000 + (int)Math.Round((joined.ScheduledAt - radio.ScheduledAt) * shared.frequency);
        int quantizedSourceJoin = 1 + radio.timeSamples + (int)Math.Round((joined.ScheduledAt - AudioSettings.dspTime) * shared.frequency);
        Check(joined.clip == shared && joined.ScheduledAt > AudioSettings.dspTime && joined.timeSamples == expectedJoin &&
            joined.timeSamples != quantizedSourceJoin && UnityWebRequest.All.Count == requests + 1,
            "Late endpoint follows shared DSP anchor rather than a deliberately mismatched native cursor, with no extra decoder");

        state.Paused = true;
        AudioSettings.dspTime = 501.3;
        radio.timeSamples = 156000;
        player.Tick(new Vector3(), false);
        Check(!radio.isPlaying && !normal.isPlaying && !joined.isPlaying && state.PositionSeconds == 3.25,
            "Manual pause captures shared position and pauses all retained endpoints");
        state.Paused = false;
        player.Tick(new Vector3(), false);
        Check(radio.timeSamples == 156000 && normal.timeSamples == 156000 && joined.timeSamples == 156000 &&
            radio.UnPauses == 1 && normal.UnPauses == 1 && joined.UnPauses == 1,
            "Resume schedules every remaining endpoint from the same retained cursor");
        AudioSettings.dspTime = 502.05;
        radio.timeSamples = 192000;
        player.Tick(new Vector3(), false);
        AudioSettings.Reset(); radio.timeSamples = 0;
        player.Tick(new Vector3(), false);
        Check(radio.timeSamples == 192000 && normal.timeSamples == 192000 && joined.timeSamples == 192000 &&
            radio.ScheduledAt == joined.ScheduledAt,
            "Device reset recovers radio and endpoints from one pre-reset position");
        player.SetInterference(0, 900, .5f);
        AudioSettings.dspTime += .1;
        radio.timeSamples = 196800;
        int schedules = radio.Schedules;
        player.Tick(new Vector3(), false);
        Check(radio.volume == 0 && normal.volume == 0 && joined.volume == 0 && state.PositionSeconds == 4.05 && radio.Schedules == schedules,
            "Shared interference can silence output without consuming a pause or changing the authoritative timeline");
        player.SetInterference(1, 22000, 0);

        AudioSettings.dspTime = radio.ScheduledAt + (shared.samples - 192000) / (double)shared.frequency + .01;
        player.Tick(new Vector3(), false);
        Check(player.TrackEnded && state.PositionSeconds == 10 && radio.timeSamples == 0 && normal.timeSamples == 0 && joined.timeSamples == 0,
            "Non-looping completion preserves exact duration and stops all endpoints");
        player.Tick(new Vector3(), false);
        Check(radio.Schedules == schedules && player.Status == "Ended",
            "Completed non-looping track does not silently restart while queue observes completion");
        state.PositionSeconds = 0;
        player.SetTrack(path);
        Check(!player.TrackEnded && !player.LoadFailed && shared.Destroyed && normal.clip == null && joined.clip == null && state.PositionSeconds == 0,
            "Queue restart resets completion without overwriting newly selected position with old clip cursor");
        player.Tick(new Vector3(), false); Request.isDone = true; player.Tick(new Vector3(), false);
        Check(radio.timeSamples == 0 && normal.clip == radio.clip && normal.ScheduledAt == radio.ScheduledAt,
            "Same-path queue restart loads once and resynchronizes endpoints");
        player.SetTrack("https://example.com/not-local.mp3"); player.Tick(new Vector3(), false);
        Check(player.LoadFailed && player.FailedPath == "https://example.com/not-local.mp3" && !player.TrackEnded,
            "Failed track identity is exposed for bounded playlist skipping");
        player.Dispose();
        Check(normal.Destroyed && joined.Destroyed && AudioSettings.Subscribers == 0,
            "Radio teardown owns and disposes all external emitters and subscriptions");
    }

    static void CheckWeather()
    {
        var clear = WeatherInterference.Sample(10, 8.08, false, 1);
        Check(clear.Gain == 1 && clear.Cutoff == 22000 && clear.Crackle == 0,
            "Disabled weather processing is neutral even in heavy rain");
        clear = WeatherInterference.Sample(0, 8.08, true, 1);
        Check(clear.Gain == 1 && clear.Cutoff == 22000 && clear.Crackle == 0,
            "Clear weather does not alter source output");
        int activeFrames = 0;
        bool bounded = true;
        for (int i = 0; i < 1100; i++)
        {
            var value = WeatherInterference.Sample(10, i / 100d, true, 1);
            if (value.Crackle > 0) activeFrames++;
            bounded &= value.Gain >= .0499f && value.Gain <= 1 && value.Crackle >= 0 && value.Crackle <= 1 && value.Cutoff >= 20;
        }
        Check(bounded && activeFrames > 0 && activeFrames <= 54,
            "Heavy-rain interference has bounded output and no event longer than 0.18 seconds per five-second window");
        var mild = WeatherInterference.Sample(10, 3.09, true, .35f);
        var strong = WeatherInterference.Sample(10, 3.09, true, 1);
        Check(mild.Gain > strong.Gain && mild.Crackle < strong.Crackle,
            "Interference strength scales the same deterministic weather event");
        var filter = new RadioStaticFilter();
        var samples = new float[128];
        filter.SetLevel(.5f, 0); filter.Process(samples, 2);
        Check(Array.TrueForAll(samples, sample => sample == 0), "Static cannot leak from an inaudible source");
        filter.SetLevel(.5f, .25f); filter.Process(samples, 2);
        Check(Array.Exists(samples, sample => sample != 0) && Array.TrueForAll(samples, sample => Math.Abs(sample) <= .09001f),
            "Static is low-amplitude output-buffer noise with no clip mutation or global random use");
    }

    static void CheckPauseTimeline(string path)
    {
        AudioSettings.dspTime = 1000;
        var state = new RadioState { TrackPath = path, Powered = true, PositionSeconds = 3.25 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { }) { RepeatTrack = false };
        var radio = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        player.BeginEndpoints();
        player.SetEndpoint(1, new Vector3(), false, 1, 1, 1, 0); var first = AudioSource.Last;
        player.SetEndpoint(2, new Vector3(), false, 2, 1, 1, 0); var second = AudioSource.Last;
        player.EndEndpoints();
        player.Tick(new Vector3(), false); Request.downloadHandler.Content.samples = 48000 * 600;
        Request.isDone = true; player.Tick(new Vector3(), false);
        double start = radio.ScheduledAt;
        AudioSettings.dspTime = start + 4;
        radio.isVirtual = true; radio.timeSamples = 0;
        first.timeSamples = 48000; second.timeSamples = 400000;
        player.Tick(new Vector3(), true);
        Check(state.PositionSeconds == 7.25 && !radio.isPlaying && !first.isPlaying && !second.isPlaying,
            "Pause snapshots shared DSP time despite virtual primary and conflicting native speaker cursors");
        AudioSettings.dspTime += 3600; // TimeScale-zero menu does not freeze Unity DSP.
        for (int i = 0; i < 3; i++) player.Tick(new Vector3(), true);
        Check(state.PositionSeconds == 7.25 && !player.TrackEnded && radio.Pauses == 1 && first.Pauses == 1,
            "An hour of suspended DSP advance cannot consume the song or repeatedly pause its retained voices");
        player.Tick(new Vector3(), false);
        Check(radio.timeSamples == 348000 && first.timeSamples == 348000 && second.timeSamples == 348000 &&
            radio.Schedules == 1 && first.Schedules == 1 && radio.UnPauses == 1 && first.UnPauses == 1,
            "Long-pause resume restores one common sample without new filtered PlayScheduled starts");
        radio.isVirtual = false;
        AudioSettings.dspTime += 2;
        radio.timeSamples = first.timeSamples = second.timeSamples = 444000;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 9.25 && radio.UnPauses == 1 && first.UnPauses == 1,
            "Resumed DSP anchor excludes the paused hour and healthy voices are untouched");
        player.Tick(new Vector3(), true); AudioSettings.dspTime += .01; player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 9.25 && radio.UnPauses == 2 && first.UnPauses == 2 && radio.Schedules == 1,
            "Short pause also retains native voices and cannot introduce extra starts");
        // Continue-through-menu policy: caller leaves suspended false, so time advances.
        AudioSettings.dspTime += 1;
        radio.timeSamples = second.timeSamples = 492000; first.timeSamples = 490100;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 10.25, "Unsuspended music advances on DSP independently of game time scale");
        Check(first.UnPauses == 2, "Normal buffer-sized native cursor lag does not cause corrective seeks");
        // Deliberate 100ms echo: require two observations outside buffer tolerance.
        first.timeSamples -= 4800;
        AudioSettings.dspTime += .25;
        radio.timeSamples = second.timeSamples = 504000; first.timeSamples = 499200;
        player.Tick(new Vector3(), false);
        Check(first.UnPauses == 2, "One suspicious cursor observation does not seek a playing source");
        AudioSettings.dspTime += .25;
        radio.timeSamples = second.timeSamples = 516000; first.timeSamples = 511200;
        player.Tick(new Vector3(), false);
        Check(first.timeSamples == 516000 && first.UnPauses == 3 && radio.UnPauses == 2 && second.UnPauses == 2,
            "Persistent audible drift repairs only the lagging voice against the shared clock");
        second.isVirtual = true; second.timeSamples = 0;
        AudioSettings.dspTime += 1; radio.timeSamples = first.timeSamples = 564000;
        player.Tick(new Vector3(), false);
        Check(second.UnPauses == 2 && state.PositionSeconds == 11.75, "Virtualized source cannot reset clock or trigger repair churn");
        second.isVirtual = false; second.isPlaying = false;
        AudioSettings.dspTime += .25; radio.timeSamples = first.timeSamples = 576000;
        player.Tick(new Vector3(), false);
        Check(second.Schedules == 2 && second.timeSamples == 578400 && radio.Schedules == 1,
            "Unexpected stopped output rejoins future shared sample without restarting neighbors");
        // Pending late joins must be cancelled on pause, not resumed with stale delays.
        player.Tick(new Vector3(), true);
        AudioSettings.dspTime += 100; player.Tick(new Vector3(), false);
        Check(second.Schedules == 3 && second.timeSamples == 578400 && radio.UnPauses == 3,
            "Pause before a late scheduled start cancels it and creates exactly one fresh aligned join");
        UnityEngine.Object.Destroy(radio);
        player.Tick(new Vector3(), false);
        var replacement = AudioSource.Last;
        Check(replacement != radio && replacement.clip == first.clip && replacement.Schedules == 1 && !player.TrackEnded,
            "Missing owned primary source is recreated while surviving speakers retain their timeline");
        replacement.enabled = false;
        player.Tick(new Vector3(), false);
        Check(replacement.enabled, "Only owned disabled audio source is re-enabled");
        player.Dispose();
        Check(replacement.Destroyed && first.Destroyed && second.Destroyed && AudioSettings.Subscribers == 0,
            "Recovery does not leave orphan emitters or listener subscriptions");
        foreach (bool resume in new[] { false, true })
        {
            AudioSettings.dspTime = 10000;
            state = new RadioState { TrackPath = path, Powered = true };
            player = new RadioPlayback(new MonoBehaviour(), state, _ => { }) { RepeatTrack = false };
            radio = AudioSource.Last;
            player.SetAcoustics(new Vector3(), true, 0);
            player.Tick(new Vector3(), false); Request.downloadHandler.Content.samples = 48000;
            Request.isDone = true; player.Tick(new Vector3(), false);
            AudioSettings.dspTime = radio.ScheduledAt + .97;
            if (resume)
            {
                player.Tick(new Vector3(), true); AudioSettings.dspTime += 100;
                player.BeginEndpoints(); player.SetEndpoint(1, new Vector3(), false, 2, 1, 1, 0); player.EndEndpoints();
                first = AudioSource.Last;
                player.Tick(new Vector3(), false);
                Check(first.Schedules == 0 && radio.timeSamples == 46560, "A new receiver cannot schedule beyond the short remaining tail on resume");
            }
            else
            {
                radio.isPlaying = false; player.Tick(new Vector3(), false);
                Check(radio.Schedules == 1 && !player.TrackEnded, "Stopped-voice repair near the end cannot write sample equal to clip length");
            }
            AudioSettings.dspTime += .1; player.Tick(new Vector3(), false);
            Check(player.TrackEnded && state.PositionSeconds == 1, "Short-tail guard still reaches ordinary shared completion");
            player.Dispose();
        }
    }

    static void CheckBassDsp()
    {
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            var filter = new RadioStaticFilter();
            filter.SetProfile(rate, true);
            var signal = new float[rate * 2];
            for (int i = 0; i < rate; i++)
            {
                signal[i * 2] = (float)(.1 * Math.Sin(2 * Math.PI * 70 * i / rate));
                signal[i * 2 + 1] = (float)(.1 * Math.Sin(2 * Math.PI * 1000 * i / rate));
            }
            filter.Process(signal, 2);
            double low = 0, high = 0;
            for (int i = rate / 2; i < rate; i++) { low += signal[i * 2] * signal[i * 2]; high += signal[i * 2 + 1] * signal[i * 2 + 1]; }
            Check(low > 10000 * high && low > rate * .008,
                "Fourth-order woofer filter preserves 70 Hz and rejects 1 kHz at sample rate " + rate);
            filter.SetProfile(rate == 48000 ? 44100 : 48000, true);
            Array.Clear(signal, 0, signal.Length); filter.Process(signal, 2);
            Check(Array.TrueForAll(signal, value => value == 0), "Sample-rate change replaces coefficient bank and clears old delay state");
            for (int i = 0; i < signal.Length; i++) signal[i] = i % 10 == 0 ? Single.NaN : 1000;
            filter.SetLevel(1, 1); filter.Process(signal, 2);
            Check(Array.TrueForAll(signal, value => !Single.IsNaN(value) && !Single.IsInfinity(value) && Math.Abs(value) <= 1),
                "Woofer output limiter remains finite and bounded for over-range PCM and full static");
        }
        var mono = new RadioStaticFilter();
        mono.SetProfile(48000, true);
        var block = new float[2048];
        mono.Process(block, 2); // JIT/warmup outside the allocation observation.
#if NET10_0_OR_GREATER
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) mono.Process(block, 2);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Steady audio callback performs no managed allocations");
#endif
    }

    static void CheckPreload(string wavPath)
    {
        string next = Path.Combine(AppContext.BaseDirectory, "next.ogg");
        string mp3 = Path.Combine(AppContext.BaseDirectory, "next.mp3");
        File.WriteAllBytes(next, new byte[] { 0 });
        WriteSilentMp3(mp3, false, false);
        var state = new RadioState { TrackPath = wavPath, Powered = true, Volume = .8f };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        player.Tick(new Vector3(), false); Request.isDone = true; player.Tick(new Vector3(), false);
        var original = source.clip;
        int requests = UnityWebRequest.All.Count;
        player.PreloadTrack(next); player.Tick(new Vector3(), false);
        var preparing = Request;
        Check(UnityWebRequest.All.Count == requests + 1 && source.clip == original && !preparing.isDone,
            "Upcoming native load runs beside the unchanged current clip");
        preparing.isDone = true; player.Tick(new Vector3(), false);
        var prepared = preparing.downloadHandler.Content;
        Check(preparing.Disposed && !prepared.Destroyed && source.clip == original, "Prepared clip is owned independently of current output");
        state.TrackPath = next; state.PositionSeconds = 0;
        player.SetTrack(next); player.Tick(new Vector3(), false);
        Check(source.clip == prepared && original.Destroyed && UnityWebRequest.All.Count == requests + 1 && source.timeSamples == 0,
            "Queue selection consumes prepared clip without a second request or old-position overwrite");
        player.BeginEndpoints(); player.SetEndpoint(1, new Vector3(), false, 2, 1, 1, 0); player.EndEndpoints();
        var speaker = AudioSource.Last;
        state.LocalVolume = 0; player.Tick(new Vector3(), false);
        Check(source.volume == 0 && speaker.volume > 0 && speaker.clip == prepared, "Radio local volume does not mute its external speakers");
        state.LocalVolume = 1;
        player.PreloadTrack(wavPath); player.Tick(new Vector3(), false);
        var pending = Request;
        state.TrackPath = wavPath; state.PositionSeconds = 1;
        player.SetTrack(wavPath); player.Tick(new Vector3(), true);
        Check(!pending.Aborted && source.clip == null && player.Status == "Loading", "Selecting pending preload transfers request ownership without restart");
        pending.isDone = true; player.Tick(new Vector3(), true);
        Check(source.clip != null && source.timeSamples == 0 && player.Status == "Suspended", "Adopted load completing during suspension cannot play");
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 48000 && speaker.clip == source.clip, "Adopted load resumes every endpoint at requested saved cursor");
        player.PreloadTrack(next); player.Tick(new Vector3(), false);
        pending = Request;
        pending.isDone = true;
        var abandonedClip = pending.downloadHandler.Content;
        player.PreloadTrack("");
        Check(pending.Disposed && abandonedClip.Destroyed, "Cancel observes and releases completed native content before next Tick");
        player.PreloadTrack(next); player.Tick(new Vector3(), false); pending = Request;
        player.PreloadTrack(mp3);
        Check(pending.Aborted && pending.Disposed, "Replacing upcoming track aborts its obsolete native request");
        player.Tick(new Vector3(), false);
        int beforeMp3 = UnityWebRequest.All.Count;
        var current = source.clip;
        // Let the real NLayer worker prepare and Tick upload, then transfer regardless
        // of completion timing. No fake MP3 decode or extra native request is involved.
        for (int i = 0; i < 100; i++) { player.Tick(new Vector3(), false); Thread.Sleep(2); }
        player.SetTrack(mp3);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && source.clip != current && current.Destroyed && source.clip.UploadedSamples > 0 && UnityWebRequest.All.Count == beforeMp3,
            "Managed MP3 preload is reusable without native MPEG requests");
        player.PreloadTrack(next); player.Tick(new Vector3(), false); pending = Request;
        pending.isHttpError = true; pending.isDone = true;
        player.Tick(new Vector3(), false);
        Check(!player.LoadFailed && player.Status == "Playing" && pending.Disposed, "Speculative decode failure cannot interrupt current track");
        player.PreloadTrack(wavPath); player.Tick(new Vector3(), false); pending = Request;
        float previousTime = Time.realtimeSinceStartup;
        Time.realtimeSinceStartup += 61;
        player.Tick(new Vector3(), false);
        Check(pending.Aborted && pending.Disposed && !player.LoadFailed && player.Status == "Playing",
            "Upcoming load timeout releases its request without failing the current track");
        Time.realtimeSinceStartup = previousTime;
        player.PreloadTrack(next); player.Tick(new Vector3(), false); pending = Request;
        pending.downloadHandler.Content.samples = RadioPlayback.MaximumMp3Samples + 1;
        pending.isDone = true;
        player.Tick(new Vector3(), false);
        Check(pending.Disposed && pending.downloadHandler.Content.Destroyed && !player.LoadFailed,
            "Oversized native predecode is discarded without replacing the current clip");
        player.PreloadTrack(wavPath); player.Tick(new Vector3(), false); pending = Request;
        player.Dispose();
        Check(pending.Aborted && pending.Disposed && AudioSettings.Subscribers == 0, "Shutdown cancels upcoming load and releases all ownership");
        foreach (bool ready in new[] { false, true })
        {
            state.TrackPath = wavPath; state.PositionSeconds = 0;
            player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
            source = AudioSource.Last;
            var emitter = GameObject.Last;
            player.Tick(new Vector3(), false); Request.isDone = true; player.Tick(new Vector3(), false);
            current = source.clip;
            player.PreloadTrack(next); player.Tick(new Vector3(), false); pending = Request;
            var nextClip = pending.downloadHandler.Content;
            if (ready) { pending.isDone = true; player.Tick(new Vector3(), false); }
            source.ThrowOnNextStop = true;
            bool stopFailed = false;
            try { player.Dispose(); }
            catch (InvalidOperationException) { stopFailed = true; }
            Check(stopFailed && current.Destroyed && pending.Disposed && (ready ? nextClip.Destroyed : pending.Aborted) &&
                source.Destroyed && emitter.Destroyed && AudioSettings.Subscribers == 0,
                "Native current-source Stop failure still releases current clip, " + (ready ? "prepared clip" : "pending preload") + " and emitter ownership");
            player.Dispose(); // Idempotent even after a teardown exception.
        }
        File.Delete(next); File.Delete(mp3);
    }

    static void CheckMp3(string wavPath)
    {
        string mp3 = Path.Combine(AppContext.BaseDirectory, "original-silent-stereo.mp3");
        WriteSilentMp3(mp3, false, false);
        var decoded = RadioPlayback.DecodeMp3(mp3, CancellationToken.None);
        Check(decoded.Channels == 2 && decoded.SampleRate == 44100 && decoded.SampleCount == 20 * 1152 * 2,
            "Real pinned NLayer decodes generated stereo MPEG frames with interleaved sample dimensions");
        Check(decoded.Blocks.Count > 1 && decoded.Blocks.TrueForAll(block => Array.TrueForAll(block, value => value == 0)),
            "Original silent MP3 decodes to bounded PCM blocks of silence");
        string mono = Path.Combine(AppContext.BaseDirectory, "original-silent-mono.mp3");
        WriteSilentMp3(mono, true, false);
        decoded = RadioPlayback.DecodeMp3(mono, CancellationToken.None);
        Check(decoded.Channels == 1 && decoded.SampleCount == 20 * 1152, "Real decoder preserves mono sample dimensions");
        string vbr = Path.Combine(AppContext.BaseDirectory, "original-silent-vbr.mp3");
        WriteSilentMp3(vbr, false, true);
        decoded = RadioPlayback.DecodeMp3(vbr, CancellationToken.None);
        Check(decoded.SampleCount == 20 * 1152 * 2, "Mixed-bitrate MPEG frames decode to complete PCM without estimated-length allocation");
        bool rejected = false;
        try { RadioPlayback.DecodeMp3(mp3, CancellationToken.None, 16384); } catch (IOException) { rejected = true; }
        Check(rejected, "Decoded PCM sample budget rejects excessive allocation");
        rejected = false;
        try { RadioPlayback.DecodeMp3(mp3, CancellationToken.None, RadioPlayback.MaximumMp3Samples, 100); }
        catch (IOException) { rejected = true; }
        using (File.Open(mp3, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Check(rejected, "Compressed input cap rejects oversized files before decoder construction and releases file handle");
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); rejected = false;
            try { RadioPlayback.DecodeMp3(mp3, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Cancelled managed decode stops before file access");
        }
        // Hold the production worker gate to force the normally brief queueing case.
        var gate = (SemaphoreSlim)typeof(RadioPlayback).GetField("Mp3DecodeSlot",
            System.Reflection.BindingFlags.Static | System.Reflection.BindingFlags.NonPublic).GetValue(null);
        gate.Wait();
        try
        {
            using (var cancelled = new CancellationTokenSource())
            {
                var waiting = Task.Run(() => RadioPlayback.DecodeMp3(mp3, cancelled.Token));
                cancelled.Cancel();
                rejected = false;
                try { waiting.GetAwaiter().GetResult(); } catch (OperationCanceledException) { rejected = true; }
                Check(rejected && gate.CurrentCount == 0, "Queued worker cancellation does not release another worker's slot");
            }
        }
        finally { gate.Release(); }
        Check(RadioPlayback.DecodeMp3(mp3, CancellationToken.None).SampleCount > 0,
            "Decoder slot remains usable after queued cancellation");
        string corrupt = Path.Combine(AppContext.BaseDirectory, "corrupt.mp3");
        File.WriteAllText(corrupt, "This is not an MPEG stream");
        rejected = false;
        try { RadioPlayback.DecodeMp3(corrupt, CancellationToken.None); } catch (Exception) { rejected = true; }
        using (File.Open(corrupt, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Check(rejected, "Malformed MP3 is rejected and constructor failure releases file handle");

        int warnings = 0, requests = UnityWebRequest.All.Count;
        var state = new RadioState { TrackPath = mp3, Powered = true, PositionSeconds = 0.1 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        player.Tick(new Vector3(), true);
        PumpUntil(player, true, () => source.clip != null);
        Check(UnityWebRequest.All.Count == requests, "MP3 never invokes unsupported Unity MPEG web loader");
        Check(source.clip.channels == 2 && source.clip.samples == 20 * 1152 && source.clip.UploadedSamples == 20 * 1152 * 2,
            "Unity PCM clip receives correct stereo frame count and chunk offsets");
        Check(AudioClip.CreateThread == Environment.CurrentManagedThreadId && source.Schedules == 0,
            "Managed decode completion creates clip on main thread and respects suspension");
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1 && source.timeSamples == 4410, "Managed MP3 starts at retained sample position");
        var clip = source.clip;
        player.Dispose();
        Check(clip.Destroyed, "Managed MP3 clip released on disposal");

        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        player.Tick(new Vector3(), false);
        player.SetTrack(wavPath);
        player.Tick(new Vector3(), false);
        for (int i=0;i<20;i++) { Thread.Sleep(1); player.Tick(new Vector3(), false); }
        Check(source.clip == null && source.Schedules == 0 && player.Status == "Loading",
            "Replacing MP3 with pending WAV cannot attach a stale worker result");
        player.Dispose();

        state = new RadioState { TrackPath = mp3, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        AudioClip.RejectUpload = true;
        PumpUntil(player, false, () => player.Status == "Unable to decode music");
        Check(AudioClip.LastCreated.Destroyed && source.clip == null && warnings == 1,
            "PCM upload failure destroys partial clip and reports one warning");
        AudioClip.RejectUpload = false;
        player.Dispose();

        state = new RadioState { TrackPath = corrupt, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        PumpUntil(player, false, () => player.Status == "Unable to decode music");
        for (int i=0;i<20;i++) player.Tick(new Vector3(), false);
        Check(warnings == 2, "Corrupt managed decode reports once without per-frame retries");
        player.Dispose();
        File.Delete(mp3); File.Delete(mono); File.Delete(vbr); File.Delete(corrupt);
    }

    static void PumpUntil(RadioPlayback player, bool suspended, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) { player.Tick(new Vector3(), suspended); Thread.Sleep(1); }
        if (!done()) throw new Exception("Managed MP3 fixture did not complete within 5 seconds");
    }

    // Original synthesized MPEG-1 Layer III frames. Zero side information means zero
    // spectral data, giving valid silent audio without copyrighted recordings or encoders.
    static void WriteSilentMp3(string path, bool mono, bool variableBitrate)
    {
        using (var output = File.Create(path))
            for (int frame=0; frame<20; frame++)
            {
                bool high = variableBitrate && frame % 2 != 0;
                int bitrate = high ? 160000 : 128000;
                var bytes = new byte[144 * bitrate / 44100];
                bytes[0] = 0xff; bytes[1] = 0xfb;
                bytes[2] = (byte)(high ? 0xa0 : 0x90);
                bytes[3] = (byte)(mono ? 0xc0 : 0);
                output.Write(bytes, 0, bytes.Length);
            }
    }
}
