using System;
using SailwindRadio.Audio;
using UnityEngine;

static partial class AudioChecks
{
    static void CheckBufferedAudioClip()
    {
        AudioClip clip;
        using (var buffer = new BufferedAudioClip("PCM fixture", 48000, 2, 7, 4))
        {
            clip = buffer.Clip;
            Check(clip.samples == 8 && clip.channels == 2 && clip.frequency == 48000 &&
                !clip.Streaming && clip.PcmRead == null && clip.PcmSetPosition == null,
                "Buffer creates a bounded clip with no managed PCM callbacks");

            buffer.Reset(37);
            float[] scratch = null;
            int reads = 0;
            BufferedAudioClip.ReadFrames read = (first, data, frames) =>
            {
                if (scratch == null) scratch = data;
                Check(ReferenceEquals(data, scratch), "Uploads reuse one staging block");
                reads++;
                for (int i = 0; i < frames; i++)
                {
                    data[2 * i] = (first + i) / 1000f;
                    data[2 * i + 1] = -(first + i) / 1000f;
                }
                return true;
            };

            Check(!buffer.TryAppend(37, 4, (first, data, frames) => false) && buffer.WrittenThrough == 37,
                "Missing PCM leaves the published clip extent at the same frame so the upload can be retried");
            Check(buffer.TryAppend(37, 4, read) && buffer.TryAppend(37, 4, read) && buffer.WrittenThrough == 45,
                "A saved nonzero song position starts a continuous clip timeline");
            Check(!buffer.TryAppend(40, 4, read) && reads == 2 && buffer.WrittenThrough == 45,
                "Refill cannot overwrite even one frame still retained for scheduled voices");
            Check(buffer.TryAppend(41, 4, read) && buffer.FirstFrame == 41 && buffer.WrittenThrough == 49 &&
                buffer.ClipFrame(41) == 4 && buffer.ClipFrame(45) == 0,
                "The next block wraps once the earlier PCM has been consumed");
            var ringData = new float[16];
            Check(clip.GetData(ringData, 0), "The clip contains uploaded PCM");
            Check(ringData[0] == .045f && ringData[1] == -.045f && ringData[8] == .041f && ringData[9] == -.041f,
                "Wrapping replaces only the released block and preserves stereo samples");

            buffer.Reset(50);
            Check(buffer.FirstFrame == 50 && buffer.WrittenThrough == 50 && buffer.ClipFrame(50) == 0,
                "Rebasing invalidates the previous playback extent and starts a new timeline at clip frame zero");
            Check(buffer.TryAppend(50, 2, read) && buffer.WrittenThrough == 52,
                "A short tail publishes only real source frames");
            Check(clip.GetData(ringData, 0) && ringData[0] == .050f && ringData[1] == -.050f &&
                ringData[2] == .051f && ringData[3] == -.051f && ringData[4] == 0 && ringData[7] == 0,
                "The final short block clears its unused frames instead of replaying old samples");
        }
        Check(clip.Destroyed, "Disposing the shared buffer releases the Unity clip");
    }
}
