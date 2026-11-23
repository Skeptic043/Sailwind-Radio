using System;
using System.IO;
using System.Runtime.InteropServices;

namespace SailwindRadio.Audio
{
    // Decodes a live forward-only ADTS AAC / HE-AAC stream (such as Shoutcast/Icecast audio/aacp)
    // through Windows Media Foundation's built-in AAC Decoder MFT with constant memory and no seeking.
    internal sealed class StreamingAacDecoder : IDisposable
    {
        private static readonly int[] AdtsRates =
            { 96000, 88200, 64000, 48000, 44100, 32000, 24000, 22050, 16000, 12000, 11025, 8000, 7350 };
        private static readonly int[] AdtsChannels = { 2, 1, 2, 3, 4, 5, 6, 8 };

        private IntPtr transform;
        private bool started;
        private bool providesSamples;
        private int outputBufferSize = 16384;
        private float[] pcm = new float[8192];

        internal int SampleRate { get; private set; }
        internal int Channels { get; private set; }

        internal StreamingAacDecoder(int coreRate, int coreChannels)
        {
            try
            {
                MediaFoundation.Startup();
                started = true;
                transform = MediaFoundation.ActivateAacDecoder();
                ConfigureInput(coreRate, coreChannels);
                SelectFloatOutput();
                var processMessage = ComCalls.Method<MediaFoundation.MftProcessMessageMethod>(transform, MediaFoundation.SlotMftProcessMessage);
                processMessage(transform, MediaFoundation.MftMessageBeginStreaming, IntPtr.Zero);
                processMessage(transform, MediaFoundation.MftMessageStartOfStream, IntPtr.Zero);
            }
            catch
            {
                Dispose();
                throw;
            }
        }

        private void ConfigureInput(int coreRate, int coreChannels)
        {
            IntPtr type = IntPtr.Zero;
            try
            {
                Check(MediaFoundation.MFCreateMediaType(out type), "create AAC input type");
                var setGuid = ComCalls.Method<MediaFoundation.SetGuidMethod>(type, MediaFoundation.SlotSetGuid);
                var setUInt = ComCalls.Method<MediaFoundation.SetUInt32Method>(type, MediaFoundation.SlotSetUInt32);
                var setBlob = ComCalls.Method<MediaFoundation.SetBlobMethod>(type, MediaFoundation.SlotSetBlob);
                Guid major = MediaFoundation.MajorType, audio = MediaFoundation.Audio;
                Guid subtype = MediaFoundation.Subtype, aac = MediaFoundation.Aac;
                Guid payloadKey = MediaFoundation.AacPayloadType;
                Guid rateKey = MediaFoundation.SamplesPerSecond;
                Guid chKey = MediaFoundation.Channels;
                Guid userDataKey = MediaFoundation.UserData;

                Check(setGuid(type, ref major, ref audio), "set AAC major type");
                Check(setGuid(type, ref subtype, ref aac), "set AAC subtype");
                Check(setUInt(type, ref payloadKey, 1), "set ADTS payload type");
                Check(setUInt(type, ref rateKey, coreRate), "set AAC sample rate");
                Check(setUInt(type, ref chKey, coreChannels), "set AAC channels");

                // 12-byte HEAACWAVEINFO prefix: wPayloadType = 1 (ADTS), wAudioProfileLevelIndication = 0xFE (unspecified).
                byte[] header = new byte[12];
                header[0] = 1;
                header[2] = 0xFE;
                Check(setBlob(type, ref userDataKey, header, header.Length), "set AAC user data");

                var setInput = ComCalls.Method<MediaFoundation.MftSetTypeMethod>(transform, MediaFoundation.SlotMftSetInputType);
                Check(setInput(transform, 0, type, 0), "configure AAC decoder input");
            }
            finally { ComCalls.Release(ref type); }
        }

