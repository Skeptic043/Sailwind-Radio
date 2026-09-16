using System;
using UnityEngine;

namespace SailwindRadio
{
    public struct WeatherInterferenceSample
    {
        public float Gain, Cutoff, Crackle;
        public WeatherInterferenceSample(float gain, float cutoff, float crackle)
        { Gain = gain; Cutoff = cutoff; Crackle = crackle; }
    }

    /// <summary>Pure rain-to-output tuning. Does not read or modify native game weather.</summary>
    public static class WeatherInterference
    {
        public static WeatherInterferenceSample Sample(float rainIntensity, double realTime, bool enabled, float strength)
        {
            if (!enabled || Double.IsNaN(realTime) || Double.IsInfinity(realTime)) return new WeatherInterferenceSample(1, 22000, 0);
            float rain = RadioSpeakerOutput.Clamp((rainIntensity - 2f) / 8f);
            float amount = rain * (float)Math.Sqrt(RadioSpeakerOutput.Clamp(strength));
            if (amount <= 0) return new WeatherInterferenceSample(1, 22000, 0);
            // One short, softly edged event per five-second window. Pure deterministic phase
            // avoids affecting Unity's global random sequence or playback progression.
            double cycle = Math.Floor(Math.Max(0, realTime) / 5);
            double pulseAt = 2 + 2 * (0.5 + 0.5 * Math.Sin(cycle * 12.9898));
            double phase = Math.Max(0, realTime) - cycle * 5 - pulseAt;
            float pulse = phase >= 0 && phase < .18 ? (float)Math.Pow(Math.Sin(Math.PI * phase / .18), 2) : 0f;
            return new WeatherInterferenceSample(1f - .95f * amount * pulse,
                22000f - 13000f * amount, amount * pulse);
        }
    }

    /// <summary>Low-level static added only to the output buffer, never to shared clip PCM.</summary>
    public sealed class RadioStaticFilter : MonoBehaviour
    {
        private volatile float amplitude;
        private uint randomState = 0x6d2b79f5;
        internal void SetLevel(float crackle, float outputGain)
        {
            // AudioSource applies endpoint gain to the filtered signal. Applying it here
            // too made static vanish at ordinary knob settings. Keep only the mute gate.
            amplitude = outputGain > 0 ? RadioSpeakerOutput.Clamp(crackle) * .18f : 0f;
        }
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
            float level = amplitude;
            BassFilter filter = bass;
            if (channels <= 0 || (level <= 0 && filter == null)) return;
            for (int i = 0; i < data.Length; i += channels)
            {
                randomState ^= randomState << 13;
                randomState ^= randomState >> 17;
                randomState ^= randomState << 5;
                float noise = ((randomState & 0xffff) / 32767.5f - 1f) * level;
                for (int channel = 0; channel < channels && i + channel < data.Length; channel++)
                {
                    float value = data[i + channel] + noise;
                    if (filter != null) value = filter.Apply(value, channel);
                    data[i + channel] = RadioSpeakerOutput.Finite(value) ? Math.Max(-1f, Math.Min(1f, value)) : 0f;
                }
            }
        }
    }
}
