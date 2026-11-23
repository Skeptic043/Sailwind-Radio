using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Net.Security;
using System.Net.Sockets;
using System.Security.Authentication;
using System.Text;
using System.Threading;
using NLayer;

namespace SailwindRadio.Audio
{
    // Streams a direct HTTP or HTTPS internet radio station (Icecast, Shoutcast v1 ICY 200 OK, or Shoutcast v2)
    // on a worker thread, stripping inline ICY metadata and decoding MP3 or ADTS AAC stereo PCM with constant memory.
    internal sealed class InternetRadioStream : IDisposable
    {
        internal const int Channels = 2;
        private const int BufferSeconds = 8;
        private const int MaxAheadSeconds = 5;
        private const int ConnectTimeoutMs = 10000;
        private const int ReadTimeoutMs = 10000;
        private const int MaxRedirects = 5;
        private const int MaxHeaderBytes = 16384;
        private const int MaxReconnects = 2;

        private readonly object gate = new object();
        private readonly string url;
        private readonly Stream testInput;
        private readonly string testContentType;
        private readonly int testMetaInt;
        private readonly Thread worker;

        private TcpClient activeClient;
        private Stream activeStream;
        private float[] samples;
        private int sampleRate;
        private int capacityFrames;
        private int crossfadeFrames;
        private float lastLeft, lastRight;
        private bool hasLastSample;
        private long written, writingEnd, consumed;
        private volatile bool ready, disposed, workerCompleted;
        private volatile Exception fault;
        private volatile string station = "";
        private volatile string title = "";
        private volatile string artist = "";

        internal bool Ready { get { return ready; } }
        internal int SampleRate { get { return sampleRate; } }
        internal int CapacityFrames { get { return capacityFrames; } }
        internal long WrittenFrames { get { return Volatile.Read(ref written); } }
        internal long FirstFrame { get { return Math.Max(0, Volatile.Read(ref writingEnd) - capacityFrames); } }
        internal Exception Fault { get { return fault; } }
        internal string Station { get { return station; } }
        internal string Title { get { return title; } }
        internal string Artist { get { return artist; } }
        internal bool WorkerCompleted { get { return workerCompleted; } }

        internal static InternetRadioStream Start(string url)
        {
            return new InternetRadioStream(url, null, null, 0, null);
        }

        // Offline seam for unit checks: decodes an in-memory MP3 or ADTS AAC stream with optional inline ICY metadata.
        internal static InternetRadioStream StartForTest(Stream input, string contentType, int icyMetaInt, string stationName)
        {
            return new InternetRadioStream(null, input, contentType, icyMetaInt, stationName);
        }

        private InternetRadioStream(string url, Stream testInput, string testContentType, int testMetaInt, string stationName)
        {
            this.url = url;
            this.testInput = testInput;
            this.testContentType = testContentType;
            this.testMetaInt = testMetaInt;
            if (!String.IsNullOrWhiteSpace(stationName)) station = stationName.Trim();
            worker = new Thread(Run)
            {
                IsBackground = true,
                Name = "Sailwind Radio Internet Stream"
            };
            worker.Start();
        }

        internal void NoteConsumed(long frame)
        {
            long current = Interlocked.Read(ref consumed);
            if (frame > current) Interlocked.Exchange(ref consumed, frame);
        }

        internal bool TryCopy(long firstFrame, float[] destination, int count)
        {
            if (!ready || count <= 0 || count > destination.Length / Channels) return false;
            float[] localSamples = samples;
            int cap = capacityFrames;
            if (localSamples == null || cap <= 0) return false;
            if (firstFrame < FirstFrame || firstFrame > Volatile.Read(ref written) - count) return false;
            int at = (int)(firstFrame % cap);
            int first = Math.Min(count, cap - at);
            Array.Copy(localSamples, at * Channels, destination, 0, first * Channels);
            Array.Copy(localSamples, 0, destination, first * Channels, (count - first) * Channels);
            Thread.MemoryBarrier();
            return ready && samples == localSamples && firstFrame >= FirstFrame;
        }

        private void Run()
        {
            try
            {
                if (testInput != null)
                {
                    DecodeSource(testInput, testContentType, testMetaInt, false);
                }
                else
                {
                    RunHttp();
                }
            }
            catch (Exception error)
            {
                if (!disposed) fault = error is IOException ? error : new IOException(error.Message, error);
            }
            finally
            {
                CloseActive();
                workerCompleted = true;
            }
        }

