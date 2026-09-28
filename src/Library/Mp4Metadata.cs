using System;
using System.IO;
using System.Text;

namespace SailwindRadio.Library
{
    // iTunes-style MP4 tags from moov/udta/meta/ilst. Walks box headers by seeking, so sample
    // tables, media data and cover art are never read into memory.
    internal static class Mp4Metadata
    {
        private const int MaximumBoxes = 4096;
        private const int MaximumValueBytes = 16384;

        internal static void Read(Stream stream, ref string title, ref string artist, ref string album)
        {
            int budget = MaximumBoxes;
            long moovStart, moovEnd;
            if (!Find(stream, 0, stream.Length, "moov", ref budget, out moovStart, out moovEnd)) return;
            long start, end;
            if (Find(stream, moovStart, moovEnd, "udta", ref budget, out start, out end) &&
                ReadMeta(stream, start, end, ref budget, ref title, ref artist, ref album)) return;
            // Some writers place meta directly in moov.
            ReadMeta(stream, moovStart, moovEnd, ref budget, ref title, ref artist, ref album);
        }

        private static bool ReadMeta(Stream stream, long parentStart, long parentEnd, ref int budget,
            ref string title, ref string artist, ref string album)
        {
            long start, end;
            if (!Find(stream, parentStart, parentEnd, "meta", ref budget, out start, out end)) return false;
            // ISO meta is a full box with a zero version and flags word. QuickTime meta starts
            // directly with its first child, whose size is never zero.
            var word = new byte[4];
            if (end - start >= 4 && ReadAt(stream, start, word) && word[0] == 0 && word[1] == 0 && word[2] == 0 && word[3] == 0)
                start += 4;
            long listStart, listEnd;
            if (!Find(stream, start, end, "ilst", ref budget, out listStart, out listEnd)) return false;
            long at = listStart;
            while (at < listEnd && budget-- > 0)
            {
                string type;
                long itemStart, itemEnd;
                if (!Header(stream, at, listEnd, out type, out itemStart, out itemEnd)) break;
                if (type == "©nam" || type == "©ART" || type == "©alb")
                {
                    string value = ReadText(stream, itemStart, itemEnd, ref budget);
                    if (value != null)
                    {
                        if (type == "©nam") title = value;
                        else if (type == "©ART") artist = value;
                        else album = value;
                    }
                }
                at = itemEnd;
            }
            return title != null || artist != null || album != null;
        }

        private static string ReadText(Stream stream, long start, long end, ref int budget)
        {
            long dataStart, dataEnd;
            if (!Find(stream, start, end, "data", ref budget, out dataStart, out dataEnd)) return null;
            long length = dataEnd - dataStart - 8;
            if (length <= 0 || length > MaximumValueBytes) return null;
            var head = new byte[8];
            if (!ReadAt(stream, dataStart, head) || head[0] != 0) return null;
            int kind = head[1] << 16 | head[2] << 8 | head[3];
            Encoding encoding = kind == 1 ? Encoding.UTF8 : kind == 2 ? Encoding.BigEndianUnicode : null;
            if (encoding == null) return null;
            var value = new byte[length];
            return ReadAt(stream, dataStart + 8, value) ? TrackMetadata.Clean(encoding.GetString(value)) : null;
        }

        private static bool Find(Stream stream, long start, long end, string wanted, ref int budget,
            out long payloadStart, out long payloadEnd)
        {
            payloadStart = payloadEnd = 0;
            long at = start;
            while (at < end && budget-- > 0)
            {
                string type;
                if (!Header(stream, at, end, out type, out payloadStart, out payloadEnd)) return false;
                if (type == wanted) return true;
                at = payloadEnd;
            }
            return false;
        }

        // Reads one box header. A zero size runs to the parent end and size 1 uses a 64-bit size.
        private static bool Header(Stream stream, long at, long end, out string type, out long payloadStart, out long payloadEnd)
        {
            type = null;
            payloadStart = payloadEnd = 0;
            if (end - at < 8) return false;
            var header = new byte[16];
            if (!ReadAt(stream, at, header, 8)) return false;
            long size = (long)BigEndian(header, 0);
            int headerLength = 8;
            if (size == 1)
            {
                if (end - at < 16 || !ReadAt(stream, at + 8, header, 8, 8)) return false;
                size = (long)((ulong)BigEndian(header, 8) << 32 | BigEndian(header, 12));
                headerLength = 16;
            }
            else if (size == 0) size = end - at;
            if (size < headerLength || size > end - at) return false;
            type = Encoding.GetEncoding(28591).GetString(header, 4, 4);
            payloadStart = at + headerLength;
            payloadEnd = at + size;
            return true;
        }

        private static uint BigEndian(byte[] bytes, int offset) =>
            (uint)(bytes[offset] << 24 | bytes[offset + 1] << 16 | bytes[offset + 2] << 8 | bytes[offset + 3]);

        private static bool ReadAt(Stream stream, long position, byte[] buffer) => ReadAt(stream, position, buffer, buffer.Length);

        private static bool ReadAt(Stream stream, long position, byte[] buffer, int count, int offset = 0)
        {
            if (position < 0 || position > stream.Length - count) return false;
            stream.Position = position;
            int read = 0;
            while (read < count)
            {
                int got = stream.Read(buffer, offset + read, count - read);
                if (got <= 0) return false;
                read += got;
            }
            return true;
        }
    }
}
