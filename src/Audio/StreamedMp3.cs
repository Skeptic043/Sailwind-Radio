using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SailwindRadio
{
    // One NLayer decoder feeds all outputs. Audio callbacks only copy published,
    // immutable PCM chunks and never touch the decoder, disk, or Unity objects.
    internal sealed class StreamedMp3 : IDisposable
    {
        private const int ChunkFrames = 4096;
        private const int WindowSeconds = 20;
        private const int AheadSeconds = 10;
        private const int SeekPrerollSeconds = 5;
        private NLayer.MpegFile decoder;
        private Stream input;
        private readonly string path;
        private readonly CancellationTokenSource cancellation;
        private readonly Chunk[] chunks;
        private readonly Task worker;
        private long requestedFrame;
        private Exception fault;
        private volatile bool disposed;
        internal readonly int Channels, SampleRate, Frames;
        internal int BufferSamples { get { return chunks.Length * ChunkFrames * Channels; } }
        internal bool WorkerCompleted { get { return worker.IsCompleted; } }
        internal Exception Fault { get { return Volatile.Read(ref fault); } }

        private sealed class Chunk
        {
            // -1 means the worker owns the slot, positive values are active readers.
            internal int State;
            internal int Start, Count;
            internal readonly float[] Samples;
            internal Chunk(int channels) { Samples = new float[ChunkFrames * channels]; }
        }

        internal sealed class Reader
        {
            private readonly StreamedMp3 owner;
            private int cursor;
            private int generation;
            internal Reader(StreamedMp3 owner) { this.owner = owner; }
            internal int Cursor { get { return Volatile.Read(ref cursor); } }
            internal int Generation { get { return Volatile.Read(ref generation); } }
            internal void SetPosition(int position)
            {
                position = Math.Max(0, Math.Min(owner.Frames - 1, position));
                int previous = Volatile.Read(ref cursor);
                Interlocked.Increment(ref generation);
                Volatile.Write(ref cursor, position);
                if (position != previous) owner.Request(position);
            }
            internal void Read(float[] data)
            {
                Array.Clear(data, 0, data.Length);
                if (owner.disposed) return;
                int observedGeneration = Volatile.Read(ref generation);
                int start = Volatile.Read(ref cursor);
                int frames = data.Length / owner.Channels;
                owner.Copy(start, data, frames);
                int advanced = start > owner.Frames - frames ? owner.Frames : start + frames;
                CommitRead(observedGeneration, start, advanced);
            }
            internal void CommitRead(int observedGeneration, int start, int advanced)
            {
                if (Volatile.Read(ref generation) == observedGeneration)
                    Interlocked.CompareExchange(ref cursor, advanced, start);
            }
        }

        internal static StreamedMp3 Open(string path, int initialFrame, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            var lifetime = CancellationTokenSource.CreateLinkedTokenSource(token);
            RadioPlayback.CancellableFileStream input = null;
            try
            {
                input = new RadioPlayback.CancellableFileStream(path, lifetime.Token);
                if (input.Length > RadioPlayback.MaximumStreamedMp3FileBytes)
                    throw new IOException("MP3 exceeds the streamed file size limit");
                var decoder = new NLayer.MpegFile(input);
                try
                {
                    int channels = decoder.Channels, rate = decoder.SampleRate;
                    long bytes = decoder.Length;
                    if (!decoder.CanSeek || channels < 1 || channels > 2 || rate < 8000 || rate > 96000 ||
                        bytes <= 0 || bytes % (channels * sizeof(float)) != 0)
                        throw new IOException("MP3 cannot be streamed with a known seekable duration");
                    long frames = bytes / (channels * sizeof(float));
                    // Unity AudioClip.Create and AudioSource.timeSamples both use int frames.
                    if (frames > Int32.MaxValue - rate * 2L)
                        throw new IOException("MP3 duration exceeds Unity's sample-position limit");
                    return new StreamedMp3(path, input, decoder, lifetime, channels, rate, (int)frames, initialFrame);
                }
                catch { decoder.Dispose(); throw; }
            }
            catch { if (input != null) input.Dispose(); lifetime.Dispose(); throw; }
        }

        private StreamedMp3(string path, Stream input, NLayer.MpegFile decoder, CancellationTokenSource cancellation,
            int channels, int rate, int frames, int initialFrame)
        {
            this.path = path;
            this.input = input;
            this.decoder = decoder;
            this.cancellation = cancellation;
            Channels = channels;
            SampleRate = rate;
            Frames = frames;
            chunks = new Chunk[Math.Max(2, (WindowSeconds * rate + ChunkFrames - 1) / ChunkFrames)];
            for (int i = 0; i < chunks.Length; i++) chunks[i] = new Chunk(channels);
            requestedFrame = Math.Max(0, Math.Min(frames - 1, initialFrame));
            worker = Task.Run(Run);
        }

        internal Reader CreateReader() { return new Reader(this); }
        internal AudioClip CreateClip(Reader reader)
        {
            if (disposed) throw new ObjectDisposedException("StreamedMp3");
            return AudioClip.Create("Sailwind Radio streamed MP3", Frames, Channels, SampleRate, true,
                reader.Read, reader.SetPosition);
        }

        internal void Request(int frame)
        { Interlocked.Exchange(ref requestedFrame, Math.Max(0, Math.Min(Frames - 1, frame))); }

        internal bool Ready(int frame)
        {
            if (disposed) return false;
            frame = Math.Max(0, Math.Min(Frames - 1, frame));
            int end = (int)Math.Min((long)Frames, (long)frame + Math.Max(1, SampleRate / 2));
            for (int at = frame; at < end; at = ((at / ChunkFrames) + 1) * ChunkFrames)
            {
                Chunk chunk = chunks[(at / ChunkFrames) % chunks.Length];
                if (Volatile.Read(ref chunk.State) < 0 || at < chunk.Start || at >= chunk.Start + chunk.Count) return false;
            }
            return true;
        }

        private void Copy(int frame, float[] destination, int frames)
        {
            if (frame < 0 || frame >= Frames) return;
            int at = Math.Max(0, frame), end = (int)Math.Min((long)Frames, (long)Math.Max(0, frame) + frames);
            while (at < end)
            {
                int slot = (at / ChunkFrames) % chunks.Length;
                Chunk chunk = chunks[slot];
                int take = Math.Min(end - at, ChunkFrames - at % ChunkFrames);
                int readers;
                do { readers = Volatile.Read(ref chunk.State); }
                while (readers >= 0 && Interlocked.CompareExchange(ref chunk.State, readers + 1, readers) != readers);
                if (readers >= 0)
                {
                    try
                    {
                        if (at >= chunk.Start && at + take <= chunk.Start + chunk.Count)
                            Array.Copy(chunk.Samples, (at - chunk.Start) * Channels,
                                destination, (at - frame) * Channels, take * Channels);
                    }
                    finally { Interlocked.Decrement(ref chunk.State); }
                }
                at += take; // Missing chunks are silence, never a blocking read.
            }
        }

        private void Run()
        {
            int next = -1;
            try
            {
                while (!cancellation.IsCancellationRequested)
                {
                    int target = (int)Interlocked.Read(ref requestedFrame);
                    int ahead = (int)Math.Min((long)Frames, (long)target + AheadSeconds * SampleRate);
                    if (next < target - SampleRate * 2 || next > ahead || next < 0 ||
                        (next >= ahead && !Ready(target)))
                    {
                        bool wasDecoding = next >= 0;
                        next = (target / ChunkFrames) * ChunkFrames;
                        // Reopen on a discontinuity. NLayer can retain a stale read
                        // buffer across a backward seek on unindexed MP3s.
                        if (wasDecoding) Reopen();
                        // MP3 bit reservoirs and decoder state need a short preroll.
                        // Starting from zero also avoids NLayer's short-file seek bug.
                        int anchor = Math.Max(0, next - SeekPrerollSeconds * SampleRate);
                        if (anchor > 0)
                            decoder.Position = (long)anchor * Channels * sizeof(float);
                        long actual = decoder.Position / (Channels * sizeof(float));
                        if (anchor > 0)
                        {
                            // Pinned NLayer 1.16 reports the preceding frame's PCM
                            // position after an indexed seek, although its next read
                            // starts one MPEG Layer III frame later.
                            actual += SampleRate >= 32000 ? 1152 : 576;
                        }
                        if (actual > next) throw new IOException("MP3 decoder seek passed the requested sample");
                        float[] discard = new float[ChunkFrames * Channels];
                        while (actual < next)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            int wanted = (int)Math.Min(next - actual, ChunkFrames) * Channels;
                            int got = decoder.ReadSamples(discard, 0, wanted);
                            if (got <= 0 || got % Channels != 0) throw new IOException("MP3 decoder could not finish seek");
                            actual += got / Channels;
                        }
                    }
                    if (next >= ahead || next >= Frames)
                    { cancellation.Token.WaitHandle.WaitOne(20); continue; }
                    Chunk chunk = chunks[(next / ChunkFrames) % chunks.Length];
                    while (Interlocked.CompareExchange(ref chunk.State, -1, 0) != 0)
                    {
                        cancellation.Token.ThrowIfCancellationRequested();
                        cancellation.Token.WaitHandle.WaitOne(1);
                    }
                    int count = Math.Min(ChunkFrames, Frames - next) * Channels;
                    int read = 0;
                    try
                    {
                        while (read < count)
                        {
                            cancellation.Token.ThrowIfCancellationRequested();
                            int got = decoder.ReadSamples(chunk.Samples, read, count - read);
                            if (got <= 0) break;
                            read += got;
                        }
                        if (read == 0) throw new IOException("MP3 ended before its reported duration");
                        if (read % Channels != 0) throw new IOException("MP3 decoder returned an incomplete frame");
                        for (int i = 0; i < read; i++)
                            if (Single.IsNaN(chunk.Samples[i]) || Single.IsInfinity(chunk.Samples[i])) chunk.Samples[i] = 0f;
                            else chunk.Samples[i] = Math.Max(-1f, Math.Min(1f, chunk.Samples[i]));
                        chunk.Start = next;
                        chunk.Count = read / Channels;
                    }
                    finally { Volatile.Write(ref chunk.State, 0); }
                    next += read / Channels;
                }
            }
            catch (OperationCanceledException) { }
            catch (Exception ex) { Volatile.Write(ref fault, ex); }
            finally { decoder.Dispose(); input.Dispose(); }
        }

        private void Reopen()
        {
            decoder.Dispose();
            input.Dispose();
            input = new RadioPlayback.CancellableFileStream(path, cancellation.Token);
            decoder = new NLayer.MpegFile(input);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            cancellation.Cancel();
            worker.ContinueWith(done =>
            {
                if (done.IsFaulted) { var observed = done.Exception; }
                cancellation.Dispose();
            }, TaskScheduler.Default);
        }
    }
}
