using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SailwindRadio.Models
{
    // Original 5 by 7 uppercase instrument alphabet. Each hex byte is one row,
    // with the five low bits read from left to right. No external font data.
    internal static class DotMatrixFont
    {
        internal const int Width = 512;
        internal const int Height = 192;
        internal const string GlyphData = @"
A:0E11111F111111
B:1E11111E11111E
C:0E11101010110E
D:1E11111111111E
E:1F10101E10101F
F:1F10101E101010
G:0E11101711110F
H:1111111F111111
I:0E04040404040E
J:0702020212120C
K:11121418141211
L:1010101010101F
M:111B1515111111
N:11191513111111
O:0E11111111110E
P:1E11111E101010
Q:0E11111115120D
R:1E11111E141211
S:0F10100E01011E
T:1F040404040404
U:1111111111110E
V:11111111110A04
W:11111115151B11
X:11110A040A1111
Y:11110A04040404
Z:1F01020408101F
0:0E11131519110E
1:040C040404040E
2:0E11010204081F
3:1E01010601011E
4:02060A121F0202
5:1F10101E01011E
6:0E10101E11110E
7:1F010204080808
8:0E11110E11110E
9:0E11110F01010E
.:00000000000C0C
,:00000000000408
-:0000001F000000
_:0000000000001F
':04040800000000
?:0E110102040004
!:04040404040004
/:01010204081010
(:02040808080402
):08040202020408
&:0C12140C15120D
+:0004041F040400
::00040400040400
;:00040400040408
=:00001F001F0000
#:0A0A1F0A1F0A0A
%:19190204081313
*:000A041F040A00
";
        private static readonly Dictionary<char, byte[]> Glyphs = CreateGlyphs();

        private static Dictionary<char, byte[]> CreateGlyphs()
        {
            var result = new Dictionary<char, byte[]> { [' '] = new byte[7] };
            foreach (string entry in GlyphData.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries))
            {
                var rows = new byte[7];
                for (int row = 0; row < 7; row++)
                    rows[row] = byte.Parse(entry.Substring(2 + row * 2, 2), NumberStyles.HexNumber, CultureInfo.InvariantCulture);
                result.Add(entry[0], rows);
            }
            return result;
        }

        internal static string Normalize(string text)
        {
            var result = new StringBuilder();
            string source = text ?? "";
            try { source = source.Normalize(NormalizationForm.FormD); }
            catch (ArgumentException) { /* A truncated surrogate falls back without breaking the radio. */ }
            foreach (char character in source)
            {
                if (CharUnicodeInfo.GetUnicodeCategory(character) == UnicodeCategory.NonSpacingMark)
                    continue;
                if (character == '…') { result.Append("..."); continue; }
                char value = character == '’' || character == '‘' ? '\'' :
                    character == '–' || character == '—' ? '-' :
                    character == '“' || character == '”' || character == '"' ? '\'' : char.ToUpperInvariant(character);
                result.Append(char.IsWhiteSpace(value) ? ' ' : value);
            }
            return result.ToString();
        }

        internal static bool CanRender(string text)
        {
            foreach (char character in Normalize(text))
                if (!Glyphs.ContainsKey(character))
                    return false;
            return true;
        }

        // One bounded texture per radio. Unsupported lines are drawn by the installed
        // Unicode font instead, preserving metadata rather than replacing it with question marks.
        internal static byte[] Rasterize(string title, string artist, string album)
        {
            var pixels = new byte[Width * Height];
            RasterizeInto(pixels, title, artist, album);
            return pixels;
        }

        internal static void RasterizeInto(byte[] pixels, string title, string artist, string album)
        {
            if (pixels == null || pixels.Length != Width * Height)
                throw new ArgumentException("Expected one complete radio display bitmap", nameof(pixels));
            Array.Clear(pixels, 0, pixels.Length);
            string[] lines = { title, artist, album };
            for (int line = 0; line < 3; line++)
            {
                string text = Normalize(lines[line]);
                if (text.Length == 0 || !CanRender(text))
                    continue;
                double pitch = Math.Min(line == 0 ? 6.2 : 5.0, 476.0 / (text.Length * 6 - 1));
                double left = (Width - (text.Length * 6 - 1) * pitch) * .5;
                double center = 146 - line * 50;
                for (int character = 0; character < text.Length; character++)
                {
                    byte[] rows = Glyphs[text[character]];
                    for (int row = 0; row < 7; row++)
                        for (int column = 0; column < 5; column++)
                            if ((rows[row] & (1 << (4 - column))) != 0)
                                Dot(pixels, left + (character * 6 + column + .5) * pitch,
                                    center + (3 - row) * pitch, pitch * .35, line == 0 ? (byte)255 : (byte)210);
                }
            }
        }

        private static void Dot(byte[] pixels, double x, double y, double radius, byte alpha)
        {
            for (int py = Math.Max(0, (int)Math.Floor(y - radius)); py <= Math.Min(Height - 1, (int)Math.Ceiling(y + radius)); py++)
                for (int px = Math.Max(0, (int)Math.Floor(x - radius)); px <= Math.Min(Width - 1, (int)Math.Ceiling(x + radius)); px++)
                {
                    double dx = px + .5 - x, dy = py + .5 - y;
                    if (dx * dx + dy * dy <= radius * radius)
                        pixels[py * Width + px] = alpha;
                }
        }
    }
}
