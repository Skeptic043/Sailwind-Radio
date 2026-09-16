using UnityEngine;

namespace SailwindRadio.Shops
{
    internal static class RadioShopCatalog
    {
        internal static readonly int[] StockKinds = { 3, 2, 2, 0, 0, 1, 1 };
        internal static int BasePrice(int kind) => kind == 0 ? 789 : kind == 1 ? 526 : kind == 2 ? 1579 : kind == 3 ? 2632 : 0;

        internal static bool Matches(ShopArea area)
        {
            if (!area || !area.transform.parent) return false;
            var scenery = area.transform.parent.GetComponent<IslandSceneryScene>();
            if (!scenery) return false;
            int island = scenery.parentIslandIndex;
            string name = island == 1 ? "shop (7)" : island == 9 ? "shop area (3)" : island == 15 ? "shop area (1)" : "";
            Vector3 position = island == 1 ? new Vector3(1521.66992f, 8.4f, -383.17816f) :
                island == 9 ? new Vector3(-90.43f, 4.65f, -544.32f) : new Vector3(-74.84f, 4.15f, 43.78f);
            Vector3 scale = island == 1 ? new Vector3(13.24279f,10.41362f,12.63754f) :
                island == 9 ? new Vector3(6.47693f,4.90124f,4.98642f) : new Vector3(7.32511f,8.45885f,8.45885f);
            var box = area.GetComponent<BoxCollider>();
            return name.Length > 0 && area.name == name && (area.transform.localPosition-position).sqrMagnitude < .0025f &&
                (area.transform.localScale-scale).sqrMagnitude < .0025f && box && box.enabled && box.isTrigger &&
                (box.size-Vector3.one).sqrMagnitude < .0001f && box.center.sqrMagnitude < .0001f &&
                Vector3.Dot(area.transform.up, Vector3.up) > .999f;
        }
    }
}
