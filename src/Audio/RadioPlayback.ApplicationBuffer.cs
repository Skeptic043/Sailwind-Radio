using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    public sealed partial class RadioPlayback
    {
        internal const float MaximumAppBufferSeconds = 2.5f;
        private const double CaptureMarginSeconds = .06;
        private const int AppUploadFrames = 512;
        private const int MaxAppUploadsPerTick = 128;
        private const int ClearBehindBlocks = 4;

        internal Func<float> AppBufferSeconds { private get; set; }
        private BufferedAudioClip appBuffer, appWooferBuffer;
        private ApplicationRing bufferedCapture;
        private BufferedAudioClip.ReadFrames readCapturedFrames, readCapturedWooferFrames;
        private readonly PeakLimiter appLimiter = new PeakLimiter();
        private readonly RadioBassFilter appWooferBass = new RadioBassFilter();
        private readonly PeakLimiter appWooferLimiter = new PeakLimiter();
        private float activeAppDelay, uploadedAppBoost;
        private int appCaptureGeneration;
        private bool appPrimed, appRunning, appRebuildBuffer, appSkipSilence;
        private long appStartFrame, appClearedThrough, appWooferClearedThrough;
        private double appStartDsp, nextAppHealthCheck;

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
                appBuffer.ClearAll();
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
                    (appRunning && CapturedFrameAt(now, rate) >= appBuffer.WrittenThrough))
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
                appBuffer.ClearAll();
                appClearedThrough = resetFrame;
                appLimiter.Reset();
                if (appWooferBuffer != null)
                {
                    appWooferBuffer.Reset(resetFrame);
                    appWooferBuffer.ClearAll();
                    appWooferClearedThrough = resetFrame;
                    appWooferBass.Reset();
                    appWooferLimiter.Reset();
                }
                uploadedAppBoost = ApplicationRing.Boost;
                appPrimed = true;
            }

            // While rebuilding a multi-frame delay buffer after a pause, skip digital silence delivered while the
            // external app is still waking up so silence never fills the delay reserve ahead of real audio.
            if (!appRunning && appSkipSilence)
            {
                if (delay <= 0f)
                {
                    appSkipSilence = false;
                }
                else
                {
                    if (ring.LastSoundEndFrame <= appBuffer.FirstFrame)
                    {
                        appBuffer.Reset(window.EndFrame);
                        appClearedThrough = window.EndFrame;
                        if (appWooferBuffer != null)
                        {
                            appWooferBuffer.Reset(window.EndFrame);
                            appWooferClearedThrough = window.EndFrame;
                        }
                        return;
                    }
                    long soundStart = Math.Max(window.FirstFrame, Math.Max(appBuffer.FirstFrame, ring.LastSoundStartFrame));
                    appBuffer.Reset(soundStart);
                    appClearedThrough = soundStart;
                    if (appWooferBuffer != null)
                    {
                        appWooferBuffer.Reset(soundStart);
                        appWooferClearedThrough = soundStart;
                    }
                    appSkipSilence = false;
                }
            }

            long playFrame = appRunning ? CapturedFrameAt(now, rate) : appBuffer.FirstFrame;
            long retainFrom = appRunning ? Math.Max(appBuffer.FirstFrame, playFrame - AppUploadFrames * 2)
                : appBuffer.FirstFrame;

            // Lazily build the woofer clip only while a woofer is connected, and keep it synced with appBuffer.
            bool needWoofer = HasWooferEndpoint();
            if (needWoofer && appWooferBuffer == null)
            {
                appWooferBuffer = new BufferedAudioClip("Sailwind Radio buffered application woofer", rate,
                    ApplicationRing.Channels, ring.Capacity, AppUploadFrames);
                appWooferBuffer.ClearAll();
                appWooferBass.SetProfile(rate, true);
                appWooferLimiter.Reset();
                long syncFrom = appRunning ? Math.Max(window.FirstFrame, playFrame) : appBuffer.FirstFrame;
                appWooferBuffer.Reset(syncFrom);
                appWooferClearedThrough = syncFrom;
            }
            else if (!needWoofer && appWooferBuffer != null)
            {
                appWooferBuffer.Dispose();
                appWooferBuffer = null;
                appWooferClearedThrough = 0;
            }

            // If the player moved the App volume slider, refresh already-queued future blocks so the change is heard right away.
            float currentBoost = ApplicationRing.Boost;
            if (Math.Abs(currentBoost - uploadedAppBoost) > 1e-4f)
            {
                uploadedAppBoost = currentBoost;
                if (appRunning)
                {
                    long rewriteFrom = Math.Max(appBuffer.FirstFrame, playFrame + AppUploadFrames * 2);
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
                    long syncFrom = Math.Max(window.FirstFrame, appRunning ? playFrame : appBuffer.FirstFrame);
                    StopWooferOutputs(appWooferBuffer);
                    appWooferBuffer.Reset(syncFrom);
                    appWooferBuffer.ClearAll();
                    appWooferClearedThrough = syncFrom;
                    appWooferBass.Reset();
                    appWooferLimiter.Reset();
                }
                for (int count = 0; count < MaxAppUploadsPerTick &&
                    appWooferBuffer.WrittenThrough + AppUploadFrames <= window.EndFrame; count++)
                {
                    if (!appWooferBuffer.TryAppend(retainFrom, AppUploadFrames, readCapturedWooferFrames)) break;
                }
            }

            if (!appRunning)
            {
                if (appBuffer.WrittenThrough - appBuffer.FirstFrame < framesAhead) return;
                appStartFrame = appBuffer.FirstFrame;
                appStartDsp = now;
                appRunning = true;
                nextAppHealthCheck = now + .25;
                playFrame = appStartFrame;
            }

            // Keep old ring blocks behind the playhead zeroed so any save freeze or app pause plays clean silence
            // without ever needing SetScheduledEndTime or filtered PlayScheduled restarts.
            ClearBehind(appBuffer, ref appClearedThrough, playFrame);
            if (appWooferBuffer != null) ClearBehind(appWooferBuffer, ref appWooferClearedThrough, playFrame);

            PlayBufferedApplicationOutput(builtIn, appBuffer, now, rate);
            foreach (var endpoint in endpoints.Values)
            {
                bool woofer = endpoint.Profile.IsWoofer && appWooferBuffer != null;
                PlayBufferedApplicationOutput(endpoint, woofer ? appWooferBuffer : appBuffer, now, rate);
            }
            CheckAppOutputRecovery(builtIn, appBuffer, now, rate);
            foreach (var endpoint in endpoints.Values)
            {
                bool woofer = endpoint.Profile.IsWoofer && appWooferBuffer != null;
                CheckAppOutputRecovery(endpoint, woofer ? appWooferBuffer : appBuffer, now, rate);
            }
            if (now >= nextAppHealthCheck)
            {
                nextAppHealthCheck = now + .25;
                RepairAppOutput(builtIn, appBuffer, now, rate);
                foreach (var endpoint in endpoints.Values)
                {
                    bool woofer = endpoint.Profile.IsWoofer && appWooferBuffer != null;
                    RepairAppOutput(endpoint, woofer ? appWooferBuffer : appBuffer, now, rate);
                }
            }
        }

        private static void ClearBehind(BufferedAudioClip buffer, ref long clearedThrough, long playFrame)
        {
            long limit = playFrame - AppUploadFrames * ClearBehindBlocks;
            if (clearedThrough < buffer.FirstFrame) clearedThrough = buffer.FirstFrame;
            int cleared = 0;
            while (cleared < MaxAppUploadsPerTick && clearedThrough + AppUploadFrames <= limit &&
                clearedThrough + AppUploadFrames <= buffer.WrittenThrough)
            {
                buffer.ClearBlock(clearedThrough);
                clearedThrough += AppUploadFrames;
                cleared++;
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
            double now, int rate)
        {
            if (output.Source == null || buffer == null) return;
            if (output.Source.clip == buffer.Clip && output.Running && output.Source.isPlaying) return;
            long frame = CapturedFrameAt(now, rate);
            if (frame < buffer.FirstFrame || frame >= buffer.WrittenThrough) return;
            if (output.Paused && output.Source.clip == buffer.Clip)
            {
                output.Resume(buffer.ClipFrame(frame), now);
                if (output.Source.isPlaying)
                {
                    output.WasInaudible = output.Source.isVirtual || output.Source.volume <= 0f;
                    return;
                }
                output.Stop();
            }
            output.PlayLooping(buffer, frame, now);
            output.WasInaudible = output.Source.isVirtual || output.Source.volume <= 0f;
        }

        // Walking out of speaker range virtualizes the voice at volume 0. Snap a drifted cursor back to live
        // audio on the first frame it becomes audible again, without disturbing an already-aligned voice.
        private void CheckAppOutputRecovery(RadioSpeakerOutput output, BufferedAudioClip buffer, double now, int rate)
        {
            if (output.Source == null || buffer == null || !output.Running || output.Source.clip != buffer.Clip) return;
            bool inaudible = output.Source.isVirtual || output.Source.volume <= 0f;
            if (inaudible)
            {
                output.WasInaudible = true;
                output.DriftObservations = 0;
                return;
            }
            if (!output.WasInaudible) return;
            output.WasInaudible = false;
            if (!output.Source.isPlaying) return;
            if (!TryGetAppDrift(output, buffer, now, rate, out int expected, out int difference)) return;
            if (difference <= rate * AppDriftTolerance(rate)) return;
            output.Pause();
            output.Resume(expected, now);
            DriftRepairs++;
        }

        private void RepairAppOutput(RadioSpeakerOutput output, BufferedAudioClip buffer, double now, int rate)
        {
            if (!output.Running || output.Source == null || buffer == null ||
                output.Source.clip != buffer.Clip || now < output.SettlesAt) return;
            if (!output.Source.isPlaying || output.Source.isVirtual || output.Source.volume <= 0f)
            {
                output.DriftObservations = 0;
                return;
            }
            if (!TryGetAppDrift(output, buffer, now, rate, out int expected, out int difference)) return;
            if (difference <= rate * AppDriftTolerance(rate))
            {
                output.DriftObservations = 0;
                return;
            }
            if (++output.DriftObservations < 2) return;
            DriftRepairs++;
            output.Pause();
            output.Resume(expected, now);
        }

        private bool TryGetAppDrift(RadioSpeakerOutput output, BufferedAudioClip buffer,
            double now, int rate, out int expected, out int difference)
        {
            expected = difference = 0;
            long expectedFrame = CapturedFrameAt(now, rate);
            if (expectedFrame < buffer.FirstFrame || expectedFrame >= buffer.WrittenThrough) return false;
            expected = buffer.ClipFrame(expectedFrame);
            difference = Math.Abs(output.Source.timeSamples - expected);
            difference = Math.Min(difference, buffer.CapacityFrames - difference);
            return true;
        }

        private static double AppDriftTolerance(int rate)
        {
            int bufferLength, bufferCount;
            AudioSettings.GetDSPBufferSize(out bufferLength, out bufferCount);
            return Math.Max(.05, 2d * bufferLength / Math.Max(8000, rate) + .01);
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
                PauseBufferedApplicationOutput(builtIn);
                foreach (var endpoint in endpoints.Values) PauseBufferedApplicationOutput(endpoint);
                appPrimed = appRunning = false;
            }
            // Pausing, underrunning, or changing delay wipes the buffer so unpausing rebuilds a full fresh reserve.
            appRebuildBuffer = true;
            appSkipSilence = true;
        }

        private void PauseBufferedApplicationOutput(RadioSpeakerOutput output)
        {
            if (output.Source == null) return;
            if ((appBuffer != null && output.Source.clip == appBuffer.Clip) ||
                (appWooferBuffer != null && output.Source.clip == appWooferBuffer.Clip))
            {
                if (output.Running && output.Source.isPlaying) output.Pause();
                else output.Stop();
            }
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
            StopBufferedApplicationOutput(builtIn);
            foreach (var endpoint in endpoints.Values) StopBufferedApplicationOutput(endpoint);
            appPrimed = appRunning = false;
            appBuffer?.Dispose();
            appBuffer = null;
            appWooferBuffer?.Dispose();
            appWooferBuffer = null;
            bufferedCapture = null;
            appClearedThrough = 0;
            appWooferClearedThrough = 0;
            appRebuildBuffer = false;
            appSkipSilence = false;
        }
    }
}
