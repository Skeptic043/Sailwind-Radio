using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SailwindRadio;
using UnityEngine;

// OGG through the production routing, preload and streaming paths, plus the shared downmix policy.
// The Vorbis decoder's own bitstream checks live in tests/Vorbis.
static partial class AudioChecks
{
    static string VorbisFixture(string name) { return Path.Combine(RepoRoot, "tests", "Vorbis", "Fixtures", name + ".ogg"); }

    static void CheckOgg()
    {
        string dir = AppContext.BaseDirectory;
        // Original libvorbis tone, four seconds of 44.1 kHz stereo.
        string ogg = Path.Combine(dir, "routing # 100%.ogg");
        File.Copy(Path.Combine(dir, "Fixtures", "sine-stereo.ogg"), ogg, true);
        Check(PcmDecoders.Supports(ogg) && PcmDecoders.Supports("UPPER.OGG"), "OGG routes to the in-source Vorbis decoder regardless of case");
        int channels = 0;
        float[] reference = CheckRoutedStream(ogg, "OGG", (decoder, all) =>
        {
            channels = decoder.Channels;
            Check(decoder is VorbisDecoder && channels == 2 && decoder.SampleRate == 44100 && decoder.Frames == 4 * 44100 &&
                Array.Exists(all, v => Math.Abs(v) > .01f),
                "Real libvorbis OGG opens over a cancellable file stream with its exact length and nonzero PCM");
            Check(SeekMatches(decoder, all), "OGG exact seek equals the full decode through the routed opener");
        }, 512, 0, length => new[] { length / 2, 3, length - 700, length / 3 });
        long frames = reference.Length / channels;

        int warnings = 0;
        var state = new RadioState { TrackPath = ogg, Powered = true, PositionSeconds = 0.1 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && Field(player, "streamed") is StreamedAudio && source.clip.Streaming && source.clip.samples == frames &&
            source.clip.channels == 2 && source.clip.frequency == 44100 && source.timeSamples == 4410 && warnings == 0,
            "OGG plays from its saved sample through a PCM-reader stream like every other format: status=" + player.Status);
        player.Dispose();
        DeleteClosed(ogg);
        CheckLongOgg();
        CheckOggFailures();
        CheckChainedOgg();
    }

