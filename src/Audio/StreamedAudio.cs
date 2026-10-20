using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;

namespace SailwindRadio.Audio
{
    // One decoder feeds all outputs. Its own worker opens the file, so the Unity thread never
    // touches disk or decoding. Audio callbacks only copy published, immutable PCM chunks and
    // never touch the decoder, disk, or Unity objects.
    internal sealed class StreamedAudio : IDisposable
    {
        private const int ChunkFrames = 4096;
        private const int WindowSeconds = 20;
        private const int AheadSeconds = 10;
        // Unity 2019.1 pre-reads 0.8 s of a streamed clip when it is created or seeks, and keeps reading that far
        // ahead. A start waits for this much decoded audio, so the pre-read never meets a missing chunk.
        private const int ReadyMilliseconds = 1000;
        private const int NoFrame = Int32.MaxValue;
        private readonly string path;
        private readonly double initialSeconds;
        private readonly CancellationTokenSource cancellation = new CancellationTokenSource();
        // Wakes the idle worker when the main thread moves its target.
        private readonly AutoResetEvent wake = new AutoResetEvent(false);
        private readonly Task worker;
        private IPcmDecoder decoder;
        private Chunk[] chunks;
        // Ring slot plus one for each track chunk, or zero. Readers still check the slot's Start,
        // because the worker may reuse it at any moment.
        private int[] slots;
        private long requestedFrame;
        // Worker only. Frames from here on are silence because the decoder ended early or failed.
        private int dataEnd = NoFrame;
        // Worker only. A fill has completed, so a later decode failure ends playback instead of failing it.
        private bool produced;
        private bool reseek;
        private int channels, sampleRate, frames;
        private Exception fault, damage;
        private int damageFrame = NoFrame;
        private volatile bool opened;
        private volatile bool disposed;
        private int missingFrames;
        // Layout fields are published by the worker before IsOpen becomes true.
        internal bool IsOpen { get { return opened; } }
        internal int Channels { get { return channels; } }
        internal int SampleRate { get { return sampleRate; } }
        internal int Frames { get { return frames; } }
        internal int BufferSamples { get { return opened ? chunks.Length * ChunkFrames * channels : 0; } }
        internal bool WorkerCompleted { get { return worker.IsCompleted; } }
        // The stream failed before producing audio, so the track cannot play.
        internal Exception Fault { get { return Volatile.Read(ref fault); } }
        // First frame lost to a decode failure at or ahead of the worker target after audio was produced,
        // or Int32.MaxValue. Frames before it play normally and frames from it on are silence.
        internal int DamageFrame { get { return Volatile.Read(ref damageFrame); } }
        internal Exception Damage { get { return Volatile.Read(ref damage); } }
        // Diagnostic count of frames Unity read before their chunk was decoded, so they played as silence unless
        // a later seek discarded them. The editor probe observes start underruns with it.
        internal int MissingFrames { get { return Volatile.Read(ref missingFrames); } }

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
            private readonly StreamedAudio owner;
            private int cursor;
            private int generation;
            internal Reader(StreamedAudio owner) { this.owner = owner; }
            internal int Cursor { get { return Volatile.Read(ref cursor); } }
            internal int Generation { get { return Volatile.Read(ref generation); } }
            // Moves only this reader. The main thread alone decides what the worker decodes.
            internal void SetPosition(int position)
            {
                position = Math.Max(0, Math.Min(owner.Frames - 1, position));
                Interlocked.Increment(ref generation);
                Volatile.Write(ref cursor, position);
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

        // Returns at once. Open failures appear as Fault, and disposal cancels an unfinished open.
        internal static StreamedAudio Start(string path, double initialSeconds) { return new StreamedAudio(path, initialSeconds); }

        private StreamedAudio(string path, double initialSeconds)
        {
            this.path = path;
            this.initialSeconds = initialSeconds;
            // A dedicated thread, because the loop lives as long as the track.
            worker = Task.Factory.StartNew(Run, CancellationToken.None, TaskCreationOptions.LongRunning, TaskScheduler.Default);
        }

        internal Reader CreateReader() { return new Reader(this); }
        internal AudioClip CreateClip(Reader reader)
        {
            if (disposed) throw new ObjectDisposedException("StreamedAudio");
            if (!opened) throw new InvalidOperationException("Streamed audio is not open yet");
            return AudioClip.Create("Sailwind Radio streamed audio", Frames, Channels, SampleRate, true,
                reader.Read, reader.SetPosition);
        }

        // Main thread. The frame the worker keeps ten seconds ahead of.
        internal void Request(int frame)
        {
            if (!opened || disposed) return;
            long target = Math.Max(0, Math.Min(Frames - 1, frame));
            if (Interlocked.Exchange(ref requestedFrame, target) != target) Wake();
        }

        private void Wake()
        {
            if (disposed) return;
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
        }

        // True once the audio Unity pre-reads from frame is decoded.
        internal bool Ready(int frame)
        {
            if (disposed || !opened) return false;
            frame = Math.Max(0, Math.Min(Frames - 1, frame));
            long end = (long)frame + Math.Max(1L, (long)SampleRate * ReadyMilliseconds / 1000);
            return Decoded(frame, (int)Math.Min(Frames, end));
        }

        private bool Decoded(int from, int to)
        {
            for (int at = from; at < to; at = ((at / ChunkFrames) + 1) * ChunkFrames)
            {
                int index = at / ChunkFrames, slot = Volatile.Read(ref slots[index]) - 1;
                if (slot < 0) return false;
                Chunk chunk = chunks[slot];
                if (Volatile.Read(ref chunk.State) < 0 || chunk.Start != index * ChunkFrames || chunk.Count <= 0) return false;
            }
            return true;
        }

        private void Copy(int frame, float[] destination, int frames)
        {
            if (frame < 0 || frame >= Frames) return;
            int at = frame, end = (int)Math.Min((long)Frames, (long)frame + frames);
            while (at < end)
            {
                int start = at / ChunkFrames * ChunkFrames;
                int take = Math.Min(end - at, start + ChunkFrames - at);
                int slot = Volatile.Read(ref slots[start / ChunkFrames]) - 1;
                bool copied = false;
                if (slot >= 0)
                {
                    Chunk chunk = chunks[slot];
                    int readers;
                    do { readers = Volatile.Read(ref chunk.State); }
                    while (readers >= 0 && Interlocked.CompareExchange(ref chunk.State, readers + 1, readers) != readers);
                    if (readers >= 0)
                    {
                        try
                        {
                            if (chunk.Start == start && at + take <= start + chunk.Count)
                            {
                                Array.Copy(chunk.Samples, (at - start) * Channels, destination, (at - frame) * Channels, take * Channels);
                                copied = true;
                            }
                        }
                        finally { Interlocked.Decrement(ref chunk.State); }
                    }
                }
                if (!copied) Interlocked.Add(ref missingFrames, take);
                at += take; // Missing chunks are silence, never a blocking read.
            }
        }

        private void Open(CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            if (!File.Exists(path)) throw new FileNotFoundException("Music file is missing or inaccessible");
            decoder = PcmDecoders.Open(path, token);
            int layoutChannels = decoder.Channels, rate = decoder.SampleRate;
            long length = decoder.Frames;
            if (length <= 0) throw new IOException("The file contains no decodable audio");
            AudioFiles.CheckPlayableRate(rate);
            // Unity AudioClip.Create and AudioSource.timeSamples both use int frames.
            if (length > Int32.MaxValue - rate * 2L)
                throw new IOException("Track duration exceeds Unity's sample-position limit");
            // A track shorter than the window is held whole, so backward seeks never decode again.
            int trackChunks = (int)((length + ChunkFrames - 1) / ChunkFrames);
            int windowChunks = (WindowSeconds * rate + ChunkFrames - 1) / ChunkFrames;
            // A cancelled open allocates no window and is never published as open.
            token.ThrowIfCancellationRequested();
            var ring = new Chunk[Math.Max(1, Math.Min(trackChunks, windowChunks))];
            for (int i = 0; i < ring.Length; i++) ring[i] = new Chunk(layoutChannels);
            double seconds = initialSeconds;
            if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) seconds = 0;
            channels = layoutChannels;
            sampleRate = rate;
            frames = (int)length;
            chunks = ring;
            slots = new int[trackChunks];
            requestedFrame = (long)Math.Min(length - 1, Math.Floor(seconds * rate));
            token.ThrowIfCancellationRequested();
            opened = true;
        }