        private void RunHttp()
        {
            int attempt = 0;
            bool splice = false;
            while (!disposed)
            {
                long framesBefore = Volatile.Read(ref written);
                try
                {
                    Stream bodyStream;
                    string contentType, icyName;
                    int metaInt;
                    OpenTcpHttp(url, out bodyStream, out contentType, out icyName, out metaInt);
                    if (!String.IsNullOrWhiteSpace(icyName)) station = icyName.Trim();
                    using (bodyStream)
                    {
                        DecodeSource(bodyStream, contentType, metaInt, splice);
                    }
                    if (ready && Volatile.Read(ref written) - framesBefore >= (long)sampleRate * 10)
                        attempt = 0;
                    // A clean EOF after audio played may be a brief server rollover; reconnect if allowed.
                    if (!ready || disposed || ++attempt > MaxReconnects) break;
                    splice = true;
                    SleepCancelable(attempt * 750);
                }
                catch (Exception error)
                {
                    if (disposed) return;
                    if (ready && Volatile.Read(ref written) - framesBefore >= (long)sampleRate * 10)
                        attempt = 0;
                    if (!ready || ++attempt > MaxReconnects || IsPermanentError(error)) throw;
                    splice = true;
                    SleepCancelable(attempt * 750);
                }
                finally
                {
                    CloseActive();
                }
            }
            if (!disposed) throw new IOException(ready ? "Stream disconnected." : "Stream ended before audio started.");
        }

        // Plain TCP + SslStream HTTP/1.0 client. Accepts HTTP/1.0, HTTP/1.1, and Shoutcast v1 "ICY 200 OK" status lines.
        private void OpenTcpHttp(string startUrl, out Stream bodyStream, out string contentType, out string icyName, out int metaInt)
        {
            string currentUrl = startUrl;
            for (int redirect = 0; redirect <= MaxRedirects; redirect++)
            {
                Uri uri;
                if (!Uri.TryCreate(currentUrl, UriKind.Absolute, out uri) ||
                    (uri.Scheme != Uri.UriSchemeHttp && uri.Scheme != Uri.UriSchemeHttps) ||
                    String.IsNullOrEmpty(uri.Host))
                    throw new PermanentStreamException("Invalid stream URL.");

                var client = new TcpClient();
                Stream wire = null;
                try
                {
                    lock (gate)
                    {
                        if (disposed) { client.Close(); throw new OperationCanceledException(); }
                        activeClient = client;
                    }
                    var connect = client.BeginConnect(uri.Host, uri.Port, null, null);
                    if (!connect.AsyncWaitHandle.WaitOne(ConnectTimeoutMs))
                        throw new IOException("Stream connection timed out.");
                    client.EndConnect(connect);
                    client.ReceiveTimeout = ReadTimeoutMs;
                    client.SendTimeout = ConnectTimeoutMs;

                    wire = client.GetStream();
                    if (uri.Scheme == Uri.UriSchemeHttps)
                    {
                        var ssl = new SslStream(wire, false);
                        ssl.AuthenticateAsClient(uri.Host, null, SslProtocols.Tls12, false);
                        wire = ssl;
                    }
                    lock (gate)
                    {
                        if (disposed) { wire.Close(); client.Close(); throw new OperationCanceledException(); }
                        activeStream = wire;
                    }

                    string pathAndQuery = String.IsNullOrEmpty(uri.PathAndQuery) ? "/" : uri.PathAndQuery;
                    string hostHeader = uri.IsDefaultPort ? uri.Host : uri.Host + ":" + uri.Port;
                    string requestText =
                        "GET " + pathAndQuery + " HTTP/1.0\r\n" +
                        "Host: " + hostHeader + "\r\n" +
                        "User-Agent: SailwindAudio/1.6\r\n" +
                        "Icy-MetaData: 1\r\n" +
                        "Accept: */*\r\n" +
                        "Connection: close\r\n\r\n";
                    byte[] requestBytes = Encoding.ASCII.GetBytes(requestText);
                    wire.Write(requestBytes, 0, requestBytes.Length);
                    wire.Flush();

                    int statusCode;
                    Dictionary<string, string> headers;
                    byte[] leftover;
                    int leftoverOffset, leftoverCount;
                    ReadHttpResponseHeaders(wire, out statusCode, out headers, out leftover, out leftoverOffset, out leftoverCount);

                    if (statusCode == 301 || statusCode == 302 || statusCode == 303 || statusCode == 307 || statusCode == 308)
                    {
                        string location;
                        if (!headers.TryGetValue("location", out location) || String.IsNullOrWhiteSpace(location))
                            throw new PermanentStreamException("Stream redirect did not include a target address.");
                        Uri nextUri;
                        if (!Uri.TryCreate(uri, location.Trim(), out nextUri))
                            throw new PermanentStreamException("Stream redirect pointed to an invalid address.");
                        currentUrl = nextUri.AbsoluteUri;
                        CloseActive();
                        continue;
                    }

                    if (statusCode != 200)
                    {
                        string msg = "Stream server returned HTTP " + statusCode;
                        if (statusCode >= 400 && statusCode < 500) throw new PermanentStreamException(msg);
                        throw new IOException(msg);
                    }

                    headers.TryGetValue("content-type", out contentType);
                    headers.TryGetValue("icy-name", out icyName);
                    metaInt = 0;
                    string rawMeta;
                    if (headers.TryGetValue("icy-metaint", out rawMeta) && !String.IsNullOrWhiteSpace(rawMeta))
                        int.TryParse(rawMeta.Trim(), NumberStyles.None, CultureInfo.InvariantCulture, out metaInt);

                    Stream body = leftoverCount > 0
                        ? (Stream)new PrefixedStream(wire, leftover, leftoverOffset, leftoverCount)
                        : wire;
                    string transfer;
                    if (headers.TryGetValue("transfer-encoding", out transfer) &&
                        transfer != null && transfer.IndexOf("chunked", StringComparison.OrdinalIgnoreCase) >= 0)
                    {
                        body = new ChunkedStream(body);
                    }
                    bodyStream = body;
                    return;
                }
                catch
                {
                    CloseActive();
                    throw;
                }
            }
            throw new PermanentStreamException("Stream redirected too many times.");
        }