        private void SelectFloatOutput()
        {
            var getAvailable = ComCalls.Method<MediaFoundation.MftGetOutputAvailableTypeMethod>(transform, MediaFoundation.SlotMftGetOutputAvailableType);
            var setOutput = ComCalls.Method<MediaFoundation.MftSetTypeMethod>(transform, MediaFoundation.SlotMftSetOutputType);
            IntPtr chosen = IntPtr.Zero;
            for (int index = 0; ; index++)
            {
                IntPtr candidate;
                int hr = getAvailable(transform, 0, index, out candidate);
                if (hr < 0 || candidate == IntPtr.Zero) break;
                Guid subtype;
                bool isFloat = MediaFoundation.GetGuid(candidate, MediaFoundation.Subtype, out subtype) &&
                    subtype == MediaFoundation.Float;
                if (isFloat)
                {
                    chosen = candidate;
                    break;
                }
                ComCalls.Release(ref candidate);
            }
            if (chosen == IntPtr.Zero)
                throw new IOException("Windows AAC decoder did not offer float PCM output.");
            try
            {
                Check(setOutput(transform, 0, chosen, 0), "set AAC float output");
            }
            finally { ComCalls.Release(ref chosen); }

            IntPtr current = IntPtr.Zero;
            try
            {
                var getCurrent = ComCalls.Method<MediaFoundation.MftGetOutputCurrentTypeMethod>(transform, MediaFoundation.SlotMftGetOutputCurrentType);
                Check(getCurrent(transform, 0, out current), "read AAC output format");
                SampleRate = MediaFoundation.GetUInt32(current, MediaFoundation.SamplesPerSecond, 0);
                Channels = MediaFoundation.GetUInt32(current, MediaFoundation.Channels, 0);
            }
            finally { ComCalls.Release(ref current); }
            if (SampleRate < 8000 || SampleRate > 192000 || Channels < 1 || Channels > 8)
                throw new IOException("Unsupported AAC stream sample rate or channel count.");

            MediaFoundation.MftOutputStreamInfo info;
            var getInfo = ComCalls.Method<MediaFoundation.MftGetOutputStreamInfoMethod>(transform, MediaFoundation.SlotMftGetOutputStreamInfo);
            if (getInfo(transform, 0, out info) >= 0)
            {
                providesSamples = (info.Flags & (MediaFoundation.MftOutputProvidesSamples | MediaFoundation.MftOutputCanProvideSamples)) != 0;
                outputBufferSize = Math.Max(16384, info.Size);
            }
        }

        internal void DecodeFrame(byte[] adtsBytes, int length, Action<float[], int, int, int> onPcm)
        {
            IntPtr inputSample = CreateInputSample(adtsBytes, length);
            try
            {
                var processInput = ComCalls.Method<MediaFoundation.MftProcessInputMethod>(transform, MediaFoundation.SlotMftProcessInput);
                int hr = processInput(transform, 0, inputSample, 0);
                if (hr < 0)
                {
                    // Drain pending output and retry once if the MFT output queue was full.
                    DrainOutput(onPcm);
                    hr = processInput(transform, 0, inputSample, 0);
                    if (hr < 0) return;
                }
            }
            finally { ComCalls.Release(ref inputSample); }
            DrainOutput(onPcm);
        }

        private void DrainOutput(Action<float[], int, int, int> onPcm)
        {
            var processOutput = ComCalls.Method<MediaFoundation.MftProcessOutputMethod>(transform, MediaFoundation.SlotMftProcessOutput);
            while (true)
            {
                IntPtr callerSample = IntPtr.Zero;
                var output = new MediaFoundation.MftOutputDataBuffer();
                if (!providesSamples)
                {
                    callerSample = CreateEmptySample(outputBufferSize);
                    output.Sample = callerSample;
                }
                int status;
                int hr = processOutput(transform, 0, 1, ref output, out status);
                if (output.Events != IntPtr.Zero) ComCalls.Release(ref output.Events);

                if (hr == MediaFoundation.HrStreamChange)
                {
                    if (providesSamples && output.Sample != IntPtr.Zero) ComCalls.Release(ref output.Sample);
                    ComCalls.Release(ref callerSample);
                    SelectFloatOutput();
                    continue;
                }
                if (hr == MediaFoundation.HrNeedMoreInput || hr < 0)
                {
                    if (providesSamples && output.Sample != IntPtr.Zero) ComCalls.Release(ref output.Sample);
                    ComCalls.Release(ref callerSample);
                    return;
                }

                IntPtr sample = output.Sample;
                try
                {
                    if (sample != IntPtr.Zero) EmitSample(sample, onPcm);
                }
                finally
                {
                    if (providesSamples && sample != IntPtr.Zero) ComCalls.Release(ref sample);
                    ComCalls.Release(ref callerSample);
                }
            }
        }

