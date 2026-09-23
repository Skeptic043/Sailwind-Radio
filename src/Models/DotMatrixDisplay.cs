using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Models
{
    internal sealed class DotMatrixDisplay : IDisposable
    {
        private static readonly Color Amber = new Color(.94f, .46f, .10f, 1f);
        private readonly GameObject root;
        private readonly Texture2D texture;
        private readonly Material fallbackMaterial;
        private readonly TextMesh[] fallback = new TextMesh[3];
        private readonly MetadataMarquee marquee = new MetadataMarquee();
        private readonly bool[] fallbackRequired = new bool[3];
        private readonly byte[] mask = new byte[DotMatrixFont.Width * DotMatrixFont.Height];
        private readonly Color32[] pixels = new Color32[DotMatrixFont.Width * DotMatrixFont.Height];
        internal DotMatrixDisplay(Transform parent, Shader shader, List<UnityEngine.Object> owned)
        {
            root = new GameObject("Recessed dot matrix display");
            root.transform.SetParent(parent, false);
            root.transform.localPosition = new Vector3(.075f, .18f, -.099f);
            // The bitmap shrinks to only a few screen pixels per glyph when held.
            // Filtered mipmaps keep its small dots visible instead of skipping them.
            texture = new Texture2D(DotMatrixFont.Width, DotMatrixFont.Height, TextureFormat.RGBA32, true)
            {
                name = "Original radio glyph bitmap", filterMode = FilterMode.Trilinear, wrapMode = TextureWrapMode.Clamp
            };
            owned.Add(texture);
            var material = new Material(shader) { mainTexture = texture, color = Amber };
            owned.Add(material);
            var mesh = new Mesh
            {
                name = "Radio display window",
                vertices = new[] { new Vector3(-.166f, -.07f, 0), new Vector3(-.166f, .07f, 0),
                    new Vector3(.166f, .07f, 0), new Vector3(.166f, -.07f, 0) },
                uv = new[] { new Vector2(0, 0), new Vector2(0, 1), new Vector2(1, 1), new Vector2(1, 0) },
                triangles = new[] { 0, 1, 2, 0, 2, 3 },
                colors = new[] { Color.white, Color.white, Color.white, Color.white }
            };
            mesh.RecalculateBounds();
            owned.Add(mesh);
            root.AddComponent<MeshFilter>().sharedMesh = mesh;
            var renderer = root.AddComponent<MeshRenderer>();
            renderer.sharedMaterial = material;
            renderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
            renderer.receiveShadows = false;

            var font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            fallbackMaterial = new Material(shader) { mainTexture = font.material.mainTexture };
            owned.Add(fallbackMaterial);
            for (int index = 0; index < fallback.Length; index++)
            {
                var label = new GameObject("Unicode metadata " + index);
                label.transform.SetParent(root.transform, false);
                label.transform.localPosition = new Vector3(0, (56f - index * 56f) / DotMatrixFont.Height * .14f, -.0002f);
                var text = label.AddComponent<TextMesh>();
                text.font = font;
                text.anchor = TextAnchor.MiddleCenter;
                text.alignment = TextAlignment.Center;
                text.fontSize = 64;
                text.characterSize = .014f;
                text.richText = false;
                text.color = Amber;
                var textRenderer = label.GetComponent<Renderer>();
                textRenderer.sharedMaterial = fallbackMaterial;
                textRenderer.shadowCastingMode = UnityEngine.Rendering.ShadowCastingMode.Off;
                textRenderer.receiveShadows = false;
                fallback[index] = text;
            }
            Font.textureRebuilt += FontRebuilt;
            SetMetadata(false, "", "", "");
        }

        internal void SetLayer(int layer)
        {
            root.layer = layer;
            foreach (var text in fallback)
                text.gameObject.layer = layer;
        }

        internal void SetMetadata(bool powered, string title, string artist, string album)
        {
            marquee.Set(powered, title, artist, album, Time.unscaledTime);
            bool wasTitleFallback = fallbackRequired[0], wasArtistFallback = fallbackRequired[1], wasAlbumFallback = fallbackRequired[2];
            fallbackRequired[0] = !DotMatrixFont.CanRender(title);
            fallbackRequired[1] = !DotMatrixFont.CanRender(artist);
            fallbackRequired[2] = !DotMatrixFont.CanRender(album);
            RefreshBitmap(wasTitleFallback != fallbackRequired[0] || wasArtistFallback != fallbackRequired[1] || wasAlbumFallback != fallbackRequired[2]);
        }

        private void RefreshBitmap(bool force = false)
        {
            bool changed = marquee.Advance(Time.unscaledTime);
            if (!changed && !force)
                return;
            DotMatrixFont.RasterizeInto(mask, fallbackRequired[0] ? "" : marquee[0],
                fallbackRequired[1] ? "" : marquee[1], fallbackRequired[2] ? "" : marquee[2]);
            for (int index = 0; index < pixels.Length; index++)
                pixels[index] = new Color32(255, 255, 255, mask[index]);
            texture.SetPixels32(pixels);
            texture.Apply(true, false);
            for (int index = 0; index < fallback.Length; index++)
                fallback[index].text = fallbackRequired[index] ? marquee[index] : "";
        }

        internal void Tick()
        {
            RefreshBitmap();
        }

        private void FontRebuilt(Font font)
        {
            if (fallback[0] && fallbackMaterial && font == fallback[0].font)
            {
                fallbackMaterial.mainTexture = font.material.mainTexture;
            }
        }

        public void Dispose() { Font.textureRebuilt -= FontRebuilt; }
    }
}