        internal static void ReadHttpResponseHeaders(Stream wire, out int statusCode, out Dictionary<string, string> headers,
            out byte[] leftover, out int leftoverOffset, out int leftoverCount)
        {
            byte[] buf = new byte[MaxHeaderBytes];
            int total = 0;
            int headerEnd = -1;
            int delimiterLen = 0;
            while (headerEnd < 0)
            {
                if (total >= buf.Length) throw new PermanentStreamException("Stream response headers were too large.");
                int got = wire.Read(buf, total, buf.Length - total);
                if (got <= 0) throw new IOException("Stream closed before sending response headers.");
                int scanStart = Math.Max(0, total - 3);
                total += got;
                for (int i = scanStart; i < total; i++)
                {
                    if (i + 3 < total && buf[i] == '\r' && buf[i + 1] == '\n' && buf[i + 2] == '\r' && buf[i + 3] == '\n')
                    { headerEnd = i; delimiterLen = 4; break; }
                    if (i + 1 < total && buf[i] == '\n' && buf[i + 1] == '\n')
                    { headerEnd = i; delimiterLen = 2; break; }
                }
            }

            string rawHeaders = Encoding.UTF8.GetString(buf, 0, headerEnd);
            string[] lines = rawHeaders.Split(new[] { "\r\n", "\n" }, StringSplitOptions.None);
            if (lines.Length == 0 || String.IsNullOrWhiteSpace(lines[0]))
                throw new PermanentStreamException("Stream server returned an empty status line.");

            string statusLine = lines[0].Trim();
            statusCode = ParseStatusCode(statusLine);
            headers = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            for (int i = 1; i < lines.Length; i++)
            {
                string line = lines[i];
                int colon = line.IndexOf(':');
                if (colon <= 0) continue;
                string key = line.Substring(0, colon).Trim();
                string value = line.Substring(colon + 1).Trim();
                if (key.Length > 0) headers[key] = value;
            }

            leftoverOffset = headerEnd + delimiterLen;
            leftoverCount = total - leftoverOffset;
            leftover = buf;
        }

        internal static int ParseStatusCode(string statusLine)
        {
            // Accepts "HTTP/1.0 200 OK", "HTTP/1.1 200 OK", and Shoutcast v1 "ICY 200 OK".
            int firstSpace = statusLine.IndexOf(' ');
            if (firstSpace <= 0) throw new PermanentStreamException("Stream server returned an invalid status line.");
            string protocol = statusLine.Substring(0, firstSpace);
            if (!protocol.StartsWith("HTTP/", StringComparison.OrdinalIgnoreCase) &&
                !String.Equals(protocol, "ICY", StringComparison.OrdinalIgnoreCase))
                throw new PermanentStreamException("Stream server returned an unrecognized protocol.");
            int start = firstSpace + 1;
            while (start < statusLine.Length && statusLine[start] == ' ') start++;
            int end = start;
            while (end < statusLine.Length && statusLine[end] >= '0' && statusLine[end] <= '9') end++;
            int code;
            if (end <= start || !int.TryParse(statusLine.Substring(start, end - start), NumberStyles.None, CultureInfo.InvariantCulture, out code))
                throw new PermanentStreamException("Stream server returned an invalid status code.");
            return code;
        }

