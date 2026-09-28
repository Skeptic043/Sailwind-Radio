using System;
using System.IO;
using System.Threading;
using SailwindRadio;
using UnityEngine;

// FLAC through the production routing, preload and streaming paths. The decoder's
// own bitstream checks live in tests/Flac.
static partial class AudioChecks
{
    static string repoRoot;
    static string RepoRoot
    {
        get
        {
            if (repoRoot != null) return repoRoot;
            string dir = AppContext.BaseDirectory;
            while (dir != null && !File.Exists(Path.Combine(dir, "Radio.csproj")))
                dir = Path.GetDirectoryName(dir.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
            if (dir == null) throw new Exception("Cannot locate the Radio repository root");
            return repoRoot = dir;
        }
    }

    static void CheckFlacRouting()
    {
        string fixture = Path.Combine(RepoRoot, "tests", "Flac", "Fixtures", "stereo16-l0.flac");
        string dir = AppContext.BaseDirectory;
        string flac = Path.Combine(dir, "routing # 100%.flac");
        File.Copy(fixture, flac, true);
        Check(PcmDecoders.Supports(flac) && PcmDecoders.Supports("UPPER.FLAC"), "FLAC routes to the in-source decoder regardless of case");
        int channels = 0, rate = 0;
        float[] reference = CheckRoutedStream(flac, "FLAC", (decoder, all) =>
        {
            channels = decoder.Channels;
            rate = decoder.SampleRate;
            Check(decoder is FlacDecoder && channels == 2 && all.Length > 8192 && Array.Exists(all, v => v != 0),
                "FLAC opens over a cancellable file stream and decodes nonzero PCM");
            Check(SeekMatches(decoder, all), "FLAC exact seek equals the full decode through the routed opener");
        }, 512, 0, length => new[] { length / 2, 3, length - 700, length / 3 });
        long frames = reference.Length / channels;

        // STREAMINFO rate patched above the probed ceiling. The routing check runs before any frame decodes.
        string fast = Path.Combine(dir, "rate-384k.flac");
        byte[] bytes = File.ReadAllBytes(Path.Combine(RepoRoot, "tests", "Flac", "Fixtures", "mono16-rate96000.flac"));
        int info = 8; // "fLaC" plus the STREAMINFO block header.
        int patched = 384000;
        bytes[info + 10] = (byte)(patched >> 12);
        bytes[info + 11] = (byte)(patched >> 4);
        bytes[info + 12] = (byte)((patched & 0xF) << 4 | (bytes[info + 12] & 0xF));
        File.WriteAllBytes(fast, bytes);
        string rejection = StreamFault(fast);
        Check(rejection.Contains("playable range"), "The playable sample-rate ceiling applies to FLAC: " + rejection);
        DeleteClosed(fast);

        int warnings = 0;
        var state = new RadioState { TrackPath = flac, Powered = true, PositionSeconds = 0.1 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && source.clip.Streaming &&
            source.clip.samples == frames && source.clip.channels == channels && source.timeSamples == rate / 10 && warnings == 0,
            "FLAC plays from its saved sample through a PCM-reader stream: status=" + player.Status + " samples=" + (source.clip == null ? -1 : source.clip.samples) + " frames=" + frames + " time=" + source.timeSamples + " rate=" + rate + " warnings=" + warnings);

        // Upcoming FLAC opens its stream beside the current track and is adopted on selection.
        string next = Path.Combine(dir, "upcoming.flac");
        File.Copy(fixture, next, true);
        var current = source.clip;
        player.PreloadTrack(next);
        player.Tick(new Vector3(), false);
        var upcoming = (StreamedAudio)Field(player, "preloadStream");
        Check(upcoming != null && WaitReady(upcoming, 0) && source.clip == current,
            "Upcoming FLAC prefills its own stream beside the unchanged current clip");
        player.SetTrack(next); player.Tick(new Vector3(), false);
        Check(Field(player, "streamed") == upcoming && source.clip != current && source.clip.Streaming && current.Destroyed &&
            source.Schedules == 2 && player.Status == "Playing" && warnings == 0,
            "Selecting the upcoming FLAC adopts its prefilled stream and starts on the next Tick");
        player.Dispose();
        DeleteClosed(next);
        DeleteClosed(flac);
    }
}
