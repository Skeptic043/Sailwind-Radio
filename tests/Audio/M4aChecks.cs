using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using SailwindRadio;
using SailwindRadio.Audio;
using UnityEngine;

// M4A and ADTS AAC through the Media Foundation decoder. Every fixture is encoded by ffmpeg from
// an original synthetic WAV written here. Three small ones are committed so the core checks run
// without ffmpeg. The rest are generated into .local/m4a and skipped when ffmpeg is absent.
// Media Foundation checks skip, and say so, when the start-up decision disables the formats.
static partial class AudioChecks
{
    // Bump when a fixture signal or encoder argument changes so scratch copies regenerate.
    const string M4aScratchVersion = "v2";
    static string ffmpeg;
    static readonly List<string> skippedChecks = new List<string>();

    static void SkipCheck(string what) { skippedChecks.Add(what); Console.WriteLine("SKIP " + what); }

    // RADIO_CHECKS_FFMPEG, then ffmpeg on PATH. A set path that does not exist skips rather than falling back.
    static string FindFfmpeg()
    {
        string chosen = Environment.GetEnvironmentVariable("RADIO_CHECKS_FFMPEG");
        if (!String.IsNullOrEmpty(chosen)) return File.Exists(chosen) ? chosen : null;
        foreach (string dir in (Environment.GetEnvironmentVariable("PATH") ?? "").Split(Path.PathSeparator))
            foreach (string name in new[] { "ffmpeg.exe", "ffmpeg" })
            {
                try { string candidate = Path.Combine(dir.Trim().Trim('"'), name); if (File.Exists(candidate)) return candidate; }
                catch (ArgumentException) { }
            }
        return null;
    }

    // A repeating chirp, so the encoder delay has one clear alignment against the source.
    static Func<int, int, double> M4aTone(int rate)
    {
        return (f, c) =>
        {
            double t = (f % (4 * rate)) / (double)rate;
            return .45 * Math.Sin(2 * Math.PI * ((300 + 50 * c) * t + 200 * t * t));
        };
    }

    static string M4aFixture(string name, int channels, int rate, int bits, double seconds, string codec)
    {
        string committed = Path.Combine(RepoRoot, "tests", "Audio", "Fixtures", name);
        if (File.Exists(committed)) return committed;
        string dir = Path.Combine(RepoRoot, ".local", "m4a", M4aScratchVersion);
        string path = Path.Combine(dir, name);
        if (File.Exists(path)) return path;
        if (ffmpeg == null) return null;
        Directory.CreateDirectory(dir);
        string wav = path + ".source.wav";
        WriteWav(wav, 1, bits, channels, rate, (int)(rate * seconds), M4aTone(rate), false, 0);
        var info = new ProcessStartInfo(ffmpeg, "-hide_banner -loglevel error -y -i \"" + wav + "\" " + codec + " \"" + path + "\"")
        { UseShellExecute = false, RedirectStandardError = true, CreateNoWindow = true };
        using (var process = Process.Start(info))
        {
            string error = process.StandardError.ReadToEnd();
            process.WaitForExit();
            DeleteClosed(wav);
            if (process.ExitCode != 0)
            {
                if (File.Exists(path)) DeleteClosed(path);
                SkipCheck(name + " fixture (ffmpeg could not encode it: " + error.Trim().Split('\n')[0] + ")");
                return null;
            }
        }
        return path;
    }