        private void EmitSample(IntPtr sample, Action<float[], int, int, int> onPcm)
        {
            IntPtr buffer = IntPtr.Zero;
            try
            {
                if (ComCalls.Method<MediaFoundation.GetPointerMethod>(sample, MediaFoundation.SlotConvertToContiguousBuffer)(sample, out buffer) < 0 ||
                    buffer == IntPtr.Zero)
                    return;
                IntPtr data;
                int maxLen, curLen;
                if (ComCalls.Method<MediaFoundation.LockMethod>(buffer, MediaFoundation.SlotLock)(buffer, out data, out maxLen, out curLen) < 0)
                    return;
                try
                {
                    int floats = curLen / sizeof(float);
                    int frames = Channels > 0 ? floats / Channels : 0;
                    if (frames <= 0) return;
                    if (pcm.Length < floats) pcm = new float[floats];
                    Marshal.Copy(data, pcm, 0, floats);
                    onPcm(pcm, frames, Channels, SampleRate);
                }
                finally
                {
                    ComCalls.Method<MediaFoundation.UnlockMethod>(buffer, MediaFoundation.SlotUnlock)(buffer);
                }
            }
            finally { ComCalls.Release(ref buffer); }
        }

        private static IntPtr CreateInputSample(byte[] bytes, int length)
        {
            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                Check(MediaFoundation.MFCreateSample(out sample), "create AAC sample");
                Check(MediaFoundation.MFCreateMemoryBuffer(length, out buffer), "create AAC buffer");
                IntPtr dest;
                int maxLen, curLen;
                Check(ComCalls.Method<MediaFoundation.LockMethod>(buffer, MediaFoundation.SlotLock)(buffer, out dest, out maxLen, out curLen), "lock AAC buffer");
                try { Marshal.Copy(bytes, 0, dest, length); }
                finally { ComCalls.Method<MediaFoundation.UnlockMethod>(buffer, MediaFoundation.SlotUnlock)(buffer); }
                Check(ComCalls.Method<MediaFoundation.SetLengthMethod>(buffer, MediaFoundation.SlotSetCurrentLength)(buffer, length), "set AAC buffer length");
                Check(ComCalls.Method<MediaFoundation.AddBufferMethod>(sample, MediaFoundation.SlotAddBuffer)(sample, buffer), "attach AAC buffer");
                ComCalls.Release(ref buffer);
                return sample;
            }
            catch
            {
                ComCalls.Release(ref buffer);
                ComCalls.Release(ref sample);
                throw;
            }
        }

        private static IntPtr CreateEmptySample(int size)
        {
            IntPtr sample = IntPtr.Zero, buffer = IntPtr.Zero;
            try
            {
                Check(MediaFoundation.MFCreateSample(out sample), "create AAC output sample");
                Check(MediaFoundation.MFCreateMemoryBuffer(size, out buffer), "create AAC output buffer");
                Check(ComCalls.Method<MediaFoundation.AddBufferMethod>(sample, MediaFoundation.SlotAddBuffer)(sample, buffer), "attach AAC output buffer");
                ComCalls.Release(ref buffer);
                return sample;
            }
            catch
            {
                ComCalls.Release(ref buffer);
                ComCalls.Release(ref sample);
                throw;
            }
        }

