#if UNITY_EDITOR
using System;
using UnityEngine;

// Decodes the editor probe's position signal once per DSP buffer on the audio thread. The left channel is a
// ramp over Period frames and the right channel a constant reference, so left over right gives the content
// frame at any gain. The probe bypasses the output's filters, which would reshape the ramp.
public sealed class RadioContentTap : MonoBehaviour
{
    internal const double RampBase = .1, RampSpan = .8;
    internal const float Reference = .5f;

    internal struct Block
    {
        public double Dsp;
        // Content frame of the block's first sample, modulo Period. Valid only when Valid > 0.
        public double Position;
        public int Frames, Valid, Outliers;
        // Exact digital zeros in both channels: frames before the first and after the last sound, and frames in
        // runs of at least MinimumGap between sounds. Lead equals Frames for a silent block.
        public int Lead, Trail, Inner;
    }

    private readonly object gate = new object();
    private readonly Block[] blocks = new Block[8192];
    private readonly double[] deltas = new double[16384];
    private int count;
    internal const int MinimumGap = 32;
    internal volatile int Period = 1;
    internal volatile int Calls;
    // On the listener, zeroes the final mix so sources can play at audible gain without reaching the device.
    internal volatile bool Silence;

    internal static float Left(long frame, int period)
    { return (float)(RampBase + RampSpan * (frame % period) / period); }

    // Frames of digital silence between sounds from dsp on, ignoring silence before the first sound and after the last.
    internal static int Gaps(Block[] blocks, double from, double to)
    {
        int gaps = 0, pending = 0;
        bool started = false;
        foreach (var block in blocks)
        {
            if (block.Dsp < from || block.Dsp > to) continue;
            if (block.Lead == block.Frames) { if (started) pending += block.Frames; continue; }
            if (started && pending + block.Lead >= MinimumGap) gaps += pending + block.Lead;
            started = true;
            gaps += block.Inner;
            pending = block.Trail;
        }
        return gaps;
    }

    // dspTime of the first sound from dsp on, or NaN when the blocks stay silent.
    internal static double FirstSound(Block[] blocks, double from, int rate)
    {
        foreach (var block in blocks)
            if (block.Dsp >= from && block.Lead < block.Frames) return block.Dsp + block.Lead / (double)rate;
        return Double.NaN;
    }

    internal static double Fold(double value, int period)
    {
        value %= period;
        if (value > period / 2.0) value -= period;
        else if (value <= -period / 2.0) value += period;
        return value;
    }

    private void OnAudioFilterRead(float[] data, int channels)
    {
        Calls++;
        if (Silence) { Array.Clear(data, 0, data.Length); return; }
        if (channels < 2) return;
        double dsp = AudioSettings.dspTime;
        int frames = Math.Min(data.Length / channels, deltas.Length), period = Period, valid = 0;
        double first = 0;
        for (int i = 0; i < frames; i++)
        {
            float left = data[i * channels], right = data[i * channels + 1];
            if (Math.Abs(right) < 1e-6f) continue;
            double ramp = (left / (double)right * Reference - RampBase) / RampSpan;
            if (Double.IsNaN(ramp) || ramp < -.001 || ramp > 1.001) continue;
            double delta = ramp * period - i;
            if (valid == 0) first = delta;
            else delta = first + Fold(delta - first, period);
            deltas[valid++] = delta;
        }
        int lead = -1, run = 0, inner = 0;
        for (int i = 0; i < frames; i++)
        {
            if (data[i * channels] == 0f && data[i * channels + 1] == 0f) { run++; continue; }
            if (lead < 0) lead = run;
            else if (run >= MinimumGap) inner += run;
            run = 0;
        }
        var block = new Block { Dsp = dsp, Frames = frames, Valid = valid, Lead = lead < 0 ? frames : lead, Trail = lead < 0 ? 0 : run, Inner = inner };
        if (valid > 0)
        {
            Array.Sort(deltas, 0, valid);
            double median = deltas[valid / 2];
            int outliers = 0;
            for (int i = 0; i < valid; i++) if (Math.Abs(deltas[i] - median) > 2) outliers++;
            block.Position = ((median % period) + period) % period;
            block.Outliers = outliers;
        }
        lock (gate)
        {
            if (count < blocks.Length) blocks[count++] = block;
        }
    }

    internal Block[] Take()
    {
        lock (gate)
        {
            var copy = new Block[count];
            Array.Copy(blocks, copy, count);
            count = 0;
            return copy;
        }
    }
}
#endif