        private void DecodeSource(Stream input, string contentType, int metaInt, bool splice)
        {
            bool isAac;
            ValidateContentType(contentType, out isAac);
            lock (gate)
            {
                if (disposed) { input.Close(); return; }
                activeStream = input;
            }
            using (var icy = new IcyAudioStream(input, metaInt, OnMetadata))
            {
                if (isAac) DecodeAac(icy, splice);
                else DecodeMp3(icy, splice);
            }
        }

        private void DecodeMp3(Stream icy, bool splice)
        {
            var decoder = new MpegFrameDecoder();
            var frame = new StreamingMpegFrame();
            var raw = new float[1152 * 2];
            var stereo = new float[1152 * 2];
            bool nextSplice = splice;
            var reader = new MpegFrameReader(icy);
            while (!disposed)
            {
                WaitForBufferRoom();
                if (disposed) return;

                if (!reader.ReadNext(frame)) break;
                if (frame.SampleRate < 8000 || frame.SampleRate > 96000) continue;

                int decodedFloats = decoder.DecodeFrame(frame, raw, 0);
                if (decodedFloats <= 0) continue;

                int frameChannels = decodedFloats >= frame.SampleCount * 2 ? 2 : 1;
                int frames = decodedFloats / frameChannels;
                if (frames <= 0) continue;

                EnsureRingRate(frame.SampleRate);
                if (frameChannels == 1)
                {
                    for (int i = 0; i < frames; i++)
                    {
                        float sample = raw[i];
                        stereo[i * 2] = sample;
                        stereo[i * 2 + 1] = sample;
                    }
                    WriteFrames(stereo, frames, nextSplice);
                }
                else
                {
                    WriteFrames(raw, frames, nextSplice);
                }
                nextSplice = false;
            }
        }

        private void DecodeAac(Stream icy, bool splice)
        {
            if (!MediaFoundationSupport.Enabled)
                throw new PermanentStreamException("AAC internet streams require Windows Media Foundation.");

            using (new ComCalls.Apartment())
            {
                var reader = new StreamingAacDecoder.AdtsFrameReader(icy);
                StreamingAacDecoder decoder = null;
                float[] stereo = new float[4096 * 2];
                bool nextSplice = splice;
                try
                {
                    while (!disposed)
                    {
                        WaitForBufferRoom();
                        if (disposed) return;

                        byte[] frameBytes;
                        int length, coreRate, coreChannels;
                        if (!reader.ReadNext(out frameBytes, out length, out coreRate, out coreChannels)) break;
                        if (decoder == null) decoder = new StreamingAacDecoder(coreRate, coreChannels);
                        decoder.DecodeFrame(frameBytes, length, (pcm, frames, ch, rate) =>
                        {
                            if (disposed || frames <= 0) return;
                            EnsureRingRate(rate);
                            if (ch == 2)
                            {
                                WriteFrames(pcm, frames, nextSplice);
                            }
                            else
                            {
                                if (stereo.Length < frames * 2) stereo = new float[frames * 2];
                                if (ch == 1)
                                {
                                    for (int i = 0; i < frames; i++)
                                        stereo[i * 2] = stereo[i * 2 + 1] = pcm[i];
                                }
                                else
                                {
                                    for (int i = 0; i < frames; i++)
                                    {
                                        stereo[i * 2] = pcm[i * ch];
                                        stereo[i * 2 + 1] = pcm[i * ch + 1];
                                    }
                                }
                                WriteFrames(stereo, frames, nextSplice);
                            }
                            nextSplice = false;
                        });
                    }
                }
                finally
                {
                    if (decoder != null) decoder.Dispose();
                }
            }
        }

        private void WaitForBufferRoom()
        {
            while (!disposed && ready &&
                Volatile.Read(ref written) - Interlocked.Read(ref consumed) > (long)sampleRate * MaxAheadSeconds)
                Thread.Sleep(20);
        }

