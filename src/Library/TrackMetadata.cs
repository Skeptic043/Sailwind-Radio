using System;
using System.IO;
using System.Text;

namespace SailwindRadio.Library
{
    internal static class TrackMetadata
    {
        // Best effort ID3 text only. Never decode audio during discovery or allocate from an unchecked tag size.
        private const int MaximumTagBytes = 65536;
        internal static string ReadLabel(string path) => ReadInfo(path).Label;
        internal static TrackInfo ReadInfo(string path)
        {
            bool readable;
            return ReadInfo(path, out readable);
        }
        internal static TrackInfo ReadInfo(string path, out bool readable)
        {
            readable = false;
            if (!LibraryPaths.Comparer.Equals(System.IO.Path.GetExtension(path), ".mp3")) return new TrackInfo(path);
            string title = null, artist = null, album = null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    readable = true;
                    var header = new byte[10];
                    if (Read(stream, header, header.Length) && header[0] == 'I' && header[1] == 'D' && header[2] == '3' &&
                        (header[3] == 3 || header[3] == 4) && header[5] == 0)
                    {
                        int size = SyncSafe(header, 6);
                        if (size >= 0 && size <= MaximumTagBytes && size <= stream.Length - 10)
                        {
                            var tag = new byte[size];
                            if (Read(stream, tag, size)) ReadFrames(tag, header[3], ref title, ref artist, ref album);
                        }
                    }
                    if (stream.Length >= 128 && (title == null || artist == null || album == null))
                    {
                        stream.Position = stream.Length - 128;
                        var tag = new byte[128];
                        if (Read(stream, tag, tag.Length) && tag[0] == 'T' && tag[1] == 'A' && tag[2] == 'G')
                        {
                            var latin = Encoding.GetEncoding(28591);
                            if (title == null) title = Clean(latin.GetString(tag, 3, 30));
                            if (artist == null) artist = Clean(latin.GetString(tag, 33, 30));
                            if (album == null) album = Clean(latin.GetString(tag, 63, 30));
                        }
                    }
                }
            }
            catch (Exception error) when (LibraryPaths.FilesystemError(error)) { readable = false; }
            return new TrackInfo(path, title, artist, album);
        }

        private static void ReadFrames(byte[] tag, int version, ref string title, ref string artist, ref string album)
        {
            for (int offset = 0; offset <= tag.Length - 10;)
            {
                if (tag[offset] == 0) break;
                int size = version == 4 ? SyncSafe(tag, offset + 4) : BigEndian(tag, offset + 4);
                if (size < 0 || size > tag.Length - offset - 10) break;
                string id = Encoding.ASCII.GetString(tag, offset, 4);
                // Skip compression, encryption, grouping and unsynchronization rather than guessing.
                if ((id == "TIT2" || id == "TPE1" || id == "TALB") && size > 1 && size <= 16384 && tag[offset + 9] == 0)
                {
                    int encoding = tag[offset + 10];
                    int start = offset + 11, count = size - 1;
                    Encoding decoder = null;
                    if (encoding == 0) decoder = Encoding.GetEncoding(28591);
                    if (encoding == 3) decoder = Encoding.UTF8;
                    if (encoding == 2) decoder = Encoding.BigEndianUnicode;
                    if (encoding == 1 && count >= 2)
                    {
                        if (tag[start] == 255 && tag[start + 1] == 254) decoder = Encoding.Unicode;
                        if (tag[start] == 254 && tag[start + 1] == 255) decoder = Encoding.BigEndianUnicode;
                        if (decoder != null) { start += 2; count -= 2; }
                    }
                    if (decoder != null)
                    {
                        string text = Clean(decoder.GetString(tag, start, count));
                        if (id == "TIT2") title = text;
                        else if (id == "TPE1") artist = text;
                        else album = text;
                    }
                    if (title != null && artist != null && album != null) break;
                }
                offset += 10 + size;
            }
        }

        private static string Clean(string value)
        {
            int terminator = value.IndexOf('\0');
            if (terminator >= 0) value = value.Substring(0, terminator);
            value = value.Replace('\r', ' ').Replace('\n', ' ').Replace('\t', ' ').Trim();
            if (value.Length > 160) value = value.Substring(0, 160);
            return value.Length == 0 ? null : value;
        }

        private static bool Read(Stream stream, byte[] buffer, int count)
        {
            int read = 0;
            while (read < count)
            {
                int next = stream.Read(buffer, read, count - read);
                if (next == 0) return false;
                read += next;
            }
            return true;
        }

        private static int SyncSafe(byte[] bytes, int offset)
        {
            int result = 0;
            for (int i = 0; i < 4; i++)
            {
                if (bytes[offset + i] >= 128) return -1;
                result = (result << 7) | bytes[offset + i];
            }
            return result;
        }

        private static int BigEndian(byte[] bytes, int offset)
        {
            if (bytes[offset] >= 128) return -1;
            return (bytes[offset] << 24) | (bytes[offset + 1] << 16) | (bytes[offset + 2] << 8) | bytes[offset + 3];
        }
    }
}
