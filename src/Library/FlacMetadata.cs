using System.IO;

namespace SailwindRadio.Library
{
    // Bounded FLAC tag lookup for library discovery. Reads metadata block headers only and never
    // decodes audio or trusts a block size beyond the stream length.
    internal static class FlacMetadata
    {
        // Returns the VORBIS_COMMENT payload prefixed with the Ogg comment packet header
        // (0x03 "vorbis"), so a Vorbis comment packet parser can read it unchanged. Null when the
        // stream is not FLAC, has no comment block or the block exceeds maximumBytes.
        internal static byte[] ReadVorbisCommentPacket(Stream stream, int maximumBytes)
        {
            byte[] block = ReadVorbisComment(stream, maximumBytes);
            if (block == null) return null;
            var packet = new byte[block.Length + 7];
            packet[0] = 3;
            packet[1] = (byte)'v'; packet[2] = (byte)'o'; packet[3] = (byte)'r';
            packet[4] = (byte)'b'; packet[5] = (byte)'i'; packet[6] = (byte)'s';
            System.Buffer.BlockCopy(block, 0, packet, 7, block.Length);
            return packet;
        }

        // Returns the raw VORBIS_COMMENT block payload, starting at the vendor string length.
        internal static byte[] ReadVorbisComment(Stream stream, int maximumBytes)
        {
            if (!SkipId3v2(stream)) return null;
            var header = new byte[4];
            if (!Read(stream, header) || header[0] != 'f' || header[1] != 'L' || header[2] != 'a' || header[3] != 'C') return null;
            for (int blocks = 0; blocks < 1024; blocks++)
            {
                if (!Read(stream, header)) return null;
                int type = header[0] & 0x7F;
                int size = (header[1] << 16) | (header[2] << 8) | header[3];
                if (type == 127 || size > stream.Length - stream.Position) return null;
                if (type == 4)
                {
                    if (size > maximumBytes) return null;
                    var block = new byte[size];
                    return Read(stream, block) ? block : null;
                }
                if ((header[0] & 0x80) != 0) return null;
                stream.Position += size;
            }
            return null;
        }

        // Skips leading ID3v2 tags as the decoder does. The library checks compile src/Library alone,
        // so this ID3v2 skip is kept local.
        private static bool SkipId3v2(Stream stream)
        {
            var tag = new byte[10];
            for (int tags = 0; tags < 16; tags++)
            {
                long start = stream.Position;
                if (!Read(stream, tag) || tag[0] != 'I' || tag[1] != 'D' || tag[2] != '3' || tag[3] == 0xFF || tag[4] == 0xFF ||
                    ((tag[6] | tag[7] | tag[8] | tag[9]) & 0x80) != 0)
                {
                    stream.Position = start;
                    return true;
                }
                long size = (tag[6] << 21) | (tag[7] << 14) | (tag[8] << 7) | tag[9];
                if ((tag[5] & 0x10) != 0) size += 10;
                if (size > stream.Length - stream.Position) return false;
                stream.Position += size;
            }
            return false;
        }

        private static bool Read(Stream stream, byte[] data)
        {
            int done = 0;
            while (done < data.Length)
            {
                int got = stream.Read(data, done, data.Length - done);
                if (got <= 0) return false;
                done += got;
            }
            return true;
        }
    }
}