        private void Run()
        {
            CancellationToken token = cancellation.Token;
            try
            {
                Open(token);
                var signals = new[] { wake, token.WaitHandle };
                while (!token.IsCancellationRequested)
                {
                    int target = (int)Interlocked.Read(ref requestedFrame);
                    int first, forwardEnd;
                    Window(target, out first, out forwardEnd);
                    // Fill the first chunk missing between the playhead and the look-ahead limit. Chunks
                    // already held are kept, so short seeks inside the window cost nothing.
                    int missing = -1;
                    for (int index = first; index < forwardEnd && missing < 0; index++) if (!Holds(index)) missing = index;
                    // Request and Dispose both signal, so an idle worker needs no timed poll.
                    if (missing < 0) { WaitHandle.WaitAny(signals); continue; }
                    Fill(missing, Victim(first, forwardEnd), token);
                }
            }
            catch (OperationCanceledException) when (token.IsCancellationRequested) { }
            catch (Exception ex) { Volatile.Write(ref fault, ex); }
            finally { if (decoder != null) decoder.Dispose(); }
        }

        // Worker only. The chunks the next ten seconds need, forward from the target to the end of the track.
        private void Window(int target, out int first, out int forwardEnd)
        {
            long ahead = (long)target + (long)AheadSeconds * sampleRate;
            first = target / ChunkFrames;
            forwardEnd = (int)((Math.Min(ahead, frames) + ChunkFrames - 1) / ChunkFrames);
        }