        private void EnsureRingRate(int rate)
        {
            if (ready && rate == sampleRate) return;
            if (ready && Volatile.Read(ref written) >= sampleRate)
                throw new PermanentStreamException("Stream sample rate changed during playback.");
            // Allow an HE-AAC/SBR stream to settle on its doubled output rate during initial startup frames.
            ready = false;
            Thread.MemoryBarrier();
            sampleRate = rate;
            capacityFrames = rate * BufferSeconds;
            crossfadeFrames = Math.Max(16, rate / 100);
            samples = new float[capacityFrames * Channels];
            lastLeft = lastRight = 0f;
            hasLastSample = false;
            Interlocked.Exchange(ref consumed, 0);
            Interlocked.Exchange(ref writingEnd, 0);
            Volatile.Write(ref written, 0);
            ready = true;
        }

        private void WriteFrames(float[] packet, int count, bool splice)
        {
            int length = count * Channels;
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
            int at = (int)(end % capacityFrames);
            int first = Math.Min(count, capacityFrames - at);
            Interlocked.Exchange(ref writingEnd, end + count);
            Array.Copy(packet, 0, samples, at * Channels, first * Channels);
            Array.Copy(packet, first * Channels, samples, 0, (count - first) * Channels);
            Volatile.Write(ref written, end + count);
        }

        private void OnMetadata(string raw)
        {
            string parsedArtist, parsedTitle;
            ParseIcyMetadata(raw, out parsedArtist, out parsedTitle);
            if (!String.IsNullOrEmpty(parsedTitle))
            {
                artist = parsedArtist;
                title = parsedTitle;
            }
        }

        internal static void ValidateContentType(string contentType, out bool isAac)
        {
            isAac = false;
            string ct = (contentType ?? "").Trim().ToLowerInvariant();
            int semi = ct.IndexOf(';');
            if (semi >= 0) ct = ct.Substring(0, semi).Trim();
            if (ct.Length == 0 || ct == "audio/mpeg" || ct == "audio/mp3" ||
                ct == "audio/x-mpeg" || ct == "audio/x-mp3" || ct == "application/octet-stream")
                return;
            if (ct == "audio/aac" || ct == "audio/aacp" || ct == "audio/x-aac")
            {
                if (!MediaFoundationSupport.Enabled)
                    throw new PermanentStreamException("AAC internet streams require Windows Media Foundation.");
                isAac = true;
                return;
            }
            if (ct.IndexOf("mpegurl", StringComparison.Ordinal) >= 0 || ct.IndexOf("m3u", StringComparison.Ordinal) >= 0)
                throw new PermanentStreamException("HLS streams are not supported yet. Use a direct MP3 or AAC stream URL.");
            if (ct.StartsWith("audio/", StringComparison.Ordinal))
                throw new PermanentStreamException("Only MP3 and AAC internet radio streams are supported right now.");
            throw new PermanentStreamException("That address did not return an MP3 or AAC audio stream.");
        }

        internal static void ParseIcyMetadata(string raw, out string artist, out string title)
        {
            artist = "";
            title = "";
            if (String.IsNullOrWhiteSpace(raw)) return;
            const string marker = "StreamTitle='";
            int start = raw.IndexOf(marker, StringComparison.OrdinalIgnoreCase);
            if (start < 0) return;
            start += marker.Length;
            int end = raw.IndexOf("';", start, StringComparison.Ordinal);
            if (end < 0) end = raw.LastIndexOf('\'');
            if (end <= start) return;
            string display = raw.Substring(start, end - start).Trim();
            if (display.Length == 0) return;
            int split = display.IndexOf(" - ", StringComparison.Ordinal);
            if (split > 0 && split + 3 < display.Length)
            {
                artist = display.Substring(0, split).Trim();
                title = display.Substring(split + 3).Trim();
            }
            else
            {
                title = display;
            }
        }

        private static bool IsPermanentError(Exception error)
        {
            return error is PermanentStreamException;
        }

        private sealed class PermanentStreamException : IOException
        {
            internal PermanentStreamException(string message) : base(message) { }
        }

        private void SleepCancelable(int milliseconds)
        {
            int waited = 0;
            while (!disposed && waited < milliseconds)
            {
                Thread.Sleep(50);
                waited += 50;
            }
        }

        private void CloseActive()
        {
            TcpClient client;
            Stream stream;
            lock (gate)
            {
                client = activeClient;
                activeClient = null;
                stream = activeStream;
                activeStream = null;
            }
            if (stream != null) { try { stream.Close(); } catch { } }
            if (client != null) { try { client.Close(); } catch { } }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            CloseActive();
        }

        // Serves any body bytes already read alongside HTTP headers before delegating to the underlying wire stream.
        private sealed class PrefixedStream : Stream
        {
            private readonly Stream inner;
            private readonly byte[] prefix;
            private int offset, remaining;

