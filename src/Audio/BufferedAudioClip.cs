using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    // Main-thread owner of PCM uploaded to one Unity clip. Each output uses the same clip but its own voice.
    internal sealed class BufferedAudioClip : IDisposable
    {
        internal delegate bool ReadFrames(long firstFrame, float[] destination, int frames);

        private readonly int channels;
        private readonly float[] upload, silence;
        private long origin;
        private bool finished;

        internal AudioClip Clip { get; private set; }
        internal int BlockFrames { get; }
        internal int CapacityFrames { get; }
        internal long FirstFrame { get; private set; }
        internal long WrittenThrough { get; private set; }

        internal BufferedAudioClip(string name, int sampleRate, int channels, int capacityFrames, int blockFrames)
        {
            if (sampleRate <= 0) throw new ArgumentOutOfRangeException(nameof(sampleRate));
            if (channels <= 0) throw new ArgumentOutOfRangeException(nameof(channels));
            if (capacityFrames <= 0) throw new ArgumentOutOfRangeException(nameof(capacityFrames));
            if (blockFrames <= 0) throw new ArgumentOutOfRangeException(nameof(blockFrames));
            this.channels = channels;
            BlockFrames = blockFrames;
            CapacityFrames = checked(((capacityFrames - 1) / blockFrames + 1) * blockFrames);
            upload = new float[checked(blockFrames * channels)];
            silence = new float[upload.Length];
            Clip = AudioClip.Create(name, CapacityFrames, channels, sampleRate, false);
            if (Clip == null) throw new InvalidOperationException("Unity could not create a buffered audio clip");
        }

        // The caller stops all voices before resetting. Only [FirstFrame, WrittenThrough) can be played.
        internal void Reset(long firstFrame)
        {
            if (firstFrame < 0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
            origin = FirstFrame = WrittenThrough = firstFrame;
            finished = false;
        }

        // Re-anchors the logical frame window onto a specific clip sample offset without stopping a running voice.
        internal void Reanchor(long firstFrame, int clipSample)
        {
            if (firstFrame < 0) throw new ArgumentOutOfRangeException(nameof(firstFrame));
            int aligned = ((clipSample % CapacityFrames) + CapacityFrames) % CapacityFrames;
            aligned = (aligned / BlockFrames) * BlockFrames;
            origin = firstFrame - aligned;
            FirstFrame = WrittenThrough = firstFrame;
            finished = false;
        }

        internal bool ClearBlock(long frame)
        {
            if (Clip == null) throw new ObjectDisposedException(nameof(BufferedAudioClip));
            return Clip.SetData(silence, ClipFrame(frame));
        }

        internal void ClearAll()
        {
            if (Clip == null) throw new ObjectDisposedException(nameof(BufferedAudioClip));
            for (int offset = 0; offset < CapacityFrames; offset += BlockFrames)
                Clip.SetData(silence, offset);
        }

        internal int ClipFrame(long frame)
        {
            long offset = (frame - origin) % CapacityFrames;
            return (int)(offset < 0 ? offset + CapacityFrames : offset);
        }

        // The caller chooses how far behind the playhead Unity may still read. Writing into that region would
        // change audio that is about to play. A short block is only used at the end of a finite source.
        internal bool TryAppend(long retainFromFrame, int frames, ReadFrames read)
        {
            if (Clip == null) throw new ObjectDisposedException(nameof(BufferedAudioClip));
            if (frames <= 0 || frames > BlockFrames) throw new ArgumentOutOfRangeException(nameof(frames));
            if (finished) throw new InvalidOperationException("The final audio block is already uploaded");

            long nextFirst = Math.Max(FirstFrame, checked(WrittenThrough + BlockFrames) - CapacityFrames);
            if (nextFirst > FirstFrame && nextFirst > retainFromFrame) return false;
            if (!read(WrittenThrough, upload, frames)) return false;

            if (frames < BlockFrames) Array.Clear(upload, frames * channels, (BlockFrames - frames) * channels);
            if (!Clip.SetData(upload, ClipFrame(WrittenThrough)))
                throw new InvalidOperationException("Unity could not upload buffered audio");

            FirstFrame = nextFirst;
            WrittenThrough += frames;
            finished = frames < BlockFrames;
            return true;
        }

        // Re-uploads one already-published block in place, for live volume slider updates ahead of the playhead.
        internal bool TryOverwrite(long firstFrame, int frames, ReadFrames read)
        {
            if (Clip == null) throw new ObjectDisposedException(nameof(BufferedAudioClip));
            if (frames <= 0 || frames > BlockFrames || firstFrame < FirstFrame || firstFrame + frames > WrittenThrough)
                return false;
            int offset = ClipFrame(firstFrame);
            if (offset + frames > CapacityFrames) return false;
            if (!read(firstFrame, upload, frames)) return false;
            if (frames < BlockFrames)
            {
                var slice = new float[frames * channels];
                Array.Copy(upload, 0, slice, 0, slice.Length);
                return Clip.SetData(slice, offset);
            }
            return Clip.SetData(upload, offset);
        }

        public void Dispose()
        {
            if (Clip != null) UnityEngine.Object.Destroy(Clip);
            Clip = null;
        }
    }
}
