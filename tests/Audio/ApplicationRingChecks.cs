using System;
using SailwindRadio.Audio;

// The application ring between the capture worker and each output's audio thread. Windows capture and the
// Unity carrier are live checks. The Unity editor showed that a 3D source's filter receives the carrier
// already panned, with both input channels summed, and a flat source's receives each channel unchanged.
static partial class AudioChecks
{
    static void CheckApplicationRing()
    {
        const int rate = 48000;
        float held = HeldVolumes.Level * .5f;

        // A packet louder than any held session plays as silence instead of 10,000 times too loud.
        var ring = new ApplicationRing(rate);
        var packet = Packet(2048, HeldVolumes.Level * 10);
        Check(!ring.Write(packet, 2048), "an unheld packet is reported");
        var reader = new ApplicationRing.Reader(ring);
        var data = Carrier(1024, 2, .5f);
        reader.Mix(data, 2, false, 0);
        Check(MaxAbs(data) == 0, "an unheld packet plays as silence");
        var nan = Packet(2048, held);
        nan[7] = Single.NaN;
        Check(!ring.Write(nan, 2048) && Finite(nan), "a packet with NaN is silenced");

        // A held full-scale peak passes, and the gain carries it past full scale for the output's limiter to hold.
        ring = new ApplicationRing(rate);
        Check(ring.Write(Packet(2048, HeldVolumes.Level), 2048), "a held full-scale packet passes the guard");
        ring = new ApplicationRing(rate);
        ring.Write(Stereo(4096, ApplicationRing.HeldPeak * .999f, -ApplicationRing.HeldPeak * .999f), 4096);
        data = Carrier(1024, 2, .5f);
        new ApplicationRing.Reader(ring).Mix(data, 2, false, 0);
        Check(Math.Abs(data[0] - 3.996f) < 1e-3f && Math.Abs(data[1] + 3.996f) < 1e-3f, "mixing leaves audio past full scale to the output limiter");

        // AppVolume lifts a quiet app.
        try
        {
            ApplicationRing.Boost = 4f;
            ring = new ApplicationRing(rate);
            ring.Write(Stereo(4096, held * .2f, -held * .2f), 4096);
            data = Carrier(1024, 2, .5f);
            new ApplicationRing.Reader(ring).Mix(data, 2, false, 0);
            Check(Near(data[0], .4f) && Near(data[1], -.4f), "AppVolume lifts a quiet app");
            CheckBoostedWoofer(rate, held);
        }
        finally { ApplicationRing.Boost = 1f; }
        CheckPeakLimiter(rate);

        // A flat output restores each channel to full level from its carrier of one half per channel.
        ring = new ApplicationRing(rate);
        ring.Write(Stereo(4096, held, -held), 4096);
        reader = new ApplicationRing.Reader(ring);
        data = Carrier(1024, 2, .5f);
        reader.Mix(data, 2, false, 0);
        Check(Near(data[0], .5f) && Near(data[1], -.5f), "a flat output keeps left and right at full level");

        // A 3D output applies each channel's pan gain to the sum of both inputs, as Unity does for a stereo clip.
        data = new float[2048];
        for (int i = 0; i < data.Length; i += 2) { data[i] = .259f; data[i + 1] = .966f; }
        ring = new ApplicationRing(rate);
        ring.Write(Stereo(4096, held, held), 4096);
        new ApplicationRing.Reader(ring).Mix(data, 2, true, 0);
        Check(Near(data[0], .259f) && Near(data[1], .966f), "a 3D output pans the summed application audio");

        // Unused channels of a wider speaker layout carry no gain and stay silent.
        data = Carrier(256, 6, 0f);
        for (int i = 0; i < data.Length; i += 6) { data[i] = .5f; data[i + 1] = .5f; }
        new ApplicationRing.Reader(ring).Mix(data, 6, false, 0);
        Check(Near(data[0], .5f) && data[2] == 0 && data[5] == 0, "a flat 5.1 output fills only the front pair");

        // A reader stays a short margin behind the newest audio and catches up when it falls far behind.
        ring = new ApplicationRing(rate);
        reader = new ApplicationRing.Reader(ring);
        var ramp = new float[4800 * 2];
        for (int frame = 0; frame < 4800; frame++) ramp[frame * 2] = ramp[frame * 2 + 1] = held * frame / 4800f;
        ring.Write(ramp, 4800);
        data = Carrier(1024, 2, .5f);
        reader.Mix(data, 2, false, 0);
        float first = data[0];
        Check(first > 0 && first < .5f, "a new reader starts just behind the newest audio");
        for (int i = 0; i < 20; i++) ring.Write(Stereo(4800, held, held), 4800);
        data = Carrier(1024, 2, .5f);
        reader.Mix(data, 2, false, 1024);
        Check(Near(data[0], .5f), "a reader far behind jumps to recent audio");

        // A speaker that joins later plays the same moment as the radio, not its own point behind the newest audio.
        ring = new ApplicationRing(rate);
        ramp = new float[48000 * 2];
        for (int frame = 0; frame < 48000; frame++) ramp[frame * 2] = ramp[frame * 2 + 1] = held * frame / 48000f;
        ring.Write(ramp, 2400);
        var radio = new ApplicationRing.Reader(ring);
        radio.Mix(Carrier(1024, 2, .5f), 2, false, 0);
        var rest = new float[(48000 - 2400) * 2];
        Array.Copy(ramp, 2400 * 2, rest, 0, rest.Length);
        ring.Write(rest, 2400);
        var fromRadio = Carrier(1024, 2, .5f);
        var fromSpeaker = Carrier(1024, 2, .5f);
        radio.Mix(fromRadio, 2, false, 1024);
        new ApplicationRing.Reader(ring).Mix(fromSpeaker, 2, false, 1024);
        Check(fromRadio[0] > 0 && Near(fromRadio[0], fromSpeaker[0]) && Near(fromRadio[2046], fromSpeaker[2046]),
            "a speaker joining later plays in step with its radio");

        // Sound marks the application as playing, and a paused player's digital silence does not.
        ring = new ApplicationRing(rate);
        ring.Write(Packet(480, 0f), 480);
        Check(!ring.Sounding, "silence is not playing");
        ring.Write(Packet(480, held), 480);
        Check(ring.Sounding, "held music is playing");

        // Nothing captured yet is silence, never stale or uninitialized samples.
        data = Carrier(1024, 2, .5f);
        new ApplicationRing.Reader(new ApplicationRing(rate)).Mix(data, 2, false, 0);
        Check(MaxAbs(data) == 0, "an empty ring plays silence");
    }