    static void CheckM4a()
    {
        ffmpeg = FindFfmpeg();
        if (ffmpeg == null) Console.WriteLine("ffmpeg not found. Generated M4A fixtures are skipped.");
        CheckMediaFoundationDecision();
        MediaFoundationSupport.Detect();
        Console.WriteLine(MediaFoundationSupport.Summary + " on " +
            (Type.GetType("Mono.Runtime") != null ? "Mono" : ".NET " + Environment.Version) + (Environment.Is64BitProcess ? " x64" : " x86"));
        if (!MediaFoundationSupport.Enabled)
        {
            SkipCheck("Media Foundation M4A and AAC decoding (" + MediaFoundationSupport.Reason + ")");
            return;
        }
        string alac16 = M4aFixture("alac16-stereo.m4a", 2, 44100, 16, 1, "-c:a alac -map_metadata -1 -fflags +bitexact");
        string aac = M4aFixture("aac-stereo.m4a", 2, 44100, 16, 2, "-c:a aac -b:a 128k -map_metadata -1 -fflags +bitexact -flags:a +bitexact");
        CheckAlac(alac16, 16, 2, 44100, 1);
        CheckAlac(M4aFixture("alac24-stereo.m4a", 2, 44100, 24, 1, "-c:a alac -map_metadata -1"), 24, 2, 44100, 1);
        CheckAac(aac, "AAC-LC stereo M4A", 2, 44100, 88200, true);
        CheckAac(M4aFixture("aac-mono.m4a", 1, 44100, 16, 2, "-c:a aac -b:a 96k -map_metadata -1"), "AAC-LC mono M4A", 1, 44100, 88200, true);
        CheckAac(M4aFixture("aac-48k.m4a", 2, 48000, 16, 2, "-c:a aac -b:a 128k -map_metadata -1"), "AAC-LC 48 kHz M4A", 2, 48000, 96000, true);
        CheckAac(M4aFixture("aac-stereo.aac", 2, 44100, 16, 2, "-c:a aac -b:a 128k -f adts -map_metadata -1"), "ADTS AAC", 2, 44100, 88200, false);
        CheckM4aFailures(aac);
        CheckM4aStreaming(alac16, aac);
        CheckM4aPlayback(alac16);
        CheckM4aExtras();
        if (skippedChecks.Count > 0) Console.WriteLine("M4A checks skipped " + skippedChecks.Count + " item(s).");
    }

    static void CheckMediaFoundationDecision()
    {
        bool aacProbed = false;
        string why;
        Check(!MediaFoundationSupport.Decide(() => true, () => { aacProbed = true; return true; }, out why) &&
            why == "Proton or Wine detected" && !aacProbed, "Wine or Proton disables M4A without calling Media Foundation");
        Check(!MediaFoundationSupport.Decide(() => false, () => false, out why) && why == "Media Foundation is unavailable on this system",
            "A missing AAC decoder disables M4A with the unavailable reason");
        Check(!MediaFoundationSupport.Decide(() => { throw new DllNotFoundException("ntdll"); }, () => true, out why) &&
            why == "Media Foundation is unavailable on this system", "A failing Wine probe disables M4A");
        Check(!MediaFoundationSupport.Decide(() => false, () => { throw new EntryPointNotFoundException("MFStartup"); }, out why),
            "A failing Media Foundation probe disables M4A");
        Check(MediaFoundationSupport.Decide(() => false, () => true, out why) && why == "", "Windows with an AAC decoder enables M4A");

        MediaFoundationSupport.Detect(() => true, () => true);
        Check(!MediaFoundationSupport.Enabled && MediaFoundationSupport.Summary == "M4A and AAC playback is disabled. Proton or Wine detected" &&
            MediaFoundationSupport.Summary.IndexOf(';') < 0, "Disabled summary is one plain log line: " + MediaFoundationSupport.Summary);
        Check(!PcmDecoders.Supports("song.m4a") && !PcmDecoders.Supports("song.AAC") && PcmDecoders.Supports("song.flac") &&
            PcmDecoders.ChooseFormatMessage == "Choose an MP3, OGG, WAV or FLAC file", "Disabled M4A and AAC are not decodable formats");
        string disabled = Path.Combine(AppContext.BaseDirectory, "disabled.m4a");
        File.WriteAllBytes(disabled, new byte[] { 0 });
        string message = "";
        try { PcmDecoders.Open(disabled, CancellationToken.None).Dispose(); }
        catch (IOException ex) { message = ex.Message; }
        Check(message == "M4A and AAC playback is off. Proton or Wine detected", "Disabled M4A open explains why: " + message);
        string warning = "";
        var player = new RadioPlayback(new MonoBehaviour(), new RadioState { TrackPath = disabled, Powered = true }, w => warning = w);
        player.Tick(new Vector3(), false);
        Check(player.LoadFailed && warning.Contains("Proton or Wine detected") && Field(player, "streamed") == null,
            "A saved M4A selection under Proton fails clearly without starting a stream: " + warning);
        player.Dispose();
        DeleteClosed(disabled);
        MediaFoundationSupport.Detect(() => false, () => true);
        Check(MediaFoundationSupport.Enabled && PcmDecoders.Supports("song.m4a") && PcmDecoders.Supports("song.aac") &&
            PcmDecoders.ChooseFormatMessage == "Choose an MP3, OGG, WAV, FLAC, M4A or AAC file" &&
            MediaFoundationSupport.Summary == "M4A and AAC playback is enabled through Media Foundation", "Enabled M4A and AAC are decodable formats");
    }

