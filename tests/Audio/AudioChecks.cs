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
    static void Main()
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
        AudioSettings.dspTime = 101;
        source.timeSamples = 144000;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 3, "Position follows source samples");
        state.Paused = true;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 3 && player.Status == "Paused", "Manual pause captures position before Stop resets source");
        player.Tick(new Vector3(), true);
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 1, "Suspend and resume never undo manual pause");
        state.Paused = false;
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 144000 && source.Schedules == 2, "Manual resume restores exact sample");
        AudioSettings.dspTime = 102;
        source.timeSamples = 192000;
        state.Powered = false;
        player.Tick(new Vector3(), false);
        Check(state.PositionSeconds == 4 && source.clip == firstClip && player.Status == "Off", "Power off preserves cursor and bounded loaded clip");
        state.Powered = true;
        AudioListener.pause = true;
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 2, "Global listener pause prevents scheduling");
        AudioListener.pause = false;
        player.Tick(new Vector3(), false);
        Check(source.Schedules == 3 && source.timeSamples == 192000, "Power resume restores position");
        AudioSettings.dspTime = 103;
        source.timeSamples = 240000;
        player.Tick(new Vector3(), false);
        AudioSettings.Reset(); source.timeSamples = 0;
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 240000 && source.Schedules == 4, "Audio device reset uses saved position rather than reset cursor");
        AudioSettings.dspTime = 0;
        source.timeSamples = 0;
        player.Tick(new Vector3(), false);
        Check(source.timeSamples == 240000 && source.Schedules == 5, "DSP clock rollback reschedules from prior position");
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
        Check(pending.Aborted && pending.Disposed && AudioSettings.Subscribers == 0, "Missing emitter in Tick disposes pending load and event subscription");
        CheckMp3(path);
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