    // The whole chain of a Turbo Wolfer at half master volume, with AppVolume at 10 on a 70 Hz app tone: mixing,
    // the bass filter's emphasis, the final limiter and then the output's volume, as Unity applies it last.
    static void CheckBoostedWoofer(int rate, float held)
    {
        ApplicationRing.Boost = 10f;
        var output = new RadioSpeakerOutput("Boost check", RadioSpeakerProfile.TurboWoofer, message => { });
        output.Carried = true;
        output.Running = true; // A playing output. A stopped one writes nothing to its source.
        output.Update(.5f, new UnityEngine.Vector3(), true);
        Check(Near(output.Source.volume, .25f) && Near(output.Input.Headroom, 4f), "an output's headroom is the level its volume turns into full scale");
        output.Update(.5f, new UnityEngine.Vector3(), false);
        Check(output.Source.volume == 0 && output.Input.Headroom == 100f, "a silent output keeps a finite headroom");
        output.Update(.5f, new UnityEngine.Vector3(), true);

        var ring = new ApplicationRing(rate);
        var reader = new ApplicationRing.Reader(ring);
        output.Input.Reader = reader;
        var bass = new RadioBassFilter();
        bass.SetProfile(rate, true);
        var tone = new float[1024 * 2];
        float loudPeak = 0, quietPeak = 0;
        // Written a block ahead of each read, as capture runs, so the reader plays the whole passage in order.
        for (int block = 0; block < rate / 1024; block++)
        {
            for (int frame = 0; frame < 1024; frame++)
            {
                int at = block * 1024 + frame;
                tone[frame * 2] = tone[frame * 2 + 1] = (at < rate / 2 ? held : held * .1f) * (float)Math.Sin(2 * Math.PI * 70 * at / rate);
            }
            ring.Write(tone, 1024);
            var data = Carrier(1024, 2, .5f);
            reader.Mix(data, 2, false, block * 1024);
            bass.Process(data, 2);
            output.Limiter.Process(data, 2);
            for (int i = 0; i < data.Length; i++) data[i] *= output.Source.volume;
            if (block > 6 && block < 20) loudPeak = Math.Max(loudPeak, MaxAbs(data));
            if (block > 40) quietPeak = Math.Max(quietPeak, MaxAbs(data));
        }
        Check(loudPeak <= 1f + 1e-3f && loudPeak > .9f, "a boosted Wolfer reaches full scale after volume, never past it");
        Check(quietPeak > .2f, "a boosted Wolfer recovers once the app is quiet again");

        // Another app starts at full gain, not at the last app's limited level.
        output.Input.Reader = new ApplicationRing.Reader(new ApplicationRing(rate));
        var block2 = Stereo(1, 2f, 2f);
        output.Limiter.Process(block2, 2);
        Check(Near(block2[0], 2f), "a new app starts at full gain");

        // Music's bass emphasis may pass full scale before volume, as the bass knob sets it, and is held at the
        // level volume turns into full scale whatever Reader the main thread has just set.
        output.Input.Reader = null;
        block2 = Stereo(1, 3f, 3f);
        output.Limiter.Process(block2, 2);
        Check(Near(block2[0], 3f), "Wolfer bass emphasis within its headroom is not clipped");
        block2 = Stereo(1024, 40f, 40f);
        output.Limiter.Process(block2, 2);
        Check(MaxAbs(block2) <= 4f + 1e-3f, "music is held at the level volume turns into full scale");
        output.Dispose();
    }

