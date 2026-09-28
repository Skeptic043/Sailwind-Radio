using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;

namespace SailwindRadio
{
    // Formats decoded in-source. Adding one is a line here plus the player-facing extension lists.
    internal static class PcmDecoders
    {
        // Replaced whole and never mutated, so stream workers never race a registration.
        private static Dictionary<string, Func<string, CancellationToken, IPcmDecoder>> openers =
            new Dictionary<string, Func<string, CancellationToken, IPcmDecoder>>(StringComparer.OrdinalIgnoreCase)
            {
                { ".mp3", Mp3PcmDecoder.Open },
                { ".wav", WavPcmDecoder.Open },
                { ".flac", OpenFlac },
                { ".ogg", OpenOgg },
            };
        private static readonly object registration = new object();
        private static Dictionary<string, Func<string, CancellationToken, IPcmDecoder>> Openers { get { return Volatile.Read(ref openers); } }

        // Adds, replaces or, with null, removes one extension. Checks use it for scripted decoders.
        internal static void Register(string extension, Func<string, CancellationToken, IPcmDecoder> open)
        {
            lock (registration)
            {
                var next = new Dictionary<string, Func<string, CancellationToken, IPcmDecoder>>(openers, StringComparer.OrdinalIgnoreCase);
                if (open == null) next.Remove(extension);
                else next[extension] = open;
                Volatile.Write(ref openers, next);
            }
        }

        // Media Foundation formats exist only when the start-up decision enabled them.
        private static bool IsMediaFoundation(string extension)
        {
            return String.Equals(extension, ".m4a", StringComparison.OrdinalIgnoreCase) ||
                String.Equals(extension, ".aac", StringComparison.OrdinalIgnoreCase);
        }

        internal static bool Supports(string path)
        {
            string extension = Path.GetExtension(path) ?? "";
            if (IsMediaFoundation(extension)) return MediaFoundationSupport.Enabled;
            return Openers.ContainsKey(extension);
        }

        internal static string ChooseFormatMessage
        {
            get
            {
                return MediaFoundationSupport.Enabled ? "Choose an MP3, OGG, WAV, FLAC, M4A or AAC file" :
                    "Choose an MP3, OGG, WAV or FLAC file";
            }
        }

        // Player-facing reason a path cannot be decoded, or null when it can.
        internal static string UnsupportedMessage(string path)
        {
            string extension = Path.GetExtension(path) ?? "";
            if (IsMediaFoundation(extension) && !MediaFoundationSupport.Enabled)
                return "M4A and AAC playback is off. " + MediaFoundationSupport.Reason;
            return null;
        }

        internal static IPcmDecoder Open(string path, CancellationToken token)
        {
            token.ThrowIfCancellationRequested();
            string extension = Path.GetExtension(path) ?? "";
            Func<string, CancellationToken, IPcmDecoder> open;
            if (IsMediaFoundation(extension))
            {
                if (!MediaFoundationSupport.Enabled) throw new IOException(UnsupportedMessage(path));
                open = MediaFoundationDecoder.Open;
            }
            else if (!Openers.TryGetValue(extension, out open)) throw new IOException("Unsupported audio format");
            IPcmDecoder decoder = open(path, token);
            if (decoder.Channels < 1 || decoder.Channels > 2 || decoder.Frames < 0)
            {
                decoder.Dispose();
                throw new IOException("Decoder reported an invalid layout");
            }
            return decoder;
        }

        private static IPcmDecoder OpenFlac(string path, CancellationToken token)
        {
            var input = new AudioFiles.CancellableFileStream(path, token);
            try { return new FlacDecoder(input, token); }
            catch (InvalidDataException ex) { throw new IOException(ex.Message, ex); }
        }

        // Ogg Vorbis only. Other Ogg codecs and floor 0 fail with the decoder's own reason.
        private static IPcmDecoder OpenOgg(string path, CancellationToken token)
        {
            var input = new AudioFiles.CancellableFileStream(path, token);
            try { return new VorbisDecoder(input, token); }
            catch (InvalidDataException ex) { throw new IOException(ex.Message, ex); }
        }
    }
}
