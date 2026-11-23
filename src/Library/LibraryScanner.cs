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
            int entry = 0;
            foreach (string raw in (configuration ?? "").Split(new[] { '|', '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                token.ThrowIfCancellationRequested();
                if (string.IsNullOrWhiteSpace(raw)) continue;
                entry++;
                if (roots.Count >= MaximumRoots) { Warn(warnings, "Only the first 64 collection folders were scanned."); break; }
                try
                {
                    string root = LibraryPaths.Canonical(raw);
                    if (rootSet.Add(root)) roots.Add(root);
                }
                catch (Exception error) when (LibraryPaths.FilesystemError(error))
                { Warn(warnings, "MusicFolders entry " + entry + " is not an absolute local folder path."); }
            }
            roots.Sort(LibraryPaths.Comparer);
            var rawCollections = new List<ScannedCollection>();
            var tracksByPath = new Dictionary<string, MusicTrack>(LibraryPaths.Comparer);
            var discoveredPlaylists = new List<string>();
            var seenPlaylists = new HashSet<string>(rootSet, LibraryPaths.Comparer);
            int directories = 0, entries = 0;
            foreach (string root in roots)
            {
                token.ThrowIfCancellationRequested();
                if (LibraryPaths.IsPlaylist(root))
                {
                    var playlistTracks = ScanPlaylist(root, tracksByPath, metadata, warnings, token);
                    rawCollections.Add(new ScannedCollection(root, PlaylistName(root), playlistTracks, true));
                    continue;
                }
                // Players share this log, so folders are named without their personal paths.
                string name = FolderName(root);
                var tracks = new List<MusicTrack>();
                int topPlaylists = 0;
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
                        if (!directory.Exists) { Warn(warnings, Unreadable(name, current.Key == root)); continue; }
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
                                // Only top-level playlists in a configured folder become collections.
                                // Album subfolders often carry auto-generated .m3u files that should not flood the menu.
                                if (current.Value == 0 && LibraryPaths.IsPlaylist(item.Name))
                                {
                                    string playlistPath = item.FullName;
                                    if (seenPlaylists.Add(playlistPath))
                                    {
                                        discoveredPlaylists.Add(playlistPath);
                                        topPlaylists++;
                                    }
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
                    { Warn(warnings, Unreadable(name, current.Key == root)); }
                }
                tracks.Sort((left, right) => LibraryPaths.Comparer.Compare(left.Path, right.Path));
                // If a configured folder only holds top-level playlists (like a dedicated Playlists folder),
                // omit the empty folder entry so only its playlists appear.
                if (tracks.Count > 0 || topPlaylists == 0)
                    rawCollections.Add(new ScannedCollection(root, name, tracks.ToArray(), false));
            }
            discoveredPlaylists.Sort(LibraryPaths.Comparer);
            foreach (string playlistPath in discoveredPlaylists)
            {
                token.ThrowIfCancellationRequested();
                if (rawCollections.Count >= MaximumRoots)
                {
                    Warn(warnings, "Only the first 64 collection folders were scanned.");
                    break;
                }
                var playlistTracks = ScanPlaylist(playlistPath, tracksByPath, metadata, warnings, token);
                if (playlistTracks.Length > 0)
                    rawCollections.Add(new ScannedCollection(playlistPath, PlaylistName(playlistPath), playlistTracks, true));
            }
            var labels = new Dictionary<string, int>(LibraryPaths.Comparer);
            var playlistFiles = new Dictionary<string, int>(LibraryPaths.Comparer);
            foreach (var item in rawCollections)
            {
                labels.TryGetValue(item.Name, out int count);
                labels[item.Name] = count + 1;
                if (item.IsPlaylist)
                {
                    string file = Path.GetFileName(item.Id);
                    playlistFiles.TryGetValue(file, out int fileCount);
                    playlistFiles[file] = fileCount + 1;
                }
            }
            var collections = new List<MusicCollection>(rawCollections.Count);
            foreach (var item in rawCollections)
            {
                string label = item.Name;
                if (labels[label] > 1)
                {
                    string file = Path.GetFileName(item.Id);
                    label += item.IsPlaylist && playlistFiles[file] == 1
                        ? " (" + file + ")"
                        : " (" + (Path.GetDirectoryName(item.Id) ?? item.Id) + ")";
                }
                collections.Add(new MusicCollection(item.Id, label, item.Tracks));
            }
            metadata?.Prune(tracksByPath.Keys);
            return new LibrarySnapshot(collections.ToArray(), warnings.ToArray());
        }

        private sealed class ScannedCollection
        {
            internal readonly string Id;
            internal readonly string Name;
            internal readonly MusicTrack[] Tracks;
            internal readonly bool IsPlaylist;
            internal ScannedCollection(string id, string name, MusicTrack[] tracks, bool isPlaylist)
            {
                Id = id;
                Name = name;
                Tracks = tracks;
                IsPlaylist = isPlaylist;
            }
        }

        private static MusicTrack[] ScanPlaylist(string playlistPath, Dictionary<string, MusicTrack> tracksByPath,
            MetadataCache metadata, List<string> warnings, CancellationToken token)
        {
            var entries = PlaylistFiles.Read(playlistPath, out string playlistWarning);
            if (playlistWarning != null) Warn(warnings, playlistWarning);
            var tracks = new List<MusicTrack>(entries.Count);
            var seenInPlaylist = new HashSet<string>(LibraryPaths.Comparer);
            foreach (var entry in entries)
            {
                token.ThrowIfCancellationRequested();
                if (!seenInPlaylist.Add(entry.Path)) continue;
                if (entry.IsStream)
                {
                    tracks.Add(new MusicTrack(entry.Path, new TrackInfo(entry.Path, entry.Title, entry.Artist, "")));
                    continue;
                }
                if (!tracksByPath.TryGetValue(entry.Path, out var track))
                {
                    if (tracksByPath.Count >= MaximumTracks)
                    {
                        Warn(warnings, "The music library reached its scan limit. Use smaller collection folders.");
                        break;
                    }
                    track = new MusicTrack(entry.Path, metadata?.GetInfo(entry.Path));
                    tracksByPath.Add(entry.Path, track);
                }
                // Keep real embedded file tags when present, and use the playlist's #EXTINF/Title as fallback.
                if (!string.IsNullOrEmpty(entry.Title) && string.IsNullOrEmpty(track.Info.Artist) &&
                    string.Equals(track.Info.Title, LibraryPaths.FileLabel(entry.Path), StringComparison.Ordinal))
                {
                    track = new MusicTrack(entry.Path, new TrackInfo(entry.Path, entry.Title, entry.Artist, track.Info.Album));
                    tracksByPath[entry.Path] = track;
                }
                tracks.Add(track);
            }
            return tracks.ToArray();
        }

        private static string PlaylistName(string path)
        {
            string name = Path.GetFileNameWithoutExtension(path);
            return string.IsNullOrEmpty(name) ? FolderName(path) : name;
        }

        private static string FolderName(string root)
        {
            string name = System.IO.Path.GetFileName(root);
            return string.IsNullOrEmpty(name) ? root : name;
        }

        private static string Unreadable(string collection, bool root) => root
            ? "Collection folder " + collection + " could not be read."
            : "A subfolder of collection " + collection + " could not be read.";

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
