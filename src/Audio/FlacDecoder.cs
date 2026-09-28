using System;
using System.IO;
using System.Threading;

namespace SailwindRadio
{
    // Managed FLAC decoder written from RFC 9639. It decodes one frame at a time into reusable
    // per-channel buffers and converts to float on Read, so memory stays bounded by the block size.
    // Worker threads only. The caller owns cancellation through the token and the stream.
    internal sealed class FlacDecoder : IPcmDecoder
    {
        private const int HeaderScanBytes = 16;
        private const int MaxBlockSize = 65536;
        // Seeking decodes forward instead of bisecting once the remaining gap is this small.
        private const int ForwardSeekBytes = 65536;

        private readonly Stream input;
        private readonly bool leaveOpen;
        private readonly CancellationToken token;
        private readonly int streamChannels, streamBits, sampleRate, outputChannels, maxBlock;
        private readonly long firstFrameOffset, streamEnd, frames;
        private readonly bool variableBlocking;
        private readonly int fixedBlockSize;
        private readonly float[] mixLeft, mixRight;
        private long[] seekSamples, seekOffsets;

        // Buffered input and 64-bit bit cache. Bits below the valid count are always zero.
        private byte[] buffer = new byte[65536];
        private long bufferOffset;
        private int length, pos, mark;
        private ulong cache;
        private int bits;

        private long[][] samples;
        private readonly long[] coefficients = new long[32];
        private long blockStart = -1, nextSample;
        private int blockSize, blockBits;
        private bool nextKnown;
        private long position;
        private bool disposed;

        internal readonly FlacStatistics Statistics = new FlacStatistics();
        // Test hook, called with the exact integer samples of each decoded frame.
        internal Action<long[][], int, long, int> FrameDecoded { get; set; }

        public int Channels { get { return outputChannels; } }
        public int SampleRate { get { return sampleRate; } }
        public long Frames { get { return frames; } }
        public long Position { get { return position; } }
        internal int BitsPerSample { get { return streamBits; } }
        internal int SourceChannels { get { return streamChannels; } }
        internal bool HasSeekTable { get { return seekSamples != null; } }

