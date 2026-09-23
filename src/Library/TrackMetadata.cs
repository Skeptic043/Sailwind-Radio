using System;
using System.IO;
using System.Text;

namespace SailwindRadio.Library
{
    internal static class TrackMetadata
    {
        // Read bounded text headers only. Discovery must never decode audio or trust a claimed tag size.
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
            string extension = System.IO.Path.GetExtension(path);
            if (!LibraryPaths.Supported(path)) return new TrackInfo(path);
            string title = null, artist = null, album = null;
            try
            {
                using (var stream = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.ReadWrite | FileShare.Delete))
                {
                    readable = true;
                    if (LibraryPaths.Comparer.Equals(extension, ".ogg"))
                    {
                        ReadOgg(stream, ref title, ref artist, ref album);
                        return new TrackInfo(path, title, artist, album);
                    }
                    if (LibraryPaths.Comparer.Equals(extension, ".wav"))
                    {
                        ReadWave(stream, ref title, ref artist, ref album);
                        return new TrackInfo(path, title, artist, album);
                    }
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

        private static void ReadOgg(Stream stream, ref string title, ref string artist, ref string album)
        {
            var header = new byte[27];
            var packet = new MemoryStream();
            int completed = 0;
            // Vorbis identification and comment packets are at the beginning of one logical stream.
            for (int page = 0; page < 16 && completed < 2; page++)
            {
                if (!Read(stream, header, header.Length) || Encoding.ASCII.GetString(header, 0, 4) != "OggS" || header[4] != 0) return;
                int segments = header[26];
                var lacing = new byte[segments];
                if (!Read(stream, lacing, segments)) return;
                long payload = 0;
                foreach (byte size in lacing) payload += size;
                if (payload > stream.Length - stream.Position) return;
                for (int segment = 0; segment < segments; segment++)
                {
                    int size = lacing[segment];
                    if (packet.Length + size > MaximumTagBytes) return;
                    var bytes = new byte[size];
                    if (!Read(stream, bytes, size)) return;
                    packet.Write(bytes, 0, size);
                    if (size == 255) continue;
                    byte[] data = packet.ToArray();
                    packet.SetLength(0);
                    if (completed == 0 && !VorbisPacket(data, 1)) return;
                    if (completed == 1)
                    {
                        if (VorbisPacket(data, 3)) ReadVorbisComments(data, ref title, ref artist, ref album);
                        return;
                    }
                    completed++;
                }
            }
        }

        private static bool VorbisPacket(byte[] data, byte kind) => data.Length >= 7 && data[0] == kind &&
            Encoding.ASCII.GetString(data, 1, 6) == "vorbis";

        private static void ReadVorbisComments(byte[] data, ref string title, ref string artist, ref string album)
        {
            int offset = 7;
            if (!ReadLength(data, ref offset, out int vendor) || vendor > data.Length - offset) return;
            offset += vendor;
            if (!ReadLength(data, ref offset, out int count) || count > 1024) return;
            for (int i = 0; i < count; i++)
            {
                if (!ReadLength(data, ref offset, out int length) || length > data.Length - offset) return;
                string entry = Encoding.UTF8.GetString(data, offset, length);
                offset += length;
                int equals = entry.IndexOf('=');
                if (equals < 1) continue;
                string key = entry.Substring(0, equals);
                string value = Clean(entry.Substring(equals + 1));
                if (key.Equals("TITLE", StringComparison.OrdinalIgnoreCase)) title = value;
                else if (key.Equals("ARTIST", StringComparison.OrdinalIgnoreCase)) artist = value;
                else if (key.Equals("ALBUM", StringComparison.OrdinalIgnoreCase)) album = value;
            }
        }

        private static void ReadWave(Stream stream, ref string title, ref string artist, ref string album)
        {
            var header = new byte[12];
            if (!Read(stream, header, header.Length) || Encoding.ASCII.GetString(header, 0, 4) != "RIFF" ||
                Encoding.ASCII.GetString(header, 8, 4) != "WAVE") return;
            long limit = Math.Min(stream.Length, 8L + UnsignedLittleEndian(header, 4));
            var chunk = new byte[8];
            // A malformed file can contain millions of empty chunks. Stop discovery
            // after a modest scan rather than let metadata parsing stall a library refresh.
            int scannedChunks = 0;
            while (stream.Position <= limit - 8 && scannedChunks++ < 4096)
            {
                if (!Read(stream, chunk, chunk.Length)) return;
                long size = UnsignedLittleEndian(chunk, 4);
                long end = stream.Position + size;
                if (end > limit) return;
                if (Encoding.ASCII.GetString(chunk, 0, 4) == "LIST" && size >= 4 && size <= MaximumTagBytes)
                {
                    var list = new byte[(int)size];
                    if (!Read(stream, list, list.Length)) return;
                    ReadWaveInfo(list, ref title, ref artist, ref album);
                }
                else if ((Encoding.ASCII.GetString(chunk, 0, 4) == "id3 " || Encoding.ASCII.GetString(chunk, 0, 4) == "ID3 ") &&
                    size >= 10 && size <= MaximumTagBytes + 10)
                {
                    var id3 = new byte[(int)size];
                    if (!Read(stream, id3, id3.Length)) return;
                    if (id3[0] == 'I' && id3[1] == 'D' && id3[2] == '3' && (id3[3] == 3 || id3[3] == 4) && id3[5] == 0)
                    {
                        int tagSize = SyncSafe(id3, 6);
                        if (tagSize >= 0 && tagSize <= size - 10)
                        {
                            var tag = new byte[tagSize];
                            Array.Copy(id3, 10, tag, 0, tagSize);
                            ReadFrames(tag, id3[3], ref title, ref artist, ref album);
                        }
                    }
                }
                stream.Position = end + (size & 1);
                if (title != null && artist != null && album != null) return;
            }
        }

        private static void ReadWaveInfo(byte[] list, ref string title, ref string artist, ref string album)
        {
            if (Encoding.ASCII.GetString(list, 0, 4) != "INFO") return;
            for (int offset = 4; offset <= list.Length - 8;)
            {
                string id = Encoding.ASCII.GetString(list, offset, 4);
                long size = UnsignedLittleEndian(list, offset + 4);
                offset += 8;
                if (size > list.Length - offset) return;
                if (size > 0 && size <= 16384 && (id == "INAM" || id == "IART" || id == "IPRD"))
                {
                    string value = Clean(DecodeWaveText(list, offset, (int)size));
                    if (id == "INAM") title = value;
                    else if (id == "IART") artist = value;
                    else album = value;
                }
                offset += (int)size + ((int)size & 1);
            }
        }

        private static string DecodeWaveText(byte[] bytes, int offset, int length)
        {
            try { return new UTF8Encoding(false, true).GetString(bytes, offset, length); }
            catch (DecoderFallbackException) { return Encoding.GetEncoding(28591).GetString(bytes, offset, length); }
        }

        private static bool ReadLength(byte[] bytes, ref int offset, out int length)
        {
            length = 0;
            if (offset > bytes.Length - 4) return false;
            uint value = UnsignedLittleEndian(bytes, offset);
            offset += 4;
            if (value > int.MaxValue) return false;
            length = (int)value;
            return true;
        }

        private static uint UnsignedLittleEndian(byte[] bytes, int offset) =>
            (uint)(bytes[offset] | bytes[offset + 1] << 8 | bytes[offset + 2] << 16 | bytes[offset + 3] << 24);

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
