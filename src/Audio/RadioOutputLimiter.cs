using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    /// <summary>The last filter on every radio output. It holds the output to the level its volume turns into full
    /// scale, which boosted application audio and the Wolfer's bass emphasis can pass. Unity applies volume after
    /// every filter, so nothing this output plays can clip.</summary>
    public sealed class RadioOutputLimiter : MonoBehaviour
    {
        internal RadioApplicationInput Input;
        // Set with the output's profile on the main thread, because the audio thread cannot ask Unity for it.
        internal volatile int SampleRate = 48000;
        private readonly PeakLimiter limiter = new PeakLimiter();
        private ApplicationRing.Reader last;

        private void OnAudioFilterRead(float[] data, int channels) { Process(data, channels); }

        internal void Process(float[] data, int channels)
        {
            RadioApplicationInput input = Input;
            if (input == null) return;
            // Another source starts at full gain instead of the last one's peak.
            ApplicationRing.Reader reader = input.Reader;
            if (reader != last) limiter.Reset();
            last = reader;
            limiter.Process(data, channels, input.Headroom, SampleRate);
        }
    }

    // Turns a whole frame down at once to hold its loudest channel at the ceiling, so peaks keep their shape
    // instead of squaring off, then recovers over about 200 ms. Used only on one output's audio thread.
    internal sealed class PeakLimiter
    {
        private int rate;
        private float release, limit = 1f;

        internal void Reset() { limit = 1f; }

        internal void Process(float[] data, int channels, float ceiling, int sampleRate)
        {
            if (channels <= 0) return;
            if (sampleRate != rate)
            {
                rate = sampleRate;
                release = 1f - (float)Math.Exp(-1.0 / (Math.Max(1, sampleRate) * .2));
            }
            for (int at = 0; at + channels <= data.Length; at += channels)
            {
                float peak = 0f;
                for (int channel = 0; channel < channels; channel++)
                {
                    float value = data[at + channel];
                    if (!RadioSpeakerOutput.Finite(value)) data[at + channel] = value = 0f;
                    peak = Math.Max(peak, Math.Abs(value));
                }
                limit += (1f - limit) * release;
                if (peak * limit > ceiling) limit = ceiling / peak;
                for (int channel = 0; channel < channels; channel++) data[at + channel] *= limit;
            }
        }
    }
}