        private static void Check(int hr, string action)
        {
            if (hr < 0) throw new IOException("Could not " + action + " (error 0x" + hr.ToString("X8") + ")");
        }

        public void Dispose()
        {
            ComCalls.Release(ref transform);
            if (started)
            {
                started = false;
                MediaFoundation.MFShutdown();
            }
        }

        // Scans a forward-only byte stream for ADTS AAC frames without seeking or retaining past frames.
        internal sealed class AdtsFrameReader
        {
            private const int MaxSyncScanBytes = 128 * 1024;
            private readonly Stream input;
            private readonly byte[] buffer = new byte[16384];
            private readonly byte[] frameScratch = new byte[8192];
            private int start, count;
            private bool synced;

            internal AdtsFrameReader(Stream input) { this.input = input; }

            internal bool ReadNext(out byte[] frameBytes, out int length, out int coreRate, out int coreChannels)
            {
                frameBytes = frameScratch;
                length = coreRate = coreChannels = 0;
                int scanned = 0;
                while (true)
                {
                    if (!Ensure(7)) return false;
                    int frameLen, rate, ch;
                    if (TryParseHeader(buffer, start, out frameLen, out rate, out ch) &&
                        frameLen <= buffer.Length - 7 && frameLen <= frameScratch.Length)
                    {
                        if (synced)
                        {
                            if (!Ensure(frameLen)) return false;
                            Buffer.BlockCopy(buffer, start, frameScratch, 0, frameLen);
                            length = frameLen;
                            coreRate = rate;
                            coreChannels = ch;
                            Consume(frameLen);
                            return true;
                        }
                        int nextLen, nextRate, nextCh;
                        if (Ensure(frameLen + 7) &&
                            TryParseHeader(buffer, start + frameLen, out nextLen, out nextRate, out nextCh) &&
                            nextRate == rate)
                        {
                            synced = true;
                            Buffer.BlockCopy(buffer, start, frameScratch, 0, frameLen);
                            length = frameLen;
                            coreRate = rate;
                            coreChannels = ch;
                            Consume(frameLen);
                            return true;
                        }
                    }
                    synced = false;
                    Consume(1);
                    if (++scanned > MaxSyncScanBytes)
                        throw new IOException("No valid ADTS AAC frames were found in this stream.");
                }
            }

            private static bool TryParseHeader(byte[] data, int offset, out int frameLength, out int sampleRate, out int channels)
            {
                frameLength = sampleRate = channels = 0;
                byte b0 = data[offset], b1 = data[offset + 1], b2 = data[offset + 2];
                byte b3 = data[offset + 3], b4 = data[offset + 4], b5 = data[offset + 5];
                if (b0 != 0xFF || (b1 & 0xF6) != 0xF0) return false;
                int rateIndex = (b2 >> 2) & 0x0F;
                if (rateIndex >= AdtsRates.Length) return false;
                int headerSize = (b1 & 1) == 0 ? 9 : 7;
                int len = ((b3 & 3) << 11) | (b4 << 3) | ((b5 >> 5) & 7);
                if (len < headerSize || len > 8192) return false;
                int chConfig = ((b2 & 1) << 2) | ((b3 >> 6) & 3);
                frameLength = len;
                sampleRate = AdtsRates[rateIndex];
                channels = chConfig < AdtsChannels.Length ? AdtsChannels[chConfig] : 2;
                return true;
            }

            private bool Ensure(int needed)
            {
                if (count >= needed) return true;
                if (start > 0)
                {
                    if (count > 0) Buffer.BlockCopy(buffer, start, buffer, 0, count);
                    start = 0;
                }
                while (count < needed)
                {
                    int got = input.Read(buffer, count, buffer.Length - count);
                    if (got <= 0) return false;
                    count += got;
                }
                return true;
            }

            private void Consume(int bytes)
            {
                start += bytes;
                count -= bytes;
                if (count == 0) start = 0;
            }
        }
    }
}
