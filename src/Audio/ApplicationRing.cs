using System;
using System.Threading;

namespace SailwindRadio.Audio
{
    // One second of held application audio. One writer adds captured stereo packets, and each output's audio
    // thread reads through its own Reader 40 ms behind the newest audio, never waiting for it. Every reader plays
    // the same captured frame at the same output frame, so a radio and its speakers never echo each other.
    internal sealed class ApplicationRing
    {
        internal const int Channels = ProcessLoopback.Channels;
        // Held audio stays far below this before the radio's gain, even with the overs of heavily limited music. A
        // louder packet comes from a session not held yet, such as a new browser tab or a moved Volume Mixer slider,
        // and plays as silence. Each output's RadioOutputLimiter keeps what passes from clipping.
        internal const float HeldPeak = HeldVolumes.Level * 4;
        internal const float Gain = 1f / HeldVolumes.Level;
        internal readonly int SampleRate;
        private readonly float[] samples;
        private readonly int frames, margin, maximumLag;
        private long written;
        // The captured frame minus the output frame that plays it, shared by every reader. Unaligned until first read.
        private const long Unaligned = long.MinValue;
        private long offset = Unaligned;
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

        // Copies the count frames that play at output frame, realigning every reader to just behind the newest
        // audio when the shared position falls out of range. Audio not captured yet plays as silence.
        private void Copy(long frame, float[] destination, int count)
        {
            long end = Volatile.Read(ref written), shift = Volatile.Read(ref offset);
            long cursor = shift == Unaligned ? -1 : frame + shift;
            if (shift == Unaligned || cursor < 0 || cursor + count > end || end - cursor > maximumLag + count)
            {
                cursor = end - count - margin;
                Volatile.Write(ref offset, cursor - frame);
            }
            if (cursor < 0)
            {
                Array.Clear(destination, 0, count * Channels);
                return;
            }
            int at = (int)(cursor % frames), first = Math.Min(count, frames - at);
            Array.Copy(samples, at * Channels, destination, 0, first * Channels);
            Array.Copy(samples, 0, destination, first * Channels, (count - first) * Channels);
        }

        // The player's AppVolume setting, applied on top of the held gain so an app left at a normal volume of its
        // own can still match music. Read by every output's audio thread.
        internal const float MinimumBoost = .1f, MaximumBoost = 50f;
        private static volatile float boost = 1f;
        internal static float Boost
        {
            get { return boost; }
            set { boost = Single.IsNaN(value) ? 1f : Math.Min(MaximumBoost, Math.Max(MinimumBoost, value)); }
        }

        // One output's mixing buffer. Used only on that output's audio thread.
        internal sealed class Reader
        {
            internal readonly ApplicationRing Ring;
            private float[] stereo = new float[0];
            internal Reader(ApplicationRing ring) { Ring = ring; }

            // Replaces the carrier in data with application audio. A 3D output's carrier holds Unity's pan gain
            // for each channel, applied to the sum of both input channels as Unity does for a stereo clip. A flat
            // output keeps left and right, and its carrier of one half per channel is doubled back. Start is the
            // output frame the block begins at, the same for every output in one Unity mix. Boosted audio may pass
            // full scale here, and the output's own RadioOutputLimiter holds it after every filter.
            internal void Mix(float[] data, int channels, bool spatial, long start)
            {
                if (channels <= 0) return;
                int count = data.Length / channels;
                if (stereo.Length < count * Channels) stereo = new float[count * Channels];
                Ring.Copy(start, stereo, count);
                float gain = Gain * Boost;
                for (int frame = 0; frame < count; frame++)
                {
                    float left = stereo[frame * 2] * gain, right = stereo[frame * 2 + 1] * gain;
                    for (int channel = 0, at = frame * channels; channel < channels; channel++, at++)
                        data[at] = spatial ? data[at] * (left + right) : 2 * data[at] * ((channel & 1) == 0 ? left : right);
                }
            }
        }
    }
}