            internal PrefixedStream(Stream inner, byte[] prefix, int offset, int count)
            {
                this.inner = inner;
                this.prefix = prefix;
                this.offset = offset;
                remaining = count;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

            public override int Read(byte[] buffer, int destOffset, int count)
            {
                if (count <= 0) return 0;
                if (remaining > 0)
                {
                    int take = Math.Min(count, remaining);
                    Buffer.BlockCopy(prefix, offset, buffer, destOffset, take);
                    offset += take;
                    remaining -= take;
                    return take;
                }
                return inner.Read(buffer, destOffset, count);
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        // Decodes HTTP/1.1 chunked transfer encoding if a proxy sends it despite HTTP/1.0.
        private sealed class ChunkedStream : Stream
        {
            private readonly Stream inner;
            private int chunkRemaining;
            private bool finished;

            internal ChunkedStream(Stream inner) { this.inner = inner; }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count <= 0 || finished) return 0;
                while (chunkRemaining == 0)
                {
                    string line = ReadLine();
                    if (line == null) { finished = true; return 0; }
                    line = line.Trim();
                    if (line.Length == 0) continue;
                    int semi = line.IndexOf(';');
                    if (semi >= 0) line = line.Substring(0, semi).Trim();
                    int size;
                    if (!int.TryParse(line, NumberStyles.HexNumber, CultureInfo.InvariantCulture, out size) || size <= 0)
                    { finished = true; return 0; }
                    chunkRemaining = size;
                }
                int got = inner.Read(buffer, offset, Math.Min(count, chunkRemaining));
                if (got <= 0) { finished = true; return 0; }
                chunkRemaining -= got;
                return got;
            }

            private string ReadLine()
            {
                var sb = new StringBuilder();
                while (sb.Length < 128)
                {
                    int b = inner.ReadByte();
                    if (b < 0) return sb.Length == 0 ? null : sb.ToString();
                    if (b == '\n') break;
                    if (b != '\r') sb.Append((char)b);
                }
                return sb.ToString();
            }

            protected override void Dispose(bool disposing)
            {
                if (disposing) inner.Dispose();
                base.Dispose(disposing);
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        // Strips inline Shoutcast/Icecast ICY metadata blocks every metaInterval bytes so the audio reader only sees frames.
        internal sealed class IcyAudioStream : Stream
        {
            private readonly Stream inner;
            private readonly int metaInterval;
            private readonly Action<string> onMetadata;
            private int bytesUntilMeta;
            private byte[] metaBuffer;

            internal IcyAudioStream(Stream inner, int metaInterval, Action<string> onMetadata)
            {
                this.inner = inner;
                this.metaInterval = metaInterval;
                this.onMetadata = onMetadata;
                bytesUntilMeta = metaInterval;
            }

            public override bool CanRead { get { return true; } }
            public override bool CanSeek { get { return false; } }
            public override bool CanWrite { get { return false; } }
            public override long Length { get { throw new NotSupportedException(); } }
            public override long Position { get { throw new NotSupportedException(); } set { throw new NotSupportedException(); } }

            public override int Read(byte[] buffer, int offset, int count)
            {
                if (count <= 0) return 0;
                if (metaInterval <= 0) return inner.Read(buffer, offset, count);
                while (true)
                {
                    if (bytesUntilMeta == 0)
                    {
                        int lengthByte = inner.ReadByte();
                        if (lengthByte < 0) return 0;
                        int metaBytes = lengthByte * 16;
                        if (metaBytes > 0)
                        {
                            if (metaBuffer == null || metaBuffer.Length < metaBytes)
                                metaBuffer = new byte[metaBytes];
                            int total = 0;
                            while (total < metaBytes)
                            {
                                int read = inner.Read(metaBuffer, total, metaBytes - total);
                                if (read <= 0) return 0;
                                total += read;
                            }
                            string text = Encoding.UTF8.GetString(metaBuffer, 0, total).TrimEnd('\0');
                            if (onMetadata != null) onMetadata(text);
                        }
                        bytesUntilMeta = metaInterval;
                    }
                    int want = Math.Min(count, bytesUntilMeta);
                    int got = inner.Read(buffer, offset, want);
                    if (got <= 0) return 0;
                    bytesUntilMeta -= got;
                    return got;
                }
            }

            public override void Flush() { }
            public override long Seek(long offset, SeekOrigin origin) { throw new NotSupportedException(); }
            public override void SetLength(long value) { throw new NotSupportedException(); }
            public override void Write(byte[] buffer, int offset, int count) { throw new NotSupportedException(); }
        }

        // Scans a forward-only byte stream for valid MPEG audio frames without seeking or retaining past frames.
        private sealed class MpegFrameReader
        {
            private const int MaxSyncScanBytes = 128 * 1024;
            private const int MaxId3Bytes = 2 * 1024 * 1024;
            private readonly Stream input;
            private readonly byte[] buffer = new byte[8192];
            private readonly StreamingMpegFrame probe = new StreamingMpegFrame();
            private int start, count;
            private bool synced;

            internal MpegFrameReader(Stream input) { this.input = input; }

            internal bool ReadNext(StreamingMpegFrame frame)
            {
                int scanned = 0;
                while (true)
                {
                    if (!Ensure(4)) return false;
                    // Skip a validated ID3v2 tag before initial sync if a stream sends one at the start.
                    if (!synced && buffer[start] == 'I' && buffer[start + 1] == 'D' && buffer[start + 2] == '3')
                    {
                        if (!Ensure(10)) return false;
                        if (buffer[start + 3] < 0xFF && buffer[start + 4] < 0xFF &&
                            (buffer[start + 6] & 0x80) == 0 && (buffer[start + 7] & 0x80) == 0 &&
                            (buffer[start + 8] & 0x80) == 0 && (buffer[start + 9] & 0x80) == 0)
                        {
                            int size = ((buffer[start + 6] & 0x7F) << 21) | ((buffer[start + 7] & 0x7F) << 14) |
                                ((buffer[start + 8] & 0x7F) << 7) | (buffer[start + 9] & 0x7F);
                            int total = 10 + size + ((buffer[start + 5] & 0x10) != 0 ? 10 : 0);
                            if (total <= MaxId3Bytes)
                            {
                                if (!Skip(total)) return false;
                                continue;
                            }
                        }
                    }

                    if (frame.TryParseHeader(buffer, start))
                    {
                        int length = frame.FrameLength;
                        if (length >= 4 && length <= buffer.Length - 4)
                        {
                            if (synced)
                            {
                                if (!Ensure(length)) return false;
                                frame.LoadBytes(buffer, start, length);
                                Consume(length);
                                return true;
                            }
                            if (Ensure(length + 4) && probe.TryParseHeader(buffer, start + length) &&
                                probe.Version == frame.Version && probe.Layer == frame.Layer &&
                                probe.SampleRate == frame.SampleRate)
                            {
                                synced = true;
                                frame.LoadBytes(buffer, start, length);
                                Consume(length);
                                return true;
                            }
                        }
                    }

                    synced = false;
                    Consume(1);
                    if (++scanned > MaxSyncScanBytes)
                        throw new IOException("No valid MP3 audio frames were found in this stream.");
                }
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

            private bool Skip(int bytes)
            {
                while (bytes > 0)
                {
                    if (count > 0)
                    {
                        int take = Math.Min(bytes, count);
                        Consume(take);
                        bytes -= take;
                    }
                    else
                    {
                        int got = input.Read(buffer, 0, Math.Min(bytes, buffer.Length));
                        if (got <= 0) return false;
                        bytes -= got;
                    }
                }
                return true;
            }
        }

        // Reusable single-frame IMpegFrame for NLayer.MpegFrameDecoder. Zero per-frame allocation.
        private sealed class StreamingMpegFrame : IMpegFrame
        {
            private static readonly int[] Mpeg1Layer1Rates = { 0, 32, 64, 96, 128, 160, 192, 224, 256, 288, 320, 352, 384, 416, 448, 0 };
            private static readonly int[] Mpeg1Layer2Rates = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 384, 0 };
            private static readonly int[] Mpeg1Layer3Rates = { 0, 32, 40, 48, 56, 64, 80, 96, 112, 128, 160, 192, 224, 256, 320, 0 };
            private static readonly int[] Mpeg2Layer1Rates = { 0, 32, 48, 56, 64, 80, 96, 112, 128, 144, 160, 176, 192, 224, 256, 0 };
            private static readonly int[] Mpeg2Layer23Rates = { 0, 8, 16, 24, 32, 40, 48, 56, 64, 80, 96, 112, 128, 144, 160, 0 };
            private static readonly int[] Mpeg1SampleRates = { 44100, 48000, 32000 };
            private static readonly int[] Mpeg2SampleRates = { 22050, 24000, 16000 };
            private static readonly int[] Mpeg25SampleRates = { 11025, 12000, 8000 };

            private byte[] data = new byte[4096];
            private int bitOffset, bitEnd;

            public int SampleRate { get; private set; }
            public int SampleRateIndex { get; private set; }
            public int FrameLength { get; private set; }
            public int BitRate { get; private set; }
            public MpegVersion Version { get; private set; }
            public MpegLayer Layer { get; private set; }
            public MpegChannelMode ChannelMode { get; private set; }
            public int ChannelModeExtension { get; private set; }
            public int SampleCount { get; private set; }
            public int BitRateIndex { get; private set; }
            public bool IsCopyrighted { get; private set; }
            public bool HasCrc { get; private set; }
            public bool IsCorrupted { get { return false; } }

            internal bool TryParseHeader(byte[] source, int offset)
            {
                byte b0 = source[offset], b1 = source[offset + 1], b2 = source[offset + 2], b3 = source[offset + 3];
                if (b0 != 0xFF || (b1 & 0xE0) != 0xE0) return false;

                int versionBits = (b1 >> 3) & 3;
                MpegVersion version;
                int[] rateTable;
                if (versionBits == 3) { version = MpegVersion.Version1; rateTable = Mpeg1SampleRates; }
                else if (versionBits == 2) { version = MpegVersion.Version2; rateTable = Mpeg2SampleRates; }
                else if (versionBits == 0) { version = MpegVersion.Version25; rateTable = Mpeg25SampleRates; }
                else return false;

                int layerBits = (b1 >> 1) & 3;
                MpegLayer layer;
                if (layerBits == 1) layer = MpegLayer.LayerIII;
                else if (layerBits == 2) layer = MpegLayer.LayerII;
                else if (layerBits == 3) layer = MpegLayer.LayerI;
                else return false;

                int bitRateIndex = (b2 >> 4) & 0x0F;
                int sampleRateIndex = (b2 >> 2) & 3;
                if (bitRateIndex == 0 || bitRateIndex == 15 || sampleRateIndex == 3) return false;

                int kbps;
                if (version == MpegVersion.Version1)
                    kbps = layer == MpegLayer.LayerI ? Mpeg1Layer1Rates[bitRateIndex]
                        : layer == MpegLayer.LayerII ? Mpeg1Layer2Rates[bitRateIndex]
                        : Mpeg1Layer3Rates[bitRateIndex];
                else
                    kbps = layer == MpegLayer.LayerI ? Mpeg2Layer1Rates[bitRateIndex]
                        : Mpeg2Layer23Rates[bitRateIndex];
                if (kbps <= 0) return false;

                int bitRate = kbps * 1000;
                int sampleRate = rateTable[sampleRateIndex];
                int padding = (b2 >> 1) & 1;
                int sampleCount = layer == MpegLayer.LayerI ? 384
                    : (layer == MpegLayer.LayerIII && version != MpegVersion.Version1 ? 576 : 1152);
                int frameLength = layer == MpegLayer.LayerI
                    ? (12 * bitRate / sampleRate + padding) * 4
                    : (sampleCount / 8) * bitRate / sampleRate + padding;
                if (frameLength < 4) return false;

                Version = version;
                Layer = layer;
                HasCrc = (b1 & 1) == 0;
                BitRateIndex = bitRateIndex;
                BitRate = bitRate;
                SampleRateIndex = sampleRateIndex;
                SampleRate = sampleRate;
                SampleCount = sampleCount;
                FrameLength = frameLength;
                ChannelMode = (MpegChannelMode)((b3 >> 6) & 3);
                ChannelModeExtension = (b3 >> 4) & 3;
                IsCopyrighted = ((b3 >> 3) & 1) != 0;
                return true;
            }

            internal void LoadBytes(byte[] source, int offset, int length)
            {
                if (data.Length < length) data = new byte[length];
                Buffer.BlockCopy(source, offset, data, 0, length);
                Reset();
            }

            public void Reset()
            {
                bitOffset = HasCrc ? 48 : 32;
                bitEnd = FrameLength * 8;
            }

            public int ReadBits(int bitCount)
            {
                if (bitCount < 1 || bitCount > 32) throw new ArgumentOutOfRangeException("bitCount");
                if (bitOffset + bitCount > bitEnd) return -1;
                int result = 0;
                int remaining = bitCount;
                while (remaining > 0)
                {
                    int byteIndex = bitOffset >> 3;
                    int bitInByte = bitOffset & 7;
                    int available = 8 - bitInByte;
                    int take = remaining < available ? remaining : available;
                    int shift = available - take;
                    int mask = (1 << take) - 1;
                    result = (result << take) | ((data[byteIndex] >> shift) & mask);
                    bitOffset += take;
                    remaining -= take;
                }
                return result;
            }
        }
    }
}