    static void CheckPeakLimiter(int rate)
    {
        // Audio under the ceiling passes untouched.
        var limiter = new PeakLimiter();
        var data = Stereo(1024, .9f, -.5f);
        limiter.Process(data, 2, 1f, rate);
        Check(data[0] == .9f && data[1] == -.5f, "the limiter leaves audio under its ceiling untouched");

        // A loud frame is turned down whole, so its channels keep their balance where a clip would flatten them.
        data = Stereo(1024, 8f, 2f);
        limiter.Process(data, 2, 4f, rate);
        Check(Near(data[2046], 4f) && Near(data[2047], 1f), "the limiter turns a whole frame down, keeping its channel balance");

        // Quieter audio right after a peak stays turned down for a moment, then recovers.
        data = Stereo(1, 2f, 2f);
        limiter.Process(data, 2, 4f, rate);
        Check(data[0] < 1.1f, "audio right after a peak stays turned down");
        data = Stereo(rate / 5, 2f, 2f);
        limiter.Process(data, 2, 4f, rate);
        // From half gain, a 200 ms release has recovered 1 - e^-1 of the way: 2 x (1 - .5 / e).
        Check(Math.Abs(data[data.Length - 1] - 1.632f) < .02f, "the limiter recovers over about 200 ms");
        data = Stereo(rate, 2f, 2f);
        limiter.Process(data, 2, 4f, rate);
        Check(data[data.Length - 1] > 1.98f, "the limiter recovers over time");

        // Damaged samples become silence instead of reaching Unity's mixer.
        data = new[] { Single.NaN, Single.PositiveInfinity, 1f, 1f };
        limiter.Process(data, 2, 4f, rate);
        Check(Finite(data) && data[0] == 0 && data[1] == 0, "the limiter silences damaged samples");
    }

    static float[] Packet(int frames, float value) => Stereo(frames, value, value);

    static float[] Stereo(int frames, float left, float right)
    {
        var samples = new float[frames * 2];
        for (int i = 0; i < samples.Length; i += 2) { samples[i] = left; samples[i + 1] = right; }
        return samples;
    }

    static float[] Carrier(int frames, int channels, float value)
    {
        var samples = new float[frames * channels];
        for (int i = 0; i < samples.Length; i++) samples[i] = value;
        return samples;
    }

    static float MaxAbs(float[] samples)
    {
        float peak = 0;
        foreach (float sample in samples) peak = Math.Max(peak, Math.Abs(sample));
        return peak;
    }

    static bool Finite(float[] samples)
    {
        foreach (float sample in samples) if (Single.IsNaN(sample) || Single.IsInfinity(sample)) return false;
        return true;
    }

    static bool Near(float actual, float expected) => Math.Abs(actual - expected) < 1e-4f;
}
