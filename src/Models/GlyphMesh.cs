using UnityEngine;

namespace SailwindRadio.Models
{
    // One font glyph in font pixels, with its atlas corners. Pixel y runs up from the baseline.
    internal struct Glyph
    {
        internal float MinX, MaxX, MinY, MaxY, Advance;
        internal Vector2 BottomLeft, BottomRight, TopLeft, TopRight;
        internal bool Present;
    }

    // Glyph quads written in place into a row's preallocated arrays. Quads are trimmed geometrically,
    // with their atlas coordinates interpolated, so the text shader needs no clip range.
    // Each quad is four vertices: bottom left, top left, top right, bottom right.
    internal static class GlyphMesh
    {
        internal const float Open = 1e4f;

        internal static void Hide(Vector3[] vertices, Vector2[] uvs, int at)
        {
            for (int i = 0; i < 4; i++)
            {
                vertices[at + i] = Vector3.zero;
                uvs[at + i] = Vector2.zero;
            }
        }

        // The rectangle x0..x1 by y0..y1 at depth z, trimmed to the clip box. A glyph spans its full
        // atlas cell across the rectangle. An empty trim collapses the quad.
        // True when anything of the quad remains.
        internal static bool Quad(Vector3[] vertices, Vector2[] uvs, int at, ref Glyph glyph, float x0, float y0, float x1, float y1, float z,
            float clipX0, float clipY0, float clipX1, float clipY1)
        {
            float a0 = Mathf.Max(x0, clipX0), a1 = Mathf.Min(x1, clipX1), b0 = Mathf.Max(y0, clipY0), b1 = Mathf.Min(y1, clipY1);
            if (!glyph.Present || a1 <= a0 || b1 <= b0 || x1 <= x0 || y1 <= y0)
            {
                Hide(vertices, uvs, at);
                return false;
            }
            float u0 = (a0 - x0) / (x1 - x0), u1 = (a1 - x0) / (x1 - x0), v0 = (b0 - y0) / (y1 - y0), v1 = (b1 - y0) / (y1 - y0);
            vertices[at] = new Vector3(a0, b0, z);
            vertices[at + 1] = new Vector3(a0, b1, z);
            vertices[at + 2] = new Vector3(a1, b1, z);
            vertices[at + 3] = new Vector3(a1, b0, z);
            uvs[at] = Atlas(ref glyph, u0, v0);
            uvs[at + 1] = Atlas(ref glyph, u0, v1);
            uvs[at + 2] = Atlas(ref glyph, u1, v1);
            uvs[at + 3] = Atlas(ref glyph, u1, v0);
            return true;
        }

        private static Vector2 Atlas(ref Glyph glyph, float u, float v) =>
            Vector2.Lerp(Vector2.Lerp(glyph.BottomLeft, glyph.BottomRight, u), Vector2.Lerp(glyph.TopLeft, glyph.TopRight, u), v);

        // A glyph's box centred on cx, with the capital centre capCentre (font pixels) on y = 0, trimmed to the clip box.
        internal static bool Centred(Vector3[] vertices, Vector2[] uvs, int at, ref Glyph glyph, float cx, float capCentre, float scale, float squeeze,
            float z, float clipX0, float clipY0, float clipX1, float clipY1)
        {
            float half = (glyph.MaxX - glyph.MinX) * scale * squeeze * .5f;
            return Quad(vertices, uvs, at, ref glyph, cx - half, (glyph.MinY - capCentre) * scale, cx + half, (glyph.MaxY - capCentre) * scale, z,
                clipX0, clipY0, clipX1, clipY1);
        }

        // A glyph at a pen position, as text is set along a line, trimmed to clipX0..clipX1.
        internal static void Pen(Vector3[] vertices, Vector2[] uvs, int at, ref Glyph glyph, float pen, float capCentre, float scale, float squeeze,
            float z, float clipX0, float clipX1)
        {
            Quad(vertices, uvs, at, ref glyph, pen + glyph.MinX * scale * squeeze, (glyph.MinY - capCentre) * scale,
                pen + glyph.MaxX * scale * squeeze, (glyph.MaxY - capCentre) * scale, z, clipX0, -Open, clipX1, Open);
        }

        // One glyph on a drum wheel at angle radians, cut into slices so it follows the curve and trimmed to the
        // row window of half-height halfHeight. Positive angles roll the glyph up, negative ones down.
        internal static void Wheel(Vector3[] vertices, Vector2[] uvs, int at, ref Glyph glyph, float cx, float capCentre, float scale, float squeeze,
            float radius, float radians, float halfHeight, int slices, float lift)
        {
            float window = Mathf.Asin(Mathf.Min(1f, halfHeight / radius));
            float y0 = (glyph.MinY - capCentre) * scale, y1 = (glyph.MaxY - capCentre) * scale;
            float low = radius * (-window - radians), high = radius * (window - radians);
            for (int k = 0; k < slices; k++)
            {
                float a = Mathf.Lerp(y0, y1, k / (float)slices), b = Mathf.Lerp(y0, y1, (k + 1) / (float)slices);
                int quad = at + k * 4;
                // A slice wholly outside the window stays collapsed rather than wrapping its point onto the drum.
                if (Centred(vertices, uvs, quad, ref glyph, cx, capCentre, scale, squeeze, 0, -Open, Mathf.Max(a, low), Open, Mathf.Min(b, high)))
                    Wrap(vertices, quad, 4, radius, radians, lift);
            }
        }

        // Turns vertices about the X axis through the hinge at y = 0, z = hinge. Positive angles tip the top
        // away from the viewer at -Z, as Quaternion.Euler(angle, 0, 0) does.
        internal static void Hinge(Vector3[] vertices, int at, int count, float hinge, float radians)
        {
            float c = Mathf.Cos(radians), s = Mathf.Sin(radians);
            for (int i = at; i < at + count; i++)
            {
                var p = vertices[i];
                float y = p.y, z = p.z - hinge;
                vertices[i] = new Vector3(p.x, y * c - z * s, y * s + z * c + hinge);
            }
        }

        // Wraps flat vertices onto a drum. Flat y becomes arc length from the drum front at angle radians,
        // and the drum front sits at depth lift toward the viewer.
        internal static void Wrap(Vector3[] vertices, int at, int count, float radius, float radians, float lift)
        {
            for (int i = at; i < at + count; i++)
            {
                var p = vertices[i];
                float angle = radians + p.y / radius;
                vertices[i] = new Vector3(p.x, radius * Mathf.Sin(angle), radius * (1f - Mathf.Cos(angle)) - lift);
            }
        }
    }
}