        // Worker only. It is the sole writer of Start, Count and the slot map.
        private bool Holds(int index)
        {
            int slot = slots[index] - 1;
            if (slot < 0) return false;
            Chunk chunk = chunks[slot];
            return chunk.Start == index * ChunkFrames && chunk.Count == Math.Min(ChunkFrames, frames - index * ChunkFrames);
        }

        // Worker only. An empty slot, otherwise the one whose chunk lies furthest behind the target
        // outside the window. The ring outgrows the window, and a track held whole always has an empty
        // slot for its missing chunk, so a slot is always found.
        private int Victim(int first, int forwardEnd)
        {
            int best = -1, bestDistance = -1, trackChunks = slots.Length;
            for (int slot = 0; slot < chunks.Length; slot++)
            {
                Chunk chunk = chunks[slot];
                if (chunk.Count == 0) return slot;
                int index = chunk.Start / ChunkFrames;
                if (index >= first && index < forwardEnd) continue;
                int distance = (first - index + trackChunks) % trackChunks;
                if (distance > bestDistance) { best = slot; bestDistance = distance; }
            }
            if (best < 0) throw new InvalidOperationException("Streamed audio window has no free slot");
            return best;
        }

        private void Fill(int index, int slot, CancellationToken token)
        {
            Chunk chunk = chunks[slot];
            int start = index * ChunkFrames;
            while (Interlocked.CompareExchange(ref chunk.State, -1, 0) != 0)
            {
                token.ThrowIfCancellationRequested();
                token.WaitHandle.WaitOne(1);
            }
            try
            {
                // Invalidate first, so an interrupted fill never exposes a mix of old and new PCM.
                int previous = chunk.Start / ChunkFrames;
                if (chunk.Count > 0 && slots[previous] == slot + 1) Volatile.Write(ref slots[previous], 0);
                chunk.Count = 0;
                chunk.Start = start;
                int count = Math.Min(ChunkFrames, frames - start);
                int read = Decode(chunk.Samples, start, count, token);
                for (int i = 0; i < read; i++)
                    if (Single.IsNaN(chunk.Samples[i]) || Single.IsInfinity(chunk.Samples[i])) chunk.Samples[i] = 0f;
                    else chunk.Samples[i] = Math.Max(-1f, Math.Min(1f, chunk.Samples[i]));
                Array.Clear(chunk.Samples, read, count * channels - read);
                chunk.Count = count;
            }
            finally { Volatile.Write(ref chunk.State, 0); }
            // Published after the chunk, so a reader that finds the slot also finds its PCM.
            Volatile.Write(ref slots[index], slot + 1);
        }

        // Worker only. Decodes up to count frames at start and returns the samples written. Frames past a
        // clean early end or a decode failure stay silent. A failure before any audio faults the stream.
        private int Decode(float[] samples, int start, int count, CancellationToken token)
        {
            int wanted = (int)Math.Min(count, Math.Max(0L, (long)dataEnd - start)) * channels;
            if (wanted == 0) return 0;
            int read = 0;
            try
            {
                if (reseek || decoder.Position != start)
                {
                    // Decoders own any preroll or reopen needed for an exact seek.
                    decoder.Seek(start);
                    if (decoder.Position != start) throw new IOException("Decoder seek missed the requested sample");
                    reseek = false;
                }
                while (read < wanted)
                {
                    token.ThrowIfCancellationRequested();
                    int got = decoder.Read(samples, read, wanted - read);
                    if (got <= 0) break;
                    read += got;
                }
                if (read % channels != 0) throw new IOException("Decoder returned an incomplete frame");
                if (read < wanted)
                {
                    if (start == 0 && read == 0) throw new IOException("The file contains no decodable audio");
                    // A clean early end. The reported remainder plays as silence.
                    dataEnd = start + read / channels;
                }
                produced = true;
            }
            catch (Exception ex) when (!(ex is OperationCanceledException && token.IsCancellationRequested))
            {
                if (!produced) throw;
                // Audio already played. It continues up to the damage, which the main thread treats as the end.
                read -= read % channels;
                int at = start + read / channels;
                dataEnd = Math.Min(dataEnd, at);
                reseek = true;
                if (damage == null) Volatile.Write(ref damage, ex);
                if (at < damageFrame) Volatile.Write(ref damageFrame, at);
            }
            return read;
        }

        public void Dispose()
        {
            if (disposed) return;
            cancellation.Cancel();
            try { wake.Set(); }
            catch (ObjectDisposedException) { }
            disposed = true;
            // Never wait for the worker. It disposes its decoder, then its token source and signal are released.
            worker.ContinueWith(done =>
            {
                if (done.IsFaulted) { var observed = done.Exception; }
                cancellation.Dispose();
                wake.Dispose();
            }, TaskScheduler.Default);
        }
    }
}