    static void CheckLongOgg()
    {
        // 27 minutes of 44.1 kHz stereo, with saved positions past 25 minutes.
        const int rate = 44100;
        long frames = 1620L * rate;
        string path = WriteLongOgg("long-27min.ogg", VorbisFixture("stereo44-q-1"), frames);
        using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
            Check(decoder is VorbisDecoder && decoder.Frames == frames && decoder.Channels == 2 && decoder.SampleRate == rate,
                "A generated 27 minute OGG reports its exact length from its final granule: frames=" + decoder.Frames);
        var stream = OpenStream(path, 1500);
        int saved = 1500 * rate;
        Check(stream.Frames == frames && stream.BufferSamples <= 22 * rate * 2, "A 27 minute OGG streams through a bounded 20 second window");
        Check(WaitReady(stream, saved), "Long OGG begins decoding at a saved position past 25 minutes");
        var clip = stream.CreateClip(stream.CreateReader());
        clip.PcmSetPosition(saved);
        var block = new float[2048 * 2];
        clip.PcmRead(block);
        Check(Array.Exists(block, v => Math.Abs(v) > .01f), "Audio 25 minutes into a long OGG is real nonzero PCM");
        int bounded = stream.BufferSamples, later = 1610 * rate;
        stream.Request(later);
        Check(WaitReady(stream, later) && stream.BufferSamples == bounded, "Seeking near the end of a long OGG refills the fixed window");
        stream.Request(10 * rate);
        Check(WaitReady(stream, 10 * rate), "A long backward seek in OGG refills the window at the new position");
        stream.Dispose();
        UnityEngine.Object.Destroy(clip);
        Check(WaitWorker(stream), "Long OGG worker exits promptly after cancellation");

        int warnings = 0;
        var state = new RadioState { TrackPath = path, Powered = true, PositionSeconds = 1500 };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++);
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && source.clip.Streaming && source.clip.samples == frames && source.timeSamples == saved && warnings == 0,
            "A 27 minute OGG plays from a saved position past 25 minutes: status=" + player.Status);
        player.Dispose();
        DeleteClosed(path);
    }

    // Other Ogg codecs and Vorbis floor 0 fail with the decoder's reason. The failed path is what the
    // queue skips. tests/Playback checks the skip with the real queue.
    static void CheckOggFailures()
    {
        string dir = AppContext.BaseDirectory;
        string[] names = { "opus", "oggflac", "speex", "theora", "floor0-synthetic" };
        string[] messages = { "Ogg Opus audio is not supported", "Ogg FLAC audio is not supported", "Ogg Speex audio is not supported",
            "Ogg Theora video without Vorbis audio is not supported", "Vorbis floor 0 is not supported" };
        for (int i = 0; i < names.Length; i++)
        {
            string path = Path.Combine(dir, "failing-" + names[i] + ".ogg");
            File.Copy(VorbisFixture(names[i]), path, true);
            Check(Rejects(path, messages[i]), names[i] + " is rejected by the routed opener, which releases the file");
            int warnings = 0;
            string lastWarning = "";
            var state = new RadioState { TrackPath = path, Powered = true };
            var player = new RadioPlayback(new MonoBehaviour(), state, w => { warnings++; lastWarning = w; });
            PumpUntil(player, false, () => player.LoadFailed);
            for (int t = 0; t < 10; t++) player.Tick(new Vector3(), false);
            Check(player.Status == "Unable to decode music" && player.FailedPath == path && warnings == 1 &&
                lastWarning == "Unable to decode music: " + messages[i] + " (" + Path.GetFileName(path) + ")" && lastWarning.IndexOf(';') < 0,
                names[i] + " fails once with a clear warning and a failed path for the queue: " + lastWarning);
            player.Dispose();
            DeleteClosed(path);
        }
    }

    static void CheckChainedOgg()
    {
        // Two independently encoded links. The first is 0.5 s of 22.05 kHz mono, the second stereo.
        string path = Path.Combine(AppContext.BaseDirectory, "chained.ogg");
        File.Copy(VorbisFixture("chained-mono22-stereo44"), path, true);
        using (var decoder = PcmDecoders.Open(path, CancellationToken.None))
            Check(decoder.Channels == 1 && decoder.SampleRate == 22050 && decoder.Frames == 11025, "Chained OGG opens as its first link");
        AudioSettings.dspTime = 30000;
        int warnings = 0;
        var state = new RadioState { TrackPath = path, Powered = true };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => warnings++) { RepeatTrack = false };
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && source.clip.Streaming && source.clip.channels == 1 && source.clip.frequency == 22050 &&
            source.clip.samples == 11025 && source.Schedules == 1 && warnings == 0, "Chained OGG plays its first link through the stream engine");
        AudioSettings.dspTime = source.ScheduledAt + .6;
        player.Tick(new Vector3(), false);
        Check(player.TrackEnded && player.Status == "Ended" && Math.Abs(state.PositionSeconds - .5) < 1e-9 && warnings == 0,
            "Chained OGG ends after its first link without an error");
        player.Dispose();
        DeleteClosed(path);
    }

    // Loops the audio packets of a real libvorbis fixture into a long stream with exact granules and
    // fresh pages. The headers are copied, so every repeated packet decodes with its own codebooks.
    static string WriteLongOgg(string name, string fixture, long frames)
    {
        byte[] bytes = File.ReadAllBytes(fixture);
        var headers = new List<byte[]>();
        var packets = new List<byte[]>();
        var sizes = new List<int>();
        using (var decoder = new VorbisDecoder(new MemoryStream(bytes), CancellationToken.None))
        {
            var reader = new OggReader(new MemoryStream(bytes), CancellationToken.None);
            reader.Select(decoder.StreamSerial, decoder.LinkEnd);
            reader.SeekTo(0);
            OggPacket packet;
            while (reader.NextPacket(VorbisDecoder.PacketLimit, out packet))
            {
                if (packet.Hole || packet.Length != packet.TotalLength) throw new Exception("Fixture packet is damaged");
                var copy = new byte[packet.Length];
                Buffer.BlockCopy(packet.Data, 0, copy, 0, packet.Length);
                if (headers.Count < 3) { headers.Add(copy); continue; }
                int flag = decoder.Codec.PeekBlockFlag(copy, copy.Length);
                if (flag < 0) continue;
                packets.Add(copy);
                sizes.Add(decoder.Codec.BlockSize(flag));
            }
        }
        string path = Path.Combine(AppContext.BaseDirectory, name);
        using (var output = new FileStream(path, FileMode.Create, FileAccess.Write))
        {
            var pages = new OggPageWriter(output, 0x52414449);
            pages.Add(headers[0], 0);
            pages.Flush(OggReader.FlagBeginning);
            pages.Add(headers[1], 0);
            pages.Add(headers[2], 0);
            pages.Flush(0);
            long granule = 0;
            int previous = -1;
            for (int index = 0; ; index++)
            {
                int k = index % packets.Count;
                if (previous >= 0) granule += previous / 4 + sizes[k] / 4;
                previous = sizes[k];
                // The final page trims the end to exactly the requested length.
                if (granule >= frames)
                {
                    pages.Add(packets[k], frames);
                    pages.Flush(OggReader.FlagEnd);
                    break;
                }
                pages.Add(packets[k], granule);
                if (pages.BodyBytes >= 4096) pages.Flush(0);
            }
        }
        return path;
    }

    // Whole packets only, so a page never continues a packet. Each page carries the granule of its last packet.
    sealed class OggPageWriter
    {
        private readonly Stream output;
        private readonly uint serial;
        private readonly List<byte> lacing = new List<byte>();
        private readonly MemoryStream body = new MemoryStream();
        private uint sequence;
        private long granule = -1;
        internal OggPageWriter(Stream output, uint serial) { this.output = output; this.serial = serial; }
        internal long BodyBytes { get { return body.Length; } }

        internal void Add(byte[] packet, long granuleAfter)
        {
            int segments = packet.Length / 255 + 1;
            if (segments > 255) throw new Exception("Packet does not fit one page");
            if (lacing.Count + segments > 255) Flush(0);
            for (int left = packet.Length; left >= 255; left -= 255) lacing.Add(255);
            lacing.Add((byte)(packet.Length % 255));
            body.Write(packet, 0, packet.Length);
            granule = granuleAfter;
        }

        internal void Flush(int flags)
        {
            var page = new byte[27 + lacing.Count + body.Length];
            page[0] = (byte)'O'; page[1] = (byte)'g'; page[2] = (byte)'g'; page[3] = (byte)'S';
            page[5] = (byte)flags;
            BitConverter.GetBytes(granule).CopyTo(page, 6);
            BitConverter.GetBytes(serial).CopyTo(page, 14);
            BitConverter.GetBytes(sequence++).CopyTo(page, 18);
            page[26] = (byte)lacing.Count;
            lacing.CopyTo(page, 27);
            body.ToArray().CopyTo(page, 27 + lacing.Count);
            BitConverter.GetBytes(OggReader.Crc(page, 0, page.Length)).CopyTo(page, 22);
            output.Write(page, 0, page.Length);
            lacing.Clear();
            body.SetLength(0);
            granule = -1;
        }
    }

    // The single policy shared by every decoder, including where WAVE masks differ from the old per-format tables.
    static void CheckDownmixPolicy()
    {
        double h = Math.Sqrt(.5);
        float[] l, r;
        ChannelDownmix.Build(ChannelDownmix.FromWaveMask(6, 0), out l, out r);
        double n = 1 + 2 * h;
        Check(Near(l, new[] { 1 / n, 0, h / n, 0, h / n, 0 }) && Near(r, new[] { 0, 1 / n, h / n, 0, 0, h / n }),
            "5.1 WAVE folds with full front, -3 dB centre and surrounds and no LFE");
        float[] fl, fr, vl, vr;
        ChannelDownmix.Build(ChannelDownmix.Flac(6), out fl, out fr);
        ChannelDownmix.Build(ChannelDownmix.Vorbis(6), out vl, out vr);
        // Vorbis 5.1 order FL C FR RL RR LFE, WAVE and FLAC order FL FR FC LFE BL BR.
        int[] vorbisOf = { 0, 2, 1, 5, 3, 4 };
        bool same = true;
        for (int c = 0; c < 6; c++)
            same &= fl[c] == l[c] && fr[c] == r[c] && vl[vorbisOf[c]] == l[c] && vr[vorbisOf[c]] == r[c];
        Check(same, "WAVE, FLAC and Vorbis 5.1 share identical weights per speaker");
        ChannelDownmix.Build(ChannelDownmix.FromWaveMask(7, 0), out l, out r);
        n = 1 + 2 * h + .5;
        Check(Near(l, new[] { 1 / n, 0, h / n, 0, .5 / n, h / n, 0 }) && Near(r, new[] { 0, 1 / n, h / n, 0, .5 / n, 0, h / n }),
            "Back centre gives half to each side");
        // Previously half weight, now -3 dB like every other rear speaker.
        ChannelDownmix.Build(ChannelDownmix.FromWaveMask(4, 0x3 | 0x8000 | 0x20000), out l, out r);
        Check(Near(l, new[] { 1 / (1 + h), 0, h / (1 + h), 0 }) && Near(r, new[] { 0, 1 / (1 + h), 0, h / (1 + h) }),
            "Top back speakers enter at -3 dB like the other rear speakers");
        // Both rows share one divisor, the louder row's weight, so an asymmetric layout keeps its balance.
        // Front left, front right, centre and back left give a left row of 1 + 2h and a right row of 1 + h.
        ChannelDownmix.Build(ChannelDownmix.FromWaveMask(4, 0x17), out l, out r);
        double left = 1 + 2 * h;
        Check(Near(l, new[] { 1 / left, 0, h / left, h / left }) && Near(r, new[] { 0, 1 / left, h / left, 0 }),
            "An asymmetric layout scales both sides by one factor and keeps the right side quieter");
        // A layout whose louder row sums below one is not raised, like the earlier WAVE and Media Foundation tables.
        ChannelDownmix.Build(ChannelDownmix.FromWaveMask(3, 0x8 | 0x10 | 0x20), out l, out r);
        Check(Near(l, new double[] { 0, h, 0 }) && Near(r, new double[] { 0, 0, h }), "A layout of only rear speakers keeps its -3 dB weights");
        ChannelDownmix.Build(ChannelDownmix.FromWaveMask(4, 0x3), out l, out r);
        Check(Near(l, new[] { .5, 0, .25, .25 }) && Near(r, new[] { 0, .5, .25, .25 }), "Channels beyond the mask split evenly as unknown positions");
        ChannelDownmix.Build(new[] { Speaker.LowFrequency, Speaker.LowFrequency, Speaker.LowFrequency }, out l, out r);
        Check(Near(l, new double[3]) && Near(r, new double[3]), "A layout of only LFE stays silent instead of dividing by zero");
        bool bounded = true, balanced = true;
        var random = new Random(11);
        for (int channels = 3; channels <= 18; channels++)
            foreach (int mask in new[] { 0, random.Next(1, 1 << 18), random.Next(1, 1 << 18) })
            {
                Speaker[] layout = ChannelDownmix.FromWaveMask(channels, mask);
                ChannelDownmix.Build(layout, out l, out r);
                double sl = 0, sr = 0;
                for (int c = 0; c < channels; c++) { sl += Math.Abs(l[c]); sr += Math.Abs(r[c]); }
                // A lone speaker sums to at most one and is never scaled, so it reveals its raw weights.
                double rawLeft = 0, rawRight = 0;
                foreach (Speaker speaker in layout)
                {
                    float[] pl, pr;
                    ChannelDownmix.Build(new[] { speaker }, out pl, out pr);
                    rawLeft += pl[0]; rawRight += pr[0];
                }
                double peak = Math.Max(sl, sr), rawPeak = Math.Max(rawLeft, rawRight);
                bounded &= peak <= 1 + 1e-6 && (rawPeak < 1 ? Math.Abs(peak - rawPeak) < 1e-6 : Math.Abs(peak - 1) < 1e-6);
                balanced &= Math.Abs(sl * rawRight - sr * rawLeft) < 1e-6;
            }
        Check(bounded, "The louder downmix row has unit absolute weight, or less when unraised, so full-scale input cannot clip");
        Check(balanced, "Every downmix keeps the left and right balance of its speaker weights");
    }

    static bool Near(float[] actual, double[] expected)
    {
        if (actual.Length != expected.Length) return false;
        for (int i = 0; i < actual.Length; i++) if (Math.Abs(actual[i] - expected[i]) > 1e-6) return false;
        return true;
    }
}
