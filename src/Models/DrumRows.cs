using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Models
{
    internal sealed class DrumRow : CellRow
    {
        // This row's first wheel on the display's shared shaft.
        internal readonly int FirstWheel;

        internal DrumRow(ModelDisplay anchor, Transform frame, Material ink, int firstWheel) : base(anchor, frame, ink)
        {
            FirstWheel = firstWheel;
        }
    }

    // Al'Ankh letter drum rows. The wheels of both rows step on one shared indexing shaft.
    internal sealed class DrumRows : RegionalRows<DrumRow>
    {
        internal static readonly Color Brass = new Color32(0xb0, 0x95, 0x59, 255);
        // Darker than the brass light floor, so the letters still read in a dark cabin.
        private static readonly Color Ink = new Color32(0x15, 0x11, 0x0b, 255);
        private const int Facets = 6, Slices = 3;
        private readonly List<DrumEvent> events = new List<DrumEvent>(8);
        private DrumShaft shaft;
        private int wheels;

        internal DrumRows(RegionalTrackDisplay display) : base(display)
        {
        }

        internal override Color InkColour => Ink;
        internal override int Renderers => Rows.Count * 2;
        internal override bool Moving => TitleRow.Animating || ArtistRow.Animating;

        // Each wheel face is a brass cylinder segment of a few facets across the visible arc, with a thin gap between wheels.
        protected override DrumRow Build(ModelDisplay anchor, Transform frame, Material ink, Shader surfaceShader, List<UnityEngine.Object> owned)
        {
            var row = new DrumRow(anchor, frame, ink, wheels);
            wheels += anchor.cells;
            row.FitGlyphs(Display.Glyphs, TextFit.DrumCap, TextFit.DrumFloor);
            var brass = DeviceModel.Surface(surfaceShader, "Character drum brass", Brass, .75f, .45f, owned);
            DeviceModel.SetLightFloor(brass);
            var surface = Display.AddMesh(row, "Character drums", brass, owned);
            float radius = anchor.radius, arc = Mathf.Asin(Mathf.Min(1f, anchor.height * .55f / radius)), half = anchor.pitch * .92f * .5f;
            int perWheel = (Facets + 1) * 2;
            var vertices = new Vector3[anchor.cells * perWheel];
            var normals = new Vector3[vertices.Length];
            var triangles = new int[anchor.cells * Facets * 6];
            for (int w = 0; w < anchor.cells; w++)
            {
                float x = row.CellX(w);
                for (int f = 0; f <= Facets; f++)
                {
                    float angle = Mathf.Lerp(-arc, arc, f / (float)Facets);
                    var point = new Vector3(0, radius * Mathf.Sin(angle), radius * (1f - Mathf.Cos(angle)) - RegionalTrackDisplay.SurfaceLift);
                    var normal = new Vector3(0, Mathf.Sin(angle), -Mathf.Cos(angle));
                    int at = w * perWheel + f * 2;
                    vertices[at] = point + new Vector3(x - half, 0, 0);
                    vertices[at + 1] = point + new Vector3(x + half, 0, 0);
                    normals[at] = normals[at + 1] = normal;
                    if (f == Facets)
                        continue;
                    int t = (w * Facets + f) * 6;
                    triangles[t] = at;
                    triangles[t + 1] = at + 2;
                    triangles[t + 2] = at + 3;
                    triangles[t + 3] = at;
                    triangles[t + 4] = at + 3;
                    triangles[t + 5] = at + 1;
                }
            }
            surface.vertices = vertices;
            surface.normals = normals;
            surface.triangles = triangles;
            surface.RecalculateBounds();
            Display.InkMesh(row, anchor.cells * 2 * Slices, owned);
            return row;
        }

        internal override void Finish(Shader surfaceShader, List<UnityEngine.Object> owned)
        {
            base.Finish(surfaceShader, owned);
            shaft = new DrumShaft(wheels);
        }

        protected override string Cut(DrumRow row, string folded) => row.Cut(folded);

        // Wheels head for the row's text.
        protected override void Start(double now)
        {
            double interval = 1 / Range(11f, 14f);
            Retarget(TitleRow, Targets.Title, now, interval);
            Retarget(ArtistRow, Targets.Artist, now, interval);
        }

        private void Retarget(DrumRow row, string text, double now, double interval)
        {
            row.Animating = true;
            for (int i = 0; i < row.Cells; i++)
                shaft.Retarget(row.FirstWheel + i, TrackDisplayText.Index(text[i]), now, interval);
        }

        internal override void Tick(double now, bool hearing)
        {
            if (shaft.Running)
            {
                events.Clear();
                shaft.Update(now, events);
                if (hearing)
                    for (int i = 0; i < events.Count; i++)
                        if (events[i].Stop)
                            Display.Sounds.Play("stop", 2f, Range(-30, 30));
                        else
                            Display.Sounds.Play("index", 20f * Mathf.Log10(Mathf.Pow(events[i].Moving, .3f)), Range(-30, 30));
            }
            Animate(TitleRow, now);
            Animate(ArtistRow, now);
        }

        private void Animate(DrumRow row, double now)
        {
            if (!row.Animating)
                return;
            bool settled = true;
            for (int i = 0; i < row.Cells; i++)
                settled &= shaft.Shown[row.FirstWheel + i] == shaft.Target[row.FirstWheel + i];
            row.Dirty = true;
            if (settled && !shaft.Running)
                row.Animating = false;
        }

        // The leaving glyph rolls up over the drum and the arriving one rises from below, both wrapped onto the
        // wheel and trimmed to the row window. Each glyph is cut into a few slices so it follows the curve.
        protected override void Render(DrumRow row, double now)
        {
            float radius = row.Anchor.radius, step = row.Anchor.height / radius;
            for (int i = 0; i < row.Cells; i++)
            {
                int wheel = row.FirstWheel + i, shown = shaft.Shown[wheel], direction = shaft.Direction(wheel);
                float turn = shaft.Turn(wheel, now), x = row.CellX(i);
                int at = i * 2 * Slices * 4;
                // Forward rolls the leaving glyph up with the next rising from below. Backward reverses both.
                WheelGlyph(row, at, shown, x, direction * turn * step, radius);
                if (turn > 0f)
                    WheelGlyph(row, at + Slices * 4, (shown + direction + TrackDisplayText.Charset.Length) % TrackDisplayText.Charset.Length, x,
                        direction * (turn - 1f) * step, radius);
                else
                    for (int k = 0; k < Slices; k++)
                        GlyphMesh.Hide(row.InkVertices, row.InkUvs, at + (Slices + k) * 4);
            }
            Display.Upload(row);
        }

        private void WheelGlyph(DrumRow row, int at, int index, float x, float angle, float radius)
        {
            var glyphs = Display.Glyphs;
            ref var glyph = ref glyphs.Glyphs[index];
            GlyphMesh.Wheel(row.InkVertices, row.InkUvs, at, ref glyph, x, glyphs.CapCentre, row.GlyphScale[index], row.GlyphSqueeze[index],
                radius, angle, row.Anchor.height * .5f, Slices, RegionalTrackDisplay.TextLift);
        }
    }
}