    static void CheckAlac(string path, int bits, int channels, int rate, double seconds)
    {
        string label = "ALAC " + bits + "-bit";
        if (path == null) { SkipCheck(label + " checks (fixture unavailable)"); return; }
        var tone = M4aTone(rate);
        double scale = bits == 16 ? 32768.0 : 8388608.0;
        using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
        {
            float[] all = ReadAll(decoder);
            Check(decoder is MediaFoundationDecoder && decoder.Channels == channels && decoder.SampleRate == rate &&
                decoder.Frames == (long)(rate * seconds), label + " reports its exact layout and length");
            bool exact = true;
            for (int i = 0; i < all.Length && exact; i++)
                exact = all[i] == (float)(Quantize(Math.Max(-1, Math.Min(1, tone(i / channels, i % channels))), bits) / scale);
            Check(exact, label + " decodes bit-exactly to the source PCM");
            Check(SeekMatches(decoder, all), label + " seek plus read equals the full decode exactly");
        }
    }

    static void CheckAac(string path, string label, int channels, int rate, long sourceFrames, bool mp4)
    {
        if (path == null) { SkipCheck(label + " checks (fixture unavailable)"); return; }
        using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
        {
            float[] all = ReadAll(decoder);
            // MP4 length comes from the track duration. ADTS length counts whole 1024-sample packets.
            Check(decoder.Channels == channels && decoder.SampleRate == rate &&
                (mp4 ? decoder.Frames == sourceFrames : decoder.Frames % 1024 == 0 && decoder.Frames >= sourceFrames && decoder.Frames < sourceFrames + 4096),
                label + " layout and length: frames=" + decoder.Frames);
            int lag;
            double error = BestLagError(all, channels, M4aTone(rate), out lag);
            Console.WriteLine(label + ": lossless-reference RMS error " + error.ToString("0.00000") + " at a lag of " + lag + " frames");
            Check(error < .02, label + " decodes close to the source chirp");
            float tolerance = mp4 ? 1e-4f : 0f;
            Check(SeekMatchesWithin(decoder, all, tolerance), label + " seek plus read matches the full decode within " + tolerance);
        }
    }

    // Lowest RMS difference against the source over a middle window, searching the encoder delay.
    static double BestLagError(float[] all, int channels, Func<int, int, double> tone, out int lag)
    {
        int frames = all.Length / channels, start = 4096, window = Math.Min(8192, frames - start - 4096);
        double best = Double.MaxValue;
        lag = -1;
        for (int candidate = 0; candidate <= 3000; candidate++)
        {
            double sum = 0;
            for (int f = 0; f < window; f += 3)
                for (int c = 0; c < channels; c++)
                {
                    double d = all[(start + candidate + f) * channels + c] - tone(start + f, c);
                    sum += d * d;
                }
            if (sum < best) { best = sum; lag = candidate; }
        }
        return Math.Sqrt(best / ((window + 2) / 3 * channels));
    }

