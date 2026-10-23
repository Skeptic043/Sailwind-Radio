using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SailwindRadio;
using SailwindRadio.Audio;
using UnityEngine;

static partial class AudioChecks
{
    static int checks;
    static void Check(bool result, string message) { if (!result) throw new Exception(message); checks++; }
    static int Main()
    {
        try { Run(); return 0; }
        catch (Exception ex) { Console.Error.WriteLine(ex); return 1; }
    }
    static void Run()
    {
        // Lifecycle fixture: a scripted decoder held by the Hold gate, so each check decides when its
        // load completes. Its ten seconds of 48 kHz mono give the sample arithmetic below.
        string path = WriteHeldTrack("fixture # 100%.fake");
        FakeDecoder.Hold.Reset();
        var state = new RadioState { TrackPath = path, PositionSeconds = 2, Powered = true };
        int warnings = 0;
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        var emitter = GameObject.Last;
        Check(emitter.PersistsAcrossScenes, "Emitter persists with plugin host across unrelated additive scene unloads");
        AudioSettings.dspTime = 100;
        player.Tick(new Vector3(1,2,3), false);
        Check(player.Status == "Loading" && source.Schedules == 0, "Async load does not start output");
        Check(player.NamesTrack, "Hover names a song while it opens");
        Check(source.pitch == 1 && source.dopplerLevel == 0 && source.spatialBlend == 1 && !source.ignoreListenerPause && source.bypassReverbZones,
            "Real time positional source keeps listener pause and bypasses external reverb zones");
        Check(emitter.transform.position.x == 1 && emitter.transform.position.z == 3, "Independent emitter follows requested position");
        var firstStream = (StreamedAudio)Field(player, "streamed");
        Settle(player);
        MakeResident(player);
        var firstClip = source.clip;
        Check(firstClip != null && firstClip.Streaming && Field(player, "streamed") == firstStream && source.Schedules == 0 && player.Status == "Suspended",
            "Load finishing during suspension stays silent, and a file name with # and % opens as a local path: status=" + player.Status + " schedules=" + source.Schedules + " same=" + (Field(player, "streamed") == firstStream));
        WaitStream(player);
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
            "Listener recovery restores output without restarting timeline or replacing the streamed clip");
        state.Paused = true;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 3 && player.Status == "Paused", "Manual pause captures shared position while retaining native voice");
        Check(player.NamesTrack, "Hover keeps naming a paused song");
        player.Tick(new Vector3(), true);
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1, "Suspend and resume never undo manual pause");
        state.Paused = false;
        WaitStream(player);
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 144000 && source.Schedules == 1 && source.UnPauses == 1, "Manual resume restores exact sample");
        AudioSettings.dspTime = 102.05;
        source.timeSamples = 192000;
        state.Powered = false;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 4 && source.clip == firstClip && player.Status == "Off", "Power off preserves cursor and the open stream");
        Check(!player.NamesTrack, "Hover names nothing while the radio is off");
        state.Powered = true;
        AudioListener.pause = true;
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1, "Global listener pause prevents scheduling");
        AudioListener.pause = false;
        WaitStream(player);
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
        Check(firstClip.Destroyed && source.clip == null && WaitWorker(firstStream), "Track reload releases previous clip and stream");
        FakeDecoder.Hold.Reset();
        player.Tick(new Vector3(), false);
        var pending = (StreamedAudio)Field(player, "streamed");
        player.SetTrack("https://example.com/music.mp3");
        Check(pending != null && !pending.IsOpen && Field(player, "streamed") == null && WaitWorker(pending),
            "Replacing a pending load cancels and releases its stream");
        player.Tick(new Vector3(), false);
        Check(warnings == 1 && player.Status == "Unable to load music", "Remote URI rejected");
        Check(!player.NamesTrack, "Hover names nothing after a failed load");
        int opens = FakeDecoder.Opens;
        for (int i=0;i<20;i++) player.Tick(new Vector3(), false);
        Check(warnings == 1 && Field(player, "streamed") == null && FakeDecoder.Opens == opens, "Invalid file is not retried each frame");
        string corrupt = Path.Combine(AppContext.BaseDirectory, "not-vorbis.ogg");
        File.WriteAllText(corrupt, "This is not an Ogg stream");
        state.PositionSeconds = 0;
        player.SetTrack(corrupt);
        PumpUntil(player, false, () => player.LoadFailed);
        Check(Field(player, "streamed") == null && source.clip == null && source.Schedules == 3 && warnings == 2 &&
            player.Status == "Unable to decode music", "Decode errors release resources without starting playback");
        state.Powered = false; player.Tick(new Vector3(), false);
        state.Powered = true; player.Tick(new Vector3(), false);
        var retry = (StreamedAudio)Field(player, "streamed");
        PumpUntil(player, false, () => player.LoadFailed);
        for (int i=0;i<20;i++) player.Tick(new Vector3(), false);
        Check(retry != null && warnings == 3 && Field(player, "streamed") == null, "Power cycling retries failed file once");
        FakeDecoder.Hold.Reset();
        state.PositionSeconds = 2;
        player.SetTrack(path);
        player.Tick(new Vector3(), false);
        pending = (StreamedAudio)Field(player, "streamed");
        player.Dispose(); player.Dispose();
        Check(pending != null && WaitWorker(pending) && emitter.Destroyed && AudioSettings.Subscribers == 0,
            "Dispose is idempotent and releases stream, emitter and event subscription");
        player.Tick(new Vector3(), false);
        Check(Field(player, "streamed") == null, "Disposed playback cannot restart");

        // An open stream whose window is still filling is not ready to play.
        string slow = WriteHeldTrack("slow-window.fake", readDelay: 100);
        FakeDecoder.Hold.Set();
        state = new RadioState { TrackPath = slow, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        PumpUntil(player, false, () => { var opened = (StreamedAudio)Field(player, "streamed"); return opened != null && opened.IsOpen || player.LoadFailed; });
        player.Tick(new Vector3(), false);
        // Unity would pre-read silence from a clip created now, so the clip waits for the window too.
        Check(player.Status == "Loading" && source.Schedules == 0 && source.clip == null, "An open stream alone does not prove its window is ready");
        PumpUntil(player, false, () => source.Schedules > 0 || player.LoadFailed);
        Check(source.Schedules == 1 && source.timeSamples == 0, "The stream starts once its window holds the saved position");
        player.Dispose();
        FakeDecoder.Hold.Reset();
        state = new RadioState { TrackPath = path, Powered = true };
        string lastWarning = "";
        player = new RadioPlayback(new MonoBehaviour(), state, w => { warnings++; lastWarning = w; });
        player.Tick(new Vector3(), false);
        pending = (StreamedAudio)Field(player, "streamed");
        Time.realtimeSinceStartup += 61;
        player.Tick(new Vector3(), true);
        Check(WaitWorker(pending) && Field(player, "streamed") == null && player.Status == "Unable to load music",
            "Unscaled timeout cancels a stuck open even during suspension");
        FakeDecoder.Hold.Set();
        string stuck = WriteFake("stuck-window.fake", 1, 48000, 480000, readDelay: 1500);
        source = AudioSource.Last;
        player.SetTrack(stuck);
        PumpUntil(player, false, () => { var opened = (StreamedAudio)Field(player, "streamed"); return opened != null && opened.IsOpen || player.LoadFailed; });
        player.Tick(new Vector3(), false);
        Time.realtimeSinceStartup += 61;
        player.Tick(new Vector3(), false);
        Check(source.clip == null && player.Status == "Unable to decode music" &&
            Field(player, "streamed") == null && lastWarning.Contains("did not recover"), "Stuck decoder releases its stream at finite deadline");
        player.Dispose();
        state = new RadioState { TrackPath = path, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        emitter = GameObject.Last;
        player.Tick(new Vector3(), false);
        Settle(player, false);
        var destroyedSourceClip = source.clip;
        UnityEngine.Object.Destroy(source);
        player.CapturePosition();
        player.Dispose();
        Check(destroyedSourceClip.Destroyed && emitter.Destroyed && AudioSettings.Subscribers == 0, "Source destroyed before lifetime callback cannot interrupt cleanup");
        FakeDecoder.Hold.Reset();
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        player.Tick(new Vector3(), false);
        pending = (StreamedAudio)Field(player, "streamed");
        UnityEngine.Object.Destroy(GameObject.Last);
        player.Tick(new Vector3(), false);
        Check(Field(player, "streamed") == pending && !pending.WorkerCompleted && AudioSettings.Subscribers == 1 && player.Status == "Loading",
            "Missing owned emitter is recreated without abandoning current load");
        player.Dispose();
        FakeDecoder.Hold.Set();
        DeleteClosed(slow); DeleteClosed(stuck); DeleteClosed(corrupt);
        CheckMp3(path);
        CheckTabletopVolume();
        CheckEndpointLifecycle(path);
        CheckBassDsp();
        CheckPreload(path);
        CheckPauseTimeline();
        CheckWav();
        CheckDownmixPolicy();
        CheckOgg();
        CheckFlacRouting();
        CheckM4a();
        CheckPreloadAdoption(path);
        CheckShortTracks();
        CheckStreamingIsolation();
        CheckStreamRecovery();
        DeleteClosed(path);
        string localMp3 = Environment.GetEnvironmentVariable("SAILWIND_RADIO_MP3_PROBE_FILE");
        if (!String.IsNullOrEmpty(localMp3))
        {
            var result = Decode(localMp3, CancellationToken.None);
            Console.WriteLine("Configured local MP3 decoded: channels=" + result.Channels + ", sampleRate=" + result.SampleRate +
                ", interleavedSamples=" + result.SampleCount + ", nonzeroPCM=" + Array.Exists(result.Samples, value => value != 0) +
                ", seconds=" + (result.SampleCount / (double)result.Channels / result.SampleRate).ToString("F3"));
        }
        Console.WriteLine("Audio checks passed: " + checks + ". Every format streams through an in-source decoder, OGG included. MP3 decoding uses real NLayer. Unity playback uses doubles and does not establish audible behavior.");
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
        Check(highPass.cutoffFrequency == 900 && highPass.highpassResonanceQ == 1 &&
            lowPass.cutoffFrequency == 5000 && lowPass.lowpassResonanceQ == 1,
            "Built-in radio uses a narrower low-resonance 900–5000 Hz bandpass");
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
        player.SetAcoustics(new Vector3(15, 0, 0), true, 0);
        RadioSpeakerProfile.BuiltIn.SetRadius(30);
        player.Tick(new Vector3(), false);
        bool widened = source.volume > 0 && source.maxDistance == 30;
        RadioSpeakerProfile.BuiltIn.SetRadius(Single.NaN);
        player.Tick(new Vector3(), false);
        bool restored = source.volume == 0 && RadioSpeakerProfile.BuiltIn.Radius == 12;
        RadioSpeakerProfile.BuiltIn.SetRadius(500);
        bool clamped = RadioSpeakerProfile.BuiltIn.Radius == RadioSpeakerProfile.MaximumRadius;
        RadioSpeakerProfile.BuiltIn.SetRadius(12);
        Check(widened && restored && clamped, "Falloff setting changes the running output on its next tick within the allowed range");
        player.SetAcoustics(new Vector3(3, 4, 0), true, 0);
        player.Tick(new Vector3(3, 3, 0), false);
        Check(source.volume == 0.25f, "Distance uses supplied listener and emitter world positions");
        player.SetAcoustics(new Vector3(3, 4, 0), true, 0, true);
        player.Tick(new Vector3(3, 3, 0), false);
        bool flatPlayback = source.spatialBlend == 0 && source.volume == 0.25f;
        player.SetAcoustics(new Vector3(3, 4, 0), true, 0);
        player.Tick(new Vector3(3, 3, 0), false);
        Check(flatPlayback && source.spatialBlend == 1, "Head listening plays flat at its own distance gain and returns to 3D after");
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
        Check(source.volume > 0.0875f && source.volume < 0.25f && lowPass.cutoffFrequency > 1500 && lowPass.cutoffFrequency < 5000,
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
        Check(Math.Abs(source.volume - 0.25f) < 0.000001f && Math.Abs(lowPass.cutoffFrequency - 5000) < 0.01f,
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
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
        var radio = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        int opens = FakeDecoder.OpensOf(path);
        player.BeginEndpoints();
        player.SetEndpoint(1, new Vector3(1, 0, 0), false, 1, .8f, 1, 0);
        var small = AudioSource.Last;
        var smallHigh = AudioHighPassFilter.Last;
        var smallLow = AudioLowPassFilter.Last;
        player.SetEndpoint(2, new Vector3(2, 0, 0), false, 2, 1, 1, 0);
        var normal = AudioSource.Last;
        var normalHigh = AudioHighPassFilter.Last;
        var normalLow = AudioLowPassFilter.Last;
        player.SetEndpoint(3, new Vector3(), false, 3, 1, .7f, 0);
        var woofer = AudioSource.Last;
        var wooferLow = AudioLowPassFilter.Last;
        player.EndEndpoints();
        player.Tick(new Vector3(), false);
        Settle(player);
        MakeResident(player);
        WaitStream(player);
        player.Tick(new Vector3(), false);
        var shared = radio.clip;
        Check(FakeDecoder.OpensOf(path) == opens + 1 && SameStream(shared, small, normal, woofer),
            "Three speaker endpoints read the radio's one stream through their own clips with one decoder");
        Check(small.ScheduledAt == radio.ScheduledAt && normal.ScheduledAt == radio.ScheduledAt && woofer.ScheduledAt == radio.ScheduledAt &&
            small.timeSamples == 96000 && normal.timeSamples == 96000 && woofer.timeSamples == 96000,
            "Initial endpoints schedule identical sample frames on the authoritative DSP start");
        Check(!radio.loop && !small.loop && !normal.loop && !woofer.loop,
            "No output source loops the track");
        Check(smallHigh.cutoffFrequency == 600 && smallLow.cutoffFrequency == 9000 && small.maxDistance == 15 &&
            normalHigh.cutoffFrequency == 160 && normalLow.cutoffFrequency == 18000 && normal.maxDistance == 20 &&
            wooferLow.cutoffFrequency == 140 && small.bypassReverbZones && normal.bypassReverbZones && woofer.bypassReverbZones,
            "Small, normal and woofer outputs receive distinct native tone and range profiles without external reverb");
        Check(Math.Abs(small.volume - .35f * .5f * .5f * .8f * .8f) < .000001f,
            "Small speaker tone change retains its previous local and master gain");
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
        Check(SameStream(shared, joined) && joined.ScheduledAt > AudioSettings.dspTime && joined.timeSamples == expectedJoin &&
            joined.timeSamples != quantizedSourceJoin && FakeDecoder.OpensOf(path) == opens + 1,
            "Late endpoint follows shared DSP anchor rather than a deliberately mismatched native cursor, with no extra decoder");

        state.Paused = true;
        AudioSettings.dspTime = 501.3;
        radio.timeSamples = 156000;
        player.Tick(new Vector3(), false);
        Check(!radio.isPlaying && !normal.isPlaying && !joined.isPlaying && state.PositionSeconds == 3.25,
            "Manual pause captures shared position and pauses all retained endpoints");
        state.Paused = false;
        WaitStream(player);
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
        AudioSettings.dspTime += .1;
        radio.timeSamples = 196800;
        int schedules = radio.Schedules;
        player.Tick(new Vector3(), false);
        Check(radio.volume > 0 && normal.volume > 0 && joined.volume > 0 && state.PositionSeconds == 4.05 && radio.Schedules == schedules,
            "Every output remains audible while the shared playback timeline advances");

        AudioSettings.dspTime = radio.ScheduledAt + (shared.samples - 192000) / (double)shared.frequency + .01;
        player.Tick(new Vector3(), false);
        Check(player.TrackEnded && state.PositionSeconds == 10 && radio.timeSamples == 0 && normal.timeSamples == 0 && joined.timeSamples == 0,
            "Non-looping completion preserves exact duration and stops all endpoints");
        // The ended stream refills its final frames on its worker. Status reads Ended once they are held.
        PumpUntil(player, false, () => player.Status == "Ended");
        for (int i = 0; i < 10; i++) player.Tick(new Vector3(), false);
        Check(!player.NamesTrack, "Hover names nothing once the song ends");
        Check(radio.Schedules == schedules && player.Status == "Ended" && player.TrackEnded,
            "Completed non-looping track does not silently restart while queue observes completion");
        state.PositionSeconds = 0;
        player.SetTrack(path);
        Check(!player.TrackEnded && !player.LoadFailed && shared.Destroyed && normal.clip == null && joined.clip == null && state.PositionSeconds == 0,
            "Queue restart resets completion without overwriting newly selected position with old clip cursor");
        player.Tick(new Vector3(), false); Settle(player); WaitStream(player); player.Tick(new Vector3(), false);
        Check(radio.timeSamples == 0 && SameStream(radio.clip, normal) && normal.ScheduledAt == radio.ScheduledAt,
            "Same-path queue restart loads once and resynchronizes endpoints");
        player.SetTrack("https://example.com/not-local.mp3"); player.Tick(new Vector3(), false);
        Check(player.LoadFailed && player.FailedPath == "https://example.com/not-local.mp3" && !player.TrackEnded,
            "Failed track identity is exposed for bounded playlist skipping");
        player.Dispose();
        Check(normal.Destroyed && joined.Destroyed && AudioSettings.Subscribers == 0,
            "Radio teardown owns and disposes all external emitters and subscriptions");
    }

    static void CheckPauseTimeline()
    {
        string path = WriteHeldTrack("timeline.fake", 600), shortPath = WriteHeldTrack("timeline-short.fake", 1);
        AudioSettings.dspTime = 1000;
        var state = new RadioState { TrackPath = path, Powered = true, PositionSeconds = 3.25 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
        var radio = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        player.BeginEndpoints();
        player.SetEndpoint(1, new Vector3(), false, 1, 1, 1, 0); var first = AudioSource.Last;
        player.SetEndpoint(2, new Vector3(), false, 2, 1, 1, 0); var second = AudioSource.Last;
        player.EndEndpoints();
        player.Tick(new Vector3(), false); Settle(player); player.Tick(new Vector3(), false);
        double start = radio.ScheduledAt;
        AudioSettings.dspTime = start + 4;
        radio.isVirtual = true; radio.timeSamples = 0;
        first.timeSamples = 48000; second.timeSamples = 400000;
        player.Tick(new Vector3(), true);
        Check(state.PositionSeconds == 7.25 && !radio.isPlaying && !first.isPlaying && !second.isPlaying,
            "Pause snapshots shared DSP time despite virtual primary and conflicting native speaker cursors");
        AudioSettings.dspTime += 3600; // TimeScale-zero menu does not freeze Unity DSP.
        for (int i = 0; i < 3; i++) player.Tick(new Vector3(), true);
        WaitStream(player);
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
        player.Tick(new Vector3(), true); WaitStream(player); AudioSettings.dspTime += .01; player.Tick(new Vector3(), false);
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
        Check(player.DriftRepairs == 1, "Diagnostic repair count observes exactly one corrective seek");
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
        player.Tick(new Vector3(), true); WaitStream(player);
        AudioSettings.dspTime += 100; player.Tick(new Vector3(), false);
        Check(second.Schedules == 3 && second.timeSamples == 578400 && radio.UnPauses == 3,
            "Pause before a late scheduled start cancels it and creates exactly one fresh aligned join");
        UnityEngine.Object.Destroy(radio);
        player.Tick(new Vector3(), false);
        var replacement = AudioSource.Last;
        Check(replacement != radio && replacement.clip != null && replacement.clip.Streaming && replacement.clip.samples == first.clip.samples &&
            replacement.Schedules == 1 && !player.TrackEnded,
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
            state = new RadioState { TrackPath = shortPath, Powered = true };
            player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
            radio = AudioSource.Last;
            player.SetAcoustics(new Vector3(), true, 0);
            player.Tick(new Vector3(), false); Settle(player); player.Tick(new Vector3(), false);
            AudioSettings.dspTime = radio.ScheduledAt + .97;
            if (resume)
            {
                player.Tick(new Vector3(), true); WaitStream(player); AudioSettings.dspTime += 100;
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
        DeleteClosed(path); DeleteClosed(shortPath);
    }

    static void CheckBassDsp()
    {
        foreach (int rate in new[] { 44100, 48000, 96000 })
        {
            var filter = new RadioBassFilter();
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
            filter.Process(signal, 2);
            Check(Array.TrueForAll(signal, value => !Single.IsNaN(value) && !Single.IsInfinity(value) && Math.Abs(value) <= 1),
                "Woofer output limiter remains finite and bounded for over-range PCM");
        }
        var mono = new RadioBassFilter();
        mono.SetProfile(48000, true);
        var block = new float[2048];
        mono.Process(block, 2); // JIT/warmup outside the allocation observation.
#if NET10_0_OR_GREATER
        long before = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) mono.Process(block, 2);
        Check(GC.GetAllocatedBytesForCurrentThread() == before, "Steady audio callback performs no managed allocations");
#endif
    }

    static void CheckPreload(string currentPath)
    {
        // The current track is held by Hold and the upcoming one by Gate, so each load completes on cue.
        string next = WriteFake("next.fake", 1, 48000, 480000, gate: true);
        string mp3 = Path.Combine(AppContext.BaseDirectory, "next.mp3");
        string broken = Path.Combine(AppContext.BaseDirectory, "broken-upcoming.ogg");
        WriteSilentMp3(mp3, false, false);
        File.WriteAllText(broken, "This is not an Ogg stream");
        var state = new RadioState { TrackPath = currentPath, Powered = true, Volume = .8f };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        player.Tick(new Vector3(), false); Settle(player); player.Tick(new Vector3(), false);
        var original = source.clip;
        FakeDecoder.Gate.Reset();
        player.PreloadTrack(next); player.Tick(new Vector3(), false);
        var preparing = (StreamedAudio)Field(player, "preloadStream");
        Check(preparing != null && !preparing.IsOpen && source.clip == original && player.Status == "Playing",
            "Upcoming load runs beside the unchanged current clip");
        FakeDecoder.Gate.Set();
        Check(WaitReady(preparing, 0), "Upcoming stream prefills once its open completes");
        player.Tick(new Vector3(), false);
        Check(Field(player, "preloadStream") == preparing && !original.Destroyed && source.clip == original,
            "Prepared stream is owned independently of current output");
        int opens = FakeDecoder.OpensOf(next);
        state.TrackPath = next; state.PositionSeconds = 0;
        player.SetTrack(next); player.Tick(new Vector3(), false);
        var prepared = source.clip;
        Check(Field(player, "streamed") == preparing && prepared != null && prepared != original && original.Destroyed &&
            FakeDecoder.OpensOf(next) == opens && source.timeSamples == 0 && source.Schedules > 0,
            "Queue selection consumes the prepared stream without a second open or old-position overwrite");
        player.BeginEndpoints(); player.SetEndpoint(1, new Vector3(), false, 2, 1, 1, 0); player.EndEndpoints();
        var speaker = AudioSource.Last;
        state.LocalVolume = 0; player.Tick(new Vector3(), false);
        Check(source.volume == 0 && speaker.volume > 0 && SameStream(prepared, speaker), "Radio local volume does not mute its external speakers");
        state.LocalVolume = 1;
        FakeDecoder.Hold.Reset();
        player.PreloadTrack(currentPath); player.Tick(new Vector3(), false);
        var pending = (StreamedAudio)Field(player, "preloadStream");
        state.TrackPath = currentPath; state.PositionSeconds = 1;
        player.SetTrack(currentPath); player.Tick(new Vector3(), true);
        Check(pending != null && Field(player, "streamed") == pending && !pending.IsOpen && !pending.WorkerCompleted &&
            source.clip == null && player.Status == "Loading", "Selecting a pending preload transfers its stream without restart");
        Settle(player);
        Check(source.clip != null && source.timeSamples == 0 && player.Status == "Suspended", "Adopted load completing during suspension cannot play");
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 48000 && speaker.timeSamples == 48000 && SameStream(source.clip, speaker),
            "Adopted load resumes every endpoint at requested saved cursor");
        player.PreloadTrack(next); player.Tick(new Vector3(), false);
        var ready = (StreamedAudio)Field(player, "preloadStream");
        Check(ready != null && WaitReady(ready, 0), "An upcoming stream opens and prefills");
        player.PreloadTrack("");
        Check(Field(player, "preloadStream") == null && WaitWorker(ready), "Cancel releases an open upcoming stream before the next Tick");
        FakeDecoder.Gate.Reset();
        player.PreloadTrack(next); player.Tick(new Vector3(), false);
        pending = (StreamedAudio)Field(player, "preloadStream");
        player.PreloadTrack(mp3);
        Check(pending != null && Field(player, "preloadStream") == null && WaitWorker(pending),
            "Replacing the upcoming track cancels its obsolete pending stream");
        FakeDecoder.Gate.Set();
        player.Tick(new Vector3(), false);
        var current = source.clip;
        // The real NLayer stream opens on its own worker. Transfer happens regardless of timing.
        PumpUntil(player, false, () => Field(player, "preloadStream") != null);
        var upcomingMp3 = (StreamedAudio)Field(player, "preloadStream");
        player.SetTrack(mp3);
        player.Tick(new Vector3(), false); // Status is stale until the Tick after selection.
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && upcomingMp3 != null && Field(player, "streamed") == upcomingMp3 && source.clip != current &&
            current.Destroyed && source.clip.Streaming, "Managed MP3 preload hands its stream over");
        player.PreloadTrack(broken);
        PumpUntil(player, false, () => (bool)Field(player, "preloadSkipped"));
        Check(!player.LoadFailed && player.Status == "Playing" && Field(player, "preloadStream") == null,
            "Speculative decode failure cannot interrupt current track");
        FakeDecoder.Gate.Reset();
        player.PreloadTrack(next); player.Tick(new Vector3(), false);
        pending = (StreamedAudio)Field(player, "preloadStream");
        float previousTime = Time.realtimeSinceStartup;
        Time.realtimeSinceStartup += 61;
        player.Tick(new Vector3(), false);
        Check(pending != null && Field(player, "preloadStream") == null && WaitWorker(pending) && !player.LoadFailed && player.Status == "Playing",
            "Upcoming load timeout releases its stream without failing the current track");
        Time.realtimeSinceStartup = previousTime;
        player.PreloadTrack(currentPath); player.Tick(new Vector3(), false);
        pending = (StreamedAudio)Field(player, "preloadStream");
        player.Dispose();
        Check(pending != null && WaitWorker(pending) && AudioSettings.Subscribers == 0, "Shutdown cancels upcoming load and releases all ownership");
        FakeDecoder.Gate.Set();
        foreach (bool prefilled in new[] { false, true })
        {
            state.TrackPath = currentPath; state.PositionSeconds = 0;
            player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
            source = AudioSource.Last;
            var emitter = GameObject.Last;
            player.Tick(new Vector3(), false); Settle(player); player.Tick(new Vector3(), false);
            current = source.clip;
            if (!prefilled) FakeDecoder.Gate.Reset();
            player.PreloadTrack(next); player.Tick(new Vector3(), false);
            pending = (StreamedAudio)Field(player, "preloadStream");
            if (prefilled) WaitReady(pending, 0);
            source.ThrowOnNextStop = true;
            bool stopFailed = false;
            try { player.Dispose(); }
            catch (InvalidOperationException) { stopFailed = true; }
            Check(stopFailed && current.Destroyed && pending != null && WaitWorker(pending) && pending.IsOpen == prefilled &&
                source.Destroyed && emitter.Destroyed && AudioSettings.Subscribers == 0,
                "Native current-source Stop failure still releases current clip, " + (prefilled ? "prepared stream" : "pending preload") + " and emitter ownership");
            player.Dispose(); // Idempotent even after a teardown exception.
            FakeDecoder.Gate.Set();
        }
        DeleteClosed(next); DeleteClosed(mp3); DeleteClosed(broken);
    }

    // Each output streams the shared track through its own clip. A speaker never shares the radio's clip.
    static bool SameStream(AudioClip master, params AudioSource[] outputs)
    {
        if (master == null || !master.Streaming) return false;
        foreach (var output in outputs)
            if (output.clip == null || !output.clip.Streaming || output.clip == master || output.clip.samples != master.samples ||
                output.clip.frequency != master.frequency || output.clip.channels != master.channels) return false;
        return true;
    }

    static void CheckMp3(string heldPath)
    {
        string mp3 = Path.Combine(AppContext.BaseDirectory, "original-silent-stereo.mp3");
        WriteSilentMp3(mp3, false, false);
        var decoded = Decode(mp3, CancellationToken.None);
        Check(decoded.Channels == 2 && decoded.SampleRate == 44100 && decoded.SampleCount == 20 * 1152 * 2,
            "Real pinned NLayer decodes generated stereo MPEG frames with interleaved sample dimensions");
        Check(Array.TrueForAll(decoded.Samples, value => value == 0), "Original silent MP3 decodes to silence");
        string mono = Path.Combine(AppContext.BaseDirectory, "original-silent-mono.mp3");
        WriteSilentMp3(mono, true, false);
        decoded = Decode(mono, CancellationToken.None);
        Check(decoded.Channels == 1 && decoded.SampleCount == 20 * 1152, "Real decoder preserves mono sample dimensions");
        string vbr = Path.Combine(AppContext.BaseDirectory, "original-silent-vbr.mp3");
        WriteSilentMp3(vbr, false, true);
        decoded = Decode(vbr, CancellationToken.None);
        Check(decoded.SampleCount == 20 * 1152 * 2, "Mixed-bitrate MPEG frames decode to complete PCM without estimated-length allocation");
        // NLayer indexes every frame at open, so MP3 alone keeps a file-size cap. Other formats rely on the
        // stream engine's frame limit, so the same oversized file named FLAC fails only as a FLAC stream.
        string oversized = Path.Combine(AppContext.BaseDirectory, "oversized.mp3");
        using (var output = new FileStream(oversized, FileMode.Create)) output.SetLength((1L << 30) + 1);
        string capMessage = "";
        try { PcmDecoders.Open(oversized, CancellationToken.None).Dispose(); }
        catch (IOException ex) { capMessage = ex.Message; }
        Check(capMessage == "Audio file is larger than 1 GB" && FileClosed(oversized),
            "MP3 above 1 GB is refused before NLayer opens it, with the cap in the message: " + capMessage);
        string oversizedFlac = Path.ChangeExtension(oversized, ".flac");
        File.Move(oversized, oversizedFlac);
        capMessage = "";
        try { PcmDecoders.Open(oversizedFlac, CancellationToken.None).Dispose(); }
        catch (IOException ex) { capMessage = ex.Message; }
        Check(capMessage == "File is not a FLAC stream" && FileClosed(oversizedFlac), "FLAC above 1 GB has no file-size cap: " + capMessage);
        DeleteClosed(oversizedFlac);
        bool rejected;
        using (var cancelled = new CancellationTokenSource())
        {
            cancelled.Cancel(); rejected = false;
            try { Decode(mp3, cancelled.Token); } catch (OperationCanceledException) { rejected = true; }
            Check(rejected, "Cancelled managed decode stops before file access");
        }
        CheckStreamedMp3(mp3, Decode(mp3, CancellationToken.None));
        string corrupt = Path.Combine(AppContext.BaseDirectory, "corrupt.mp3");
        File.WriteAllText(corrupt, "This is not an MPEG stream");
        rejected = false;
        try { Decode(corrupt, CancellationToken.None); } catch (Exception) { rejected = true; }
        using (File.Open(corrupt, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
        Check(rejected, "Malformed MP3 is rejected and constructor failure releases file handle");

        int warnings = 0;
        var state = new RadioState { TrackPath = mp3, Powered = true, PositionSeconds = 0.1 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        player.Tick(new Vector3(), true);
        PumpUntil(player, true, () => source.clip != null);
        Check(Field(player, "streamed") is StreamedAudio, "MP3 loads through the stream engine, never a Unity loader");
        Check(source.clip.Streaming && source.clip.channels == 2 && source.clip.samples == 20 * 1152 && source.clip.frequency == 44100,
            "A short MP3 plays through a PCM-reader stream with the correct stereo frame count");
        Check(AudioClip.CreateThread == Environment.CurrentManagedThreadId && source.Schedules == 0,
            "The stream opens on its worker, the clip is created on the main thread and suspension is respected");
        PumpUntil(player, false, () => source.Schedules > 0 || player.LoadFailed);
        Check(source.Schedules == 1 && source.timeSamples == 4410, "Managed MP3 starts at retained sample position");
        var clip = source.clip;
        player.Dispose();
        Check(clip.Destroyed, "Managed MP3 clip released on disposal");

        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        player.Tick(new Vector3(), false);
        var abandoned = (StreamedAudio)Field(player, "streamed");
        FakeDecoder.Hold.Reset();
        player.SetTrack(heldPath);
        player.Tick(new Vector3(), false);
        for (int i=0;i<20;i++) { Thread.Sleep(1); player.Tick(new Vector3(), false); }
        Check(source.clip == null && source.Schedules == 0 && player.Status == "Loading" && WaitWorker(abandoned),
            "Replacing MP3 with a pending track stops the old stream worker and cannot attach its result");
        player.Dispose();
        FakeDecoder.Hold.Set();

        state = new RadioState { TrackPath = mp3, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        AudioClip.RejectCreate = true;
        try
        {
            player.Tick(new Vector3(), false);
            var refused = (StreamedAudio)Field(player, "streamed");
            PumpUntil(player, false, () => player.Status == "Unable to decode music");
            Check(source.clip == null && warnings == 1 && Field(player, "streamed") == null && WaitWorker(refused),
                "Streamed clip creation failure releases its stream and reports one warning");
        }
        finally { AudioClip.RejectCreate = false; }
        player.Dispose();

        state = new RadioState { TrackPath = corrupt, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        PumpUntil(player, false, () => player.Status == "Unable to decode music");
        for (int i=0;i<20;i++) player.Tick(new Vector3(), false);
        Check(warnings == 2, "Corrupt managed decode reports once without per-frame retries");
        player.Dispose();

        string missing = Path.Combine(AppContext.BaseDirectory, "missing-track.mp3");
        string lastWarning = "";
        state = new RadioState { TrackPath = missing, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, w => { warnings++; lastWarning = w; });
        PumpUntil(player, false, () => player.LoadFailed);
        Check(player.Status == "Unable to load music" && lastWarning.Contains("missing or inaccessible") && warnings == 3,
            "A missing in-source file is found missing by the stream worker and reported as a load failure: " + lastWarning);
        player.Dispose();
        DeleteClosed(mp3); DeleteClosed(mono); DeleteClosed(vbr); DeleteClosed(corrupt);
    }

    static void CheckStreamedMp3(string path, Pcm reference)
    {
        string longPath = Path.Combine(AppContext.BaseDirectory, "long-stream-fixture.mp3");
        WriteSilentMp3(longPath, false, false, 29200);
        var longStream = OpenStream(longPath, 700);
        Check((long)longStream.Frames * longStream.Channels > 64L * 1024 * 1024 &&
            longStream.BufferSamples <= 22 * longStream.SampleRate * longStream.Channels,
            "A real MP3 longer than 12 minutes streams through a bounded 20 second window");
        int savedFrame = 700 * longStream.SampleRate;
        Check(WaitReady(longStream, savedFrame), "Long streamed MP3 begins decoding at the saved position");
        int laterFrame = 740 * longStream.SampleRate;
        int boundedSamples = longStream.BufferSamples;
        longStream.Request(laterFrame);
        Check(WaitReady(longStream, laterFrame) && longStream.BufferSamples == boundedSamples,
            "Seeking within a long MP3 refills the fixed-size PCM window");
        longStream.Request(10 * longStream.SampleRate);
        Check(WaitReady(longStream, 10 * longStream.SampleRate), "A long seek backward refills the window at the new position");
        longStream.Dispose();
        Check(WaitWorker(longStream), "Long decoder worker exits promptly after cancellation");
        var longState = new RadioState { TrackPath = longPath, PositionSeconds = 700, Powered = true };
        var longPlayer = new RadioPlayback(new MonoBehaviour(), longState, _ => { });
        var builtInEmitter = GameObject.Last;
        var radioSource = AudioSource.Last;
        longPlayer.BeginEndpoints();
        longPlayer.SetEndpoint(991, new Vector3(), false, 2, 1, 1, 0);
        longPlayer.EndEndpoints();
        var speakerSource = AudioSource.Last;
        PumpUntil(longPlayer, false, () => longPlayer.Status == "Playing" || longPlayer.LoadFailed);
        Check(!longPlayer.LoadFailed && radioSource.clip != null && radioSource.clip.Streaming &&
            speakerSource.clip != null && speakerSource.clip.Streaming && radioSource.clip != speakerSource.clip &&
            radioSource.Schedules == 1 && speakerSource.Schedules == 1 &&
            radioSource.timeSamples == 700 * radioSource.clip.frequency &&
            speakerSource.timeSamples == radioSource.timeSamples,
            "Long-track playback restores saved DSP position on independently streamed radio and speaker clips");
        var radioClip = radioSource.clip;
        var speakerClip = speakerSource.clip;
        UnityEngine.Object.Destroy(builtInEmitter);
        longPlayer.Tick(new Vector3(), false);
        var recoveredSource = AudioSource.Last;
        var recoveredClip = recoveredSource.clip;
        Check(!radioClip.Destroyed && !speakerClip.Destroyed && recoveredClip != null &&
            recoveredClip.Streaming && recoveredClip != radioClip && speakerSource.clip == speakerClip,
            "Recreating a lost built-in emitter retains the master timeline and healthy speaker clip");
        AudioSettings.dspTime = radioSource.ScheduledAt + 1;
        longState.Paused = true;
        longPlayer.Tick(new Vector3(), false);
        Check(recoveredSource.Pauses == 1 && speakerSource.Pauses == 1 && longState.PositionSeconds >= 701,
            "Long-track pause keeps synchronized output state");
        longPlayer.Dispose();
        Check(radioClip.Destroyed && recoveredClip.Destroyed && speakerClip.Destroyed && AudioSettings.Subscribers == 0,
            "Long-track disposal releases all output clips and configuration subscription");
        var invalidState = new RadioState { TrackPath = longPath, PositionSeconds = Double.NaN, Powered = true };
        var invalidPlayer = new RadioPlayback(new MonoBehaviour(), invalidState, _ => { });
        var invalidSource = AudioSource.Last;
        PumpUntil(invalidPlayer, false, () => invalidPlayer.Status == "Playing" || invalidPlayer.LoadFailed);
        Check(!invalidPlayer.LoadFailed && invalidSource.timeSamples == 0,
            "Malformed NaN save position safely starts a long MP3 at zero");
        invalidState.Paused = true;
        invalidState.PositionSeconds = Double.PositiveInfinity;
        invalidPlayer.Tick(new Vector3(), false);
        Check(invalidPlayer.Status == "Paused",
            "Infinite saved position cannot index the streamed PCM buffer");
        invalidPlayer.Dispose();
        Check(FileClosed(longPath), "Long-track disposal closes its worker-owned file promptly");
        DeleteClosed(longPath);

        var stream = OpenStream(path, 0.1);
        int frames = reference.SampleCount / reference.Channels;
        Check(stream.Frames == frames && stream.BufferSamples == (frames + 4095) / 4096 * 4096 * stream.Channels,
            "A short MP3 streams too, with a ring no larger than the track");
        var first = stream.CreateReader();
        var second = stream.CreateReader();
        var firstClip = stream.CreateClip(first);
        var secondClip = stream.CreateClip(second);
        Check(firstClip.Streaming && secondClip.Streaming && firstClip != secondClip &&
            firstClip.samples == secondClip.samples && firstClip.PcmRead != secondClip.PcmRead,
            "Each output gets its own streaming callback and cursor");
        int staleGeneration = first.Generation, staleCursor = first.Cursor;
        first.SetPosition(100);
        first.CommitRead(staleGeneration, staleCursor, staleCursor + 1024);
        Check(first.Cursor == 100,
            "An in-flight PCM read cannot overwrite a newer seek position");
        var silent = new float[1024 * stream.Channels];
        int resume = stream.SampleRate / 10;
        firstClip.PcmSetPosition(resume);
        stream.Request(resume);
        Check(WaitReady(stream, resume), "Worker fills a saved position: " + (stream.Fault == null ? "not ready" : stream.Fault.ToString()));
        secondClip.PcmSetPosition(0);
        stream.Request(0);
        Check(WaitReady(stream, 0), "A backward request inside a short track is served from the held ring");
        secondClip.PcmRead(silent);
        firstClip.PcmRead(silent);
        Check(stream.Ready(0) && Array.TrueForAll(silent, value => value == 0),
            "Independent readers can join and read the same buffered PCM window");
        firstClip.PcmSetPosition(stream.Frames - 1024);
        firstClip.PcmSetPosition(0);
        stream.Request(0);
        Check(WaitReady(stream, 0), "A seek back to the beginning requests it and the worker keeps it available");
        Check(stream.Ready(Int32.MinValue), "Invalid direct buffer position is clamped safely");
#if NET10_0_OR_GREATER
        firstClip.PcmRead(silent); // JIT callback before measuring steady-state work.
        long allocatedBefore = GC.GetAllocatedBytesForCurrentThread();
        for (int i = 0; i < 100; i++) firstClip.PcmRead(silent);
        Check(GC.GetAllocatedBytesForCurrentThread() == allocatedBefore,
            "Steady streamed audio callback allocates no managed memory");
#endif
        stream.Dispose();
        Array.Fill(silent, 0.8f);
        firstClip.PcmRead(silent);
        Check(Array.TrueForAll(silent, value => value == 0),
            "Disposed stream returns silence to a late native callback");
        UnityEngine.Object.Destroy(firstClip);
        UnityEngine.Object.Destroy(secondClip);
        var opening = StreamedAudio.Start(path, 0);
        opening.Dispose();
        Check(WaitWorker(opening) && opening.Fault == null && !opening.Ready(0) && FileClosed(path),
            "Disposing a stream while it opens stops its worker and closes the file");
        CheckNonzeroStreamedMp3();
        CheckMp3AdapterSeek();
    }

    static void CheckNonzeroStreamedMp3()
    {
        // Original deterministic pink-noise fixture, generated with ffmpeg's
        // anoisesrc at seeds 12345 and 54321. It carries no personal music.
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stereo-noise.mp3");
        var reference = Decode(path, CancellationToken.None);
        Check(Array.Exists(reference.Samples, sample => Math.Abs(sample) > .001f),
            "Deterministic stereo fixture contains audible nonzero PCM");
        var stream = OpenStream(path, 8);
        Check(stream.Frames * stream.Channels == reference.SampleCount, "Nonzero stereo fixture streams with its full-decode length");
        var clipA = stream.CreateClip(stream.CreateReader());
        var clipB = stream.CreateClip(stream.CreateReader());
        int seek = 8 * stream.SampleRate;
        clipA.PcmSetPosition(seek);
        clipB.PcmSetPosition(seek);
        stream.Request(seek);
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!stream.Ready(seek) && stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(2);
        Check(stream.Fault == null && stream.Ready(seek), "Nonzero seek fills streaming window: " +
            (stream.Fault == null ? "not ready" : stream.Fault.ToString()));
        var a = new float[1024 * stream.Channels];
        var b = new float[a.Length];
        clipA.PcmRead(a);
        clipB.PcmRead(b);
        bool seekMatches = true, readersMatch = true;
        int seekMismatch = -1;
        for (int i = 0; i < a.Length; i++)
        {
            if (Math.Abs(a[i] - DecodedSample(reference, seek * stream.Channels + i)) > .00001f)
            { seekMatches = false; if (seekMismatch < 0) seekMismatch = i; }
            if (Math.Abs(a[i] - b[i]) > .00001f) readersMatch = false;
        }
        Check(seekMatches && readersMatch && Array.Exists(a, sample => Math.Abs(sample) > .001f),
            "After seek, two independent readers reproduce the same nonzero full-decode PCM: mismatch=" +
            seekMismatch + " a=" + (seekMismatch < 0 ? 0 : a[seekMismatch]) + " ref=" +
            (seekMismatch < 0 ? 0 : DecodedSample(reference, seek * stream.Channels + seekMismatch)));
        clipA.PcmSetPosition(stream.Frames - 1024);
        clipA.PcmSetPosition(0);
        clipB.PcmSetPosition(0);
        stream.Request(0);
        deadline = DateTime.UtcNow.AddSeconds(5);
        while (!stream.Ready(0) && stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(2);
        Check(stream.Fault == null && stream.Ready(0), "A nonzero seek back to the start refills the start of the file: " +
            (stream.Fault == null ? "not ready" : stream.Fault.ToString()));
        bool loopMatches = true, loopNonzero = false;
        int firstMismatch = -1;
        for (int block = 0; block < 4; block++)
        {
            clipA.PcmRead(a);
            clipB.PcmRead(b);
            for (int i = 0; i < a.Length; i++)
            {
                if (Math.Abs(a[i]) > .001f) loopNonzero = true;
                if (Math.Abs(a[i] - b[i]) > .00001f ||
                    Math.Abs(a[i] - DecodedSample(reference, block * a.Length + i)) > .00001f)
                { loopMatches = false; if (firstMismatch < 0) firstMismatch = block * a.Length + i; }
            }
        }
        Check(loopMatches && loopNonzero,
            "After seeking back to the start, both readers reproduce nonzero full-decode PCM from the start: mismatch=" +
            firstMismatch);
        stream.Dispose();
        UnityEngine.Object.Destroy(clipA);
        UnityEngine.Object.Destroy(clipB);
    }

    static void CheckMp3AdapterSeek()
    {
        // Reference decode uses raw NLayer without its length index, independent of the adapter.
        string path = Path.Combine(AppContext.BaseDirectory, "Fixtures", "stereo-noise.mp3");
        var raw = new System.Collections.Generic.List<float>();
        using (var reference = new NLayer.MpegFile(path))
        {
            var block = new float[8192];
            int got;
            while ((got = reference.ReadSamples(block, 0, block.Length)) > 0)
                for (int i = 0; i < got; i++) raw.Add(block[i]);
        }
        using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
        {
            int channels = decoder.Channels, rate = decoder.SampleRate;
            float[] all = ReadAll(decoder);
            Check(decoder is Mp3PcmDecoder && all.Length == raw.Count && decoder.Frames * channels == raw.Count &&
                Matches(all, 0, raw.ToArray(), 0, all.Length, 0),
                "MP3 adapter full read after its length index equals an independent raw NLayer decode");
            // Targets inside the preroll decode from zero. Deeper targets rely on the pinned
            // NLayer one-frame correction, which holds for this fixture beyond its twelfth frame.
            long[] targets = { 3000, 8L * rate + 123, rate, decoder.Frames - 1500, 0, 6L * rate + 1 };
            bool exact = true;
            string detail = "";
            foreach (long target in targets)
            {
                decoder.Seek(target);
                var slice = new float[2048 * channels];
                int read = ReadInto(decoder, slice);
                int expected = (int)Math.Min(2048, decoder.Frames - target) * channels;
                if (read != expected || !Matches(slice, 0, all, (int)target * channels, read, .00001f) ||
                    decoder.Position != target + read / channels)
                { exact = false; detail += " " + target; }
            }
            Check(exact, "MP3 adapter seeks forward, backward, past preroll and to the tail match full decode:" + detail);
        }
    }

    static void CheckWav()
    {
        string dir = AppContext.BaseDirectory;
        const int rate = 44100, frames = 12000;
        Func<int, int, double> tone = (f, c) => .6 * Math.Sin(2 * Math.PI * (330 + 110 * c) * f / rate) +
            .3 * Math.Sin(f * 0.37 + c * 1.3);
        var cases = new[]
        {
            new { Name = "8-bit", Tag = 1, Bits = 8, Extensible = false, Tolerance = 1.01 / 128 },
            new { Name = "16-bit", Tag = 1, Bits = 16, Extensible = false, Tolerance = 1.01 / 32768 },
            new { Name = "24-bit", Tag = 1, Bits = 24, Extensible = false, Tolerance = 1.01 / 8388608 },
            new { Name = "32-bit integer", Tag = 1, Bits = 32, Extensible = false, Tolerance = 1e-6 },
            new { Name = "32-bit float", Tag = 3, Bits = 32, Extensible = false, Tolerance = 1e-7 },
            new { Name = "extensible 24-bit", Tag = 1, Bits = 24, Extensible = true, Tolerance = 1.01 / 8388608 },
            new { Name = "extensible float", Tag = 3, Bits = 32, Extensible = true, Tolerance = 1e-7 },
        };
        foreach (var item in cases)
            foreach (int channels in new[] { 1, 2 })
            {
                string path = Path.Combine(dir, "wav-" + item.Bits + "-" + item.Tag + "-" + channels + ".wav");
                WriteWav(path, item.Tag, item.Bits, channels, rate, frames, tone, item.Extensible, 0, oddChunks: true);
                using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
                {
                    float[] all = ReadAll(decoder);
                    Check(decoder.Channels == channels && decoder.SampleRate == rate && decoder.Frames == frames &&
                        all.Length == frames * channels && MatchesTone(all, channels, tone, item.Tolerance),
                        "WAV " + item.Name + " " + channels + "-channel decodes exact frames around odd unknown chunks");
                    Check(SeekMatches(decoder, all), "WAV " + item.Name + " " + channels + "-channel exact seek equals full decode");
                }
                DeleteClosed(path);
            }

        string ordered = Path.Combine(dir, "wav-data-first.wav");
        WriteWav(ordered, 1, 16, 2, rate, frames, tone, false, 0, dataFirst: true);
        using (var decoder = PcmDecoders.Open(ordered, CancellationToken.None))
            Check(decoder.Frames == frames && MatchesTone(ReadAll(decoder), 2, tone, 1.01 / 32768),
                "WAV data chunk before its format chunk still decodes");
        DeleteClosed(ordered);

        string placeholder = Path.Combine(dir, "wav-placeholder.wav");
        WriteWav(placeholder, 1, 16, 2, rate, frames, tone, false, 0, dataSizeOverride: UInt32.MaxValue);
        using (var decoder = PcmDecoders.Open(placeholder, CancellationToken.None))
            Check(decoder.Frames == frames, "Streaming writer placeholder data size uses the frames present");
        DeleteClosed(placeholder);

        string truncated = Path.Combine(dir, "wav-truncated.wav");
        WriteWav(truncated, 1, 24, 2, rate, frames, tone, false, 0, truncateBytes: 6 * 100 + 4);
        using (var decoder = PcmDecoders.Open(truncated, CancellationToken.None))
        {
            float[] all = ReadAll(decoder);
            Check(decoder.Frames == frames - 101 && all.Length == (frames - 101) * 2 && MatchesTone(all, 2, tone, 1.01 / 8388608),
                "Truncated WAV data plays only its complete frames");
        }
        File.WriteAllBytes(truncated, Prefix(File.ReadAllBytes(truncated), 30));
        Check(Rejects(truncated, null), "WAV with a truncated header is rejected");
        DeleteClosed(truncated);

        // 5.1 in standard WAVE order. Centre and rears enter at -3 dB and LFE is dropped.
        string surround = Path.Combine(dir, "wav-51.wav");
        WriteWav(surround, 1, 16, 6, rate, frames, tone, false, 0);
        double norm = 1 + 2 * Math.Sqrt(.5);
        using (var decoder = PcmDecoders.Open(surround, CancellationToken.None))
        {
            float[] all = ReadAll(decoder);
            bool matches = decoder.Channels == 2 && all.Length == frames * 2;
            for (int f = 0; f < frames && matches; f++)
            {
                double l = (Q16(tone(f, 0)) + Math.Sqrt(.5) * Q16(tone(f, 2)) + Math.Sqrt(.5) * Q16(tone(f, 4))) / norm;
                double r = (Q16(tone(f, 1)) + Math.Sqrt(.5) * Q16(tone(f, 2)) + Math.Sqrt(.5) * Q16(tone(f, 5))) / norm;
                matches = Math.Abs(all[f * 2] - l) < 1e-5 && Math.Abs(all[f * 2 + 1] - r) < 1e-5;
            }
            Check(matches, "5.1 WAV downmixes to stereo with fixed centre and surround gains and no LFE");
            Check(SeekMatches(decoder, all), "Downmixed WAV seeks exactly");
        }
        WriteWav(surround, 3, 32, 8, rate, 64, (f, c) => 1.0, false, 0);
        using (var decoder = PcmDecoders.Open(surround, CancellationToken.None))
            Check(Array.TrueForAll(ReadAll(decoder), value => value <= 1.000001f && value > .99f),
                "Full-scale 7.1 input downmixes without exceeding full scale");
        // An explicit mask wins over the default layout. Here the third channel is LFE only.
        WriteWav(surround, 1, 16, 3, rate, frames, tone, true, 0x0B);
        using (var decoder = PcmDecoders.Open(surround, CancellationToken.None))
        {
            float[] all = ReadAll(decoder);
            bool matches = true;
            for (int f = 0; f < frames && matches; f++)
                matches = Math.Abs(all[f * 2] - Q16(tone(f, 0))) < 1e-6 && Math.Abs(all[f * 2 + 1] - Q16(tone(f, 1))) < 1e-6;
            Check(matches, "Extensible channel mask selects front pair and drops LFE");
        }
        DeleteClosed(surround);

        string rejected = Path.Combine(dir, "wav-rejected.wav");
        WriteWav(rejected, 2, 4, 2, rate, 1000, tone, false, 0);
        Check(Rejects(rejected, "compressed format 0x0002"), "ADPCM WAV is rejected with a compressed-format error");
        WriteWav(rejected, 2, 16, 2, rate, 1000, tone, true, 0);
        Check(Rejects(rejected, "compressed format 0x0002"), "Extensible WAV with a compressed subformat is rejected");
        WriteWav(rejected, 1, 16, 2, rate, 1000, tone, false, 0, riff: "RF64");
        Check(Rejects(rejected, "RF64"), "RF64 WAV is rejected clearly");
        WriteWav(rejected, 1, 12, 2, rate, 1000, tone, false, 0);
        Check(Rejects(rejected, "12-bit"), "Unsupported integer width is rejected");
        WriteWav(rejected, 1, 16, 2, 4000, 1000, tone, false, 0);
        Check(Rejects(rejected, "playable range"), "WAV sample rate below 8 kHz is rejected");
        WriteWav(rejected, 1, 16, 2, 192000, 1000, tone, false, 0);
        using (var decoder = PcmDecoders.Open(rejected, CancellationToken.None))
            Check(decoder.SampleRate == 192000, "WAV decoder accepts 192 kHz input");
        bool playable = !StreamFault(rejected).Contains("playable range"), rejected2;
        Check(playable == (AudioFiles.MaximumPlayableSampleRate >= 192000),
            "Playback applies the single probed sample-rate ceiling to 192 kHz WAV");
        rejected2 = false;
        try { AudioFiles.CheckPlayableRate(AudioFiles.MaximumPlayableSampleRate + 1); }
        catch (IOException ex) { rejected2 = ex.Message.Contains("playable range"); }
        Check(rejected2, "Rates above the probed playable ceiling are rejected with a clear error");
        DeleteClosed(rejected);

        // Streamed WAV PCM must equal the full decode.
        string wav = Path.Combine(dir, "wav-stream.wav");
        WriteWav(wav, 1, 16, 2, rate, 3 * rate, tone, false, 0);
        float[] reference;
        using (var decoder = PcmDecoders.Open(wav, CancellationToken.None)) reference = ReadAll(decoder);
        var streamed = OpenStream(wav, 1.5);
        Check(streamed.Frames == 3 * rate && streamed.Channels == 2 && streamed.BufferSamples == (3 * rate + 4095) / 4096 * 4096 * 2,
            "A three second WAV streams with the whole track in its ring");
        var reader = streamed.CreateClip(streamed.CreateReader());
        foreach (int frame in new[] { rate * 3 / 2, 100, rate * 2 + 7 })
        {
            reader.PcmSetPosition(frame);
            streamed.Request(frame);
            var deadline = DateTime.UtcNow.AddSeconds(5);
            while (!streamed.Ready(frame) && streamed.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(2);
            var slice = new float[1024 * 2];
            reader.PcmRead(slice);
            Check(streamed.Fault == null && Matches(slice, 0, reference, frame * 2, slice.Length, 0),
                "Streamed WAV at frame " + frame + " equals the full decode exactly");
        }
        streamed.Dispose();
        UnityEngine.Object.Destroy(reader);

        int warnings = 0;
        var state = new RadioState { TrackPath = wav, Powered = true, PositionSeconds = 0.1 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        PumpUntil(player, true, () => source.clip != null || player.LoadFailed);
        Check(source.clip != null && source.clip.Streaming && source.clip.samples == 3 * rate && source.clip.channels == 2,
            "WAV loads through the in-source decoder into a PCM-reader stream");
        PumpUntil(player, false, () => source.Schedules > 0 || player.LoadFailed);
        Check(source.Schedules == 1 && source.timeSamples == 4410 && warnings == 0, "Decoded WAV starts at its saved sample");
        player.Dispose();

        string longWav = WriteLongSilentWav("long-silent.wav");
        state = new RadioState { TrackPath = longWav, Powered = true, PositionSeconds = 3000 };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && source.clip.Streaming && source.clip.frequency == 8000 &&
            source.timeSamples == 3000 * 8000 && warnings == 0,
            "An hour-long WAV streams from its saved position");
        player.Dispose();
        DeleteClosed(wav);
        DeleteClosed(longWav);
    }

    static void CheckPreloadAdoption(string current)
    {
        string dir = AppContext.BaseDirectory;
        string longMp3 = Path.Combine(dir, "long-preload.mp3");
        WriteSilentMp3(longMp3, false, false, 29200);
        string shortWav = Path.Combine(dir, "short-preload.wav");
        WriteWav(shortWav, 1, 16, 2, 44100, 44100 * 3, (f, c) => .3 * Math.Sin(f * .05 + c), false, 0);
        string gated = WriteFake("gated-preload.fake", 2, 44100, 44100 * 2, gate: true);
        string shortOgg = Path.Combine(dir, "short-preload.ogg");
        File.Copy(Path.Combine(AppContext.BaseDirectory, "Fixtures", "sine-stereo.ogg"), shortOgg, true);
        // Real files open too quickly to catch mid-open, so a gated fake decoder covers that case.
        string[] targets = { longMp3, shortWav, shortOgg, gated };
        for (int index = 0; index < targets.Length; index++)
            {
                string target = targets[index];
                bool prefilled = target != gated;
                string label = Path.GetExtension(target) + (prefilled ? " prefilled" : " still opening");
                int warnings = 0, opens = FakeDecoder.OpensOf(target);
                var state = new RadioState { TrackPath = current, Powered = true };
                var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
                var source = AudioSource.Last;
                player.SetAcoustics(new Vector3(), true, 0);
                FakeDecoder.Hold.Reset();
                if (target == gated) FakeDecoder.Gate.Reset();
                player.Tick(new Vector3(), false);
                // The plugin names the upcoming track after each engine Tick.
                player.PreloadTrack(target);
                player.Tick(new Vector3(), false);
                Check(Field(player, "preloadStream") == null, "Upcoming " + label + " waits until the current track can play");
                Settle(player); player.Tick(new Vector3(), false);
                var currentClip = source.clip;
                var upcoming = (StreamedAudio)Field(player, "preloadStream");
                Check(upcoming != null && player.Status == "Playing" && source.clip == currentClip && !currentClip.Destroyed,
                    "Upcoming " + label + " opens its own stream beside the unchanged current track: status=" + player.Status +
                    " upcoming=" + (upcoming != null) + " skipped=" + Field(player, "preloadSkipped") + " started=" + Field(player, "preloadStarted"));
                if (prefilled)
                {
                    Check(WaitReady(upcoming, 0), "Upcoming " + label + " prefills its window from the start");
                    for (int i = 0; i < 5; i++) player.Tick(new Vector3(), false);
                    Check(!(bool)Field(player, "preloadSkipped") && Field(player, "preloadStream") == upcoming && upcoming.Ready(0),
                        "A prefilled upcoming stream stays prepared while the current track plays");
                }
                else Check(!upcoming.IsOpen, "Upcoming " + label + " is still opening at selection");
                int schedules = source.Schedules;
                player.SetTrack(target);
                Check(Field(player, "streamed") == upcoming && Field(player, "preloadStream") == null && currentClip.Destroyed,
                    "Selecting the upcoming " + label + " track hands its stream over without reopening it");
                if (prefilled)
                {
                    player.Tick(new Vector3(), false);
                    Check(player.Status == "Playing" && source.clip.Streaming && source.Schedules == schedules + 1 && source.timeSamples == 0,
                        "An adopted " + label + " stream schedules on the first Tick after selection");
                }
                else
                {
                    player.Tick(new Vector3(), false);
                    Check(player.Status == "Loading" && Field(player, "streamed") == upcoming, "An adopted stream still opening shows Loading");
                    FakeDecoder.Gate.Set();
                    PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
                }
                Check(!player.LoadFailed && warnings == 0 && source.clip.Streaming && source.clip.samples == upcoming.Frames &&
                    source.timeSamples == 0 && FakeDecoder.OpensOf(target) == opens + (target == gated ? 1 : 0) && Field(player, "streamed") == upcoming,
                    "Adopted " + label + " plays from its start with no second loader: status=" + player.Status + " warnings=" + warnings);
                player.Dispose();
                Check(WaitWorker(upcoming), "Disposal stops the adopted " + label + " stream worker");
            }
        FakeDecoder.Gate.Set();
        CheckPreloadLifecycle(current, shortWav, longMp3, gated);
        DeleteClosed(longMp3);
        DeleteClosed(shortWav);
        DeleteClosed(shortOgg);
        DeleteClosed(gated);
    }

    static void CheckPreloadLifecycle(string current, string shortWav, string longMp3, string gated)
    {
        string dir = AppContext.BaseDirectory;
        string corrupt = Path.Combine(dir, "corrupt-upcoming.mp3");
        File.WriteAllText(corrupt, "This is not an MPEG stream");
        string slow = WriteFake("slow-upcoming.fake", 2, 44100, 44100 * 60, readDelay: 400);
        int warnings = 0;
        string lastWarning = "";
        var state = new RadioState { TrackPath = shortWav, Powered = true };
        var player = new RadioPlayback(new MonoBehaviour(), state, w => { warnings++; lastWarning = w; });
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        var currentStream = (StreamedAudio)Field(player, "streamed");
        Check(currentStream != null && source.clip.Streaming && player.Status == "Playing", "A streamed current track plays before preloading");

        player.PreloadTrack(longMp3); player.Tick(new Vector3(), false);
        var upcoming = (StreamedAudio)Field(player, "preloadStream");
        Check(upcoming != null && WaitReady(upcoming, 0), "An upcoming track also preloads beside a streamed current track");
        int window = 20 * upcoming.SampleRate * upcoming.Channels + 4096 * upcoming.Channels;
        Check(currentStream.BufferSamples <= window && upcoming.BufferSamples <= window && player.Status == "Playing",
            "A current and an upcoming stream hold at most two bounded windows: current=" + currentStream.BufferSamples +
            " upcoming=" + upcoming.BufferSamples);

        // Superseding a preload whose worker is blocked inside a slow decoder read never waits for it. The
        // bounds here sit well under the scripted 400 ms read.
        int readsBefore = FakeDecoder.Reads;
        player.PreloadTrack(slow); player.Tick(new Vector3(), false);
        Check(WaitWorker(upcoming), "Replacing the upcoming track disposes its prefilled stream");
        var slowStream = (StreamedAudio)Field(player, "preloadStream");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (FakeDecoder.Reads == readsBefore && DateTime.UtcNow < deadline) Thread.Sleep(1);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        player.PreloadTrack(gated);
        long supersedeMs = watch.ElapsedMilliseconds;
        Check(slowStream.IsOpen && !slowStream.WorkerCompleted && supersedeMs < 150,
            "Superseding a preload during a slow decoder read returns without waiting: " + supersedeMs + " ms");
        Check(WaitWorker(slowStream) && slowStream.Fault == null, "The superseded slow stream worker exits after its read");

        // A speculative open that never finishes times out quietly. Selection then loads normally.
        FakeDecoder.Gate.Reset();
        player.Tick(new Vector3(), false);
        var stuck = (StreamedAudio)Field(player, "preloadStream");
        Check(stuck != null && !stuck.IsOpen, "A gated upcoming stream is still opening");
        float previousTime = Time.realtimeSinceStartup;
        Time.realtimeSinceStartup += 61;
        player.Tick(new Vector3(), false);
        Check((bool)Field(player, "preloadSkipped") && Field(player, "preloadStream") == null && WaitWorker(stuck) &&
            !player.LoadFailed && warnings == 0 && player.Status == "Playing",
            "An upcoming open timeout releases its stream without failing or warning over the current track");
        Time.realtimeSinceStartup = previousTime;
        FakeDecoder.Gate.Set();

        // A preload released while opening never revives when its open later completes.
        FakeDecoder.Gate.Reset();
        int live = FakeDecoder.Live;
        player.PreloadTrack(slow); player.Tick(new Vector3(), false);
        var released = (StreamedAudio)Field(player, "preloadStream");
        player.PreloadTrack(""); player.Tick(new Vector3(), false);
        FakeDecoder.Gate.Set();
        for (int i = 0; i < 20; i++) { player.Tick(new Vector3(), false); Thread.Sleep(1); }
        Check(WaitWorker(released) && !released.IsOpen && Field(player, "preloadStream") == null && FakeDecoder.Live == live &&
            player.Status == "Playing", "A cancelled preload that finishes opening later is never adopted or kept");

        // Failed speculation is silent. Selecting the failed track retries through the normal loader.
        player.PreloadTrack(corrupt);
        PumpUntil(player, false, () => (bool)Field(player, "preloadSkipped"));
        Check(!player.LoadFailed && warnings == 0 && player.Status == "Playing" && Field(player, "preloadStream") == null,
            "A failed upcoming decode cannot interrupt or warn over the current track");
        player.SetTrack(corrupt);
        Check(Field(player, "streamed") == null, "A skipped preload is never adopted");
        PumpUntil(player, false, () => player.LoadFailed);
        Check(player.Status == "Unable to decode music" && warnings == 1 && player.FailedPath == corrupt,
            "Selecting the failed track reports its real decoder failure once: " + lastWarning);

        // Power and failure gates.
        state.TrackPath = shortWav; state.PositionSeconds = 0;
        player.SetTrack(shortWav);
        player.Tick(new Vector3(), false);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        state.Powered = false; player.Tick(new Vector3(), false);
        player.PreloadTrack(longMp3); player.Tick(new Vector3(), false);
        Check(Field(player, "preloadStream") == null, "An unpowered radio never opens an upcoming stream");
        state.Powered = true;
        PumpUntil(player, false, () => Field(player, "preloadStream") != null);
        upcoming = (StreamedAudio)Field(player, "preloadStream");
        Check(player.Status == "Playing" && upcoming != null, "Power restored with a ready current track resumes preloading");

        // Rapid skipping. Each selection releases the previous stream without blocking.
        var abandoned = new System.Collections.Generic.List<StreamedAudio>();
        long slowestSkipMs = 0;
        foreach (string next in new[] { longMp3, shortWav, slow, longMp3, shortWav, slow, shortWav })
        {
            var before = (StreamedAudio)Field(player, "streamed");
            if (before != null) abandoned.Add(before);
            state.PositionSeconds = 0;
            watch.Restart();
            player.SetTrack(next);
            player.Tick(new Vector3(), false);
            slowestSkipMs = Math.Max(slowestSkipMs, watch.ElapsedMilliseconds);
        }
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(abandoned.TrueForAll(WaitWorker) && !player.LoadFailed && warnings == 1 && slowestSkipMs < 150,
            "Rapid skipping releases every abandoned stream without waiting and lands on the last track: slowest selection " + slowestSkipMs + " ms");

        var finalUpcoming = (StreamedAudio)null;
        player.PreloadTrack(longMp3);
        PumpUntil(player, false, () => (finalUpcoming = (StreamedAudio)Field(player, "preloadStream")) != null);
        var finalCurrent = (StreamedAudio)Field(player, "streamed");
        player.Dispose();
        Check(WaitWorker(finalUpcoming) && WaitWorker(finalCurrent) && AudioSettings.Subscribers == 0,
            "Shutdown releases the current and the upcoming stream");
        DeleteClosed(corrupt);
        DeleteClosed(slow);
    }

    static object Field(RadioPlayback player, string name)
    {
        return typeof(RadioPlayback).GetField(name, System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic)
            .GetValue(player);
    }

    static float[] ReadAll(IPcmDecoder decoder)
    {
        decoder.Seek(0);
        var all = new float[checked((int)(decoder.Frames * decoder.Channels))];
        int read = ReadInto(decoder, all);
        if (read != all.Length || decoder.Read(new float[decoder.Channels * 16], 0, decoder.Channels * 16) != 0)
            throw new Exception("Decoder length does not match its PCM");
        return all;
    }

    static int ReadInto(IPcmDecoder decoder, float[] buffer)
    {
        int read = 0, got;
        while (read < buffer.Length && (got = decoder.Read(buffer, read, Math.Min(4000, buffer.Length - read))) > 0) read += got;
        return read;
    }

    static bool SeekMatches(IPcmDecoder decoder, float[] all)
    {
        int channels = decoder.Channels;
        foreach (long target in new[] { decoder.Frames / 2, 1, decoder.Frames - 5, 777, 0, decoder.Frames })
        {
            decoder.Seek(target);
            var slice = new float[600 * channels];
            int read = ReadInto(decoder, slice);
            if (decoder.Position != target + read / channels || read != Math.Min(600, decoder.Frames - target) * channels ||
                !Matches(slice, 0, all, (int)target * channels, read, 0)) return false;
        }
        return true;
    }

    // Decodes path whole through the routed opener, then streams it from a quarter second and compares a slice
    // at each frame with that decode. Inspect sees the open decoder and its full decode. Returns the decode.
    static float[] CheckRoutedStream(string path, string label, Action<IPcmDecoder, float[]> inspect, int sliceFrames, float tolerance,
        Func<int, int[]> framesOf)
    {
        float[] reference;
        using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
        {
            reference = ReadAll(decoder);
            if (inspect != null) inspect(decoder, reference);
        }
        var streamed = OpenStream(path, .25);
        int channels = streamed.Channels, frames = streamed.Frames;
        Check(frames * channels == reference.Length && streamed.BufferSamples == (frames + 4095) / 4096 * 4096 * channels,
            "Short " + label + " streams through the shared engine with the whole track in its ring");
        var clip = streamed.CreateClip(streamed.CreateReader());
        foreach (int frame in framesOf(frames))
        {
            clip.PcmSetPosition(frame);
            streamed.Request(frame);
            WaitReady(streamed, frame);
            var slice = new float[sliceFrames * channels];
            clip.PcmRead(slice);
            int valid = Math.Min(sliceFrames, frames - frame) * channels;
            Check(valid > 0 && streamed.Fault == null && Matches(slice, 0, reference, frame * channels, valid, tolerance),
                "Streamed " + label + " at frame " + frame + " matches the full decode" + (streamed.Fault != null ? ": " + streamed.Fault.Message : ""));
        }
        streamed.Dispose();
        UnityEngine.Object.Destroy(clip);
        return reference;
    }

    static bool Matches(float[] a, int aAt, float[] b, int bAt, int count, float tolerance)
    {
        for (int i = 0; i < count; i++) if (Math.Abs(a[aAt + i] - b[bAt + i]) > tolerance) return false;
        return true;
    }

    static bool MatchesTone(float[] all, int channels, Func<int, int, double> tone, double tolerance)
    {
        for (int i = 0; i < all.Length; i++)
            if (Math.Abs(all[i] - tone(i / channels, i % channels)) > tolerance) return false;
        return true;
    }

    static double Q16(double value) { return Quantize(value, 16) / 32768.0; }

    // Two's complement full scale, matching the decoder's power-of-two divisors.
    static long Quantize(double value, int bits)
    {
        long scale = 1L << (bits - 1);
        return Math.Max(-scale, Math.Min(scale - 1, (long)Math.Round(value * scale)));
    }

    static bool Rejects(string path, string text)
    {
        try { PcmDecoders.Open(path, CancellationToken.None).Dispose(); return false; }
        catch (IOException ex)
        {
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return text == null || ex.Message.Contains(text);
        }
    }

    // Original synthetic PCM written directly, never recorded or copied audio.
    static void WriteWav(string path, int tag, int bits, int channels, int rate, int frames, Func<int, int, double> signal,
        bool extensible, int mask, bool oddChunks = false, bool dataFirst = false, int truncateBytes = 0,
        long dataSizeOverride = -1, string riff = "RIFF")
    {
        int bytes = Math.Max(1, bits / 8), blockAlign = channels * bytes;
        var fmt = new MemoryStream();
        var f = new BinaryWriter(fmt);
        f.Write((ushort)(extensible ? 0xFFFE : tag)); f.Write((ushort)channels); f.Write(rate);
        f.Write(rate * blockAlign); f.Write((ushort)blockAlign); f.Write((ushort)bits);
        if (extensible)
        {
            f.Write((ushort)22); f.Write((ushort)bits); f.Write(mask);
            f.Write((ushort)tag); f.Write(new byte[] { 0, 0, 0, 0, 0x10, 0, 0x80, 0, 0, 0xAA, 0, 0x38, 0x9B, 0x71 });
        }
        else if (tag != 1) f.Write((ushort)0);
        var data = new MemoryStream();
        var d = new BinaryWriter(data);
        for (int i = 0; i < frames; i++)
            for (int c = 0; c < channels; c++)
            {
                double v = Math.Max(-1, Math.Min(1, signal(i, c)));
                if (tag == 3) d.Write((float)v);
                else if (bits == 8) d.Write((byte)(Quantize(v, 8) + 128));
                else if (bits == 16 || bits == 12 || bits == 4) d.Write((short)Quantize(v, 16));
                else if (bits == 24) { int s = (int)Quantize(v, 24); d.Write((byte)s); d.Write((byte)(s >> 8)); d.Write((byte)(s >> 16)); }
                else d.Write((int)Quantize(v, 32));
            }
        var file = new MemoryStream();
        var w = new BinaryWriter(file);
        w.Write(System.Text.Encoding.ASCII.GetBytes(riff)); w.Write(0); w.Write(System.Text.Encoding.ASCII.GetBytes("WAVE"));
        Action<string, byte[], long> chunk = (id, body, size) =>
        {
            w.Write(System.Text.Encoding.ASCII.GetBytes(id)); w.Write((uint)size); w.Write(body);
            if ((body.Length & 1) != 0) w.Write((byte)0);
        };
        if (oddChunks) chunk("LIST", new byte[] { 1, 2, 3 }, 3);
        if (dataFirst) chunk("data", data.ToArray(), data.Length);
        chunk("fmt ", fmt.ToArray(), fmt.Length);
        if (oddChunks) chunk("junk", new byte[] { 9 }, 1);
        if (!dataFirst) chunk("data", data.ToArray(), dataSizeOverride >= 0 ? dataSizeOverride : data.Length);
        byte[] result = file.ToArray();
        BitConverter.GetBytes(result.Length - 8).CopyTo(result, 4);
        File.WriteAllBytes(path, Prefix(result, result.Length - truncateBytes));
    }

    // 8-bit mono silence lasting an hour. Only its length matters to the streaming checks.
    static string WriteLongSilentWav(string name)
    {
        string path = Path.Combine(AppContext.BaseDirectory, name);
        long frames = 3600L * 8000;
        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var w = new BinaryWriter(output);
            w.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); w.Write((uint)(36 + frames));
            w.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); w.Write(16);
            w.Write((short)1); w.Write((short)1); w.Write(8000); w.Write(8000); w.Write((short)1); w.Write((short)8);
            w.Write(System.Text.Encoding.ASCII.GetBytes("data")); w.Write((uint)frames);
            var silence = new byte[1 << 20];
            for (int i = 0; i < silence.Length; i++) silence[i] = 128;
            for (long left = frames; left > 0; left -= silence.Length) w.Write(silence, 0, (int)Math.Min(left, silence.Length));
        }
        return path;
    }

    static byte[] Prefix(byte[] bytes, int count)
    {
        var result = new byte[count];
        Array.Copy(bytes, result, count);
        return result;
    }

    static float DecodedSample(Pcm decoded, int index) { return decoded.Samples[index]; }


    sealed class Pcm
    {
        internal int Channels, SampleRate;
        internal float[] Samples;
        internal int SampleCount { get { return Samples.Length; } }
    }

    // Test reference only. Production never decodes a whole file.
    static Pcm Decode(string path, CancellationToken token)
    {
        using (var decoder = PcmDecoders.Open(path, token))
            return new Pcm { Channels = decoder.Channels, SampleRate = decoder.SampleRate, Samples = ReadAll(decoder) };
    }

    static StreamedAudio OpenStream(string path, double seconds)
    {
        var stream = StreamedAudio.Start(path, seconds);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!stream.IsOpen && stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(1);
        if (!stream.IsOpen) { stream.Dispose(); throw new Exception("Stream did not open: " + path + " " + stream.Fault); }
        return stream;
    }

    // Empty when the stream opened, otherwise its fault message.
    static string StreamFault(string path)
    {
        var stream = StreamedAudio.Start(path, 0);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!stream.IsOpen && stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(1);
        stream.Dispose();
        WaitWorker(stream);
        return stream.Fault == null ? "" : stream.Fault.Message;
    }

    static bool WaitReady(StreamedAudio stream, int frame)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!stream.Ready(frame) && stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(1);
        return stream.Fault == null && stream.Ready(frame);
    }

    static bool WaitWorker(StreamedAudio stream)
    {
        if (stream == null) return false;
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!stream.WorkerCompleted && DateTime.UtcNow < deadline) Thread.Sleep(1);
        return stream.WorkerCompleted;
    }

    // Stream workers release their files shortly after disposal, never during it.
    static void DeleteClosed(string path)
    {
        if (File.Exists(path) && !FileClosed(path)) throw new Exception("File still open after disposal: " + path);
        File.Delete(path);
    }

    static bool FileClosed(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            try { using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) return true; }
            catch (IOException) { Thread.Sleep(2); }
        }
        return false;
    }

    static void PumpUntil(RadioPlayback player, bool suspended, Func<bool> done)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (!done() && DateTime.UtcNow < deadline) { player.Tick(new Vector3(), suspended); Thread.Sleep(1); }
        if (!done()) throw new Exception("Playback did not reach the awaited state within 5 seconds");
    }

    // Original synthesized MPEG-1 Layer III frames. Zero side information means zero
    // spectral data, giving valid silent audio without copyrighted recordings or encoders.
    static void WriteSilentMp3(string path, bool mono, bool variableBitrate, int frameCount = 20)
    {
        using (var output = File.Create(path))
            for (int frame=0; frame<frameCount; frame++)
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
