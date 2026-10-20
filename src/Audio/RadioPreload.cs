using System;
using System.IO;
using UnityEngine;

namespace SailwindRadio.Audio
{
    public sealed partial class RadioPlayback
    {
        private string preloadPath;
        // Skipped means the speculative load failed. Selection then uses the normal loader.
        private bool preloadStarted, preloadSkipped;
        // An upcoming stream opens at its start and prefills its window.
        private StreamedAudio preloadStream;
        private double preloadStartedAt;

        /// <summary>Keep only one upcoming track beside the current track. Empty cancels it.</summary>
        public void PreloadTrack(string path)
        {
            if (disposed) return;
            path = path ?? "";
            if (String.Equals(path, state.TrackPath, StringComparison.OrdinalIgnoreCase)) path = "";
            if (String.Equals(path, preloadPath ?? "", StringComparison.OrdinalIgnoreCase)) return;
            ReleasePreload();
            preloadPath = path;
        }

        private void PollPreload(bool currentReady)
        {
            if (String.IsNullOrEmpty(preloadPath) || preloadSkipped) return;
            try
            {
                if (!preloadStarted)
                {
                    if (!currentReady) return;
                    string fullPath = LocalPath(preloadPath);
                    preloadStartedAt = Time.realtimeSinceStartup;
                    preloadStarted = true;
                    if (!PcmDecoders.Supports(fullPath))
                        throw new IOException(PcmDecoders.UnsupportedMessage(fullPath) ?? PcmDecoders.ChooseFormatMessage);
                    preloadStream = StreamedAudio.Start(fullPath, 0);
                }
                if (preloadStream.Fault != null) throw new IOException("Upcoming stream failed");
                if (!preloadStream.IsOpen && Time.realtimeSinceStartup - preloadStartedAt >= LoadTimeoutSeconds)
                    throw new IOException("Upcoming load timed out");
            }
            catch
            {
                // Speculation never interrupts or warns over the current song. A real
                // selection retries through the normal loader and exposes failure there.
                SkipPreload();
            }
        }

        private void SkipPreload()
        {
            string skippedPath = preloadPath;
            ReleasePreload();
            preloadPath = skippedPath;
            preloadSkipped = true;
        }

        private bool AdoptPreload(string path)
        {
            if (!preloadStarted || preloadSkipped || !String.Equals(path, preloadPath, StringComparison.OrdinalIgnoreCase)) return false;
            requestedPath = path;
            TrackLabel = SafeLabel(path);
            // An open stream gets its clip on the next Tick. One still opening is simply awaited there.
            streamed = preloadStream;
            // The load deadline counts from adoption, so a preload still opening cannot time out at once.
            loadStartedAt = Time.realtimeSinceStartup;
            preloadStream = null;
            preloadPath = null;
            preloadStarted = preloadSkipped = false;
            return true;
        }

        // A preload of another track survives a selection that cannot adopt it, such as a restart of
        // the current track. PreloadTrack reconciles it with the queue on the next Tick. A preload of
        // the selected track that cannot be adopted is released, so the track never opens twice.
        private bool KeepOrAdoptPreload(string path)
        {
            if (AdoptPreload(path)) return true;
            if (String.Equals(path ?? "", preloadPath ?? "", StringComparison.OrdinalIgnoreCase)) ReleasePreload();
            return false;
        }

        private void ReleasePreload()
        {
            // Stream disposal only signals its worker, so this never blocks the Unity thread.
            if (preloadStream != null) preloadStream.Dispose();
            preloadPath = null;
            preloadStream = null;
            preloadStarted = preloadSkipped = false;
        }
    }
}
