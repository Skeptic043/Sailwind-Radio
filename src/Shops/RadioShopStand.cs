using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Shops
{
    internal sealed class RadioShopStand : MonoBehaviour
    {
        private Material wood;
        private Material platformWood;
        private Material canvas;
        private Material roofWood;
        private readonly List<Renderer> structureRenderers = new List<Renderer>();
        private readonly List<Collider> structureColliders = new List<Collider>();
        internal void SetVisible(bool visible)
        {
            foreach (var renderer in structureRenderers) if (renderer) renderer.enabled = visible;
            foreach (var collider in structureColliders) if (collider) collider.enabled = visible;
        }
        internal static RadioShopStand Create(Transform scenery, Vector3 position, Quaternion rotation, int island)
        {
            var root = new GameObject("Radio merchant display");
            try
            {
                root.transform.SetParent(scenery, false);
                root.transform.position = position;
                root.transform.rotation = rotation;
                var scale = scenery.lossyScale;
                root.transform.localScale = new Vector3(1 / scale.x, 1 / scale.y, 1 / scale.z);
                var stand = root.AddComponent<RadioShopStand>();
                var shader = Shader.Find("Standard");
                if (!shader) throw new System.InvalidOperationException("Display stand shader unavailable");
                // The game's market materials are texture atlases. Applying one
                // directly to a primitive cube selects unrelated atlas regions,
                // so keep these town-specific finishes owned by the display.
                stand.wood = new Material(shader) { color = island == 9 ?
                    new Color(.31f, .18f, .14f) : island == 15 ?
                    new Color(.37f, .29f, .23f) : new Color(.39f, .24f, .15f) };
                stand.wood.SetFloat("_Glossiness", .16f);
                if(island==9)
                {
                    stand.platformWood=new Material(shader) { color=new Color(.34f, .23f, .18f) };
                    stand.platformWood.SetFloat("_Glossiness",.10f);
                    stand.roofWood=new Material(shader) { color=new Color(.25f, .14f, .11f) };
                    stand.roofWood.SetFloat("_Glossiness",.10f);
                }
                stand.canvas = new Material(shader) { color = island == 9 ?
                    new Color(.36f, .20f, .16f) : new Color(.42f, .21f, .15f) };
                stand.canvas.SetFloat("_Glossiness", .05f);
                // A flat low sales table lets every physical display item rest on
                // the same level. Each item retains its owned shop stock anchor.
                stand.Board("Flat display top", new Vector3(0, .78f, 0), new Vector3(2.36f, .075f, 1.13f));
                stand.Board("Front retaining lip", new Vector3(0, .78f, -.55f), new Vector3(2.38f, .09f, .055f));
                stand.Board("Rear retaining lip", new Vector3(0, .78f, .55f), new Vector3(2.38f, .09f, .055f));
                for (int x = -1; x <= 1; x += 2)
                    stand.Board("Side retaining lip", new Vector3(x * 1.17f, .78f, 0), new Vector3(.055f, .09f, 1.1f));
                for (int x = -1; x <= 1; x += 2)
                    for (int z = -1; z <= 1; z += 2)
                        stand.Board("Wooden leg", new Vector3(x * 1.06f, .38f, z * .44f),
                            new Vector3(.105f, .76f, .105f));
                stand.Board("Rear brace", new Vector3(0, .37f, .45f), new Vector3(2.2f, .09f, .08f));
                stand.Board("Front brace", new Vector3(0, .26f, -.45f), new Vector3(2.2f, .09f, .08f));
                if (island == 9)
                {
                    // Broad planking covers the customer's approach and the
                    // Wolfer on the right, then continues toward the house.
                    for (int i = -5; i <= 7; i++)
                        stand.Board("Dragon Cliffs platform plank", new Vector3(i * .33f, -.045f, .7f), new Vector3(.31f, .09f, 3.8f),stand.platformWood);
                    for (int z = -1; z <= 1; z += 2)
                        stand.Board("Platform cross beam", new Vector3(.33f, -.13f, .7f + z * 1.8f), new Vector3(4.28f, .15f, .1f),stand.platformWood);
                }
                if (island == 9 || island == 15)
                {
                    for (int x = -1; x <= 1; x += 2)
                        for (int z = -1; z <= 1; z += 2)
                            stand.Board("Canopy post", new Vector3(x * 1.34f, 1.25f, .35f + z * 1.05f), new Vector3(.065f, 2.5f, .065f));
                    if(island==9)
                    {
                        // Native Dragon Cliffs stalls use a dark wooden awning
                        // with only a short fabric valance across the front.
                        for (int i = -3; i <= 3; i++)
                            stand.Board("Dragon Cliffs awning plank", new Vector3(i * .41f, 2.53f, .35f),
                                new Vector3(.405f, .045f, 2.35f), stand.roofWood);
                        stand.Board("Dragon Cliffs hanging front fabric", new Vector3(0, 2.38f, -.83f),
                            new Vector3(2.9f, .28f, .045f), stand.canvas);
                        stand.Board("Dragon Cliffs front wood rail", new Vector3(0, 2.53f, -.83f),
                            new Vector3(2.9f, .075f, .065f), stand.roofWood);
                    }
                    else
                    {
                        stand.Board("Canvas canopy left", new Vector3(-.72f, 2.52f, .35f), new Vector3(1.5f, .055f, 2.35f), stand.canvas)
                            .transform.localRotation = Quaternion.Euler(0, 0, 7);
                        stand.Board("Canvas canopy right", new Vector3(.72f, 2.52f, .35f), new Vector3(1.5f, .055f, 2.35f), stand.canvas)
                            .transform.localRotation = Quaternion.Euler(0, 0, -7);
                    }
                }
                return stand;
            }
            catch { Destroy(root); throw; }
        }
        private GameObject Board(string name, Vector3 center, Vector3 size, Material material = null)
        {
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = name;
            board.transform.SetParent(transform, false);
            board.transform.localPosition = center;
            board.transform.localScale = size;
            var renderer = board.GetComponent<Renderer>();
            renderer.sharedMaterial = material ? material : wood;
            structureRenderers.Add(renderer);
            structureColliders.Add(board.GetComponent<Collider>());
            return board;
        }
        private void OnDestroy()
        {
            if (wood) Destroy(wood);
            if (platformWood) Destroy(platformWood);
            if (roofWood) Destroy(roofWood);
            if (canvas) Destroy(canvas);
        }
    }
}
