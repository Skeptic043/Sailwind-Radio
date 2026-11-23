using System;
using SailwindRadio;
using SailwindRadio.Audio;
using UnityEngine;

// The application ring and callback-free buffered upload path between capture and Unity's native voices.
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
        ApplicationRing.CaptureWindow window;
        var copy = new float[2048 * 2];
        Check(ring.TryGetWindow(out window) && ring.TryCopy(window.FirstFrame, copy, 2048, window.Generation) && MaxAbs(copy) == 0,
            "an unheld packet plays as silence");

        var nan = Packet(2048, held);
        nan[7] = Single.NaN;
        Check(!ring.Write(nan, 2048) && Finite(nan), "a packet with NaN is silenced");

        // A held full-scale peak passes the guard.
        ring = new ApplicationRing(rate);
        Check(ring.Write(Packet(2048, HeldVolumes.Level), 2048), "a held full-scale packet passes the guard");

        // Sound marks the application as playing, and a paused player's digital silence does not.
        ring = new ApplicationRing(rate);
        ring.Write(Packet(480, 0f), 480);
        Check(!ring.Sounding, "silence is not playing");
        ring.Write(Packet(480, held), 480);
        Check(ring.Sounding, "held music is playing");

        // Smooth crossfade splice across a capture drop keeps the stream continuous without a step jump.
        ring = new ApplicationRing(rate);
        ring.Write(Stereo(1024, held, -held), 1024);
        ring.Write(Stereo(1024, -held, held), 1024, splice: true);
        Check(ring.TryGetWindow(out window) && window.EndFrame == 2048 && ring.Discontinuities == 1,
            "a capture drop records a discontinuity without resetting playback history");
        var spliced = new float[1024 * 2];
        Check(ring.TryCopy(1024, spliced, 1024, window.Generation), "spliced frames remain readable in the same generation");
        // First spliced frame stays close to previous tail (held, -held) instead of jumping straight to (-held, held).
        Check(spliced[0] > held * .9f && spliced[1] < -held * .9f && Near(spliced[2046], -held) && Near(spliced[2047], held),
            "a capture drop crossfades smoothly from the previous sample into the new packet");

        CheckPeakLimiter(rate);
        CheckBufferedApplicationHistory(rate, held);
        CheckBufferedApplicationPlayback(rate, held);
        CheckBufferedWooferAndLiveBoost(rate, held);
    }

    static void CheckBufferedApplicationPlayback(int rate, float held)
    {
        double previousTime = AudioSettings.dspTime;
        AudioSettings.dspTime = 90;
        var ring = new ApplicationRing(rate);
        using (var player = new RadioPlayback(new MonoBehaviour(), new RadioState(), _ => { }))
        {
            var radio = AudioSource.Last;
            player.SetEndpoint(1, new Vector3(0, 0, 0), false, DeviceKind.WalnutSpeaker, 1, .5f, 0);
            var speaker = AudioSource.Last;
            ring.Write(Stereo(rate / 4, held, -held), rate / 4);
            player.UpdateBufferedApplication(ring, .5f);
            Check(radio.Schedules == 0, "The delayed app waits for its selected reserve");

            ring.Write(Stereo(rate / 2, held, -held), rate / 2);
            player.UpdateBufferedApplication(ring, .5f);
            Check(radio.Schedules == 1 && speaker.Schedules == 1 && radio.clip == speaker.clip &&
                !radio.clip.Streaming && radio.clip.PcmRead == null &&
                radio.timeSamples == speaker.timeSamples && radio.ScheduledAt == speaker.ScheduledAt &&
                radio.ScheduledEnd == speaker.ScheduledEnd && radio.ScheduledEnd - radio.ScheduledAt >= .45,
                "Radio and speakers play the same callback-free buffered clip through the last published frames");

            var clip = radio.clip;
            double originalEnd = radio.ScheduledEnd;
            AudioSettings.dspTime += .10;
            ring.Write(Stereo(rate / 10, held * .5f, -held * .5f), rate / 10);
            player.UpdateBufferedApplication(ring, .5f);
            Check(radio.Schedules == 1 && radio.clip == clip && radio.ScheduledEnd > originalEnd &&
                radio.ScheduledEnd == speaker.ScheduledEnd,
                "New captured audio extends the existing schedule without a track-transition restart");

            // Zero extra delay still uses the callback-free buffered clip with low latency.
            ring.Reset();
            AudioSettings.dspTime += .10;
            player.UpdateBufferedApplication(ring, 0f);
            Check(!radio.isPlaying && !speaker.isPlaying, "A new capture session invalidates the previous app's queued audio");
            ring.Write(Stereo(rate / 5, held, -held), rate / 5);
            player.UpdateBufferedApplication(ring, 0f);
            Check(radio.Schedules == 2 && !radio.clip.Streaming && radio.clip.PcmRead == null,
                "Zero app delay still uses the callback-free buffered clip");

            // Raising AppBufferSeconds waits to fill the new reserve without jumping backward into old audio.
            AudioSettings.dspTime += .05;
            player.UpdateBufferedApplication(ring, 1.0f);
            Check(radio.Schedules == 2 && !radio.isPlaying, "Raising app buffer waits for fresh reserve instead of replaying old audio");
            ring.Write(Stereo(rate + rate / 4, held, -held), rate + rate / 4);
            player.UpdateBufferedApplication(ring, 1.0f);
            Check(radio.Schedules == 3 && radio.ScheduledEnd - radio.ScheduledAt >= 1.0,
                "Raised app buffer starts once the full reserve is queued");

            // Pausing the game wipes the buffer so unpausing skips wake-up silence and rebuilds a full fresh reserve.
            AudioSettings.dspTime += .15;
            player.StopBufferedApplication();
            ring.Write(Stereo(rate / 20, held, -held), rate / 20); // Pre-pause tail before Spotify finishes pausing.
            player.StopBufferedApplication();
            AudioSettings.dspTime += 2.0;
            player.UpdateBufferedApplication(ring, 1.0f);
            Check(radio.Schedules == 3 && !radio.isPlaying,
                "Unpausing wipes pre-pause audio and waits to rebuild a full fresh buffer");
            // Digital silence while the external app wakes up must be skipped rather than buffered ahead of music.
            ring.Write(new float[rate * 2], rate);
            player.UpdateBufferedApplication(ring, 1.0f);
            Check(radio.Schedules == 3 && !radio.isPlaying,
                "Wake-up digital silence is skipped instead of filling the buffer with silence");
            ring.Write(Stereo(rate / 2, held, -held), rate / 2);
            player.UpdateBufferedApplication(ring, 1.0f);
            Check(radio.Schedules == 3 && !radio.isPlaying,
                "Partial fresh audio keeps building until the full delay reserve is ready");
            ring.Write(Stereo(rate * 3 / 4, held, -held), rate * 3 / 4);
            player.UpdateBufferedApplication(ring, 1.0f);
            Check(radio.Schedules == 4 && radio.ScheduledEnd - radio.ScheduledAt >= 1.0,
                "Unpausing returns to audio once the full fresh buffer reserve is rebuilt");
        }
        AudioSettings.dspTime = previousTime;
    }

    static void CheckBufferedWooferAndLiveBoost(int rate, float held)
    {
        double previousTime = AudioSettings.dspTime;
        AudioSettings.dspTime = 120;
        try
        {
            ApplicationRing.Boost = 10f;
            var ring = new ApplicationRing(rate);
            var tone = new float[rate * 2];
            for (int frame = 0; frame < rate; frame++)
                tone[frame * 2] = tone[frame * 2 + 1] = held * (float)Math.Sin(2 * Math.PI * 70 * frame / rate);
            ring.Write(tone, rate);

            using (var player = new RadioPlayback(new MonoBehaviour(), new RadioState { Powered = true, Volume = 1f }, _ => { }))
            {
                var radio = AudioSource.Last;
                player.SetEndpoint(2, new Vector3(0, 0, 0), false, DeviceKind.WalnutWoofer, 1f, 1f, 0f);
                var woofer = AudioSource.Last;
                player.UpdateBufferedApplication(ring, .5f);
                Check(radio.Schedules == 1 && woofer.Schedules == 1 && radio.clip != null && woofer.clip != null &&
                    radio.clip != woofer.clip, "A connected woofer receives its own pre-filtered buffered clip");
                var mainSamples = new float[radio.clip.samples * 2];
                var wooferSamples = new float[woofer.clip.samples * 2];
                radio.clip.GetData(mainSamples, 0);
                woofer.clip.GetData(wooferSamples, 0);
                Check(MaxAbs(mainSamples) <= 1f + 1e-4f && MaxAbs(mainSamples) > .5f,
                    "Boosted app audio is limited cleanly to full scale before upload");
                Check(MaxAbs(wooferSamples) <= 1f + 1e-4f && MaxAbs(wooferSamples) > .5f,
                    "Boosted woofer audio is bass-filtered and limited cleanly to full scale before upload");

                // Dropping AppVolume while queued rewrites future blocks immediately.
                ApplicationRing.Boost = .2f;
                AudioSettings.dspTime += .05;
                player.UpdateBufferedApplication(ring, .5f);
                var updated = new float[radio.clip.samples * 2];
                radio.clip.GetData(updated, 0);
                int tailStart = (radio.timeSamples + 4096) % radio.clip.samples;
                float tailPeak = 0f;
                for (int i = 0; i < 512; i++)
                {
                    int idx = ((tailStart + i) % radio.clip.samples) * 2;
                    tailPeak = Math.Max(tailPeak, Math.Abs(updated[idx]));
                }
                Check(tailPeak < .2f, "Changing App volume rewrites queued future blocks immediately");
            }
        }
        finally
        {
            ApplicationRing.Boost = 1f;
            AudioSettings.dspTime = previousTime;
        }
    }

    static void CheckBufferedApplicationHistory(int rate, float held)
    {
        var ring = new ApplicationRing(rate);
        Check(ring.Capacity >= rate * 4, "capture keeps 2.5 seconds of app delay with time for the player to catch up");
        int packetFrames = rate / 10;
        for (int packet = 1; packet <= 45; packet++)
        {
            float level = held * packet / 50;
            ring.Write(Stereo(packetFrames, level, -level), packetFrames);
        }

        ApplicationRing.CaptureWindow window;
        Check(ring.TryGetWindow(out window) && window.EndFrame == 45L * packetFrames &&
            window.FirstFrame == window.EndFrame - ring.Capacity, "capture advances monotonically and publishes its retained frame window");
        var copy = new float[8];
        Check(ring.TryCopy(window.EndFrame - rate * 5 / 2, copy, 4, window.Generation) &&
            Math.Abs(copy[0] - held * 21 / 50) < 1e-8f && copy[1] == -copy[0],
            "the delayed consumer can read guarded stereo PCM at 2.5 seconds");
        Check(ring.TryCopy(ring.Capacity - 2, copy, 4, window.Generation) &&
            Math.Abs(copy[0] - held * 40 / 50) < 1e-8f && Math.Abs(copy[4] - held * 41 / 50) < 1e-8f,
            "a delayed read crosses the storage wrap in frame order");
        Check(!ring.TryCopy(window.FirstFrame - 1, copy, 4, window.Generation), "older overwritten PCM is unavailable");
        Check(!ring.TryCopy(window.EndFrame - 3, copy, 4, window.Generation) && ring.Discontinuities == 0,
            "an incomplete read returns no PCM without recording a capture discontinuity");

        ring.Write(Stereo(packetFrames, held, -held), packetFrames, splice: true);
        ApplicationRing.CaptureWindow spliced;
        Check(ring.TryGetWindow(out spliced) && spliced.Generation == window.Generation &&
            spliced.EndFrame == window.EndFrame + packetFrames &&
            ring.Discontinuities == 1 && ring.LastPacketTimestamp > 0,
            "a spliced capture drop stays in the same generation and keeps playback continuous");

        ring.Reset();
        ApplicationRing.CaptureWindow restarted;
        Check(ring.TryGetWindow(out restarted) && restarted.Generation != spliced.Generation &&
            restarted.FirstFrame == spliced.EndFrame && restarted.EndFrame == spliced.EndFrame &&
            !ring.Sounding && ring.LastPacketTimestamp == 0 && ring.Discontinuities == 1,
            "a process restart drops stale PCM and does not count as a dropped packet");
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