    static bool SeekMatchesWithin(IPcmDecoder decoder, float[] all, float tolerance)
    {
        int channels = decoder.Channels;
        foreach (long target in new[] { decoder.Frames / 2, 1, decoder.Frames - 5, 20000, 777, decoder.Frames / 3, 0, decoder.Frames })
        {
            decoder.Seek(target);
            var slice = new float[3000 * channels];
            int read = ReadInto(decoder, slice);
            if (decoder.Position != target + read / channels || read != Math.Min(3000, decoder.Frames - target) * channels) return false;
            if (!Matches(slice, 0, all, (int)target * channels, read, tolerance))
            {
                double worst = 0;
                for (int i = 0; i < read; i++) worst = Math.Max(worst, Math.Abs(slice[i] - all[target * channels + i]));
                Console.WriteLine("Seek to " + target + " differs by up to " + worst);
                return false;
            }
        }
        return true;
    }

    static void CheckM4aFailures(string aac)
    {
        string dir = AppContext.BaseDirectory;
        string garbage = Path.Combine(dir, "garbage.m4a");
        var random = new Random(3);
        var noise = new byte[4096];
        random.NextBytes(noise);
        File.WriteAllBytes(garbage, noise);
        Check(RejectsPlainly(garbage, "not a readable M4A"), "Random bytes named M4A are rejected per file");
        string notAdts = Path.Combine(dir, "not-adts.aac");
        File.WriteAllBytes(notAdts, noise);
        Check(RejectsPlainly(notAdts, "not an ADTS AAC stream"), "A non-ADTS AAC file is rejected before Media Foundation opens it");
        DeleteClosed(garbage);
        DeleteClosed(notAdts);
        if (aac == null) return;
        // ffmpeg writes moov last by default, so a cut file loses its sample tables.
        string truncated = Path.Combine(dir, "truncated.m4a");
        byte[] bytes = File.ReadAllBytes(aac);
        File.WriteAllBytes(truncated, Prefix(bytes, bytes.Length * 3 / 5));
        Check(RejectsPlainly(truncated, null), "A truncated M4A fails per file with a plain message");
        DeleteClosed(truncated);
        string faststart = M4aFixture("aac-faststart.m4a", 2, 44100, 16, 2, "-c:a aac -b:a 128k -movflags +faststart -map_metadata -1");
        if (faststart == null) { SkipCheck("truncated faststart M4A (fixture unavailable)"); return; }
        bytes = File.ReadAllBytes(faststart);
        File.WriteAllBytes(truncated, Prefix(bytes, bytes.Length * 3 / 5));
        string outcome;
        try
        {
            using (var decoder = PcmDecoders.Open(truncated, CancellationToken.None))
            {
                float[] all = ReadAll(decoder);
                outcome = "decoded " + all.Length / decoder.Channels + " of " + decoder.Frames + " frames with silent padding";
            }
        }
        catch (IOException ex)
        {
            Check(ex.Message.IndexOf(';') < 0, "Truncated M4A error is plain text");
            outcome = "failed with: " + ex.Message;
        }
        Console.WriteLine("Faststart M4A cut to 60% " + outcome);
        Check(FileClosed(truncated), "A faststart M4A cut mid-data decodes or fails plainly and releases the file");
        DeleteClosed(truncated);
    }

