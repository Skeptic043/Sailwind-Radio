using System;
using System.Reflection;

namespace SailwindRadio.Shops
{
    // Required merchant fields for Radio stock presentation.
    internal static class NativeShopContract
    {
        internal static readonly FieldInfo KeeperRegion = typeof(Shopkeeper).GetField("parentRegion", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static readonly FieldInfo KeeperEconomy = typeof(Shopkeeper).GetField("economy", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static readonly FieldInfo KeeperHome = typeof(Shopkeeper).GetField("homePos", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static readonly FieldInfo KeeperShop = typeof(Shopkeeper).GetField("shop", BindingFlags.Instance | BindingFlags.NonPublic);
        internal static readonly FieldInfo AreaKeeper = typeof(ShopArea).GetField("keeper", BindingFlags.Instance | BindingFlags.NonPublic);

        internal static bool KeeperReady(ShopArea area)
        {
            var keeper = area.GetShopkeeper();
            if (!keeper) return false;
            return KeeperRegion?.GetValue(keeper) is Region && ReferenceEquals(KeeperShop?.GetValue(keeper), area);
        }
    }
}
