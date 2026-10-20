using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Models
{
    // Title and artist rows built at a regional radio's authored anchors, animated by the display's
    // mechanism in scaled game time so the pause menu freezes them. Each row is one ink mesh and, for
    // split-flaps and drums, one surface mesh. Glyph quads come straight from the font atlas and are
    // updated in place only while a row animates. SplitFlapRows, DrumRows and ScrollRows build, animate
    // and draw their mechanism's rows. This class holds the parts they share.
    internal sealed class RegionalTrackDisplay : ITrackDisplay
    {
        // Each layer sits a little in front of the last along the anchor normal.
        internal const float SurfaceLift = .0005f, TextLift = .001f;
        private const int FontSize = 80;
        private const float MechanismLoopDecibels = -26f;
        private static readonly Dictionary<Font, GlyphSet> Fonts = new Dictionary<Font, GlyphSet>();
        private static bool glyphsReported;

        // The charset's glyphs at FontSize, refreshed whenever the font atlas is rebuilt.
        internal sealed class GlyphSet
        {
            internal readonly Glyph[] Glyphs = new Glyph[TrackDisplayText.Charset.Length];
            internal float CapPixels, CapCentre, WidestBox, NarrowestAdvance;
            internal bool Valid;

            internal void Refresh(Font font)
            {
                font.RequestCharactersInTexture(TrackDisplayText.Charset, FontSize);
                WidestBox = 0;
                NarrowestAdvance = float.MaxValue;
                for (int i = 0; i < Glyphs.Length; i++)
                {
                    char c = TrackDisplayText.Charset[i];
                    bool present = font.GetCharacterInfo(c, out var info, FontSize);
                    Glyphs[i] = new Glyph
                    {
                        Present = present && c != ' ', MinX = info.minX, MaxX = info.maxX, MinY = info.minY, MaxY = info.maxY, Advance = info.advance,
                        BottomLeft = info.uvBottomLeft, BottomRight = info.uvBottomRight, TopLeft = info.uvTopLeft, TopRight = info.uvTopRight
                    };
                    if (!present)
                        continue;
                    if (c != ' ')
                        WidestBox = Mathf.Max(WidestBox, info.maxX - info.minX);
                    if (info.advance > 0)
                        NarrowestAdvance = Mathf.Min(NarrowestAdvance, info.advance);
                    if (c == 'H')
                    {
                        CapPixels = info.maxY - info.minY;
                        CapCentre = (info.maxY + info.minY) * .5f;
                    }
                }
                Valid = CapPixels > 0 && WidestBox > 0 && NarrowestAdvance < float.MaxValue;
            }
        }

        internal readonly GlyphSet Glyphs;
        internal readonly DisplaySoundPlayer Sounds;
        internal int VerticesUpdated;
        private readonly List<GameObject> objects = new List<GameObject>();
        private readonly RegionalRows rows;
        private readonly Font font;
        private readonly Func<bool> audible;
        private readonly DisplayMechanism mechanism;
        private readonly int renderers;
        private bool silenced, atlasChanged;

        internal RegionalTrackDisplay(Transform parent, IReadOnlyList<ModelDisplay> anchors, Shader surfaceShader, Shader textShader,
            Font font, Func<bool> audible, DisplaySoundBank bank, Transform carriage, Action<string> report, List<UnityEngine.Object> owned)
        {
            this.font = font;
            this.audible = audible;
            if (!Fonts.TryGetValue(font, out Glyphs))
            {
                Glyphs = new GlyphSet();
                Glyphs.Refresh(font);
                Fonts[font] = Glyphs;
            }
            if (!Glyphs.Valid && !glyphsReported)
            {
                glyphsReported = true;
                report?.Invoke("Regional display font " + font.name + " has no usable glyphs. Display rows stay blank.");
            }
            mechanism = anchors[0].Mechanism;
            rows = mechanism == DisplayMechanism.SplitFlap ? new SplitFlapRows(this) : mechanism == DisplayMechanism.Drum ? new DrumRows(this) :
                (RegionalRows)new ScrollRows(this, carriage);
            var centre = Vector3.zero;
            foreach (var anchor in anchors)
            {
                centre += RegionalRows.Vector(anchor.center) / anchors.Count;
                var frame = Child(parent, "Regional " + anchor.row + " display");
                frame.transform.localPosition = RegionalRows.Vector(anchor.center);
                // Local -Z faces the viewer and +X runs along the anchor's right.
                frame.transform.localRotation = Quaternion.LookRotation(-RegionalRows.Vector(anchor.normal), RegionalRows.Vector(anchor.up));
                var ink = new Material(textShader) { name = "Regional display ink", mainTexture = font.material.mainTexture, color = rows.InkColour };
                owned.Add(ink);
                rows.Add(anchor, frame.transform, ink, surfaceShader, owned);
            }
            rows.Finish(surfaceShader, owned);
            renderers = rows.Renderers;
            Sounds = new DisplaySoundPlayer(parent, centre, bank, MechanismLoopDecibels);
            Font.textureRebuilt += FontRebuilt;
            // Every row starts dirty, so this draws them all.
            rows.RenderDirty(Time.time);
        }

        internal GameObject Child(Transform parent, string name)
        {
            var child = new GameObject(name);
            child.transform.SetParent(parent, false);
            objects.Add(child);
            return child;
        }

        internal Mesh AddMesh(RegionalRow row, string name, Material material, List<UnityEngine.Object> owned)
        {
            var part = Child(row.Frame, name);
            var mesh = new Mesh { name = name };
            mesh.MarkDynamic();
            owned.Add(mesh);
            part.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = part.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            if (material == row.Ink)
                renderer.receiveShadows = false;
            return mesh;
        }

        internal static int[] QuadTriangles(int quads)
        {
            var triangles = new int[quads * 6];
            for (int q = 0; q < quads; q++)
            {
                triangles[q * 6] = q * 4;
                triangles[q * 6 + 1] = q * 4 + 1;
                triangles[q * 6 + 2] = q * 4 + 2;
                triangles[q * 6 + 3] = q * 4;
                triangles[q * 6 + 4] = q * 4 + 2;
                triangles[q * 6 + 5] = q * 4 + 3;
            }
            return triangles;
        }

        internal void InkMesh(RegionalRow row, int quads, List<UnityEngine.Object> owned)
        {
            row.InkMesh = AddMesh(row, "Regional display ink", row.Ink, owned);
            row.InkVertices = new Vector3[quads * 4];
            row.InkUvs = new Vector2[quads * 4];
            row.InkMesh.vertices = row.InkVertices;
            row.InkMesh.uv = row.InkUvs;
            row.InkMesh.triangles = QuadTriangles(quads);
        }

        internal void Upload(RegionalRow row)
        {
            row.InkMesh.vertices = row.InkVertices;
            row.InkMesh.uv = row.InkUvs;
            row.InkMesh.RecalculateBounds();
            VerticesUpdated += row.InkVertices.Length;
        }

        public void SetLayer(int layer)
        {
            foreach (var part in objects)
                part.layer = layer;
        }

        public void SetMetadata(bool powered, string title, string artist, string album) => rows.SetMetadata(powered, title, artist);

        // Idle displays only check whether the atlas changed. Sound state is read while something moves or sounds.
        public void Tick()
        {
            bool timing = DisplayTiming.On;
            if (timing)
                DisplayTiming.Begin();
            VerticesUpdated = 0;
            double now = Time.time;
            if (atlasChanged)
                RefreshAtlas();
            bool moving = rows.Moving;
            if (moving || Sounds.Busy)
            {
                bool hearing = audible();
                if (!hearing && !silenced)
                    Sounds.Silence();
                silenced = !hearing;
                rows.Tick(now, hearing);
                Sounds.Tick();
            }
            rows.RenderDirty(now);
            if (timing)
                DisplayTiming.End(mechanism, moving, VerticesUpdated, renderers);
        }

        private void FontRebuilt(Font rebuilt)
        {
            if (rebuilt == font)
                atlasChanged = true;
        }

        // A rebuilt atlas moves every glyph, so the characters are requested again.
        private void RefreshAtlas()
        {
            atlasChanged = false;
            Glyphs.Refresh(font);
            rows.Redraw(font);
        }

        public void Dispose()
        {
            Font.textureRebuilt -= FontRebuilt;
            Sounds.Dispose();
        }
    }
}
