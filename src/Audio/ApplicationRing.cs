using System;
using System.Threading;

namespace SailwindRadio.Audio
{
    // One second of held application audio. One writer adds captured stereo packets, and each output's audio
    // thread reads through its own Reader 40 ms behind the newest audio, never waiting for it.
    internal sealed class ApplicationRing
    {
        internal const int Channels = ProcessLoopback.Channels;
        // Held audio stays far below this before the radio's gain, even with the overs of heavily limited music. A
        // louder packet comes from a session not held yet, such as a new browser tab or a moved Volume Mixer slider,
        // and plays as silence. Mix clips everything to full scale, so audio below it is never louder than music.
        internal const float HeldPeak = HeldVolumes.Level * 4;
        internal const float Gain = 1f / HeldVolumes.Level;
        internal readonly int SampleRate;
        private readonly float[] samples;
        private readonly int frames, margin, maximumLag;
        private long written;
        // Milliseconds tick of the last sound. It starts a minute ago, so a new ring is silent.
        private int soundAt = unchecked(Environment.TickCount - 60000);

        internal ApplicationRing(int sampleRate)
        {
            SampleRate = sampleRate;
            frames = sampleRate;
            samples = new float[frames * Channels];
            // Windows delivers about every 10 ms, and Spotify measured gaps up to 22 ms between packets. Unity reads
            // 21 ms blocks on its own schedule, so readers stay 40 ms behind to never run dry.
            margin = sampleRate / 25;
            maximumLag = sampleRate / 8;
        }

        internal int Capacity { get { return frames; } }

        // The application made sound within the last second, so it is playing rather than paused.
        internal bool Sounding { get { return unchecked(Environment.TickCount - Volatile.Read(ref soundAt)) < 1000; } }

        // Writer only. Adds up to Capacity frames and returns false when the packet was too loud and became silence.
        internal bool Write(float[] packet, int count)
        {
            int length = count * Channels;
            bool held = true, sound = false;
            for (int i = 0; i < length && held; i++)
            {
                float level = Math.Abs(packet[i]);
                if (Single.IsNaN(packet[i]) || level > HeldPeak) held = false;
                // Anything above -80 dB of the held full scale is sound, not a paused player's digital silence.
                else if (level > HeldVolumes.Level * 1e-4f) sound = true;
            }
            if (!held) Array.Clear(packet, 0, length);
            else if (sound) Volatile.Write(ref soundAt, Environment.TickCount);
            long end = written;
            int at = (int)(end % frames), first = Math.Min(count, frames - at);
            Array.Copy(packet, 0, samples, at * Channels, first * Channels);
            Array.Copy(packet, first * Channels, samples, 0, (count - first) * Channels);
            // Published after the samples, so a reader that sees the new end also sees them.
            Volatile.Write(ref written, end + count);
            return held;
        }

        // Copies count frames from cursor, moving it to just behind the newest audio when it falls out of range.
        // Audio not captured yet plays as silence.
        private void Copy(ref long cursor, float[] destination, int count)
        {
            long end = Volatile.Read(ref written);
            if (cursor < 0 || cursor + count > end || end - cursor > maximumLag + count) cursor = end - count - margin;
            if (cursor < 0)
            {
                cursor = -1;
                Array.Clear(destination, 0, count * Channels);
                return;
            }
            int at = (int)(cursor % frames), first = Math.Min(count, frames - at);
            Array.Copy(samples, at * Channels, destination, 0, first * Channels);
            Array.Copy(samples, 0, destination, first * Channels, (count - first) * Channels);
            cursor += count;
        }

        private static float Clip(float value) { return value > 1f ? 1f : value < -1f ? -1f : value; }

        // One output's position. Used only on that output's audio thread.
        internal sealed class Reader
        {
            internal readonly ApplicationRing Ring;
            private long cursor = -1;
            private float[] stereo = new float[0];
            internal Reader(ApplicationRing ring) { Ring = ring; }

            // Replaces the carrier in data with application audio. A 3D output's carrier holds Unity's pan gain
            // for each channel, applied to the sum of both input channels as Unity does for a stereo clip. A flat
            // output keeps left and right, and its carrier of one half per channel is doubled back.
            internal void Mix(float[] data, int channels, bool spatial)
            {
                if (channels <= 0) return;
                int count = data.Length / channels;
                if (stereo.Length < count * Channels) stereo = new float[count * Channels];
                Ring.Copy(ref cursor, stereo, count);
                for (int frame = 0; frame < count; frame++)
                {
                    float left = Clip(stereo[frame * 2] * Gain), right = Clip(stereo[frame * 2 + 1] * Gain);
                    for (int channel = 0, at = frame * channels; channel < channels; channel++, at++)
                        data[at] = spatial ? data[at] * (left + right) : 2 * data[at] * ((channel & 1) == 0 ? left : right);
                }
            }
        }
    }
}
