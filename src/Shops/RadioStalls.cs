using UnityEngine;

namespace SailwindRadio.Shops
{
    internal sealed class RadioStall
    {
        internal readonly int Island;
        internal readonly int Family;
        // Scenery-local stand anchor and yaw.
        internal readonly Vector3 SceneryAnchor;
        internal readonly float Yaw;
        // Radius of the keeper's held-goods resale trigger.
        internal readonly float SaleRadius;
        // Gold Rock's wider native counter: the stand copies the neighbouring native stall, and the Wolfer stands farther out.
        internal readonly bool WideCounter;
        // The stand settles onto static support up to .30 m below the authored height.
        internal readonly bool SettlesOnSupport;
        // Visible ground offset beneath the support the placement ray finds.
        internal readonly float GroundVisualOffset;
        internal RadioStall(int island, int family, Vector3 sceneryAnchor, float yaw, float saleRadius = RadioStalls.DefaultSaleRadius,
            bool wideCounter = false, bool settlesOnSupport = false, float groundVisualOffset = 0f)
        {
            Island = island; Family = family; SceneryAnchor = sceneryAnchor; Yaw = yaw; SaleRadius = saleRadius;
            WideCounter = wideCounter; SettlesOnSupport = settlesOnSupport; GroundVisualOffset = groundVisualOffset;
        }
    }

    internal static class RadioStalls
    {
        internal const int GoldRock = 1, DragonCliffs = 9, FortAestrin = 15, Chronos = 25;
        internal const float DefaultSaleRadius = 1.75f;
        // Gold Rock's scenery has half scale, so one world metre is two local metres there. Its wider counter
        // needs the native 2 m reach to accept held goods from its customer edge, and its visible sand and the
        // keeper's feet sit .10 m below the static dock support. Chronos reuses the empty native tent
        // "market stall medi 3 (3)" at (494.88, 504.69). The stand turns with its canopy, so the keeper behind
        // faces into the square like the neighbouring keepers, and sits half the 1.13 m counter depth toward the
        // customers from the tent's pivot, in line with those vendors. Chronos sells the walnut set. Each keeper
        // trades in the currency of the region the native neighbouring keeper uses.
        private static readonly RadioStall[] Table =
        {
            new RadioStall(GoldRock, DeviceKind.AlAnkh, new Vector3(1596.6110f, 3.1f, -436.3066f), 58.4f,
                saleRadius: 2f, wideCounter: true, settlesOnSupport: true, groundVisualOffset: -.10f),
            new RadioStall(DragonCliffs, DeviceKind.Emerald, new Vector3(-104.6f, 2.1f, -530f), 45f),
            new RadioStall(FortAestrin, DeviceKind.Aestrin, new Vector3(-136.97f, 2.3f, 43.95f), 180f, settlesOnSupport: true),
            new RadioStall(Chronos, DeviceKind.Walnut, new Vector3(495.25f, 5.55f, 505.117f), -139.1f, settlesOnSupport: true)
        };
        internal static bool TryGet(int island, out RadioStall stall)
        {
            foreach (var candidate in Table)
                if (candidate.Island == island) { stall = candidate; return true; }
            stall = null;
            return false;
        }
    }
}
