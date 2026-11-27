using System;
using System.Diagnostics;
using System.Threading;

namespace SailwindRadio.Audio
{
    // Captured stereo frames in order, with one writer and main-thread readers that never wait.
    // The history covers the longest app delay and leaves room to catch up after a save or hitch.
    internal sealed class ApplicationRing
    {
        internal const int Channels = ProcessLoopback.Channels;
        private const int HistorySeconds = 4;
        // Held audio stays far below this before the radio's gain, even with the overs of heavily limited music. A
        // louder packet comes from a session not held yet, such as a new browser tab or a moved Volume Mixer slider,
        // and plays as silence. The upload path's PeakLimiter keeps what passes from clipping.
        internal const float HeldPeak = HeldVolumes.Level * 4;
        internal const float Gain = 1f / HeldVolumes.Level;
        internal readonly int SampleRate;
        private readonly float[] samples;
        private readonly int frames, crossfadeFrames;
        private long written, writingEnd, generationStart;
        private long lastSoundStartFrame, lastSoundEndFrame;
        private int generation, discontinuities;
        private long lastPacketTimestamp, maximumPacketGapTicks;
        private float lastLeft, lastRight;
        private bool hasLastSample;
        // Milliseconds tick of the last sound. It starts a minute ago, so a new ring is silent.
        private int soundAt = unchecked(Environment.TickCount - 60000);

        internal ApplicationRing(int sampleRate)
        {
            SampleRate = sampleRate;
            frames = sampleRate * HistorySeconds;
            // About 10 ms to blend cleanly across a brief capture drop without clicking or stopping playback.
            crossfadeFrames = Math.Max(16, sampleRate / 100);
            samples = new float[frames * Channels];
        }

        internal int Capacity { get { return frames; } }
        internal int Discontinuities => Volatile.Read(ref discontinuities);
        internal long LastPacketTimestamp => Volatile.Read(ref lastPacketTimestamp);
        internal long MaximumPacketGapTicks => Volatile.Read(ref maximumPacketGapTicks);

        // The application made sound within the last second, so it is playing rather than paused.
        internal bool Sounding { get { return SoundedWithin(1000); } }
        internal bool SoundedWithin(int milliseconds) =>
            unchecked(Environment.TickCount - Volatile.Read(ref soundAt)) < milliseconds;
        internal long LastSoundStartFrame { get { return Volatile.Read(ref lastSoundStartFrame); } }
        internal long LastSoundEndFrame { get { return Volatile.Read(ref lastSoundEndFrame); } }

        // Writer only. Adds up to Capacity frames and returns false when the packet was too loud and became silence.
        // When splice is true (Windows dropped packets during a pause), the new packet blends smoothly from the
        // previous sample so playback stays continuous without a click or silent restart.
        internal bool Write(float[] packet, int count, bool splice = false)
        {
            if (count <= 0 || count > frames || count > packet.Length / Channels)
                throw new ArgumentOutOfRangeException(nameof(count));

            long now = Stopwatch.GetTimestamp();
            if (lastPacketTimestamp != 0)
            {
                long gap = now - lastPacketTimestamp;
                if (gap > maximumPacketGapTicks) Volatile.Write(ref maximumPacketGapTicks, gap);
            }
            Volatile.Write(ref lastPacketTimestamp, now);
            if (splice) Interlocked.Increment(ref discontinuities);

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

            if (splice && hasLastSample)
            {
                int fade = Math.Min(crossfadeFrames, count);
                for (int i = 0; i < fade; i++)
                {
                    float t = (i + 1f) / (fade + 1f);
                    float keep = 1f - t;
                    packet[i * 2] = lastLeft * keep + packet[i * 2] * t;
                    packet[i * 2 + 1] = lastRight * keep + packet[i * 2 + 1] * t;
                }
            }
            lastLeft = packet[length - 2];
            lastRight = packet[length - 1];
            hasLastSample = true;

            long end = written;
            int at = (int)(end % frames), first = Math.Min(count, frames - at);
            // Mark any old frames being overwritten before touching their storage.
            Interlocked.Exchange(ref writingEnd, end + count);
            Array.Copy(packet, 0, samples, at * Channels, first * Channels);
            Array.Copy(packet, first * Channels, samples, 0, (count - first) * Channels);
            // Published after the samples, so a reader that sees the new end also sees them.
            Volatile.Write(ref written, end + count);
            if (held && sound)
            {
                Volatile.Write(ref lastSoundStartFrame, end);
                Volatile.Write(ref lastSoundEndFrame, end + count);
            }
            return held;
        }

        // The process changed. Old samples are no longer eligible, even though their absolute indices are retained.
        internal void Reset()
        {
            Volatile.Write(ref generationStart, written);
            Volatile.Write(ref soundAt, unchecked(Environment.TickCount - 60000));
            Volatile.Write(ref lastPacketTimestamp, 0);
            Volatile.Write(ref lastSoundStartFrame, 0);
            Volatile.Write(ref lastSoundEndFrame, 0);
            lastLeft = lastRight = 0f;
            hasLastSample = false;
            Interlocked.Increment(ref generation);
        }

        internal struct CaptureWindow
        {
            internal readonly long FirstFrame, EndFrame;
            internal readonly int Generation;

            internal CaptureWindow(long firstFrame, long endFrame, int generation)
            {
                FirstFrame = firstFrame;
                EndFrame = endFrame;
                Generation = generation;
            }
        }

        private long OldestFrame => Math.Max(Volatile.Read(ref generationStart), Volatile.Read(ref writingEnd) - frames);

        internal bool TryGetWindow(out CaptureWindow window)
        {
            int current = Volatile.Read(ref generation);
            long end = Volatile.Read(ref written);
            window = new CaptureWindow(Math.Min(OldestFrame, end), end, current);
            return current == Volatile.Read(ref generation);
        }

        // Consumers discard the destination on false. A writer can wrap during a copy, while unrelated
        // appends leave the copy valid and do not make readers wait or retry.
        internal bool TryCopy(long firstFrame, float[] destination, int count, int expectedGeneration)
        {
            if (count <= 0 || count > destination.Length / Channels || expectedGeneration != Volatile.Read(ref generation) ||
                firstFrame < OldestFrame || firstFrame > Volatile.Read(ref written) - count)
                return false;

            int at = (int)(firstFrame % frames), first = Math.Min(count, frames - at);
            Array.Copy(samples, at * Channels, destination, 0, first * Channels);
            Array.Copy(samples, 0, destination, first * Channels, (count - first) * Channels);
            Thread.MemoryBarrier();
            return expectedGeneration == Volatile.Read(ref generation) && firstFrame >= OldestFrame;
        }

        // The player's AppVolume setting, applied on top of the held gain so an app left at a normal volume of its
        // own can still match music.
        internal const float MinimumBoost = .1f, MaximumBoost = 50f;
        private static volatile float boost = 1f;
        internal static float Boost
        {
            get { return boost; }
            set { boost = Single.IsNaN(value) ? 1f : Math.Min(MaximumBoost, Math.Max(MinimumBoost, value)); }
        }
    }
}
