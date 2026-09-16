using System;
using System.Globalization;

namespace SailwindRadio.Models
{
    internal sealed class MetadataMarquee
    {
        internal const int VisibleCharacters = 20;
        internal const double DwellSeconds = 2;
        internal const double StepSeconds = .35;
        private readonly string[] text = { "", "", "" };
        private readonly int[][] elements = { Array.Empty<int>(), Array.Empty<int>(), Array.Empty<int>() };
        private readonly double[] started = new double[3];
        private readonly int[] offsets = { -1, -1, -1 };
        private readonly string[] windows = new string[3];
        private bool powered;

        internal string this[int line] => windows[line] ?? "";

        internal void Set(bool on, string title, string artist, string album, double now)
        {
            bool restart = on && !powered;
            powered = on;
            SetLine(0, title, now, restart);
            SetLine(1, artist, now, restart);
            SetLine(2, album, now, restart);
        }

        private void SetLine(int line, string value, double now, bool restart)
        {
            value = value ?? "";
            if (text[line] == value && !restart)
                return;
            text[line] = value;
            elements[line] = StringInfo.ParseCombiningCharacters(value);
            started[line] = now;
            offsets[line] = -1;
        }

        internal bool Advance(double now)
        {
            bool changed = false;
            for (int line = 0; line < 3; line++)
            {
                int maximum = Math.Max(0, elements[line].Length - VisibleCharacters);
                int offset = 0;
                if (powered && maximum > 0)
                {
                    double elapsed = now - started[line];
                    if (double.IsNaN(elapsed) || double.IsInfinity(elapsed) || elapsed < 0)
                        elapsed = 0;
                    double cycle = 2 * DwellSeconds + maximum * StepSeconds;
                    double phase = elapsed % cycle;
                    offset = Math.Min(maximum, (int)Math.Floor(Math.Max(0, phase - DwellSeconds) / StepSeconds + 1e-9));
                }
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
