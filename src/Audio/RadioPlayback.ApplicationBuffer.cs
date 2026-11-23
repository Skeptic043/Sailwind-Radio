using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    public sealed partial class RadioPlayback
    {
        internal const float MaximumAppBufferSeconds = 2.5f;
        private const double CaptureMarginSeconds = .04;
        private const double AppScheduleLeadSeconds = .03;
        private const int AppUploadFrames = 512;
        private const int MaxAppUploadsPerTick = 128;

        internal Func<float> AppBufferSeconds { private get; set; }
        private BufferedAudioClip appBuffer, appWooferBuffer;
        private ApplicationRing bufferedCapture;
        private BufferedAudioClip.ReadFrames readCapturedFrames, readCapturedWooferFrames;
        private readonly PeakLimiter appLimiter = new PeakLimiter();
        private readonly RadioBassFilter appWooferBass = new RadioBassFilter();
        private readonly PeakLimiter appWooferLimiter = new PeakLimiter();
        private float activeAppDelay, uploadedAppBoost;
        private int appCaptureGeneration;
        private bool appPrimed, appScheduled, appRebuildBuffer, appSkipSilence;
        private long appStartFrame;
        private double appStartDsp, appEndDsp, appWooferEndDsp;

        internal void UpdateBufferedApplication(ApplicationRing ring, float delay)
        {
            delay = Single.IsNaN(delay) ? 0f : Math.Min(MaximumAppBufferSeconds, Math.Max(0f, delay));
            if (appBuffer == null || bufferedCapture != ring)
            {
                ReleaseBufferedApplication();
                activeAppDelay = delay;
                bufferedCapture = ring;
                appBuffer = new BufferedAudioClip("Sailwind Radio buffered application", ring.SampleRate,
                    ApplicationRing.Channels, ring.Capacity, AppUploadFrames);
                readCapturedFrames = ReadCapturedFrames;
                readCapturedWooferFrames = ReadCapturedWooferFrames;
                appLimiter.Reset();
                appWooferBass.SetProfile(ring.SampleRate, true);
                appWooferLimiter.Reset();
                uploadedAppBoost = ApplicationRing.Boost;
            }
            else if (Math.Abs(activeAppDelay - delay) > 1e-4f)
            {
                // Changing delay rebuilds the buffer cleanly to the newly chosen size.
                StopBufferedApplication();
                activeAppDelay = delay;
            }

            if (!ring.TryGetWindow(out var window)) return;
            double now = AudioSettings.dspTime;
            int rate = ring.SampleRate;
            // Choose the reserve ahead of playback rounded to full upload blocks so priming never stalls on a partial block.
            long rawAhead = (long)Math.Ceiling((delay + CaptureMarginSeconds) * rate);
            long framesAhead = Math.Max(AppUploadFrames * 2,
                ((rawAhead + AppUploadFrames - 1) / AppUploadFrames) * AppUploadFrames);
            if (appPrimed)
            {
                if (appCaptureGeneration != window.Generation || appBuffer.WrittenThrough < window.FirstFrame ||
                    (appScheduled && now >= appEndDsp))
                {
                    StopBufferedApplication();
                }
            }
            if (!appPrimed)
            {
                appCaptureGeneration = window.Generation;
                // After a pause, underrun, or delay change, discard pre-pause audio and rebuild a fresh buffer
                // starting from the live edge. On a fresh start with existing ring audio, start from the newest
                // framesAhead window so startup stays responsive.
                long resetFrame = appRebuildBuffer
                    ? window.EndFrame
                    : Math.Max(window.FirstFrame, window.EndFrame - framesAhead);
                appRebuildBuffer = false;
                appBuffer.Reset(resetFrame);
                appLimiter.Reset();
                if (appWooferBuffer != null)
                {
                    appWooferBuffer.Reset(resetFrame);
                    appWooferBass.Reset();
                    appWooferLimiter.Reset();
                }
                appWooferEndDsp = 0;
                uploadedAppBoost = ApplicationRing.Boost;
                appPrimed = true;
            }

            // While rebuilding after a pause, skip digital silence delivered while the external app is still
            // waking up so silence never gets buffered and replayed ahead of real audio.
            if (!appScheduled && appSkipSilence)
            {
                if (ring.LastSoundEndFrame <= appBuffer.FirstFrame)
                {
                    appBuffer.Reset(window.EndFrame);
                    if (appWooferBuffer != null) appWooferBuffer.Reset(window.EndFrame);
                    return;
                }
                long soundStart = Math.Max(window.FirstFrame, Math.Max(appBuffer.FirstFrame, ring.LastSoundStartFrame));
                appBuffer.Reset(soundStart);
                if (appWooferBuffer != null) appWooferBuffer.Reset(soundStart);
                appSkipSilence = false;
            }

            long retainFrom = appScheduled ? Math.Max(appBuffer.FirstFrame, CapturedFrameAt(now, rate) - AppUploadFrames * 2)
                : appBuffer.FirstFrame;

            // Lazily build the woofer clip only while a woofer is connected, and keep it synced with appBuffer.
            bool needWoofer = HasWooferEndpoint();
            if (needWoofer && appWooferBuffer == null)
            {
                appWooferBuffer = new BufferedAudioClip("Sailwind Radio buffered application woofer", rate,
                    ApplicationRing.Channels, ring.Capacity, AppUploadFrames);
                appWooferBass.SetProfile(rate, true);
                appWooferLimiter.Reset();
                long syncFrom = appScheduled ? Math.Max(window.FirstFrame, CapturedFrameAt(now + ScheduleLeadSeconds, rate))
                    : appBuffer.FirstFrame;
                appWooferBuffer.Reset(syncFrom);
                appWooferEndDsp = 0;
            }
            else if (!needWoofer && appWooferBuffer != null)
            {
                appWooferBuffer.Dispose();
                appWooferBuffer = null;
                appWooferEndDsp = 0;
            }

            // If the player moved the App volume slider, refresh already-queued future blocks so the change is heard right away.
            float currentBoost = ApplicationRing.Boost;
            if (Math.Abs(currentBoost - uploadedAppBoost) > 1e-4f)
            {
                uploadedAppBoost = currentBoost;
                if (appScheduled)
                {
                    long rewriteFrom = Math.Max(appBuffer.FirstFrame, CapturedFrameAt(now, rate) + AppUploadFrames * 2);
                    RewriteFutureBlocks(appBuffer, rewriteFrom, readCapturedFrames, appLimiter);
                    if (appWooferBuffer != null)
                    {
                        appWooferBass.Reset();
                        RewriteFutureBlocks(appWooferBuffer, Math.Max(appWooferBuffer.FirstFrame, rewriteFrom),
                            readCapturedWooferFrames, appWooferLimiter);
                    }
                }
            }

            for (int count = 0; count < MaxAppUploadsPerTick &&
                appBuffer.WrittenThrough + AppUploadFrames <= window.EndFrame; count++)
            {
                if (!appBuffer.TryAppend(retainFrom, AppUploadFrames, readCapturedFrames)) break;
            }
            if (appWooferBuffer != null)
            {
                if (appWooferBuffer.WrittenThrough < window.FirstFrame)
                {
                    long syncFrom = Math.Max(window.FirstFrame, appScheduled ? CapturedFrameAt(now + ScheduleLeadSeconds, rate) : appBuffer.FirstFrame);
                    StopWooferOutputs(appWooferBuffer);
                    appWooferBuffer.Reset(syncFrom);
                    appWooferBass.Reset();
                    appWooferLimiter.Reset();
                    appWooferEndDsp = 0;
                }
                for (int count = 0; count < MaxAppUploadsPerTick &&
                    appWooferBuffer.WrittenThrough + AppUploadFrames <= window.EndFrame; count++)
                {
                    if (!appWooferBuffer.TryAppend(retainFrom, AppUploadFrames, readCapturedWooferFrames)) break;
                }
            }

            if (!appScheduled)
            {
                if (appBuffer.WrittenThrough - appBuffer.FirstFrame < framesAhead) return;
                appStartFrame = appBuffer.FirstFrame;
                appStartDsp = now + AppScheduleLeadSeconds;
                appEndDsp = appStartDsp;
                appWooferEndDsp = appStartDsp;
                appScheduled = true;
            }

            double mainEnd = appStartDsp + (appBuffer.WrittenThrough - appStartFrame) / (double)rate;
            bool extendMain = mainEnd > appEndDsp;
            if (extendMain) appEndDsp = mainEnd;
            PlayBufferedApplicationOutput(builtIn, appBuffer, now, rate, appEndDsp, extendMain);
            double wooferEnd = appWooferBuffer == null ? appEndDsp
                : appStartDsp + (appWooferBuffer.WrittenThrough - appStartFrame) / (double)rate;
            bool extendWoofer = appWooferBuffer != null && wooferEnd > appWooferEndDsp;
            if (extendWoofer) appWooferEndDsp = wooferEnd;
            foreach (var endpoint in endpoints.Values)
            {
                bool woofer = endpoint.Profile.IsWoofer && appWooferBuffer != null;
                PlayBufferedApplicationOutput(endpoint, woofer ? appWooferBuffer : appBuffer,
                    now, rate, woofer ? appWooferEndDsp : appEndDsp, woofer ? extendWoofer : extendMain);
            }
        }

        private static void RewriteFutureBlocks(BufferedAudioClip buffer, long fromFrame,
            BufferedAudioClip.ReadFrames read, PeakLimiter limiter)
        {
            // Align to the buffer's uploaded block boundaries so each rewrite lands cleanly without wrapping mid-block.
            long offset = (fromFrame - buffer.FirstFrame) % buffer.BlockFrames;
            long at = offset == 0 ? fromFrame : fromFrame + (buffer.BlockFrames - offset);
            limiter.Reset();
            while (at + buffer.BlockFrames <= buffer.WrittenThrough)
            {
                if (!buffer.TryOverwrite(at, buffer.BlockFrames, read)) break;
                at += buffer.BlockFrames;
            }
        }

        private long CapturedFrameAt(double dsp, int rate) =>
            appStartFrame + (long)Math.Round(Math.Max(0, dsp - appStartDsp) * rate);

        private void PlayBufferedApplicationOutput(RadioSpeakerOutput output, BufferedAudioClip buffer,
            double now, int rate, double endDsp, bool extend)
        {
            if (output.Source == null || buffer == null) return;
            if (output.Source.clip == buffer.Clip && output.Running &&
                (now < output.StartsAt || output.Source.isPlaying))
            {
                if (extend) output.ExtendBuffered(endDsp);
                return;
            }
            double start = Math.Max(now + AppScheduleLeadSeconds, appStartDsp);
            long frame = CapturedFrameAt(start, rate);
            if (frame < buffer.FirstFrame || frame >= buffer.WrittenThrough || endDsp <= start) return;
            output.PlayBuffered(buffer, frame, start, endDsp, true);
        }

        private bool ReadCapturedFrames(long firstFrame, float[] data, int frames)
        {
            if (!bufferedCapture.TryCopy(firstFrame, data, frames, appCaptureGeneration)) return false;
            float gain = ApplicationRing.Gain * ApplicationRing.Boost;
            int samples = frames * ApplicationRing.Channels;
            for (int i = 0; i < samples; i++) data[i] *= gain;
            appLimiter.Process(data, ApplicationRing.Channels, frames, 1f, bufferedCapture.SampleRate);
            return true;
        }

        private bool ReadCapturedWooferFrames(long firstFrame, float[] data, int frames)
        {
            if (!bufferedCapture.TryCopy(firstFrame, data, frames, appCaptureGeneration)) return false;
            float gain = ApplicationRing.Gain * ApplicationRing.Boost;
            int samples = frames * ApplicationRing.Channels;
            for (int i = 0; i < samples; i++) data[i] *= gain;
            appWooferBass.Process(data, ApplicationRing.Channels, frames);
            appWooferLimiter.Process(data, ApplicationRing.Channels, frames, 1f, bufferedCapture.SampleRate);
            return true;
        }

        private void StopWooferOutputs(BufferedAudioClip buffer)
        {
            if (buffer == null) return;
            foreach (var endpoint in endpoints.Values)
                if (endpoint.Source != null && endpoint.Source.clip == buffer.Clip) endpoint.Stop(true);
        }

        internal void StopBufferedApplication()
        {
            if (appBuffer == null) return;
            if (appPrimed)
            {
                StopBufferedApplicationOutput(builtIn);
                foreach (var endpoint in endpoints.Values) StopBufferedApplicationOutput(endpoint);
                appPrimed = appScheduled = false;
                appWooferEndDsp = 0;
            }
            // Pausing, underrunning, or changing delay wipes the buffer so unpausing rebuilds a full fresh reserve.
            appRebuildBuffer = true;
            appSkipSilence = true;
        }

        private void StopBufferedApplicationOutput(RadioSpeakerOutput output)
        {
            if (output.Source == null) return;
            if ((appBuffer != null && output.Source.clip == appBuffer.Clip) ||
                (appWooferBuffer != null && output.Source.clip == appWooferBuffer.Clip))
            {
                output.Stop(true);
                output.Source.loop = false;
            }
        }

        private void ReleaseBufferedApplication()
        {
            if (appBuffer == null && appWooferBuffer == null) return;
            StopBufferedApplication();
            appBuffer?.Dispose();
            appBuffer = null;
            appWooferBuffer?.Dispose();
            appWooferBuffer = null;
            bufferedCapture = null;
            appRebuildBuffer = false;
            appSkipSilence = false;
        }
    }
}
