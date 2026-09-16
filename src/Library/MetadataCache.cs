using System;
using System.Collections.Generic;
using System.IO;

namespace SailwindRadio.Library
{
    // Owned exclusively by the scanner's single worker, including replacement scans.
    internal sealed class MetadataCache
    {
        private sealed class Entry
        {
            internal long Length;
            internal long Modified;
            internal TrackInfo Info;
        }
        private readonly Dictionary<string, Entry> entries = new Dictionary<string, Entry>(LibraryPaths.Comparer);
        internal int Count => entries.Count;

        internal string GetLabel(string path) => GetInfo(path).Label;
        internal TrackInfo GetInfo(string path)
        {
            if (!LibraryPaths.Comparer.Equals(Path.GetExtension(path), ".mp3")) return new TrackInfo(path);
            try
            {
                var file = new FileInfo(path);
                long length = file.Length, modified = file.LastWriteTimeUtc.Ticks;
                if (entries.TryGetValue(path, out var cached) && cached.Length == length && cached.Modified == modified) return cached.Info;
                var info = TrackMetadata.ReadInfo(path, out bool readable);
                file.Refresh();
                // Do not cache a failed read or a label from a file changing during the read.
                if (readable && file.Exists && file.Length == length && file.LastWriteTimeUtc.Ticks == modified &&
                    (entries.ContainsKey(path) || entries.Count < LibraryScanner.MaximumTracks))
                    entries[path] = new Entry { Length = length, Modified = modified, Info = info };
                else entries.Remove(path);
                return info;
            }
            catch (Exception error) when (LibraryPaths.FilesystemError(error))
            {
                entries.Remove(path);
                return new TrackInfo(path);
            }
        }

        internal void Prune(IEnumerable<string> retained)
        {
            var live = new HashSet<string>(retained, LibraryPaths.Comparer);
            var obsolete = new List<string>();
            foreach (string path in entries.Keys) if (!live.Contains(path)) obsolete.Add(path);
            foreach (string path in obsolete) entries.Remove(path);
        }
    }
}
