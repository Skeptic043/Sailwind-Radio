using System;
using System.Globalization;

namespace SailwindRadio.Models
{
    internal sealed class MetadataMarquee
    {
        internal const int VisibleCharacters = 16;
        internal const double DwellSeconds = 2;
        internal const double StepSeconds = .35;
        private readonly string[] text = { "", "", "" };
        private readonly int[][] elements = { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };
        private double started;
        private readonly int[] offsets = { -1, -1, -1 };
        private readonly string[] windows = new string[3];
        private bool powered;

        internal string this[int line] => windows[line] ?? "";

        internal void Set(bool on, string title, string artist, string album, double now)
        {
            bool restart = on && !powered;
            powered = on;
            bool changed = SetLine(0, title) | SetLine(1, artist) | SetLine(2, album);
            if (restart || changed)
            {
                // One clock controls all lines. A new track or power-on returns
                // every line to its first window at the same instant.
                started = now;
                for (int line = 0; line < offsets.Length; line++) offsets[line] = -1;
            }
        }

        private bool SetLine(int line, string value)
        {
            value = value ?? "";
            if (text[line] == value)
                return false;
            text[line] = value;
            elements[line] = StringInfo.ParseCombiningCharacters(value);
            return true;
        }

        internal bool Advance(double now)
        {
            bool changed = false;
            int maximum = 0;
            for (int line = 0; line < 3; line++)
                maximum = Math.Max(maximum, elements[line].Length - VisibleCharacters);
            int sharedOffset = 0;
            if (powered && maximum > 0)
            {
                double elapsed = now - started;
                if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0)
                    elapsed = 0;
                double cycle = 2 * DwellSeconds + maximum * StepSeconds;
                double phase = elapsed % cycle;
                sharedOffset = Math.Min(maximum, (int)Math.Floor(Math.Max(0, phase - DwellSeconds) / StepSeconds + 1e-9));
            }
            for (int line = 0; line < 3; line++)
            {
                int offset = Math.Min(sharedOffset, Math.Max(0, elements[line].Length - VisibleCharacters));
                if (!powered)
                {
                    if (windows[line] != "") { windows[line] = ""; changed = true; }
                    offsets[line] = -1;
                }
                else if (offsets[line] != offset)
                {
                    int start = elements[line].Length == 0 ? 0 : elements[line][offset];
                    int endIndex = offset + VisibleCharacters;
                    int end = endIndex >= elements[line].Length ? text[line].Length : elements[line][endIndex];
                    string window = text[line].Substring(start, end - start);
                    if (windows[line] != window) { windows[line] = window; changed = true; }
                    offsets[line] = offset;
                }
            }
            return changed;
        }
    }
}