        public FlacDecoder(Stream input, CancellationToken token, bool leaveOpen = false)
        {
            if (input == null) throw new ArgumentNullException("input");
            this.input = input;
            this.leaveOpen = leaveOpen;
            this.token = token;
            try
            {
                if (!input.CanRead || !input.CanSeek) throw new IOException("FLAC input must be a seekable stream");
                streamEnd = input.Length;
                AudioFiles.SkipId3v2(input);
                var header = new byte[34];
                if (!AudioFiles.ReadFully(input, header, 4) || header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C')
                    throw new InvalidDataException("File is not a FLAC stream");
                bool haveInfo = false, last = false;
                long totalSamples = 0;
                int minBlock = 0;
                while (!last)
                {
                    token.ThrowIfCancellationRequested();
                    if (!AudioFiles.ReadFully(input, header, 4)) throw new InvalidDataException("FLAC metadata is truncated");
                    last = (header[0] & 0x80) != 0;
                    int type = header[0] & 0x7F;
                    int size = (header[1] << 16) | (header[2] << 8) | header[3];
                    if (size > streamEnd - input.Position) throw new InvalidDataException("FLAC metadata is truncated");
                    if (type == 127) throw new InvalidDataException("FLAC metadata block type is invalid");
                    if (!haveInfo && type != 0) throw new InvalidDataException("FLAC stream does not start with STREAMINFO");
                    if (type == 0)
                    {
                        if (haveInfo || size < 34) throw new InvalidDataException("FLAC STREAMINFO block is invalid");
                        AudioFiles.ReadFully(input, header, 34);
                        input.Position += size - 34;
                        minBlock = (header[0] << 8) | header[1];
                        maxBlock = (header[2] << 8) | header[3];
                        sampleRate = (header[10] << 12) | (header[11] << 4) | (header[12] >> 4);
                        streamChannels = ((header[12] >> 1) & 7) + 1;
                        streamBits = (((header[12] & 1) << 4) | (header[13] >> 4)) + 1;
                        totalSamples = ((long)(header[13] & 15) << 32) | ((long)header[14] << 24) | ((long)header[15] << 16) |
                            ((long)header[16] << 8) | header[17];
                        haveInfo = true;
                    }
                    else if (type == 3 && seekSamples == null) ReadSeekTable(size);
                    else input.Position += size;
                }
                if (sampleRate == 0) throw new InvalidDataException("FLAC stream has no valid sample rate");
                if (streamBits < 4) throw new InvalidDataException("FLAC bit depth below 4 is not supported");
                if (maxBlock < 16 || minBlock > maxBlock) maxBlock = MaxBlockSize;
                firstFrameOffset = input.Position;
                outputChannels = streamChannels == 1 ? 1 : 2;
                if (streamChannels > 2) ChannelDownmix.Build(ChannelDownmix.Flac(streamChannels), out mixLeft, out mixRight);

                // The first frame fixes the blocking strategy and the nominal block size of fixed streams.
                SeekTo(firstFrameOffset);
                int available = Ensure(HeaderScanBytes);
                FrameHeader first;
                int status = TryParseHeader(buffer, pos, available, out first);
                if (available == 0) throw new InvalidDataException("FLAC stream has no audio frames");
                if (status == HeaderNeedMore) throw Truncated();
                if (status == HeaderCrcMismatch) throw new InvalidDataException("FLAC frame header CRC mismatch");
                if (status != HeaderOk) throw new InvalidDataException("FLAC first frame header is invalid");
                variableBlocking = first.Variable;
                fixedBlockSize = first.BlockSize;
                if (!variableBlocking && first.Number != 0) throw new InvalidDataException("FLAC first frame is not frame zero");

                frames = totalSamples > 0 ? totalSamples : ScanTotalSamples();
                SeekTo(firstFrameOffset);
                nextSample = 0;
                nextKnown = true;
            }
            catch
            {
                if (!leaveOpen) input.Dispose();
                throw;
            }
        }

        public void Seek(long frame)
        {
            if (disposed) throw new ObjectDisposedException("FlacDecoder");
            if (frame < 0) throw new ArgumentOutOfRangeException("frame");
            if (frame > frames) frame = frames;
            position = frame;
            if (frame == frames) return;
            if (blockStart >= 0 && frame >= blockStart && frame < blockStart + blockSize) return;
            // Short forward jumps decode through the intervening frames.
            if (nextKnown && frame >= nextSample && frame - nextSample < Math.Max(sampleRate, 4 * fixedBlockSize)) return;
            Locate(frame);
        }

        public int Read(float[] output, int offset, int count)
        {
            if (disposed) throw new ObjectDisposedException("FlacDecoder");
            if (output == null) throw new ArgumentNullException("output");
            if (offset < 0 || count < 0 || count > output.Length - offset) throw new ArgumentOutOfRangeException("count");
            int wanted = count / outputChannels, written = 0;
            while (written < wanted && position < frames)
            {
                if (blockStart < 0 || position < blockStart || position >= blockStart + blockSize)
                {
                    // Relocating is only needed after a decode error left the reader position unknown.
                    if (!nextKnown || position < nextSample)
                    {
                        Locate(position);
                        continue;
                    }
                    // Frames skipped by a short forward seek are found with resynchronisation.
                    if (position > nextSample && WalkTo(position)) continue;
                    DecodeFrame();
                    if (blockStart > position) throw new InvalidDataException("FLAC stream skips samples");
                    continue;
                }
                int index = (int)(position - blockStart);
                int available = (int)(Math.Min(blockStart + blockSize, frames) - position);
                int n = Math.Min(wanted - written, available);
                Convert(index, n, output, offset + written * outputChannels);
                written += n;
                position += n;
            }
            return written * outputChannels;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            if (!leaveOpen) input.Dispose();
        }

        private void ReadSeekTable(int size)
        {
            int count = size / 18;
            var point = new byte[18];
            var sampleList = new long[count];
            var offsetList = new long[count];
            int kept = 0;
            bool valid = true;
            for (int i = 0; i < count; i++)
            {
                AudioFiles.ReadFully(input, point, 18);
                long sample = 0, offset = 0;
                for (int b = 0; b < 8; b++) { sample = (sample << 8) | point[b]; offset = (offset << 8) | point[8 + b]; }
                if (sample == -1) continue; // Placeholder point.
                // Points must ascend. Anything else makes the table untrustworthy, so it is ignored.
                if (sample < 0 || offset < 0 || (kept > 0 && (sample <= sampleList[kept - 1] || offset <= offsetList[kept - 1]))) valid = false;
                else
                {
                    sampleList[kept] = sample;
                    offsetList[kept] = offset;
                    kept++;
                }
            }
            input.Position += size - count * 18L;
            if (!valid || kept == 0) return;
            Array.Resize(ref sampleList, kept);
            Array.Resize(ref offsetList, kept);
            seekSamples = sampleList;
            seekOffsets = offsetList;
        }

        private void Convert(int index, int count, float[] output, int offset)
        {
            float scale = (float)(1.0 / (1L << (blockBits - 1)));
            if (outputChannels == 1)
            {
                long[] s = samples[0];
                for (int i = 0; i < count; i++) output[offset + i] = s[index + i] * scale;
            }
            else if (streamChannels == 2)
            {
                long[] a = samples[0], b = samples[1];
                for (int i = 0, o = offset; i < count; i++, o += 2)
                {
                    output[o] = a[index + i] * scale;
                    output[o + 1] = b[index + i] * scale;
                }
            }
            else
            {
                for (int i = 0, o = offset; i < count; i++, o += 2)
                {
                    float l = 0, r = 0;
                    for (int c = 0; c < streamChannels; c++)
                    {
                        float v = samples[c][index + i] * scale;
                        l += mixLeft[c] * v;
                        r += mixRight[c] * v;
                    }
                    output[o] = l;
                    output[o + 1] = r;
                }
            }
        }

        private void Locate(long target)
        {
            token.ThrowIfCancellationRequested();
            long lo = firstFrameOffset, loSample = 0, hi = streamEnd;
            if (seekSamples != null)
            {
                int i = Array.BinarySearch(seekSamples, target);
                if (i < 0) i = ~i - 1;
                if (i >= 0)
                {
                    long point = firstFrameOffset + seekOffsets[i];
                    long found, sample;
                    // A point is trusted only when a verified frame with the promised sample starts there.
                    if (point < streamEnd && FindFrame(point, point + 1, false, out found, out sample) && sample == seekSamples[i])
                    {
                        lo = point;
                        loSample = sample;
                    }
                    else
                    {
                        seekSamples = seekOffsets = null;
                        i = -1;
                    }
                }
                if (seekSamples != null && i + 1 < seekSamples.Length) hi = Math.Min(hi, firstFrameOffset + seekOffsets[i + 1]);
            }
            // Bisect on verified frames. The frame containing target starts at or after lo and before hi.
            while (hi - lo > ForwardSeekBytes && target - loSample > 2L * maxBlock)
            {
                token.ThrowIfCancellationRequested();
                long mid = lo + (hi - lo) / 2;
                long found, sample;
                if (FindFrame(mid, hi, false, out found, out sample) && sample <= target)
                {
                    // The frame just verified holds target, so keep it decoded.
                    if (sample + blockSize > target) return;
                    lo = found;
                    loSample = sample;
                }
                else hi = mid;
            }
            SeekTo(lo);
            blockStart = -1;
            nextSample = loSample;
            nextKnown = true;
            WalkTo(target);
        }

        // Walks verified frames from the reader position up to target. Unlike plain decoding this
        // resynchronises past a corrupt frame. Returns false, positioned after the last good frame,
        // when target lies in damaged or missing data, so the next strict decode reports it.
        private bool WalkTo(long target)
        {
            long offset = bufferOffset + pos - (bits >> 3), offsetSample = nextSample;
            while (true)
            {
                long found, sample;
                if (!FindFrame(offset, streamEnd, false, out found, out sample) || sample > target) break;
                if (sample + blockSize > target) return true;
                offset = bufferOffset + pos;
                offsetSample = sample + blockSize;
            }
            SeekTo(offset);
            blockStart = -1;
            nextSample = offsetSample;
            nextKnown = true;
            return false;
        }

        // Finds the first frame starting in [from, limit) that decodes with valid CRCs.
        // Leaves the reader after that frame and its samples decoded.
        private bool FindFrame(long from, long limit, bool anySample, out long frameOffset, out long frameSample)
        {
            SeekTo(from);
            while (true)
            {
                long offset = bufferOffset + pos;
                if (offset >= limit) break;
                token.ThrowIfCancellationRequested();
                mark = pos;
                int available = Ensure(HeaderScanBytes);
                if (available < 2) break;
                int end = pos + (int)Math.Min(length - pos - 1, limit - offset);
                int p = pos;
                while (p < end && !(buffer[p] == 0xFF && (buffer[p + 1] & 0xFE) == 0xF8)) p++;
                if (p == end)
                {
                    pos = end;
                    continue;
                }
                pos = p;
                mark = pos;
                available = Ensure(HeaderScanBytes);
                FrameHeader h;
                long candidate = bufferOffset + pos;
                if (TryParseHeader(buffer, pos, available, out h) == HeaderOk && Plausible(ref h, anySample))
                {
                    try
                    {
                        DecodeFrame();
                        frameOffset = candidate;
                        frameSample = blockStart;
                        return true;
                    }
                    catch (InvalidDataException) { }
                    SeekTo(candidate + 1);
                }
                else pos++;
            }
            blockStart = -1;
            frameOffset = -1;
            frameSample = -1;
            return false;
        }

        private bool Plausible(ref FrameHeader h, bool anySample)
        {
            if (h.Channels != streamChannels || h.Variable != variableBlocking) return false;
            if ((h.Bits != 0 && h.Bits != streamBits) || (h.SampleRate != 0 && h.SampleRate != sampleRate)) return false;
            if (h.BlockSize > maxBlock) return false;
            long start = h.Variable ? h.Number : h.Number * fixedBlockSize;
            return anySample || start < frames;
        }

        // STREAMINFO left the length unknown. Searching backward from the end, the first frame
        // that decodes with valid CRCs is the last complete one and gives the length.
        private long ScanTotalSamples()
        {
            const int Window = 65536;
            var chunk = new byte[Window + HeaderScanBytes];
            long end = streamEnd;
            while (end > firstFrameOffset)
            {
                token.ThrowIfCancellationRequested();
                long from = Math.Max(firstFrameOffset, end - Window);
                int count = (int)(Math.Min(streamEnd, end + HeaderScanBytes) - from);
                input.Position = from;
                if (!AudioFiles.ReadFully(input, chunk, count)) throw Truncated();
                for (int i = (int)(end - from) - 1; i >= 0; i--)
                {
                    if (chunk[i] != 0xFF || i + 1 >= count || (chunk[i + 1] & 0xFE) != 0xF8) continue;
                    FrameHeader h;
                    if (TryParseHeader(chunk, i, count - i, out h) != HeaderOk || !Plausible(ref h, true)) continue;
                    SeekTo(from + i);
                    try
                    {
                        DecodeFrame();
                        return blockStart + blockSize;
                    }
                    catch (InvalidDataException) { }
                }
                end = from;
            }
            throw new InvalidDataException("FLAC stream length is unknown and no frames decode");
        }

        internal struct FrameHeader
        {
            internal bool Variable;
            internal long Number;
            internal int BlockSize, SampleRate, ChannelCode, Channels, Bits, Length;
        }

        internal const int HeaderOk = 1, HeaderNeedMore = 0, HeaderInvalid = -1, HeaderCrcMismatch = -2;

        // Parses a frame header from bytes, including the CRC-8. Sample rate and bits are 0 when
        // the header defers to STREAMINFO.
        internal static int TryParseHeader(byte[] b, int offset, int available, out FrameHeader h)
        {
            h = new FrameHeader();
            int end = offset + available;
            if (available < 5) return available >= 2 && (b[offset] != 0xFF || (b[offset + 1] & 0xFE) != 0xF8) ? HeaderInvalid : HeaderNeedMore;
            if (b[offset] != 0xFF || (b[offset + 1] & 0xFE) != 0xF8) return HeaderInvalid;
            h.Variable = (b[offset + 1] & 1) != 0;
            int blockCode = b[offset + 2] >> 4, rateCode = b[offset + 2] & 15;
            int channelCode = b[offset + 3] >> 4, bitsCode = (b[offset + 3] >> 1) & 7;
            if ((b[offset + 3] & 1) != 0 || blockCode == 0 || rateCode == 15 || channelCode > 10 || bitsCode == 3) return HeaderInvalid;
            int p = offset + 4;
            int first = b[p++], extra;
            long number;
            if (first < 0x80) { number = first; extra = 0; }
            else if ((first & 0xE0) == 0xC0) { number = first & 0x1F; extra = 1; }
            else if ((first & 0xF0) == 0xE0) { number = first & 0x0F; extra = 2; }
            else if ((first & 0xF8) == 0xF0) { number = first & 0x07; extra = 3; }
            else if ((first & 0xFC) == 0xF8) { number = first & 0x03; extra = 4; }
            else if ((first & 0xFE) == 0xFC) { number = first & 0x01; extra = 5; }
            else if (first == 0xFE) { number = 0; extra = 6; }
            else return HeaderInvalid;
            if (!h.Variable && extra > 5) return HeaderInvalid;
            if (p + extra > end) return HeaderNeedMore;
            for (int i = 0; i < extra; i++)
            {
                int c = b[p++];
                if ((c & 0xC0) != 0x80) return HeaderInvalid;
                number = (number << 6) + (c & 0x3F);
            }
            h.Number = number;
            int need = (blockCode == 6 ? 1 : blockCode == 7 ? 2 : 0) + (rateCode == 12 ? 1 : rateCode == 13 || rateCode == 14 ? 2 : 0) + 1;
            if (p + need > end) return HeaderNeedMore;
            if (blockCode == 1) h.BlockSize = 192;
            else if (blockCode <= 5) h.BlockSize = 576 << (blockCode - 2);
            else if (blockCode == 6) h.BlockSize = b[p++] + 1;
            else if (blockCode == 7) { h.BlockSize = ((b[p] << 8) | b[p + 1]) + 1; p += 2; }
            else h.BlockSize = 256 << (blockCode - 8);
            switch (rateCode)
            {
                case 0: h.SampleRate = 0; break;
                case 1: h.SampleRate = 88200; break;
                case 2: h.SampleRate = 176400; break;
                case 3: h.SampleRate = 192000; break;
                case 4: h.SampleRate = 8000; break;
                case 5: h.SampleRate = 16000; break;
                case 6: h.SampleRate = 22050; break;
                case 7: h.SampleRate = 24000; break;
                case 8: h.SampleRate = 32000; break;
                case 9: h.SampleRate = 44100; break;
                case 10: h.SampleRate = 48000; break;
                case 11: h.SampleRate = 96000; break;
                case 12: h.SampleRate = b[p++] * 1000; break;
                case 13: h.SampleRate = (b[p] << 8) | b[p + 1]; p += 2; break;
                default: h.SampleRate = ((b[p] << 8) | b[p + 1]) * 10; p += 2; break;
            }
            if ((rateCode >= 12 && h.SampleRate == 0)) return HeaderInvalid;
            h.ChannelCode = channelCode;
            h.Channels = channelCode < 8 ? channelCode + 1 : 2;
            switch (bitsCode)
            {
                case 0: h.Bits = 0; break;
                case 1: h.Bits = 8; break;
                case 2: h.Bits = 12; break;
                case 4: h.Bits = 16; break;
                case 5: h.Bits = 20; break;
                case 6: h.Bits = 24; break;
                default: h.Bits = 32; break;
            }
            if (Crc8(b, offset, p - offset) != b[p]) return HeaderCrcMismatch;
            h.Length = p + 1 - offset;
            return HeaderOk;
        }

        // Decodes the frame at the reader position into samples. Throws InvalidDataException on
        // corrupt or truncated data.
        private void DecodeFrame()
        {
            token.ThrowIfCancellationRequested();
            blockStart = -1;
            nextKnown = false;
            Release();
            mark = pos;
            int available = Ensure(HeaderScanBytes);
            if (available == 0) throw new InvalidDataException("FLAC stream ended before its reported length");
            FrameHeader h;
            int status = TryParseHeader(buffer, pos, available, out h);
            if (status == HeaderNeedMore) throw Truncated();
            if (status == HeaderCrcMismatch) throw new InvalidDataException("FLAC frame header CRC mismatch");
            if (status != HeaderOk) throw new InvalidDataException("FLAC frame header is invalid");
            if (h.Channels != streamChannels) throw new InvalidDataException("FLAC channel count changes mid-stream");
            if (h.Variable != variableBlocking) throw new InvalidDataException("FLAC blocking strategy changes mid-stream");
            int frameBits = h.Bits == 0 ? streamBits : h.Bits;
            int n = h.BlockSize;
            pos += h.Length;
            EnsureCapacity(n);
            int code = h.ChannelCode;
            for (int c = 0; c < streamChannels; c++)
            {
                bool side = (code == 8 && c == 1) || (code == 9 && c == 0) || (code == 10 && c == 1);
                DecodeSubframe(samples[c], n, frameBits + (side ? 1 : 0));
            }
            if (code >= 8)
            {
                long[] a = samples[0], b = samples[1];
                if (code == 8) for (int i = 0; i < n; i++) b[i] = a[i] - b[i];
                else if (code == 9) for (int i = 0; i < n; i++) a[i] += b[i];
                else
                {
                    for (int i = 0; i < n; i++)
                    {
                        long side = b[i], mid = (a[i] << 1) | (side & 1);
                        a[i] = (mid + side) >> 1;
                        b[i] = (mid - side) >> 1;
                    }
                }
            }
            Statistics.ChannelModes[code < 8 ? 0 : code - 7]++;
            // Byte alignment padding, then the CRC-16 over the whole frame.
            int drop = bits & 7;
            cache <<= drop;
            bits -= drop;
            int frameEnd = pos - (bits >> 3);
            int crc = Crc16(buffer, mark, frameEnd - mark);
            if ((int)ReadBits(16) != crc) throw new InvalidDataException("FLAC frame CRC mismatch");
            Release();
            blockStart = h.Variable ? h.Number : h.Number * fixedBlockSize;
            blockSize = n;
            blockBits = frameBits;
            nextSample = blockStart + n;
            nextKnown = true;
            Statistics.Frames++;
            if (h.Variable) Statistics.VariableFrames++;
            if (FrameDecoded != null) FrameDecoded(samples, n, blockStart, frameBits);
        }

        private void EnsureCapacity(int n)
        {
            if (samples != null && samples[0].Length >= n) return;
            int size = Math.Max(n, Math.Min(maxBlock, MaxBlockSize));
            samples = new long[streamChannels][];
            for (int c = 0; c < streamChannels; c++) samples[c] = new long[size];
        }

        private void DecodeSubframe(long[] s, int n, int sampleBits)
        {
            if (ReadBits(1) != 0) throw new InvalidDataException("FLAC subframe header is invalid");
            int type = (int)ReadBits(6);
            int wasted = 0;
            if (ReadBits(1) != 0)
            {
                wasted = ReadUnary(sampleBits) + 1;
                if (wasted >= sampleBits) throw new InvalidDataException("FLAC subframe wasted bits are invalid");
                sampleBits -= wasted;
                Statistics.WastedBitsSubframes++;
            }
            if (type == 0)
            {
                long v = ReadSigned(sampleBits);
                for (int i = 0; i < n; i++) s[i] = v;
                Statistics.Constant++;
            }
            else if (type == 1)
            {
                for (int i = 0; i < n; i++) s[i] = ReadSigned(sampleBits);
                Statistics.Verbatim++;
            }
            else if (type >= 8 && type <= 12)
            {
                int order = type - 8;
                if (order > n) throw new InvalidDataException("FLAC predictor order exceeds block size");
                for (int i = 0; i < order; i++) s[i] = ReadSigned(sampleBits);
                DecodeResidual(s, n, order);
                RestoreFixed(s, n, order);
                Statistics.Fixed[order]++;
            }
            else if (type >= 32)
            {
                int order = type - 31;
                if (order > n) throw new InvalidDataException("FLAC predictor order exceeds block size");
                for (int i = 0; i < order; i++) s[i] = ReadSigned(sampleBits);
                int precision = (int)ReadBits(4) + 1;
                if (precision == 16) throw new InvalidDataException("FLAC LPC precision is invalid");
                int shift = (int)ReadSigned(5);
                if (shift < 0) throw new InvalidDataException("FLAC negative LPC shift is not supported");
                for (int i = 0; i < order; i++) coefficients[i] = ReadSigned(precision);
                DecodeResidual(s, n, order);
                RestoreLpc(s, n, order, shift);
                Statistics.Lpc++;
                if (order > Statistics.MaxLpcOrder) Statistics.MaxLpcOrder = order;
            }
            else throw new InvalidDataException("FLAC subframe type is reserved");
            if (wasted > 0) for (int i = 0; i < n; i++) s[i] <<= wasted;
        }

        private void DecodeResidual(long[] s, int n, int order)
        {
            int method = (int)ReadBits(2);
            if (method > 1) throw new InvalidDataException("FLAC residual coding method is reserved");
            int parameterBits = method == 0 ? 4 : 5, escape = method == 0 ? 15 : 31;
            int partitionOrder = (int)ReadBits(4);
            int partitionSize = n >> partitionOrder;
            if ((partitionSize << partitionOrder) != n || partitionSize < order)
                throw new InvalidDataException("FLAC residual partition order is invalid");
            int i = order;
            int partitions = 1 << partitionOrder;
            if (method == 1) Statistics.Rice5Partitions += partitions;
            for (int p = 0; p < partitions; p++)
            {
                int count = p == 0 ? partitionSize - order : partitionSize;
                int k = (int)ReadBits(parameterBits);
                if (k == escape)
                {
                    int width = (int)ReadBits(5);
                    if (width == 0) for (int j = 0; j < count; j++) s[i + j] = 0;
                    else for (int j = 0; j < count; j++) s[i + j] = ReadSigned(width);
                    Statistics.EscapedPartitions++;
                }
                else ReadRice(s, i, count, k);
                i += count;
            }
        }

        private static void RestoreFixed(long[] s, int n, int order)
        {
            switch (order)
            {
                case 1: for (int i = 1; i < n; i++) s[i] += s[i - 1]; break;
                case 2: for (int i = 2; i < n; i++) s[i] += 2 * s[i - 1] - s[i - 2]; break;
                case 3: for (int i = 3; i < n; i++) s[i] += 3 * (s[i - 1] - s[i - 2]) + s[i - 3]; break;
                case 4: for (int i = 4; i < n; i++) s[i] += 4 * (s[i - 1] + s[i - 3]) - 6 * s[i - 2] - s[i - 4]; break;
            }
        }

        private void RestoreLpc(long[] s, int n, int order, int shift)
        {
            long[] c = coefficients;
            if (order == 8)
            {
                long c0 = c[0], c1 = c[1], c2 = c[2], c3 = c[3], c4 = c[4], c5 = c[5], c6 = c[6], c7 = c[7];
                for (int i = 8; i < n; i++)
                    s[i] += (c0 * s[i - 1] + c1 * s[i - 2] + c2 * s[i - 3] + c3 * s[i - 4] +
                        c4 * s[i - 5] + c5 * s[i - 6] + c6 * s[i - 7] + c7 * s[i - 8]) >> shift;
                return;
            }
            for (int i = order; i < n; i++)
            {
                long sum = 0;
                for (int j = 0, k = i - 1; j < order; j++, k--) sum += c[j] * s[k];
                s[i] += sum >> shift;
            }
        }

        private static InvalidDataException Truncated() { return new InvalidDataException("FLAC stream is truncated"); }

        private void SeekTo(long offset)
        {
            cache = 0;
            bits = 0;
            if (offset >= bufferOffset && offset <= bufferOffset + length)
            {
                pos = (int)(offset - bufferOffset);
                mark = pos;
                return;
            }
            bufferOffset = offset;
            length = pos = mark = 0;
        }

        // Returns whole unread bytes held in the cache to the buffer. Only valid on byte boundaries.
        private void Release()
        {
            pos -= bits >> 3;
            cache = 0;
            bits = 0;
        }

        // Makes n bytes available at pos when possible and returns how many are.
        private int Ensure(int n)
        {
            while (length - pos < n && Load()) { }
            return Math.Min(n, length - pos);
        }

        // Appends stream data to the buffer, keeping every byte from mark onward.
        private bool Load()
        {
            if (mark > 0 && (length == buffer.Length || mark >= buffer.Length / 2))
            {
                Buffer.BlockCopy(buffer, mark, buffer, 0, length - mark);
                length -= mark;
                pos -= mark;
                bufferOffset += mark;
                mark = 0;
            }
            if (length == buffer.Length) Array.Resize(ref buffer, buffer.Length * 2);
            long at = bufferOffset + length;
            if (at >= streamEnd) return false;
            if (input.Position != at) input.Position = at;
            int got = input.Read(buffer, length, (int)Math.Min(buffer.Length - length, streamEnd - at));
            if (got <= 0) return false;
            length += got;
            return true;
        }

        private void Fill()
        {
            while (bits <= 48)
            {
                if (pos >= length && !Load()) return;
                cache |= (ulong)buffer[pos++] << (56 - bits);
                bits += 8;
            }
        }

        private uint ReadBits(int n)
        {
            if (bits < n)
            {
                Fill();
                if (bits < n) throw Truncated();
            }
            uint v = (uint)(cache >> (64 - n));
            cache <<= n;
            bits -= n;
            return v;
        }

        private long ReadSigned(int n)
        {
            if (n <= 32) return ((long)ReadBits(n) << (64 - n)) >> (64 - n);
            long v = ((long)ReadBits(n - 32) << 32) | ReadBits(32);
            return (v << (64 - n)) >> (64 - n);
        }

        private int ReadUnary(int limit)
        {
            int count = 0;
            while (true)
            {
                if (cache == 0)
                {
                    count += bits;
                    bits = 0;
                    if (count > limit) throw new InvalidDataException("FLAC unary value is out of range");
                    Fill();
                    if (bits == 0) throw Truncated();
                    continue;
                }
                int z = LeadingZeros(cache);
                cache <<= z + 1;
                bits -= z + 1;
                count += z;
                if (count > limit) throw new InvalidDataException("FLAC unary value is out of range");
                return count;
            }
        }

        // Rice partition decode, the hot loop. Works on local copies of the cache and buffer.
        private void ReadRice(long[] s, int start, int count, int k)
        {
            ulong c = cache;
            int n = bits, p = pos, len = length;
            byte[] b = buffer;
            long limit = uint.MaxValue >> k;
            for (int i = start, end = start + count; i < end; i++)
            {
                long q = 0;
                while (c == 0)
                {
                    q += n;
                    n = 0;
                    if (q > limit) throw new InvalidDataException("FLAC residual is out of range");
                    if (p + 8 <= len) { do { c |= (ulong)b[p++] << (56 - n); n += 8; } while (n <= 48); }
                    else
                    {
                        cache = c; bits = n; pos = p; Fill(); c = cache; n = bits; p = pos; len = length; b = buffer;
                        if (n == 0) throw Truncated();
                    }
                }
                int z = LeadingZeros(c);
                q += z;
                c <<= z + 1;
                n -= z + 1;
                if (q > limit) throw new InvalidDataException("FLAC residual is out of range");
                if (n < k)
                {
                    if (p + 8 <= len) { do { c |= (ulong)b[p++] << (56 - n); n += 8; } while (n <= 48); }
                    else
                    {
                        cache = c; bits = n; pos = p; Fill(); c = cache; n = bits; p = pos; len = length; b = buffer;
                        if (n < k) throw Truncated();
                    }
                }
                uint u = (uint)(q << k);
                if (k > 0)
                {
                    u |= (uint)(c >> (64 - k));
                    c <<= k;
                    n -= k;
                }
                s[i] = (int)(u >> 1) ^ -(int)(u & 1);
            }
            cache = c;
            bits = n;
            pos = p;
        }

        private static readonly byte[] Clz8 = BuildClz8();
        private static byte[] BuildClz8()
        {
            var table = new byte[256];
            table[0] = 8;
            for (int i = 1; i < 256; i++) { int z = 0; while ((i & (0x80 >> z)) == 0) z++; table[i] = (byte)z; }
            return table;
        }

        private static int LeadingZeros(ulong x)
        {
            int n = 0;
            if ((x >> 32) == 0) { n = 32; x <<= 32; }
            if ((x >> 48) == 0) { n += 16; x <<= 16; }
            if ((x >> 56) == 0) { n += 8; x <<= 8; }
            return n + Clz8[x >> 56];
        }

        private static readonly byte[] Crc8Table = BuildCrc8();
        private static readonly ushort[] Crc16Table = BuildCrc16();

        private static byte[] BuildCrc8()
        {
            var table = new byte[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i;
                for (int j = 0; j < 8; j++) c = (c & 0x80) != 0 ? (c << 1) ^ 0x07 : c << 1;
                table[i] = (byte)c;
            }
            return table;
        }

        private static ushort[] BuildCrc16()
        {
            var table = new ushort[256];
            for (int i = 0; i < 256; i++)
            {
                int c = i << 8;
                for (int j = 0; j < 8; j++) c = (c & 0x8000) != 0 ? (c << 1) ^ 0x8005 : c << 1;
                table[i] = (ushort)c;
            }
            return table;
        }

        internal static int Crc8(byte[] data, int offset, int count)
        {
            int crc = 0;
            for (int i = offset, end = offset + count; i < end; i++) crc = Crc8Table[crc ^ data[i]];
            return crc;
        }

        internal static int Crc16(byte[] data, int offset, int count)
        {
            int crc = 0;
            for (int i = offset, end = offset + count; i < end; i++) crc = ((crc << 8) ^ Crc16Table[(crc >> 8) ^ data[i]]) & 0xFFFF;
            return crc;
        }
    }

    // Decode counters for diagnostics and tests. Plain increments, not thread safe.
    internal sealed class FlacStatistics
    {
        internal long Frames, VariableFrames, Constant, Verbatim, Lpc, WastedBitsSubframes, EscapedPartitions, Rice5Partitions;
        internal int MaxLpcOrder;
        internal readonly long[] Fixed = new long[5];
        // Independent, left/side, right/side, mid/side.
        internal readonly long[] ChannelModes = new long[4];
    }
}
