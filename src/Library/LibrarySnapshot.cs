using System;
using System.Collections.Generic;
using System.IO;

namespace SailwindRadio.Library
{
    internal sealed class TrackInfo
    {
        internal string Path { get; }
        internal string Title { get; }
        internal string Artist { get; }
        internal string Album { get; }
        internal string Label => Artist.Length > 0 ? Artist + " - " + Title : Title;
        internal TrackInfo(string path, string title = null, string artist = null, string album = null)
        {
            Path = path ?? "";
            Title = string.IsNullOrEmpty(title) ? LibraryPaths.FileLabel(path) : title;
            Artist = artist ?? "";
            Album = album ?? "";
        }
    }

    internal sealed class MusicTrack
    {
        internal string Path { get; }
        internal TrackInfo Info { get; }
        internal MusicTrack(string path, TrackInfo info = null)
        {
            Path = path;
            Info = info ?? TrackMetadata.ReadInfo(path);
        }
    }

    internal sealed class MusicCollection
    {
        internal string Id { get; }
        internal string Label { get; }
        internal IReadOnlyList<MusicTrack> Tracks { get; }
        internal MusicCollection(string id, string label, MusicTrack[] tracks)
        {
            Id = id;
            Label = label;
            Tracks = Array.AsReadOnly(tracks);
        }
    }

    internal sealed class LibrarySnapshot
    {
        private readonly Dictionary<string, TrackInfo> tracks = new Dictionary<string, TrackInfo>(LibraryPaths.Comparer);
        private readonly string[] warnings;
        internal static readonly LibrarySnapshot Empty = new LibrarySnapshot(Array.Empty<MusicCollection>(), Array.Empty<string>());
        internal IReadOnlyList<MusicCollection> Collections { get; }
        internal IReadOnlyList<string> Warnings { get; }
        internal LibrarySnapshot(MusicCollection[] collections, string[] warnings)
        {
            Collections = Array.AsReadOnly(collections);
            this.warnings = warnings;
            Warnings = Array.AsReadOnly(warnings);
            foreach (var collection in collections)
                foreach (var track in collection.Tracks) tracks[track.Path] = track.Info;
        }
        internal TrackInfo GetTrackInfo(string path) => !string.IsNullOrEmpty(path) && tracks.TryGetValue(path, out var info)
            ? info : new TrackInfo(path);

        // Every power-on rescans, so only problems the previous scan did not report are new.
        internal IEnumerable<string> WarningsSince(LibrarySnapshot previous)
        {
            foreach (string warning in warnings)
                if (previous == null || Array.IndexOf(previous.warnings, warning) < 0) yield return warning;
        }
    }

    internal static class LibraryPaths
    {
        // Sailwind runs on Windows. Preserve display casing while comparing identities without case.
        internal static readonly StringComparer Comparer = StringComparer.OrdinalIgnoreCase;
        internal static string Canonical(string path)
        {
            string trimmed = path.Trim();
            // Match the decoder's local-file contract before touching a folder. Drive-relative,
            // rooted-current-drive, UNC and URI paths must not trigger a scan that cannot play.
            if (trimmed.Length < 3 || !char.IsLetter(trimmed[0]) || trimmed[1] != ':' ||
                (trimmed[2] != '\\' && trimmed[2] != '/'))
                throw new ArgumentException("Collection folders must use an absolute local drive path");
            string full = System.IO.Path.GetFullPath(trimmed);
            string root = System.IO.Path.GetPathRoot(full);
            return full.Length > root.Length ? full.TrimEnd(System.IO.Path.DirectorySeparatorChar, System.IO.Path.AltDirectorySeparatorChar) : full;
        }
        // Set once at plugin start, before the first scan, from the Media Foundation decision.
        // When false, M4A and AAC never enter a collection, so shuffle cannot land on them.
        private static volatile bool mediaFoundationFormats;
        internal static bool MediaFoundationFormats { get => mediaFoundationFormats; set => mediaFoundationFormats = value; }
        internal static bool Supported(string path)
        {
            string extension = System.IO.Path.GetExtension(path);
            return Comparer.Equals(extension, ".mp3") || Comparer.Equals(extension, ".ogg") || Comparer.Equals(extension, ".wav") ||
                Comparer.Equals(extension, ".flac") ||
                (MediaFoundationFormats && (Comparer.Equals(extension, ".m4a") || Comparer.Equals(extension, ".aac")));
        }
        internal static string FileLabel(string path)
        {
            try { return System.IO.Path.GetFileNameWithoutExtension(path ?? "").Replace('_', ' '); }
            catch (Exception error) when (FilesystemError(error)) { return "Unavailable track"; }
        }
        internal static bool FilesystemError(Exception error) => error is IOException || error is UnauthorizedAccessException ||
            error is System.Security.SecurityException || error is ArgumentException || error is NotSupportedException;
    }
}
