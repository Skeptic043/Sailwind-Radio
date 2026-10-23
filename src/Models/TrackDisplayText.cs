using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace SailwindRadio.Models
{
    // Text rules for the regional title and artist rows. Pure so they can be checked without Unity.
    internal static class TrackDisplayText
    {
        // Split-flap leaves and drum wheels carry the characters in this order and only move forward.
        internal const string Charset = " ABCDEFGHIJKLMNOPQRSTUVWXYZ0123456789!\"#$&'()*,-./:?[]_";

        internal static int Index(char c) => Math.Max(0, Charset.IndexOf(c));
        // Forward steps from one character to another, wrapping after the last.
        internal static int Steps(int from, int to) => (to - from + Charset.Length) % Charset.Length;
        // Drum wheels turn the shorter way round the ring: +1 forward, -1 backward, 0 when there. A tie goes forward.
        internal static int ShortestStep(int from, int to)
        {
            int forward = Steps(from, to);
            return forward == 0 ? 0 : forward <= Charset.Length - forward ? 1 : -1;
        }
        internal static int Distance(int from, int to) => Math.Min(Steps(from, to), Charset.Length - Steps(from, to));

        // Uppercase letters, digits, space and the punctuation the cells carry. Accents fold to their base
        // letter and anything else becomes a space. Runs of spaces collapse.
        internal static string Fold(string text)
        {
            var folded = new StringBuilder();
            foreach (char raw in (text ?? "").Normalize(NormalizationForm.FormD))
            {
                if (CharUnicodeInfo.GetUnicodeCategory(raw) == UnicodeCategory.NonSpacingMark)
                    continue;
                char c = raw >= 'a' && raw <= 'z' ? (char)(raw - 32) : raw;
                if (c != ' ' && Charset.IndexOf(c) > 0)
                    folded.Append(c);
                else if (folded.Length > 0 && folded[folded.Length - 1] != ' ')
                    folded.Append(' ');
            }
            return folded.ToString().Trim();
        }

        // As many characters from the start as fit, so long text runs to the end of the row, even mid-word.
        // A cut landing on a space drops it. No ellipsis is added because the cells carry none.
        internal static string Cut(string folded, Func<string, float> measure, float limit)
        {
            if (measure(folded) <= limit)
                return folded;
            int take = 0;
            while (take < folded.Length && measure(folded.Substring(0, take + 1)) <= limit)
                take++;
            return folded.Substring(0, take).TrimEnd();
        }

        internal static string CutCells(string folded, int cells) => Cut(folded, s => s.Length, cells);

        // A cell's centre along its row, cell 0 at the left end.
        internal static float CellX(int cell, int cells, float pitch) => (cell - (cells - 1) * .5f) * pitch;

        // Rows start at their left end. Cells after the text show spaces.
        internal static string Left(string text, int cells) => text.PadRight(cells);
    }

    // Glyph scale from measured font metrics: world units per font pixel, and a horizontal squeeze.
    internal static class TextFit
    {
        // Floors are low so a wide letter condenses rather than shrinking, which would shrink its whole row with it.
        internal const float DrumCap = .82f, FlapCap = .82f, CellRoom = .9f, FlapFloor = .45f, DrumFloor = .55f;
        // Share of the row height a glyph may reach above or below the capital centre.
        internal const float CellReach = .48f;
        internal const float ScrollCap = .62f, ScrollMargin = .3f, ScrollMinSqueeze = .85f;

        // Capitals at cap of the row height.
        internal static float CapScale(float capPixels, float height, float cap) => cap * height / capPixels;

        // Each glyph squeezes on its own, only as far as it needs to fit room, never below floor.
        // A glyph still too wide at the floor is drawn smaller, and so is one reaching further than reach
        // above or below the capital centre, so no glyph leaves its cell.
        internal static void Glyph(float boxPixels, float reachPixels, float scale, float room, float reach, float floor,
            out float glyphScale, out float squeeze)
        {
            glyphScale = scale;
            float width = boxPixels * scale;
            squeeze = width <= room ? 1f : room / width;
            if (squeeze < floor)
            {
                glyphScale = scale * squeeze / floor;
                squeeze = floor;
            }
            if (reachPixels * glyphScale > reach)
                glyphScale = reach / reachPixels;
        }

        // How much wider than the row's most condensed letter a glyph may stay: enough for letters to keep some
        // character, not so much that an I keeps its full stroke weight beside condensed neighbours.
        internal const float CellSpread = .12f;

        // Every glyph is drawn no taller than the row's smallest letter, which a glyph too wide for its cell sets, and
        // no wider than its most condensed letter plus spread. It only ever narrows and shrinks, so every glyph still
        // fits its cell. Scales and squeezes are indexed like charset.
        internal static void Even(float[] scale, float[] squeeze, string charset, float spread)
        {
            float smallest = float.MaxValue, condensed = float.MaxValue;
            for (int i = 0; i < charset.Length; i++)
                if (char.IsLetter(charset[i]))
                {
                    smallest = Math.Min(smallest, scale[i]);
                    condensed = Math.Min(condensed, squeeze[i]);
                }
            if (smallest == float.MaxValue)
                return;
            for (int i = 0; i < charset.Length; i++)
                if (charset[i] != ' ')
                {
                    scale[i] = Math.Min(scale[i], smallest);
                    squeeze[i] = Math.Min(squeeze[i], condensed + spread);
                }
        }

        internal static float ScrollScale(float capPixels, float height) => CapScale(capPixels, height, ScrollCap);
        internal static float ScrollRoom(float width, float height) => width - 2 * ScrollMargin * height;

        // Squeeze to fit the room, never below the minimum. The caller cuts text that needs more.
        internal static float Squeeze(float width, float room, float minimum) => width <= room ? 1f : Math.Max(minimum, room / width);
    }

    // What a display shows. An update animates only when the fitted text or the power state changes.
    internal sealed class DisplayTargets
    {
        internal string Title { get; private set; } = "";
        internal string Artist { get; private set; } = "";

        internal bool Set(string title, string artist)
        {
            if (title == Title && artist == Artist)
                return false;
            Title = title;
            Artist = artist;
            return true;
        }
    }
}
