using System;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using UnityEngine;
using UnityEngine.Networking;

namespace SailwindRadio
{
    public sealed partial class RadioPlayback
    {
        private string preloadPath;
        private bool preloadStarted, preloadFailed;
        private AudioClip preloadClip;
        private UnityWebRequest preloadRequest;
        private Task<DecodedMp3> preloadTask;
        private CancellationTokenSource preloadCancellation;
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

        private void PollPreload()
        {
            if (String.IsNullOrEmpty(preloadPath) || preloadFailed) return;
            try
            {
                // Give the selected track priority and avoid a third decoded allocation.
                if (!preloadStarted)
                {
                    if (!state.Powered || clip == null || clip.loadState != AudioDataLoadState.Loaded) return;
                    if (!Path.IsPathRooted(preloadPath)) throw new IOException("Expected an absolute local path");
                    string fullPath = Path.GetFullPath(preloadPath);
                    var uri = new Uri(fullPath);
                    if (!uri.IsFile || uri.IsUnc || !File.Exists(fullPath)) throw new IOException("Upcoming track unavailable");
                    if (new FileInfo(fullPath).Length > MaximumMp3FileBytes) throw new IOException("Upcoming file exceeds the size limit");
                    string extension = Path.GetExtension(fullPath).ToLowerInvariant();
                    preloadStartedAt = Time.realtimeSinceStartup;
                    preloadStarted = true;
                    if (extension == ".mp3")
                    {
                        preloadCancellation = new CancellationTokenSource();
                        preloadCancellation.CancelAfter((int)(LoadTimeoutSeconds * 1000));
                        CancellationToken token = preloadCancellation.Token;
                        preloadTask = Task.Run(() => DecodeMp3(fullPath, token), token);
                    }
                    else
                    {
                        AudioType type = extension == ".ogg" ? AudioType.OGGVORBIS : extension == ".wav" ? AudioType.WAV : AudioType.UNKNOWN;
                        if (type == AudioType.UNKNOWN) throw new IOException("Unsupported upcoming format");
                        preloadRequest = UnityWebRequestMultimedia.GetAudioClip(uri.AbsoluteUri, type);
                        ((DownloadHandlerAudioClip)preloadRequest.downloadHandler).streamAudio = false;
                        preloadRequest.SendWebRequest();
                    }
                }
                if (preloadRequest != null && preloadRequest.isDone)
                {
                    if (preloadRequest.isNetworkError || preloadRequest.isHttpError) throw new IOException("Upcoming decode failed");
                    preloadClip = DownloadHandlerAudioClip.GetContent(preloadRequest);
                    preloadRequest.Dispose();
                    preloadRequest = null;
                    ValidatePreloadClip();
                }
                if (preloadTask != null && preloadTask.IsCompleted)
                {
                    if (preloadTask.IsCanceled || preloadTask.IsFaulted) throw new IOException("Upcoming MP3 decode failed");
                    DecodedMp3 decoded = preloadTask.Result;
                    preloadTask = null;
                    preloadCancellation.Dispose();
                    preloadCancellation = null;
                    preloadClip = AudioClip.Create("Sailwind Radio next MP3", decoded.SampleCount / decoded.Channels, decoded.Channels, decoded.SampleRate, false);
                    if (preloadClip == null) throw new IOException("Upcoming clip allocation failed");
                    int offset = 0;
                    foreach (float[] block in decoded.Blocks)
                    {
                        if (!preloadClip.SetData(block, offset)) throw new IOException("Upcoming clip upload failed");
                        offset += block.Length / decoded.Channels;
                    }
                    ValidatePreloadClip();
                }
                if (preloadClip != null && preloadClip.loadState == AudioDataLoadState.Failed) throw new IOException("Upcoming audio data failed");
                if ((preloadClip == null || preloadClip.loadState != AudioDataLoadState.Loaded) &&
                    Time.realtimeSinceStartup - preloadStartedAt >= LoadTimeoutSeconds) throw new IOException("Upcoming load timed out");
            }
            catch
            {
                // Speculation never interrupts or warns over the current song. A real
                // selection retries through the normal loader and exposes failure there.
                string failedPath = preloadPath;
                ReleasePreload();
                preloadPath = failedPath;
                preloadFailed = true;
            }
        }

        private void ValidatePreloadClip()
        {
            if (preloadClip == null || preloadClip.samples <= 0 || preloadClip.frequency <= 0 ||
                preloadClip.loadState == AudioDataLoadState.Failed || (long)preloadClip.samples * preloadClip.channels > MaximumMp3Samples)
                throw new IOException("Upcoming audio exceeds the decoded limit or is invalid");
        }

        private bool AdoptPreload(string path)
        {
            if (!preloadStarted || preloadFailed || !String.Equals(path, preloadPath, StringComparison.OrdinalIgnoreCase)) return false;
            requestedPath = path;
            TrackLabel = SafeLabel(path);
            clip = preloadClip;
            request = preloadRequest;
            mp3Task = preloadTask;
            mp3Cancellation = preloadCancellation;
            loadStartedAt = preloadStartedAt;
            if (source != null) source.clip = clip;
            preloadClip = null;
            preloadRequest = null;
            preloadTask = null;
            preloadCancellation = null;
            preloadPath = null;
            preloadStarted = preloadFailed = false;
            return true;
        }

        private void ReleasePreload()
        {
            if (preloadCancellation != null)
            {
                Task<DecodedMp3> abandoned = preloadTask;
                CancellationTokenSource cancellation = preloadCancellation;
                cancellation.Cancel();
                if (abandoned == null) cancellation.Dispose();
                else abandoned.ContinueWith(completed =>
                {
                    if (completed.IsFaulted) { var observed = completed.Exception; }
                    cancellation.Dispose();
                }, TaskScheduler.Default);
            }
            try { DiscardRequest(ref preloadRequest, preloadClip); }
            finally
            {
                if (preloadClip != null) UnityEngine.Object.Destroy(preloadClip);
                preloadPath = null;
                preloadClip = null;
                preloadTask = null;
                preloadCancellation = null;
                preloadStarted = preloadFailed = false;
            }
        }

        private static void DiscardRequest(ref UnityWebRequest owned, AudioClip retained)
        {
            UnityWebRequest abandoned = owned;
            owned = null;
            if (abandoned == null) return;
            try
            {
                if (!abandoned.isDone) abandoned.Abort();
                else if (!abandoned.isNetworkError && !abandoned.isHttpError)
                {
                    // A completed request can own a native clip before Tick observes it.
                    AudioClip unused = DownloadHandlerAudioClip.GetContent(abandoned);
                    if (unused != null && unused != retained) UnityEngine.Object.Destroy(unused);
                }
            }
            catch { /* Teardown must still release the request if native content is gone. */ }
            finally { abandoned.Dispose(); }
        }
    }
}
