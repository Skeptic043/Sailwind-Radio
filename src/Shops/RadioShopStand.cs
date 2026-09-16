using UnityEngine;

namespace SailwindRadio.Shops
{
    internal sealed class RadioShopStand : MonoBehaviour
    {
        private Material wood;
        internal static RadioShopStand Create(Transform scenery, Vector3 position, Quaternion rotation)
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
                stand.wood = new Material(shader) { color = new Color(.23f, .125f, .052f) };
                stand.wood.SetFloat("_Glossiness", .16f);
                stand.Board("Upper shelf", new Vector3(0, 1.02f, 0), new Vector3(2.35f, .07f, .92f));
                stand.Board("Lower shelf", new Vector3(0, .13f, 0), new Vector3(2.35f, .06f, .92f));
                for (int x = -1; x <= 1; x += 2)
                    for (int z = -1; z <= 1; z += 2)
                        stand.Board("Wooden leg", new Vector3(x * 1.1f, .49f, z * .39f), new Vector3(.085f, 1.06f, .085f));
                stand.Board("Rear brace", new Vector3(0, .52f, .40f), new Vector3(2.2f, .085f, .07f));
                return stand;
            }
            catch { Destroy(root); throw; }
        }
        private void Board(string name, Vector3 center, Vector3 size)
        {
            var board = GameObject.CreatePrimitive(PrimitiveType.Cube);
            board.name = name;
            board.transform.SetParent(transform, false);
            board.transform.localPosition = center;
            board.transform.localScale = size;
            board.GetComponent<Renderer>().sharedMaterial = wood;
        }
        private void OnDestroy() { if (wood) Destroy(wood); }
    }
}
