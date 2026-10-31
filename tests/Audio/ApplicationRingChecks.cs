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
        reader.Mix(data, 2, false);
        Check(MaxAbs(data) == 0, "an unheld packet plays as silence");
        var nan = Packet(2048, held);
        nan[7] = Single.NaN;
        Check(!ring.Write(nan, 2048) && Finite(nan), "a packet with NaN is silenced");

        // A held full-scale peak passes, and anything the gain lifts past full scale is clipped to it.
        ring = new ApplicationRing(rate);
        Check(ring.Write(Packet(2048, HeldVolumes.Level), 2048), "a held full-scale packet passes the guard");
        ring = new ApplicationRing(rate);
        ring.Write(Stereo(4096, ApplicationRing.HeldPeak * .999f, -ApplicationRing.HeldPeak * .999f), 4096);
        data = Carrier(1024, 2, .5f);
        new ApplicationRing.Reader(ring).Mix(data, 2, false);
        Check(Near(data[0], 1f) && Near(data[1], -1f), "audio past full scale is clipped to it");

        // A flat output restores each channel to full level from its carrier of one half per channel.
        ring = new ApplicationRing(rate);
        ring.Write(Stereo(4096, held, -held), 4096);
        reader = new ApplicationRing.Reader(ring);
        data = Carrier(1024, 2, .5f);
        reader.Mix(data, 2, false);
        Check(Near(data[0], .5f) && Near(data[1], -.5f), "a flat output keeps left and right at full level");

        // A 3D output applies each channel's pan gain to the sum of both inputs, as Unity does for a stereo clip.
        data = new float[2048];
        for (int i = 0; i < data.Length; i += 2) { data[i] = .259f; data[i + 1] = .966f; }
        ring = new ApplicationRing(rate);
        ring.Write(Stereo(4096, held, held), 4096);
        new ApplicationRing.Reader(ring).Mix(data, 2, true);
        Check(Near(data[0], .259f) && Near(data[1], .966f), "a 3D output pans the summed application audio");

        // Unused channels of a wider speaker layout carry no gain and stay silent.
        data = Carrier(256, 6, 0f);
        for (int i = 0; i < data.Length; i += 6) { data[i] = .5f; data[i + 1] = .5f; }
        new ApplicationRing.Reader(ring).Mix(data, 6, false);
        Check(Near(data[0], .5f) && data[2] == 0 && data[5] == 0, "a flat 5.1 output fills only the front pair");

        // A reader stays a short margin behind the newest audio and catches up when it falls far behind.
        ring = new ApplicationRing(rate);
        reader = new ApplicationRing.Reader(ring);
        var ramp = new float[4800 * 2];
        for (int frame = 0; frame < 4800; frame++) ramp[frame * 2] = ramp[frame * 2 + 1] = held * frame / 4800f;
        ring.Write(ramp, 4800);
        data = Carrier(1024, 2, .5f);
        reader.Mix(data, 2, false);
        float first = data[0];
        Check(first > 0 && first < .5f, "a new reader starts just behind the newest audio");
        for (int i = 0; i < 20; i++) ring.Write(Stereo(4800, held, held), 4800);
        data = Carrier(1024, 2, .5f);
        reader.Mix(data, 2, false);
        Check(Near(data[0], .5f), "a reader far behind jumps to recent audio");

        // Sound marks the application as playing, and a paused player's digital silence does not.
        ring = new ApplicationRing(rate);
        ring.Write(Packet(480, 0f), 480);
        Check(!ring.Sounding, "silence is not playing");
        ring.Write(Packet(480, held), 480);
        Check(ring.Sounding, "held music is playing");

        // Nothing captured yet is silence, never stale or uninitialized samples.
        data = Carrier(1024, 2, .5f);
        new ApplicationRing.Reader(new ApplicationRing(rate)).Mix(data, 2, false);
        Check(MaxAbs(data) == 0, "an empty ring plays silence");
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
