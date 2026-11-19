using System.Text;

namespace SailwindRadio.Library
{
    // Track titles sometimes spell words in Unicode's mathematical letters, such as bold 𝐌 or script 𝓜, which the
    // game's fonts can't draw. They show as their plain letter or digit instead. Everything else stays as it is.
    internal static class TitleText
    {
        // Thirteen 52-letter styles, A to Z then a to z, from bold to monospace.
        private const int LettersFirst = 0x1D400, LettersEnd = 0x1D6A4;
        // Five 10-digit styles, from bold to monospace.
        private const int DigitsFirst = 0x1D7CE, DigitsEnd = 0x1D800;

        internal static string Plain(string text)
        {
            if (string.IsNullOrEmpty(text) || !Folds(text)) return text;
            var plain = new StringBuilder(text.Length);
            for (int i = 0; i < text.Length; i++)
            {
                char c = text[i];
                if (char.IsHighSurrogate(c) && i + 1 < text.Length && char.IsLowSurrogate(text[i + 1]))
                {
                    char folded = Letter(char.ConvertToUtf32(c, text[i + 1]));
                    if (folded != '\0') { plain.Append(folded); i++; continue; }
                    plain.Append(c).Append(text[++i]);
                    continue;
                }
                plain.Append(Letterlike(c));
            }
            return plain.ToString();
        }

        private static bool Folds(string text)
        {
            foreach (char c in text)
                if (char.IsHighSurrogate(c) || Letterlike(c) != c) return true;
            return false;
        }

        private static char Letter(int code)
        {
            if (code >= LettersFirst && code < LettersEnd)
            {
                int letter = (code - LettersFirst) % 52;
                return (char)(letter < 26 ? 'A' + letter : 'a' + letter - 26);
            }
            if (code >= DigitsFirst && code < DigitsEnd) return (char)('0' + (code - DigitsFirst) % 10);
            return '\0';
        }

        // The few mathematical letters Unicode keeps in the Letterlike Symbols block instead.
        private static char Letterlike(char c)
        {
            switch (c)
            {
                case 'ℊ': return 'g';
                case 'ℋ': case 'ℌ': case 'ℍ': return 'H';
                case 'ℎ': return 'h';
                case 'ℐ': case 'ℑ': return 'I';
                case 'ℒ': return 'L';
                case 'ℕ': return 'N';
                case 'ℙ': return 'P';
                case 'ℚ': return 'Q';
                case 'ℛ': case 'ℜ': case 'ℝ': return 'R';
                case 'ℤ': case 'ℨ': return 'Z';
                case 'ℂ': case 'ℭ': return 'C';
                case 'ℬ': return 'B';
                case 'ℯ': return 'e';
                case 'ℰ': return 'E';
                case 'ℱ': return 'F';
                case 'ℳ': return 'M';
                case 'ℴ': return 'o';
                default: return c;
            }
        }
    }
}
