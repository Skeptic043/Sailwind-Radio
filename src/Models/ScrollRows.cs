using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Models
{
    // The text being written and the old ink being cleared, with pen positions in the row frame.
    internal sealed class ScrollRow : RegionalRow
    {
        internal int Capacity;
        internal string New = "", Old = "";
        internal float[] NewPens = Array.Empty<float>(), OldPens = Array.Empty<float>(), NewEdges = { 0f };
        internal float NewSqueeze = 1f, OldSqueeze = 1f, OldFloor = float.NegativeInfinity, OldReveal = float.PositiveInfinity, Start, Offset;

        internal ScrollRow(ModelDisplay anchor, Transform frame, Material ink) : base(anchor, frame, ink)
        {
        }
    }

    // Emerald wax scroll rows, written by the stylus on the model's carriage and cleared by its felt pad.
    internal sealed class ScrollRows : RegionalRows<ScrollRow>
    {
        // Darker than the belt's light floor, so the writing still reads in a dark cabin.
        private static readonly Color Ink = new Color32(0x14, 0x18, 0x14, 255);
        // The devices.json material the stylus writes on.
        private const string WritingBelt = "Wax-coated cloth writing belt";
        private static readonly Color StylusTipColour = new Color32(0x2b, 0x24, 0x18, 255), PadFelt = new Color32(0x51, 0x49, 0x37, 255);
        private const float InkLift = .0006f;
        private const float StylusRod = .0012f, StylusTip = .003f, PadHalf = .0035f, PadThick = .004f;
        private readonly List<ScrollSound> scrollSounds = new List<ScrollSound>(16);
        private readonly ScrollUpdate scroll = new ScrollUpdate();
        private readonly Transform carriage;
        private Vector3 carriagePivot;
        private float carriageAuthored, carriageX;
        // The Emerald stylus arm: a brass rod and a dark tip hanging from the carriage, in carriage space.
        private Mesh stylus;
        private Vector3[] stylusVertices;
        private Vector3 stylusUp, stylusRight, stylusOut, stylusTop;
        private float stylusRest, stylusTitle, stylusArtist, stylusDepth, padBottom, padDepth;
        private AudioSource eraserVoice;
        private bool scrollShown = true;

        internal ScrollRows(RegionalTrackDisplay display, Transform carriage) : base(display)
        {
            this.carriage = carriage;
        }

        internal override Color InkColour => Ink;
        internal override int Renderers => Rows.Count + (carriage ? 1 : 0);
        internal override bool Moving => scroll.Running || !scrollShown;

        // The belt itself is model geometry. The scroll only draws ink, old and new, on its front.
        protected override ScrollRow Build(ModelDisplay anchor, Transform frame, Material ink, Shader surfaceShader, List<UnityEngine.Object> owned)
        {
            var glyphs = Display.Glyphs;
            var row = new ScrollRow(anchor, frame, ink);
            row.Scale = glyphs.Valid ? TextFit.ScrollScale(glyphs.CapPixels, anchor.height) : 0f;
            row.Start = -anchor.width * .5f + TextFit.ScrollMargin * anchor.height;
            float room = TextFit.ScrollRoom(anchor.width, anchor.height);
            row.Capacity = glyphs.Valid ? Mathf.CeilToInt(room / (glyphs.NarrowestAdvance * row.Scale * TextFit.ScrollMinSqueeze)) + 1 : 1;
            Display.InkMesh(row, row.Capacity * 2, owned);
            return row;
        }

        internal override void Finish(Shader surfaceShader, List<UnityEngine.Object> owned)
        {
            base.Finish(surfaceShader, owned);
            // The writing belt is model geometry, so its light floor goes on the device's own belt material.
            foreach (var material in Display.Device.GetComponent<Renderer>().sharedMaterials)
                if (material && material.name == WritingBelt)
                    DeviceModel.SetLightFloor(material);
            ArtistRow.Offset = Vector3.Dot(Vector(ArtistRow.Anchor.center) - Vector(TitleRow.Anchor.center), Vector(TitleRow.Anchor.right));
            if (carriage)
            {
                carriagePivot = carriage.localPosition;
                carriageAuthored = Vector3.Dot(carriagePivot - Vector(TitleRow.Anchor.center), Vector(TitleRow.Anchor.right));
            }
            carriageX = carriageAuthored;
            if (carriage)
                BuildStylus(surfaceShader, owned);
        }

        private float Advance(char c) => Display.Glyphs.Glyphs[TrackDisplayText.Index(c)].Advance;

        private float Width(ScrollRow row, string text)
        {
            float pixels = 0;
            foreach (char c in text)
                pixels += Advance(c);
            return pixels * row.Scale;
        }

        protected override string Cut(ScrollRow row, string folded)
        {
            float room = TextFit.ScrollRoom(row.Anchor.width, row.Anchor.height);
            return TrackDisplayText.Cut(folded, s => Width(row, s) * TextFit.ScrollMinSqueeze, room);
        }

        // An update clears what is visible now, so an interrupted update carries on from its current state.
        protected override void Start(double now)
        {
            var phase = scroll.Phase(now);
            float cleared = scroll.Cleared(now);
            float from = scroll.Running ? scroll.Carriage(now) : carriageX;
            foreach (var row in Rows)
            {
                if (phase == ScrollPhase.Smoothing)
                    row.OldFloor = Mathf.Max(row.OldFloor, cleared - row.Offset);
                else if (phase == ScrollPhase.Jog || phase == ScrollPhase.Carry)
                    row.Old = "";
                else if (phase != ScrollPhase.Approach)
                {
                    row.Old = row.New;
                    row.OldPens = row.NewPens;
                    row.OldSqueeze = row.NewSqueeze;
                    row.OldFloor = float.NegativeInfinity;
                    row.OldReveal = Reveal(row, now);
                }
                string text = row.Title ? Targets.Title : Targets.Artist;
                row.New = text;
                row.NewSqueeze = TextFit.Squeeze(Width(row, text), TextFit.ScrollRoom(row.Anchor.width, row.Anchor.height), TextFit.ScrollMinSqueeze);
                row.NewPens = new float[text.Length];
                row.NewEdges = new float[text.Length + 1];
                float pen = row.Start;
                row.NewEdges[0] = pen + row.Offset;
                for (int i = 0; i < text.Length; i++)
                {
                    row.NewPens[i] = pen;
                    pen += Advance(text[i]) * row.Scale * row.NewSqueeze;
                    row.NewEdges[i + 1] = pen + row.Offset;
                }
            }
            // The pad sweeps only as far as the old ink reaches, slower for longer rows, and not at all on a clear belt.
            OldInk(TitleRow, out float titleRight, out int titleChars);
            OldInk(ArtistRow, out float artistRight, out int artistChars);
            float sweepTo = Mathf.Max(titleRight, artistRight) + PadHalf + .002f;
            scroll.Start(now, ScrollUpdate.Smoothing(Mathf.Max(titleChars, artistChars)), Range(5f, 6f), Strokes(TitleRow.New), Strokes(ArtistRow.New), from,
                TitleRow.Start, sweepTo, PadHalf, TitleRow.NewEdges, ArtistRow.NewEdges, TitleRow.Start);
            scrollShown = false;
        }

        // The right end of a row's remaining old ink along the title row, and how many of its characters remain.
        private void OldInk(ScrollRow row, out float right, out int count)
        {
            right = float.NegativeInfinity;
            count = 0;
            for (int i = 0; i < row.Old.Length; i++)
            {
                if (row.Old[i] == ' ' || row.OldPens[i] >= row.OldReveal)
                    continue;
                float end = Mathf.Min(row.OldReveal, row.OldPens[i] + Advance(row.Old[i]) * row.Scale * row.OldSqueeze);
                if (end <= row.OldFloor)
                    continue;
                right = Mathf.Max(right, end + row.Offset);
                count = i + 1;
            }
        }

        private static int[] Strokes(string text)
        {
            var strokes = new int[text.Length];
            for (int i = 0; i < text.Length; i++)
                strokes[i] = text[i] == ' ' ? 0 : Random.Next(2) + 1;
            return strokes;
        }

        // New ink shows up to the stylus while its row is written, everything once the row is done.
        private float Reveal(ScrollRow row, double now)
        {
            var phase = scroll.Phase(now);
            if (phase == ScrollPhase.Idle)
                return float.PositiveInfinity;
            float written = row.Title ? scroll.TitleWritten(now) : scroll.ArtistWritten(now);
            int count = row.New.Length;
            if (written <= 0f)
                return float.NegativeInfinity;
            return written >= count ? float.PositiveInfinity : ScrollUpdate.Edge(row.NewEdges, written) - row.Offset;
        }

        internal override void Tick(double now, bool hearing)
        {
            var sounds = Display.Sounds;
            if (scroll.Running)
            {
                scrollSounds.Clear();
                scroll.Update(now, scrollSounds);
                for (int i = 0; i < scrollSounds.Count; i++)
                {
                    var sound = scrollSounds[i];
                    if (sound == ScrollSound.End)
                    {
                        sounds.EndLoop();
                        continue;
                    }
                    if (!hearing)
                        continue;
                    if (sound == ScrollSound.Eraser) eraserVoice = sounds.Play("eraser", Range(-6, 0), 0);
                    else if (sound == ScrollSound.EraserEnd) sounds.FadeOut(eraserVoice);
                    else if (sound == ScrollSound.Paper) sounds.Play("paper", Range(-6, 0), 0);
                    else if (sound == ScrollSound.Tick) sounds.Play("tick", Range(-21, -16), 0);
                    else sounds.Play("stroke", Range(-10, 0), Range(-30, 30));
                }
                // The run loop sounds under the whole update, and resumes after a pause or slot.
                if (hearing && scroll.Running)
                    sounds.StartLoop();
            }
            if (scrollShown)
                return;
            TitleRow.Dirty = ArtistRow.Dirty = true;
            if (carriage)
            {
                carriageX = scroll.Carriage(now);
                carriage.localPosition = carriagePivot + Vector(TitleRow.Anchor.right) * (carriageX - carriageAuthored);
                Mechanism(scroll.StylusReach(now), scroll.Scribble(now), scroll.PadDown(now));
            }
            // One last render leaves the finished text fully shown and the carriage at home.
            if (!scroll.Running)
                scrollShown = true;
        }

        // Old ink shows from the pad to where it was written, new ink up to the stylus.
        protected override void Render(ScrollRow row, double now)
        {
            var glyphs = Display.Glyphs;
            float cleared = scroll.Running ? Mathf.Max(row.OldFloor, scroll.Cleared(now) - row.Offset) : float.PositiveInfinity;
            float reveal = Reveal(row, now);
            for (int i = 0; i < row.Capacity; i++)
            {
                int old = i * 4, written = (row.Capacity + i) * 4;
                if (i < row.Old.Length)
                {
                    ref var glyph = ref glyphs.Glyphs[TrackDisplayText.Index(row.Old[i])];
                    GlyphMesh.Pen(row.InkVertices, row.InkUvs, old, ref glyph, row.OldPens[i], glyphs.CapCentre, row.Scale, row.OldSqueeze,
                        -InkLift, cleared, row.OldReveal);
                }
                else
                    GlyphMesh.Hide(row.InkVertices, row.InkUvs, old);
                if (i < row.New.Length)
                {
                    ref var glyph = ref glyphs.Glyphs[TrackDisplayText.Index(row.New[i])];
                    GlyphMesh.Pen(row.InkVertices, row.InkUvs, written, ref glyph, row.NewPens[i], glyphs.CapCentre, row.Scale, row.NewSqueeze,
                        -InkLift, -GlyphMesh.Open, reveal);
                }
                else
                    GlyphMesh.Hide(row.InkVertices, row.InkUvs, written);
            }
            Display.Upload(row);
        }

        // Heights along the title row's up axis, relative to the carriage pivot. The arm rests a little below
        // the carriage and reaches down to each row's text centre line while writing it.
        private void BuildStylus(Shader surfaceShader, List<UnityEngine.Object> owned)
        {
            stylusUp = Vector(TitleRow.Anchor.up);
            stylusRight = Vector(TitleRow.Anchor.right);
            stylusOut = Vector(TitleRow.Anchor.normal);
            var filter = carriage.GetComponent<MeshFilter>();
            float bottom = filter && filter.sharedMesh ? filter.sharedMesh.bounds.min.y : 0f;
            stylusTop = stylusUp * bottom;
            stylusRest = bottom - .004f;
            stylusTitle = Vector3.Dot(Vector(TitleRow.Anchor.center) - carriagePivot, stylusUp);
            stylusArtist = Vector3.Dot(Vector(ArtistRow.Anchor.center) - carriagePivot, stylusUp);
            // The tip and the pad ride just in front of the belt. Lowered, the pad covers both rows with a margin.
            float belt = Vector3.Dot(Vector(TitleRow.Anchor.center) - carriagePivot, stylusOut) + .0003f;
            stylusDepth = belt + StylusTip * .5f;
            padDepth = belt + PadThick * .5f;
            padBottom = stylusArtist - ArtistRow.Anchor.height * .5f - .003f;
            var arm = Display.Child(carriage, "Emerald stylus arm");
            stylus = new Mesh { name = "Emerald stylus arm" };
            stylus.MarkDynamic();
            owned.Add(stylus);
            stylusVertices = new Vector3[72];
            var normals = new Vector3[72];
            Vector3[] faces = { stylusRight, -stylusRight, stylusUp, -stylusUp, stylusOut, -stylusOut };
            for (int box = 0; box < 3; box++)
                for (int f = 0; f < 6; f++)
                    for (int k = 0; k < 4; k++)
                        normals[box * 24 + f * 4 + k] = faces[f];
            Mechanism(0f, 0f, 0f);
            stylus.vertices = stylusVertices;
            stylus.normals = normals;
            stylus.subMeshCount = 3;
            var quads = RegionalTrackDisplay.QuadTriangles(18);
            for (int part = 0; part < 3; part++)
            {
                var triangles = new int[36];
                Array.Copy(quads, part * 36, triangles, 0, 36);
                stylus.SetTriangles(triangles, part);
            }
            arm.AddComponent<MeshFilter>().sharedMesh = stylus;
            var renderer = arm.AddComponent<MeshRenderer>();
            renderer.sharedMaterials = new[] {
                DeviceModel.Surface(surfaceShader, "Stylus brass", DrumRows.Brass, .75f, .45f, owned),
                DeviceModel.Surface(surfaceShader, "Stylus tip", StylusTipColour, .6f, .4f, owned),
                DeviceModel.Surface(surfaceShader, "Smoothing felt", PadFelt, 0f, .1f, owned) };
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
        }

        // reach runs 0 at rest, 1 at the title row and 2 at the artist row. The scribble moves the tip along up.
        // The felt pad hangs as a short stub under the carriage and lowers over both rows for the smoothing pass.
        private void Mechanism(float reach, float scribble, float pad)
        {
            float target = reach <= 1f ? Mathf.Lerp(stylusRest, stylusTitle, reach) : Mathf.Lerp(stylusTitle, stylusArtist, reach - 1f);
            float rowHeight = TitleRow.Anchor.height;
            target += scribble * .15f * rowHeight * Mathf.Min(1f, reach);
            float top = Vector3.Dot(stylusTop, stylusUp), tipTop = target + StylusTip * .5f;
            var depth = stylusOut * stylusDepth;
            Box(0, stylusUp * ((top + tipTop) * .5f) + depth, StylusRod * .5f, Mathf.Max(.0001f, (top - tipTop) * .5f), StylusRod * .5f);
            Box(24, stylusUp * target + depth, StylusTip * .5f, StylusTip * .5f, StylusTip * .5f);
            float padLow = Mathf.Lerp(top - .003f, padBottom, pad);
            Box(48, stylusUp * ((top + padLow) * .5f) + stylusOut * padDepth, PadHalf, Mathf.Max(.0001f, (top - padLow) * .5f), PadThick * .5f);
            if (stylus)
            {
                stylus.vertices = stylusVertices;
                stylus.RecalculateBounds();
                Display.VerticesUpdated += stylusVertices.Length;
            }
        }

        // An axis-aligned box in the row's axes, six faces of four vertices wound to face outward.
        private void Box(int at, Vector3 centre, float halfRight, float halfUp, float halfOut)
        {
            Vector3 r = stylusRight * halfRight, u = stylusUp * halfUp, o = stylusOut * halfOut;
            Face(at, centre + r, -o, u);
            Face(at + 4, centre - r, o, u);
            Face(at + 8, centre + u, r, -o);
            Face(at + 12, centre - u, r, o);
            Face(at + 16, centre + o, r, u);
            Face(at + 20, centre - o, -r, u);
        }

        // Corners from -a-b round to +a-b. Viewed from outside, these quads wind as the display's quads do.
        private void Face(int at, Vector3 centre, Vector3 a, Vector3 b)
        {
            stylusVertices[at] = centre - a - b;
            stylusVertices[at + 1] = centre - a + b;
            stylusVertices[at + 2] = centre + a + b;
            stylusVertices[at + 3] = centre + a - b;
        }
    }
}
