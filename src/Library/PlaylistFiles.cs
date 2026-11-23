using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace SailwindRadio.Library
{
    internal sealed class PlaylistEntry
    {
        internal readonly string Path;
        internal readonly string Title;
        internal readonly string Artist;
        internal readonly bool IsStream;

        internal PlaylistEntry(string path, string title, string artist, bool isStream)
        {
            Path = path;
            Title = title ?? "";
            Artist = artist ?? "";
            IsStream = isStream;
        }
    }

    // Reads local .m3u, .m3u8, and .pls playlists into ordered tracks and direct HTTP/HTTPS MP3 stream entries.
    internal static class PlaylistFiles
    {
        private const long MaximumPlaylistBytes = 4L * 1024 * 1024;
        private const int MaximumPlaylistEntries = 10000;

        internal static List<PlaylistEntry> Read(string playlistPath, out string warning)
        {
            warning = null;
            var entries = new List<PlaylistEntry>();
            string name = Path.GetFileName(playlistPath);
            try
            {
                var info = new FileInfo(playlistPath);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0)
                {
                    warning = "Playlist " + name + " could not be read.";
                    return entries;
                }
                if (info.Length > MaximumPlaylistBytes)
                {
                    warning = "Playlist " + name + " is larger than the 4 MB limit.";
                    return entries;
                }
                string[] lines;
                using (var reader = new StreamReader(playlistPath, Encoding.UTF8, true))
                {
                    var all = new List<string>();
                    string line;
                    while ((line = reader.ReadLine()) != null) all.Add(line);
                    lines = all.ToArray();
                }
                string baseDirectory = Path.GetDirectoryName(playlistPath) ?? "";
                string extension = Path.GetExtension(playlistPath);
                if (LibraryPaths.Comparer.Equals(extension, ".pls"))
                    ReadPls(name, baseDirectory, lines, entries, out warning);
                else
                    ReadM3u(name, baseDirectory, lines, entries, out warning);
            }
            catch (Exception error) when (LibraryPaths.FilesystemError(error))
            {
                warning = "Playlist " + name + " could not be read.";
            }
            return entries;
        }

        private static void ReadM3u(string name, string baseDirectory, string[] lines, List<PlaylistEntry> entries, out string warning)
        {
            warning = null;
            string pendingTitle = "", pendingArtist = "";
            bool skippedUnsupported = false, skippedHls = false;
            for (int i = 0; i < lines.Length; i++)
            {
                string line = lines[i].Trim();
                if (line.Length == 0) continue;
                if (line.StartsWith("#EXT-X-", StringComparison.OrdinalIgnoreCase))
                {
                    entries.Clear();
                    warning = "Playlist " + name + " uses HLS streaming, which is not supported.";
                    return;
                }
                if (line.StartsWith("#EXTINF:", StringComparison.OrdinalIgnoreCase))
                {
                    int comma = line.IndexOf(',');
                    if (comma >= 0 && comma + 1 < line.Length)
                        SplitDisplay(line.Substring(comma + 1), out pendingArtist, out pendingTitle);
                    continue;
                }
                if (line[0] == '#') continue;
                if (entries.Count >= MaximumPlaylistEntries) break;

                var entry = ResolveTarget(baseDirectory, line, pendingTitle, pendingArtist, ref skippedHls);
                pendingTitle = "";
                pendingArtist = "";
                if (entry != null) entries.Add(entry);
                else skippedUnsupported = true;
            }
            if (skippedHls) warning = "Playlist " + name + " skipped HLS stream links, which are not supported.";
            else if (skippedUnsupported && entries.Count == 0) warning = "Playlist " + name + " had no playable tracks or streams.";
        }

        private static void ReadPls(string name, string baseDirectory, string[] lines, List<PlaylistEntry> entries, out string warning)
        {
            warning = null;
            var files = new SortedDictionary<int, string>();
            var titles = new Dictionary<int, string>();
            foreach (string raw in lines)
            {
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#' || line[0] == ';' || line[0] == '[') continue;
                int equals = line.IndexOf('=');
                if (equals <= 4) continue;
                string key = line.Substring(0, equals).Trim();
                string value = line.Substring(equals + 1).Trim();
                if (key.StartsWith("File", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(key.Substring(4), NumberStyles.None, CultureInfo.InvariantCulture, out int fileIndex) &&
                    fileIndex > 0)
                {
                    files[fileIndex] = value;
                }
                else if (key.StartsWith("Title", StringComparison.OrdinalIgnoreCase) &&
                    int.TryParse(key.Substring(5), NumberStyles.None, CultureInfo.InvariantCulture, out int titleIndex) &&
                    titleIndex > 0)
                {
                    titles[titleIndex] = value;
                }
            }
            bool skippedUnsupported = false, skippedHls = false;
            foreach (var pair in files)
            {
                if (entries.Count >= MaximumPlaylistEntries) break;
                string title = "", artist = "";
                if (titles.TryGetValue(pair.Key, out string display) && !string.IsNullOrWhiteSpace(display))
                    SplitDisplay(display, out artist, out title);
                var entry = ResolveTarget(baseDirectory, pair.Value, title, artist, ref skippedHls);
                if (entry != null) entries.Add(entry);
                else skippedUnsupported = true;
            }
            if (skippedHls) warning = "Playlist " + name + " skipped HLS stream links, which are not supported.";
            else if (skippedUnsupported && entries.Count == 0) warning = "Playlist " + name + " had no playable tracks or streams.";
        }

        private static PlaylistEntry ResolveTarget(string baseDirectory, string raw, string title, string artist, ref bool skippedHls)
        {
            string target = (raw ?? "").Trim();
            if (target.Length == 0) return null;
            if (LibraryPaths.IsStreamUrl(target))
            {
                try
                {
                    var uri = new Uri(target);
                    string ext = Path.GetExtension(uri.AbsolutePath);
                    if (LibraryPaths.Comparer.Equals(ext, ".m3u8") || LibraryPaths.Comparer.Equals(ext, ".m3u") ||
                        LibraryPaths.Comparer.Equals(ext, ".pls"))
                    {
                        skippedHls = true;
                        return null;
                    }
                    return new PlaylistEntry(target, title, artist, true);
                }
                catch { return null; }
            }
            if (target.StartsWith("file://", StringComparison.OrdinalIgnoreCase))
            {
                if (!Uri.TryCreate(target, UriKind.Absolute, out Uri fileUri) || !fileUri.IsFile || fileUri.IsUnc)
                    return null;
                target = fileUri.LocalPath;
            }
            // Reject other URI schemes (rtsp://, mms://, ftp://) and UNC shares cleanly.
            if (target.IndexOf("://", StringComparison.Ordinal) >= 0 || target.StartsWith("\\\\", StringComparison.Ordinal) ||
                target.StartsWith("//", StringComparison.Ordinal))
                return null;
            try
            {
                string combined = Path.IsPathRooted(target) ? target : Path.Combine(baseDirectory, target);
                string full = LibraryPaths.Canonical(combined);
                if (!LibraryPaths.Supported(full)) return null;
                var info = new FileInfo(full);
                if (!info.Exists || (info.Attributes & FileAttributes.ReparsePoint) != 0) return null;
                return new PlaylistEntry(full, title, artist, false);
            }
            catch (Exception error) when (LibraryPaths.FilesystemError(error))
            {
                return null;
            }
        }

        private static void SplitDisplay(string raw, out string artist, out string title)
        {
            artist = "";
            title = "";
            string cleaned = TitleText.Plain((raw ?? "").Trim());
            if (cleaned.Length == 0) return;
            int separator = cleaned.IndexOf(" - ", StringComparison.Ordinal);
            if (separator > 0 && separator + 3 < cleaned.Length)
            {
                artist = cleaned.Substring(0, separator).Trim();
                title = cleaned.Substring(separator + 3).Trim();
            }
            else
            {
                title = cleaned;
            }
        }
    }
}
