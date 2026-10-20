using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Models
{
    // One title or artist row: its anchor frame and the ink mesh every mechanism draws.
    internal class RegionalRow
    {
        internal readonly bool Title;
        internal readonly ModelDisplay Anchor;
        internal readonly Transform Frame;
        internal readonly Material Ink;
        internal bool Dirty = true;
        internal float Scale = 1f;
        internal Mesh InkMesh;
        internal Vector3[] InkVertices;
        internal Vector2[] InkUvs;

        internal RegionalRow(ModelDisplay anchor, Transform frame, Material ink)
        {
            Title = anchor.row == "title";
            Anchor = anchor;
            Frame = frame;
            Ink = ink;
        }
    }

    // Split-flap and drum rows: a fixed run of cells, with each glyph's own scale and squeeze indexed like the charset.
    internal class CellRow : RegionalRow
    {
        internal readonly float[] GlyphScale = new float[TrackDisplayText.Charset.Length], GlyphSqueeze = new float[TrackDisplayText.Charset.Length];
        internal readonly int Cells;
        internal bool Animating;

        internal CellRow(ModelDisplay anchor, Transform frame, Material ink) : base(anchor, frame, ink)
        {
            Cells = anchor.cells;
        }

        internal float CellX(int cell) => TrackDisplayText.CellX(cell, Cells, Anchor.pitch);

        internal string Cut(string folded) => TrackDisplayText.Left(TrackDisplayText.CutCells(folded, Cells), Cells);

        // Capitals at the row's cap height, centred on the capital centre. Each glyph squeezes into its cell only
        // as far as it needs, and a glyph reaching past the cell above or below that centre is drawn smaller.
        internal void FitGlyphs(RegionalTrackDisplay.GlyphSet glyphs, float cap, float floor)
        {
            Scale = glyphs.Valid ? TextFit.CapScale(glyphs.CapPixels, Anchor.height, cap) : 0f;
            for (int i = 0; i < glyphs.Glyphs.Length; i++)
            {
                ref var glyph = ref glyphs.Glyphs[i];
                float reach = Mathf.Max(glyph.MaxY - glyphs.CapCentre, glyphs.CapCentre - glyph.MinY);
                TextFit.Glyph(glyph.MaxX - glyph.MinX, reach, Scale, TextFit.CellRoom * Anchor.pitch, TextFit.CellReach * Anchor.height,
                    floor, out GlyphScale[i], out GlyphSqueeze[i]);
            }
        }
    }

    // What RegionalTrackDisplay asks of a mechanism: SplitFlapRows, DrumRows or ScrollRows.
    internal abstract class RegionalRows
    {
        protected static readonly System.Random Random = new System.Random();

        internal static Vector3 Vector(float[] values) => new Vector3(values[0], values[1], values[2]);
        protected static float Range(float min, float max) => min + (float)Random.NextDouble() * (max - min);

        internal abstract Color InkColour { get; }
        internal abstract int Renderers { get; }
        internal abstract bool Moving { get; }
        internal abstract void Add(ModelDisplay anchor, Transform frame, Material ink, Shader surfaceShader, List<UnityEngine.Object> owned);
        internal abstract void Finish(Shader surfaceShader, List<UnityEngine.Object> owned);
        internal abstract void SetMetadata(bool powered, string title, string artist);
        internal abstract void Tick(double now, bool hearing);
        internal abstract void RenderDirty(double now);
        internal abstract void Redraw(Font font);
    }

    // A mechanism's rows in anchor order, with the title and artist rows picked out.
    internal abstract class RegionalRows<TRow> : RegionalRows where TRow : RegionalRow
    {
        protected readonly RegionalTrackDisplay Display;
        protected readonly List<TRow> Rows = new List<TRow>();
        protected readonly DisplayTargets Targets = new DisplayTargets();
        protected TRow TitleRow, ArtistRow;

        protected RegionalRows(RegionalTrackDisplay display)
        {
            Display = display;
        }

        protected abstract TRow Build(ModelDisplay anchor, Transform frame, Material ink, Shader surfaceShader, List<UnityEngine.Object> owned);
        // Squeeze to the type's minimum, then keep as many characters as fit.
        protected abstract string Cut(TRow row, string folded);
        protected abstract void Start(double now);
        protected abstract void Render(TRow row, double now);

        internal sealed override void Add(ModelDisplay anchor, Transform frame, Material ink, Shader surfaceShader, List<UnityEngine.Object> owned) =>
            Rows.Add(Build(anchor, frame, ink, surfaceShader, owned));

        // Anchors always hold one title row and one artist row.
        internal override void Finish(Shader surfaceShader, List<UnityEngine.Object> owned)
        {
            TitleRow = Rows.Find(row => row.Title);
            ArtistRow = Rows.Find(row => !row.Title);
        }

        // Album is not shown on regional rows. Only a change of the fitted text, power included, animates.
        internal sealed override void SetMetadata(bool powered, string title, string artist)
        {
            string fittedTitle = Fit(TitleRow, powered ? title : ""), fittedArtist = Fit(ArtistRow, powered ? artist : "");
            if (Targets.Set(fittedTitle, fittedArtist))
                Start(Time.time);
        }

        private string Fit(TRow row, string text)
        {
            string folded = TrackDisplayText.Fold(text);
            return Display.Glyphs.Valid ? Cut(row, folded) : "";
        }

        internal sealed override void RenderDirty(double now)
        {
            for (int i = 0; i < Rows.Count; i++)
                if (Rows[i].Dirty)
                {
                    Rows[i].Dirty = false;
                    Render(Rows[i], now);
                }
        }

        // A rebuilt atlas moves every glyph, so every row is redrawn.
        internal sealed override void Redraw(Font font)
        {
            foreach (var row in Rows)
            {
                if (row.Ink)
                    row.Ink.mainTexture = font.material.mainTexture;
                row.Dirty = true;
            }
        }
    }
}
