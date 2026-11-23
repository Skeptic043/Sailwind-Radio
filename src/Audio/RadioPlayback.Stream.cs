using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    public sealed partial class RadioPlayback
    {
        private const double StreamPrebufferSeconds = 1.5;
        private const double StreamOpenTimeoutSeconds = 15;
        private const int StreamUploadFrames = 1024;
        private const int MaxStreamUploadsPerTick = 32;

        private InternetRadioStream internetStream;
        private BufferedAudioClip streamBuffer, streamWooferBuffer;
        private BufferedAudioClip.ReadFrames readStreamFrames, readStreamWooferFrames;
        private readonly RadioBassFilter streamWooferBass = new RadioBassFilter();
        private readonly PeakLimiter streamWooferLimiter = new PeakLimiter();
        private bool streamPrimed, streamScheduled;
        private long streamStartFrame, streamResumeFrame;
        private double streamStartDsp, streamEndDsp, streamWooferEndDsp;

        // Live metadata reported by an active internet radio stream, or empty when none is playing.
        public string StreamStation { get; private set; }
        public string StreamTitle { get; private set; }
        public string StreamArtist { get; private set; }

        // Test seam: lets offline checks feed a local MP3/ICY stream without touching the network.
        internal Func<string, InternetRadioStream> OpenInternetStream { get; set; }

        internal static bool IsStreamUrl(string path)
        {
            if (String.IsNullOrWhiteSpace(path)) return false;
            string trimmed = path.Trim();
            if (!trimmed.StartsWith("http://", StringComparison.OrdinalIgnoreCase) &&
                !trimmed.StartsWith("https://", StringComparison.OrdinalIgnoreCase))
                return false;
            Uri uri;
            return Uri.TryCreate(trimmed, UriKind.Absolute, out uri) &&
                (uri.Scheme == Uri.UriSchemeHttp || uri.Scheme == Uri.UriSchemeHttps) &&
                !String.IsNullOrEmpty(uri.Host);
        }

        private void TickInternetStream(bool shouldPlay)
        {
            if (!state.Powered || state.Paused)
            {
                ReleaseInternetStream();
                Status = !state.Powered ? "Off" : failure != null ? failure : "Paused";
                return;
            }
            if (internetStream == null && failure == null)
            {
                loadStartedAt = Time.realtimeSinceStartup;
                internetStream = OpenInternetStream != null
                    ? OpenInternetStream(requestedPath)
                    : InternetRadioStream.Start(requestedPath);
            }
            if (internetStream != null)
            {
                Exception streamFault = internetStream.Fault;
                if (streamFault != null)
                {
                    Fail("Unable to play stream", streamFault.Message);
                }
                else if (!internetStream.Ready && Time.realtimeSinceStartup - loadStartedAt >= StreamOpenTimeoutSeconds)
                {
                    Fail("Unable to play stream", "Stream connection timed out");
                }
                else
                {
                    StreamStation = internetStream.Station;
                    StreamTitle = internetStream.Title;
                    StreamArtist = internetStream.Artist;
                    if (shouldPlay && internetStream.Ready) UpdateBufferedInternetStream();
                    else StopBufferedInternetStream();
                }
            }
            Status = !state.Powered ? "Off" : failure != null ? failure : !shouldPlay ? "Suspended" :
                streamScheduled ? "Playing" : "Loading";
        }

        private void UpdateBufferedInternetStream()
        {
            int rate = internetStream.SampleRate;
            if (streamBuffer != null && streamBuffer.Clip != null && streamBuffer.Clip.frequency != rate)
            {
                StopBufferedInternetStream();
                streamBuffer.Dispose();
                streamBuffer = null;
                streamWooferBuffer?.Dispose();
                streamWooferBuffer = null;
                streamResumeFrame = 0;
            }
            if (streamBuffer == null)
            {
                streamBuffer = new BufferedAudioClip("Sailwind Radio internet stream", rate,
                    InternetRadioStream.Channels, internetStream.CapacityFrames, StreamUploadFrames);
                readStreamFrames = ReadInternetStreamFrames;
                readStreamWooferFrames = ReadInternetStreamWooferFrames;
                streamWooferBass.SetProfile(rate, true);
                streamWooferLimiter.Reset();
            }

            double now = AudioSettings.dspTime;
            long firstAvailable = internetStream.FirstFrame;
            long written = internetStream.WrittenFrames;
            if (streamPrimed && (streamBuffer.WrittenThrough < firstAvailable || (streamScheduled && now >= streamEndDsp)))
            {
                // Advance past already-played frames so a network underrun waits for fresh audio instead of replaying.
                streamResumeFrame = Math.Max(streamResumeFrame, streamBuffer.WrittenThrough);
                StopBufferedInternetStream();
            }

            if (!streamPrimed)
            {
                long startFrom = Math.Max(firstAvailable, streamResumeFrame);
                streamBuffer.Reset(startFrom);
                if (streamWooferBuffer != null)
                {
                    streamWooferBuffer.Reset(startFrom);
                    streamWooferBass.Reset();
                    streamWooferLimiter.Reset();
                }
                streamWooferEndDsp = 0;
                streamPrimed = true;
            }

            long currentFrame = streamScheduled ? StreamFrameAt(now, rate) : streamBuffer.FirstFrame;
            internetStream.NoteConsumed(currentFrame);
            long retainFrom = streamScheduled
                ? Math.Max(streamBuffer.FirstFrame, currentFrame - StreamUploadFrames * 2)
                : streamBuffer.FirstFrame;

            bool needWoofer = HasWooferEndpoint();
            if (needWoofer && streamWooferBuffer == null)
            {
                streamWooferBuffer = new BufferedAudioClip("Sailwind Radio internet stream woofer", rate,
                    InternetRadioStream.Channels, internetStream.CapacityFrames, StreamUploadFrames);
                streamWooferBass.SetProfile(rate, true);
                streamWooferLimiter.Reset();
                long syncFrom = streamScheduled ? Math.Max(firstAvailable, StreamFrameAt(now + ScheduleLeadSeconds, rate))
                    : streamBuffer.FirstFrame;
                streamWooferBuffer.Reset(syncFrom);
                streamWooferEndDsp = 0;
            }
            else if (!needWoofer && streamWooferBuffer != null)
            {
                streamWooferBuffer.Dispose();
                streamWooferBuffer = null;
                streamWooferEndDsp = 0;
            }

            for (int count = 0; count < MaxStreamUploadsPerTick &&
                streamBuffer.WrittenThrough + StreamUploadFrames <= written; count++)
            {
                if (!streamBuffer.TryAppend(retainFrom, StreamUploadFrames, readStreamFrames)) break;
            }
            if (streamWooferBuffer != null)
            {
                if (streamWooferBuffer.WrittenThrough < firstAvailable)
                {
                    long syncFrom = Math.Max(firstAvailable, streamScheduled ? StreamFrameAt(now + ScheduleLeadSeconds, rate) : streamBuffer.FirstFrame);
                    StopWooferOutputs(streamWooferBuffer);
                    streamWooferBuffer.Reset(syncFrom);
                    streamWooferBass.Reset();
                    streamWooferLimiter.Reset();
                    streamWooferEndDsp = 0;
                }
                for (int count = 0; count < MaxStreamUploadsPerTick &&
                    streamWooferBuffer.WrittenThrough + StreamUploadFrames <= written; count++)
                {
                    if (!streamWooferBuffer.TryAppend(retainFrom, StreamUploadFrames, readStreamWooferFrames)) break;
                }
            }

            if (!streamScheduled)
            {
                long needFrames = Math.Max(StreamUploadFrames * 2, (long)Math.Ceiling(StreamPrebufferSeconds * rate));
                if (streamBuffer.WrittenThrough - streamBuffer.FirstFrame < needFrames) return;
                streamStartFrame = streamBuffer.FirstFrame;
                streamStartDsp = now + ScheduleLeadSeconds;
                streamEndDsp = streamStartDsp;
                streamWooferEndDsp = streamStartDsp;
                streamScheduled = true;
            }

            double mainEnd = streamStartDsp + (streamBuffer.WrittenThrough - streamStartFrame) / (double)rate;
            bool extendMain = mainEnd > streamEndDsp;
            if (extendMain) streamEndDsp = mainEnd;
            PlayBufferedInternetStreamOutput(builtIn, streamBuffer, now, rate, streamEndDsp, extendMain);
            double wooferEnd = streamWooferBuffer == null ? streamEndDsp
                : streamStartDsp + (streamWooferBuffer.WrittenThrough - streamStartFrame) / (double)rate;
            bool extendWoofer = streamWooferBuffer != null && wooferEnd > streamWooferEndDsp;
            if (extendWoofer) streamWooferEndDsp = wooferEnd;
            foreach (var endpoint in endpoints.Values)
            {
                bool woofer = endpoint.Profile.IsWoofer && streamWooferBuffer != null;
                PlayBufferedInternetStreamOutput(endpoint, woofer ? streamWooferBuffer : streamBuffer,
                    now, rate, woofer ? streamWooferEndDsp : streamEndDsp, woofer ? extendWoofer : extendMain);
            }
        }

        private long StreamFrameAt(double dsp, int rate) =>
            streamStartFrame + (long)Math.Round(Math.Max(0, dsp - streamStartDsp) * rate);

        private void PlayBufferedInternetStreamOutput(RadioSpeakerOutput output, BufferedAudioClip buffer,
            double now, int rate, double endDsp, bool extend)
        {
            if (output.Source == null || buffer == null) return;
            if (output.Source.clip == buffer.Clip && output.Running &&
                (now < output.StartsAt || output.Source.isPlaying))
            {
                if (extend) output.ExtendBuffered(endDsp);
                return;
            }
            double start = Math.Max(now + ScheduleLeadSeconds, streamStartDsp);
            long frame = StreamFrameAt(start, rate);
            if (frame < buffer.FirstFrame || frame >= buffer.WrittenThrough || endDsp <= start) return;
            output.PlayBuffered(buffer, frame, start, endDsp, true);
        }

        private bool ReadInternetStreamFrames(long firstFrame, float[] data, int frames)
        {
            return internetStream != null && internetStream.TryCopy(firstFrame, data, frames);
        }

        private bool ReadInternetStreamWooferFrames(long firstFrame, float[] data, int frames)
        {
            if (internetStream == null || !internetStream.TryCopy(firstFrame, data, frames)) return false;
            streamWooferBass.Process(data, InternetRadioStream.Channels, frames);
            streamWooferLimiter.Process(data, InternetRadioStream.Channels, frames, 1f, internetStream.SampleRate);
            return true;
        }

        private void StopBufferedInternetStream()
        {
            if (streamBuffer == null || !streamPrimed) return;
            StopBufferedInternetStreamOutput(builtIn);
            foreach (var endpoint in endpoints.Values) StopBufferedInternetStreamOutput(endpoint);
            streamPrimed = streamScheduled = false;
            streamWooferEndDsp = 0;
        }

        private void StopBufferedInternetStreamOutput(RadioSpeakerOutput output)
        {
            if (output.Source == null) return;
            if ((streamBuffer != null && output.Source.clip == streamBuffer.Clip) ||
                (streamWooferBuffer != null && output.Source.clip == streamWooferBuffer.Clip))
            {
                output.Stop(true);
                output.Source.loop = false;
            }
        }

        private void ReleaseInternetStream()
        {
            StopBufferedInternetStream();
            streamResumeFrame = 0;
            streamBuffer?.Dispose();
            streamBuffer = null;
            streamWooferBuffer?.Dispose();
            streamWooferBuffer = null;
            readStreamFrames = null;
            readStreamWooferFrames = null;
            if (internetStream != null) { internetStream.Dispose(); internetStream = null; }
            StreamStation = "";
            StreamTitle = "";
            StreamArtist = "";
        }
    }
}
