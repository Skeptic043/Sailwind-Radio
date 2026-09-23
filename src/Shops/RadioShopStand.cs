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
        internal bool UsesNativeGoldRockCounter { get; private set; }
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
                // The neighboring Gold Rock empty stall is one native mesh for
                // the counter, frame and cream canopy. Keep an independent
                // Radio-owned copy so its source remains untouched.
                var nativeGoldRockStall = island == 1 && stand.NativeGoldRockStall(scenery);
                stand.UsesNativeGoldRockCounter = nativeGoldRockStall;
                if (!nativeGoldRockStall)
                {
                    // A flat low sales table lets every physical display item
                    // rest on the same level at its owned shop stock anchor.
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
                }
                if (island == 9 && !stand.NativeDragonCliffsDeck(scenery))
                {
                    // The native scenery mesh is unavailable after a scene or
                    // asset change. Keep the stand usable until that is fixed.
                    for (int i = -5; i <= 7; i++)
                        stand.Board("Dragon Cliffs platform plank", new Vector3(i * .33f, -.045f, .7f), new Vector3(.31f, .09f, 3.8f),stand.platformWood);
                    for (int z = -1; z <= 1; z += 2)
                        stand.Board("Platform cross beam", new Vector3(.33f, -.13f, .7f + z * 1.8f), new Vector3(4.28f, .15f, .1f),stand.platformWood);
                    for (int i = -5; i <= 7; i++)
                        stand.Board("Rear Dragon Cliffs platform plank", new Vector3(i * .33f, -.045f, 4.9f), new Vector3(.31f, .09f, 3.8f),stand.platformWood);
                    for (int i = 4; i <= 16; i++)
                        stand.Board("Left Dragon Cliffs platform plank", new Vector3(i * .33f, -.05f, .7f), new Vector3(.31f, .09f, 3.8f),stand.platformWood);
                    for (int z = -1; z <= 1; z += 2)
                        stand.Board("Left platform cross beam", new Vector3(3.2f, -.135f, .7f + z * 1.8f), new Vector3(4.2f, .15f, .1f),stand.platformWood);
                }
                var nativeDragonCliffsCover = island == 9 && stand.NativeDragonCliffsVisual(scenery, "east_market_roof", "east_market_roof", "Dragon Cliffs native canopy", new Vector3(0, 2.05f, 0), .65f, false);
                if ((island == 9 && !nativeDragonCliffsCover) || island == 15)
                {
                    for (int x = -1; x <= 1; x += 2)
                        for (int z = -1; z <= 1; z += 2)
                        {
                            var height = island == 9 ? (z < 0 ? 2.36f : 2.66f) : 2.5f;
                            stand.Board("Canopy post", new Vector3(x * 1.34f, height / 2, .35f + z * 1.05f),
                                new Vector3(.065f, height, .065f));
                        }
                    if(island==9)
                    {
                        // The neighboring stalls have a broad, slightly pitched
                        // reddish-brown cover rather than exposed roof planks.
                        for (int i = -1; i <= 1; i++)
                            stand.Board("Dragon Cliffs canvas roof panel", new Vector3(i * .98f, 2.53f, .35f),
                                new Vector3(.99f, .04f, 2.45f), stand.canvas)
                                .transform.localRotation = Quaternion.Euler(-7f, 0, 0);
                        stand.Board("Dragon Cliffs hanging front fabric", new Vector3(0, 2.29f, -.88f),
                            new Vector3(2.9f, .18f, .045f), stand.canvas);
                        stand.Board("Dragon Cliffs front wood rail", new Vector3(0, 2.39f, -.88f),
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
        private bool NativeGoldRockStall(Transform scenery)
        {
            MeshFilter source = null;
            MeshRenderer sourceRenderer = null;
            var bestDistance = 30f;
            foreach (var filter in scenery.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter || !filter.sharedMesh || filter.name != "market_stall (2)" ||
                    filter.sharedMesh.name != "market_stall_001") continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (!renderer || renderer.sharedMaterials.Length != 1 || !renderer.sharedMaterials[0] ||
                    renderer.sharedMaterials[0].name != "buildings_A_paint") continue;
                var distance = Vector3.Distance(filter.transform.position, transform.position);
                if (distance >= bestDistance) continue;
                source = filter;
                sourceRenderer = renderer;
                bestDistance = distance;
            }
            if (!source) return false;
            var copy = new GameObject("Gold Rock native covered market stall");
            copy.transform.SetParent(transform, false);
            // The native mesh's Z axis is up, Y runs along the counter, and X
            // runs from the keeper toward the customer. Its half-size scene
            // scale yields a 2.7 m counter with 1.3 m depth and a full canopy.
            copy.transform.localRotation = Quaternion.Euler(0, 180f, 0) * Quaternion.LookRotation(Vector3.up, Vector3.left);
            copy.transform.localScale = source.transform.lossyScale;
            // The mesh's horizontal counter spans raw X [-1.06, .20]. After
            // the half-scale and 180-degree flip, a .24 m Z offset places its
            // top from about -.29 to +.34 m in stand space. The canopy still
            // reaches the keeper at +1.04 m.
            copy.transform.localPosition = new Vector3(0, 1.045f, .24f);
            copy.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
            var rendererCopy = copy.AddComponent<MeshRenderer>();
            rendererCopy.sharedMaterials = sourceRenderer.sharedMaterials;
            structureRenderers.Add(rendererCopy);
            var support = new GameObject("Gold Rock stock support");
            support.transform.SetParent(transform, false);
            // The native tabletop rises toward the rear stock row. Keep the
            // simple physics support close beneath it rather than letting
            // gravity settle stock into the visible mesh.
            support.transform.localPosition = new Vector3(0, .897f, .025f);
            support.transform.localRotation = Quaternion.Euler(-4.3f, 0, 0);
            var collider = support.AddComponent<BoxCollider>();
            collider.size = new Vector3(2.7f, .06f, .65f);
            structureColliders.Add(collider);
            return true;
        }
        private bool NativeDragonCliffsDeck(Transform scenery)
        {
            if (!NativeDragonCliffsVisual(scenery, "east_dock", "east_dock", "Dragon Cliffs native plank deck", new Vector3(0, .128f, .7f), .7f, true)) return false;
            NativeDragonCliffsVisual(scenery, "east_dock", "east_dock", "Dragon Cliffs rear native plank deck", new Vector3(0, .128f, 4.9f), .7f, true);
            // The Wolfer stands to the keeper's left (+X). Overlap this row
            // slightly with the first so the visible planks cover its feet.
            NativeDragonCliffsVisual(scenery, "east_dock", "east_dock", "Dragon Cliffs left native plank deck", new Vector3(3.2f, .125f, .7f), .7f, true);
            return true;
        }
        private bool NativeDragonCliffsVisual(Transform scenery, string sourceName, string meshName, string copyName, Vector3 position, float sizeScale, bool solid)
        {
            // Use an already-loaded native mesh with its matching atlas material
            // and UVs. The source stall stays untouched; this owned object follows
            // only Radio's scenery lifetime. Source names can acquire suffixes as
            // Unity duplicates them, so select the closest matching direct child.
            MeshFilter source = null;
            MeshRenderer sourceRenderer = null;
            float bestDistance = 30f;
            foreach (var filter in scenery.GetComponentsInChildren<MeshFilter>(true))
            {
                if (!filter || filter.transform.parent != scenery || !filter.sharedMesh ||
                    !filter.name.StartsWith(sourceName, System.StringComparison.Ordinal) ||
                    filter.sharedMesh.name != meshName) continue;
                var renderer = filter.GetComponent<MeshRenderer>();
                if (!renderer || renderer.sharedMaterials.Length != 1 || !renderer.sharedMaterials[0] ||
                    renderer.sharedMaterials[0].name != "buildings_E_paint") continue;
                var distance = Vector3.Distance(filter.transform.position, transform.position);
                if (distance >= bestDistance) continue;
                source = filter;
                sourceRenderer = renderer;
                bestDistance = distance;
            }
            if (!source) return false;
            var copy = new GameObject(copyName);
            copy.transform.SetParent(transform, false);
            copy.transform.localPosition = position;
            // Keep the native panel's own pitch and axis mapping. The stand root
            // has unit world scale even when an island scenery root does not.
            copy.transform.rotation = Quaternion.AngleAxis(90f, Vector3.up) * source.transform.rotation;
            // The neighboring roof and deck are about 6 m across. Scale their
            // native geometry to this counter while preserving its UV mapping.
            copy.transform.localScale = source.transform.lossyScale * sizeScale;
            copy.AddComponent<MeshFilter>().sharedMesh = source.sharedMesh;
            var rendererCopy = copy.AddComponent<MeshRenderer>();
            rendererCopy.sharedMaterials = sourceRenderer.sharedMaterials;
            structureRenderers.Add(rendererCopy);
            if (solid)
            {
                // Native render meshes can be marked non-readable for collision
                // cooking. A thin owned box supports items without that hazard.
                var support = new GameObject("Dragon Cliffs deck support");
                support.transform.SetParent(transform, false);
                support.transform.localPosition = new Vector3(position.x, -.03f, position.z);
                var collider = support.AddComponent<BoxCollider>();
                collider.size = new Vector3(4.2f, .06f, 4.2f);
                structureColliders.Add(collider);
            }
            return true;
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