    static bool RejectsPlainly(string path, string text)
    {
        try { PcmDecoders.Open(path, CancellationToken.None).Dispose(); return false; }
        catch (IOException ex)
        {
            Console.WriteLine(Path.GetFileName(path) + " rejected: " + ex.Message);
            using (File.Open(path, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) { }
            return ex.Message.IndexOf(';') < 0 && (text == null || ex.Message.Contains(text));
        }
    }

    static void CheckM4aStreaming(string alac, string aac)
    {
        foreach (string path in new[] { alac, aac })
        {
            if (path == null) continue;
            bool lossless = path == alac;
            CheckRoutedStream(path, lossless ? "ALAC" : "AAC", null, 1024, lossless ? 0 : 1e-4f, length => new[] { 11025, 40000, 5, length - 900, 20000 });
        }
        if (alac == null) return;
        // Every call joins the multithreaded apartment, so the thread that opened a decoder does not matter.
        float[] expected;
        using (var decoder = PcmDecoders.Open(alac, CancellationToken.None)) expected = ReadAll(decoder);
        IPcmDecoder opened = Task.Run(() => PcmDecoders.Open(alac, CancellationToken.None)).Result;
        float[] crossThread = Task.Run(() => { try { return ReadAll(opened); } finally { opened.Dispose(); } }).Result;
        Check(Matches(crossThread, 0, expected, 0, expected.Length, 0) && crossThread.Length == expected.Length,
            "A decoder opened on one worker thread reads identically on another");
        using (var cancellation = new CancellationTokenSource())
        {
            var decoder = PcmDecoders.Open(alac, cancellation.Token);
            var buffer = new float[4096];
            decoder.Read(buffer, 0, buffer.Length);
            cancellation.Cancel();
            bool canceled = false;
            try { decoder.Read(buffer, 0, buffer.Length); }
            catch (OperationCanceledException) { canceled = true; }
            decoder.Dispose();
            decoder.Dispose();
            Check(canceled, "M4A decoding honors its cancellation token and disposes idempotently");
        }
    }

    static void CheckM4aPlayback(string alac)
    {
        if (alac == null) return;
        string dir = AppContext.BaseDirectory;
        string named = Path.Combine(dir, "lossless # 100%.m4a");
        File.Copy(alac, named, true);
        int warnings = 0;
        var state = new RadioState { TrackPath = named, Powered = true, PositionSeconds = .5 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && source.clip.Streaming && source.clip.samples == 44100 &&
            source.timeSamples == 22050 && warnings == 0, "M4A with URI-like characters plays from its saved sample through a PCM-reader stream");
        player.Dispose();
        DeleteClosed(named);

        string longAac = M4aFixture("aac-long.m4a", 2, 44100, 16, 800, "-c:a aac -b:a 24k -map_metadata -1");
        if (longAac == null) { SkipCheck("long streamed M4A playback (fixture unavailable)"); return; }
        state = new RadioState { TrackPath = longAac, Powered = true, PositionSeconds = 700 };
        player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        var deadline = DateTime.UtcNow.AddSeconds(30);
        while (player.Status != "Playing" && !player.LoadFailed && DateTime.UtcNow < deadline) { player.Tick(new Vector3(), false); Thread.Sleep(2); }
        Check(!player.LoadFailed && player.Status == "Playing" && source.clip.Streaming && source.clip.frequency == 44100 &&
            source.clip.samples == 800 * 44100 && source.timeSamples == 700 * 44100 && warnings == 0,
            "A long M4A streams from its saved position");
        player.Dispose();
    }

    static void CheckM4aExtras()
    {
        string surround = M4aFixture("aac-51.m4a", 6, 48000, 16, 1, "-c:a aac -b:a 256k -map_metadata -1");
        if (surround == null) SkipCheck("5.1 AAC downmix (fixture unavailable)");
        else
            using (var decoder = PcmDecoders.Open(surround, CancellationToken.None))
            {
                float[] all = ReadAll(decoder);
                double energy = 0;
                foreach (float value in all) energy += value * value;
                Check(decoder.Channels == 2 && decoder.SampleRate == 48000 && energy > 100 && Array.TrueForAll(all, v => Math.Abs(v) <= 1.01f),
                    "5.1 AAC downmixes to bounded stereo");
            }
        string fast = M4aFixture("alac-384k.m4a", 1, 384000, 16, .25, "-c:a alac -map_metadata -1");
        if (fast == null) { SkipCheck("ALAC above the playable rate (fixture unavailable)"); return; }
        string message = "";
        try { PcmDecoders.Open(fast, CancellationToken.None).Dispose(); }
        catch (IOException ex) { message = ex.Message; }
        Check(message.Contains("playable range") || message.Contains("unavailable") || message.Contains("could not"),
            "ALAC above the playable ceiling is refused: " + message);
        Console.WriteLine("384 kHz ALAC outcome: " + message);
    }
}
