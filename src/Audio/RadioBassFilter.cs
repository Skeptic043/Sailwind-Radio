using System;
using UnityEngine;

namespace SailwindRadio
{
    /// <summary>Low-frequency processing for the Turbo Wolfer output only.</summary>
    public sealed class RadioBassFilter : MonoBehaviour
    {
        private volatile BassFilter bass;
        internal void SetProfile(int sampleRate, bool woofer)
        {
            BassFilter current = bass;
            if (!woofer) { bass = null; return; }
            if (sampleRate < 8000 || sampleRate > 192000) sampleRate = 48000;
            if (current == null || current.SampleRate != sampleRate) bass = new BassFilter(sampleRate);
        }

        // Coefficients and channel storage are allocated on the main thread. The audio
        // callback alone mutates the delay state. A sample-rate change swaps the entire
        // bank, so it cannot expose half-written coefficients to the audio thread.
        private sealed class BassFilter
        {
            internal readonly int SampleRate;
            private readonly double[] b0 = new double[2], b1 = new double[2], b2 = new double[2], a1 = new double[2], a2 = new double[2];
            private readonly double[] z1 = new double[16], z2 = new double[16];
            internal BassFilter(int sampleRate)
            {
                SampleRate = sampleRate;
                double w = 2 * Math.PI * 140 / sampleRate, c = Math.Cos(w), s = Math.Sin(w);
                for (int stage = 0; stage < 2; stage++)
                {
                    double q = stage == 0 ? .541196100146197 : 1.30656296487638;
                    double alpha = s / (2 * q), denominator = 1 + alpha;
                    b0[stage] = (1 - c) / (2 * denominator);
                    b1[stage] = (1 - c) / denominator;
                    b2[stage] = b0[stage];
                    a1[stage] = -2 * c / denominator;
                    a2[stage] = (1 - alpha) / denominator;
                }
            }
            internal float Apply(float sample, int channel)
            {
                if (channel >= 8) return 0;
                double value = RadioSpeakerOutput.Finite(sample) ? sample : 0;
                for (int stage = 0; stage < 2; stage++)
                {
                    int index = stage * 8 + channel;
                    double result = b0[stage] * value + z1[index];
                    z1[index] = b1[stage] * value - a1[stage] * result + z2[index];
                    z2[index] = b2[stage] * value - a2[stage] * result;
                    value = result;
                }
                return (float)(value * 2); // Bounded bass emphasis before the final limiter.
            }
        }
        private void OnAudioFilterRead(float[] data, int channels) { Process(data, channels); }
        internal void Process(float[] data, int channels)
        {
            BassFilter filter = bass;
            if (channels <= 0 || filter == null) return;
            for (int i = 0; i < data.Length; i += channels)
            {
                for (int channel = 0; channel < channels && i + channel < data.Length; channel++)
                {
                    float value = filter.Apply(data[i + channel], channel);
                    data[i + channel] = RadioSpeakerOutput.Finite(value) ? Math.Max(-1f, Math.Min(1f, value)) : 0f;
                }
            }
        }
    }
}
