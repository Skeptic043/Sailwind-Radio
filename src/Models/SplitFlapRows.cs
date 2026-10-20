using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Models
{
    internal sealed class FlapRow : CellRow
    {
        internal readonly FlapCell[] Flaps;
        internal Mesh SurfaceMesh;
        internal Vector3[] SurfaceVertices, SurfaceNormals;

        internal FlapRow(ModelDisplay anchor, Transform frame, Material ink) : base(anchor, frame, ink)
        {
            Flaps = new FlapCell[Cells];
            for (int i = 0; i < Flaps.Length; i++)
                Flaps[i] = new FlapCell();
        }
    }

    // Aestrin split-flap rows. Each cell's leaf falls from the old character to the next in turn.
    internal sealed class SplitFlapRows : RegionalRows<FlapRow>
    {
        private static readonly Color Leaf = new Color32(0x27, 0x36, 0x32, 255), Ink = new Color32(0xe3, 0xd5, 0xae, 255);
        // The falling leaf hangs in front of the static halves.
        private const float SplitLift = .0011f, LeafLift = .0014f, LeafTextLift = .0003f;
        private const float FlipsPerSecond = 12f;

        internal SplitFlapRows(RegionalTrackDisplay display) : base(display)
        {
        }

        internal override Color InkColour => Ink;
        internal override int Renderers => Rows.Count * 2;
        internal override bool Moving => TitleRow.Animating || ArtistRow.Animating;

        // Five surface quads per cell: top half, bottom half, split gap, falling leaf front and back.
        // Four ink quads per cell: the static top and bottom halves and the two faces of the falling leaf.
        protected override FlapRow Build(ModelDisplay anchor, Transform frame, Material ink, Shader surfaceShader, List<UnityEngine.Object> owned)
        {
            var row = new FlapRow(anchor, frame, ink);
            row.FitGlyphs(Display.Glyphs, TextFit.FlapCap, TextFit.FlapFloor);
            // Plain charcoal leaves in the cabinet's shader, the look the user preferred.
            var material = DeviceModel.Surface(surfaceShader, "Split-flap leaves", Leaf, 0, .25f, owned);
            row.SurfaceMesh = Display.AddMesh(row, "Split-flap leaves", material, owned);
            int quads = anchor.cells * 5;
            row.SurfaceVertices = new Vector3[quads * 4];
            row.SurfaceNormals = new Vector3[quads * 4];
            row.SurfaceMesh.vertices = row.SurfaceVertices;
            row.SurfaceMesh.normals = row.SurfaceNormals;
            row.SurfaceMesh.triangles = RegionalTrackDisplay.QuadTriangles(quads);
            Display.InkMesh(row, anchor.cells * 4, owned);
            return row;
        }

        protected override string Cut(FlapRow row, string folded) => row.Cut(folded);

        // Cells head forward to the row's text.
        protected override void Start(double now)
        {
            Retarget(TitleRow, Targets.Title, 0, now);
            Retarget(ArtistRow, Targets.Artist, 1, now);
        }

        private static void Retarget(FlapRow row, string text, int rowIndex, double now)
        {
            row.Animating = true;
            for (int i = 0; i < row.Cells; i++)
            {
                int target = TrackDisplayText.Index(text[i]);
                row.Flaps[i].Retarget(target, now + i * .035 + rowIndex * .012 + Range(0, .012f), 1 / (FlipsPerSecond * (1 + Range(-.05f, .05f))));
            }
        }

        internal override void Tick(double now, bool hearing)
        {
            Animate(TitleRow, now, hearing);
            Animate(ArtistRow, now, hearing);
        }

        private void Animate(FlapRow row, double now, bool hearing)
        {
            if (!row.Animating)
                return;
            bool settled = true;
            for (int i = 0; i < row.Cells; i++)
            {
                // Landings within a short window share one voice.
                if (row.Flaps[i].Update(now, out bool arrived) > 0 && hearing)
                    Display.Sounds.Coalesce("flap", Range(-9, 0) + (arrived ? 2f : 0f), 40f);
                settled &= row.Flaps[i].Settled;
            }
            row.Dirty = true;
            if (settled)
                row.Animating = false;
        }

        // The static top shows the next character and the static bottom the old one while the leaf falls between
        // them. Its front shows the old top half, and past vertical its back shows the new bottom half. Every
        // glyph is trimmed to its leaf, so none reaches past the display.
        protected override void Render(FlapRow row, double now)
        {
            var glyphs = Display.Glyphs;
            float h = row.Anchor.height, half = row.Anchor.pitch * .45f, hinge = -LeafLift, top = h * .5f;
            for (int i = 0; i < row.Cells; i++)
            {
                var flap = row.Flaps[i];
                float x = row.CellX(i), p = flap.Progress(now), x0 = x - half, x1 = x + half;
                bool falling = flap.Flipping;
                int s = i * 5, g = i * 4;
                SurfaceQuad(row, s, x0, 0, x1, top, -RegionalTrackDisplay.SurfaceLift);
                SurfaceQuad(row, s + 1, x0, -top, x1, 0, -RegionalTrackDisplay.SurfaceLift);
                SurfaceQuad(row, s + 2, x0, -h * .015f, x1, h * .015f, -SplitLift);
                int upperIndex = falling ? flap.Next : flap.Shown, oldIndex = flap.Shown, nextIndex = flap.Next;
                ref var upper = ref glyphs.Glyphs[upperIndex];
                ref var old = ref glyphs.Glyphs[oldIndex];
                GlyphMesh.Centred(row.InkVertices, row.InkUvs, g * 4, ref upper, x, glyphs.CapCentre, row.GlyphScale[upperIndex], row.GlyphSqueeze[upperIndex],
                    -RegionalTrackDisplay.TextLift, x0, 0, x1, top);
                GlyphMesh.Centred(row.InkVertices, row.InkUvs, (g + 1) * 4, ref old, x, glyphs.CapCentre, row.GlyphScale[oldIndex], row.GlyphSqueeze[oldIndex],
                    -RegionalTrackDisplay.TextLift, x0, -top, x1, 0);
                bool front = falling && p < .5f, back = falling && p >= .5f;
                float angle = -Mathf.PI * p;
                if (front)
                {
                    SurfaceQuad(row, s + 3, x0, 0, x1, top, hinge);
                    GlyphMesh.Hinge(row.SurfaceVertices, (s + 3) * 4, 4, hinge, angle);
                    HingeNormals(row, (s + 3) * 4, angle);
                    GlyphMesh.Centred(row.InkVertices, row.InkUvs, (g + 2) * 4, ref old, x, glyphs.CapCentre, row.GlyphScale[oldIndex], row.GlyphSqueeze[oldIndex],
                        hinge - LeafTextLift, x0, 0, x1, top);
                    GlyphMesh.Hinge(row.InkVertices, (g + 2) * 4, 4, hinge, angle);
                }
                else
                {
                    HideSurface(row, s + 3);
                    GlyphMesh.Hide(row.InkVertices, row.InkUvs, (g + 2) * 4);
                }
                if (back)
                {
                    // Drawn upright as the new bottom half, then turned half a revolution further than the leaf.
                    SurfaceQuad(row, s + 4, x0, -top, x1, 0, hinge);
                    GlyphMesh.Hinge(row.SurfaceVertices, (s + 4) * 4, 4, hinge, angle + Mathf.PI);
                    HingeNormals(row, (s + 4) * 4, angle + Mathf.PI);
                    ref var next = ref glyphs.Glyphs[nextIndex];
                    GlyphMesh.Centred(row.InkVertices, row.InkUvs, (g + 3) * 4, ref next, x, glyphs.CapCentre, row.GlyphScale[nextIndex], row.GlyphSqueeze[nextIndex],
                        hinge - LeafTextLift, x0, -top, x1, 0);
                    GlyphMesh.Hinge(row.InkVertices, (g + 3) * 4, 4, hinge, angle + Mathf.PI);
                }
                else
                {
                    HideSurface(row, s + 4);
                    GlyphMesh.Hide(row.InkVertices, row.InkUvs, (g + 3) * 4);
                }
            }
            Display.Upload(row);
            row.SurfaceMesh.vertices = row.SurfaceVertices;
            row.SurfaceMesh.normals = row.SurfaceNormals;
            row.SurfaceMesh.RecalculateBounds();
            Display.VerticesUpdated += row.SurfaceVertices.Length;
        }

        private static void SurfaceQuad(FlapRow row, int quad, float x0, float y0, float x1, float y1, float z)
        {
            int at = quad * 4;
            row.SurfaceVertices[at] = new Vector3(x0, y0, z);
            row.SurfaceVertices[at + 1] = new Vector3(x0, y1, z);
            row.SurfaceVertices[at + 2] = new Vector3(x1, y1, z);
            row.SurfaceVertices[at + 3] = new Vector3(x1, y0, z);
            for (int i = 0; i < 4; i++)
                row.SurfaceNormals[at + i] = Vector3.back;
        }

        private static void HideSurface(FlapRow row, int quad)
        {
            for (int i = quad * 4; i < quad * 4 + 4; i++)
                row.SurfaceVertices[i] = Vector3.zero;
        }

        private static void HingeNormals(FlapRow row, int at, float radians)
        {
            var normal = new Vector3(0, Mathf.Sin(radians), -Mathf.Cos(radians));
            for (int i = at; i < at + 4; i++)
                row.SurfaceNormals[i] = normal;
        }
    }
}
