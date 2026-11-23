using System;

namespace SailwindRadio.Audio
{
    // Turns a whole frame down at once to hold its loudest channel at the ceiling, so peaks keep their shape
    // instead of squaring off, then recovers over about 200 ms. Run before uploading PCM to Unity so no
    // managed OnAudioFilterRead callback runs on Unity's audio thread.
    internal sealed class PeakLimiter
    {
        private int rate;
        private float release, limit = 1f;

        internal void Reset() { limit = 1f; }

        internal void Process(float[] data, int channels, float ceiling, int sampleRate)
        {
            if (channels > 0) Process(data, channels, data.Length / channels, ceiling, sampleRate);
        }

        internal void Process(float[] data, int channels, int frames, float ceiling, int sampleRate)
        {
            if (channels <= 0 || frames <= 0) return;
            if (sampleRate != rate)
            {
                rate = sampleRate;
                release = 1f - (float)Math.Exp(-1.0 / (Math.Max(1, sampleRate) * .2));
            }
            int total = Math.Min(data.Length, frames * channels);
            for (int at = 0; at + channels <= total; at += channels)
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
