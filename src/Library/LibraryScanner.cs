using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using System.Threading.Tasks;

namespace SailwindRadio.Library
{
    // RequestScan/Poll/Dispose belong to the host thread. Scan performs no Unity work.
    // A blocked filesystem call cannot be interrupted, so replacement requests wait for that
    // one worker instead of spawning an unbounded number of canceled workers.
    internal sealed class LibraryScanner : IDisposable
    {
        internal const int MaximumRoots = 64;
        internal const int MaximumDirectories = 20000;
        internal const int MaximumEntries = 100000;
        internal const int MaximumTracks = 20000;
        private Task<LibrarySnapshot> task;
        private CancellationTokenSource cancellation;
        private string pending;
        private bool hasPending;
        private bool disposed;
        private readonly MetadataCache metadata = new MetadataCache();
        internal LibrarySnapshot Snapshot { get; private set; } = LibrarySnapshot.Empty;
        internal bool IsScanning => !disposed && (task != null || hasPending);

        internal void RequestScan(string configuredRoots)
        {
            if (disposed) return;
            pending = configuredRoots ?? "";
            hasPending = true;
            cancellation?.Cancel();
            if (task == null) StartPending();
        }

        private void StartPending()
        {
            string roots = pending;
            hasPending = false;
            cancellation = new CancellationTokenSource();
            CancellationToken token = cancellation.Token;
            task = Task.Run(() => Scan(roots, token, metadata), token);
        }

        internal bool Poll()
        {
            if (disposed || task == null || !task.IsCompleted) return false;
            bool changed = false;
            if (task.Status == TaskStatus.RanToCompletion && !hasPending && !cancellation.IsCancellationRequested)
            {
                Snapshot = task.Result;
                changed = true;
            }
            // Observe all faults even when a newer request has superseded the result.
            if (task.IsFaulted) { var ignored = task.Exception; }
            task = null;
            cancellation.Dispose();
            cancellation = null;
            if (hasPending) StartPending();
            return changed;
        }

        internal static LibrarySnapshot Scan(string configuration, CancellationToken token, MetadataCache metadata = null)
        {
            var roots = new List<string>();
            var rootSet = new HashSet<string>(LibraryPaths.Comparer);
            var warnings = new List<string>();
            foreach (string raw in (configuration ?? "").Split(new[] { '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                if (roots.Count >= MaximumRoots) { Warn(warnings, "Only the first 64 collection folders were scanned."); break; }
                try
                {
                    string root = LibraryPaths.Canonical(raw);
                    if (rootSet.Add(root)) roots.Add(root);
                }
                catch (Exception error) when (LibraryPaths.FilesystemError(error)) { Warn(warnings, "A collection folder path could not be read: " + raw); }
            }
            roots.Sort(LibraryPaths.Comparer);
            var labels = new Dictionary<string, int>(LibraryPaths.Comparer);
            foreach (string root in roots)
            {
                string name = FolderName(root);
                labels.TryGetValue(name, out int count);
                labels[name] = count + 1;
            }
            var collections = new List<MusicCollection>();
            var tracksByPath = new Dictionary<string, MusicTrack>(LibraryPaths.Comparer);
            int directories = 0, entries = 0;
            foreach (string root in roots)
            {
                token.ThrowIfCancellationRequested();
                var tracks = new List<MusicTrack>();
                var visited = new HashSet<string>(LibraryPaths.Comparer);
                var stack = new Stack<KeyValuePair<string, int>>();
                stack.Push(new KeyValuePair<string, int>(root, 0));
                while (stack.Count > 0)
                {
                    token.ThrowIfCancellationRequested();
                    var current = stack.Pop();
                    if (!visited.Add(current.Key)) continue;
                    if (directories >= MaximumDirectories || entries >= MaximumEntries || tracksByPath.Count >= MaximumTracks)
                    {
                        Warn(warnings, "The music library reached its scan limit. Use smaller collection folders.");
                        break;
                    }
                    directories++;
                    try
                    {
                        var directory = new DirectoryInfo(current.Key);
                        if (!directory.Exists) { Warn(warnings, "A music folder could not be read: " + current.Key); continue; }
                        if ((directory.Attributes & FileAttributes.ReparsePoint) != 0) continue;
                        using (var items = directory.EnumerateFileSystemInfos().GetEnumerator())
                        {
                            while (items.MoveNext())
                            {
                                token.ThrowIfCancellationRequested();
                                if (++entries > MaximumEntries)
                                { Warn(warnings, "The music library reached its scan limit. Use smaller collection folders."); break; }
                                var item = items.Current;
                                FileAttributes attributes;
                                try { attributes = item.Attributes; }
                                catch (Exception error) when (LibraryPaths.FilesystemError(error)) { continue; }
                                if ((attributes & FileAttributes.ReparsePoint) != 0) continue;
                                if ((attributes & FileAttributes.Directory) != 0)
                                {
                                    if (current.Value < 64) stack.Push(new KeyValuePair<string, int>(item.FullName, current.Value + 1));
                                    else Warn(warnings, "A collection contains folders deeper than the scan limit.");
                                    continue;
                                }
                                if (!LibraryPaths.Supported(item.Name)) continue;
                                string path = item.FullName;
                                if (!tracksByPath.TryGetValue(path, out var track))
                                {
                                    if (tracksByPath.Count >= MaximumTracks)
                                    { Warn(warnings, "The music library reached its scan limit. Use smaller collection folders."); break; }
                                    track = new MusicTrack(path, metadata?.GetInfo(path));
                                    tracksByPath.Add(path, track);
                                }
                                tracks.Add(track);
                            }
                        }
                    }
                    catch (Exception error) when (LibraryPaths.FilesystemError(error))
                    { Warn(warnings, "A music folder could not be read: " + current.Key); }
                }
                tracks.Sort((left, right) => LibraryPaths.Comparer.Compare(left.Path, right.Path));
                string label = FolderName(root);
                if (labels[label] > 1) label += " (" + (System.IO.Path.GetDirectoryName(root) ?? root) + ")";
                collections.Add(new MusicCollection(root, label, tracks.ToArray()));
            }
            metadata?.Prune(tracksByPath.Keys);
            return new LibrarySnapshot(collections.ToArray(), warnings.ToArray());
        }

        private static string FolderName(string root)
        {
            string name = System.IO.Path.GetFileName(root);
            return string.IsNullOrEmpty(name) ? root : name;
        }

        private static void Warn(List<string> warnings, string warning)
        {
            if (warnings.Count < 16 && !warnings.Contains(warning)) warnings.Add(warning);
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            hasPending = false;
            cancellation?.Cancel();
            if (task != null)
            {
                var source = cancellation;
                task.ContinueWith(completed =>
                {
                    if (completed.IsFaulted) { var ignored = completed.Exception; }
                    source.Dispose();
                }, CancellationToken.None, TaskContinuationOptions.ExecuteSynchronously, TaskScheduler.Default);
            }
            cancellation = null;
            task = null;
        }
    }
}
