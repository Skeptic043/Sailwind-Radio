using System;
using System.Reflection;

namespace SailwindRadio.Shops
{
    // Required merchant fields for Radio stock presentation.
    internal static class NativeShopContract
    {
        internal static bool KeeperReady(ShopArea area)
        {
            var keeper=area.GetShopkeeper();
            if(!keeper) return false;
            var region=typeof(Shopkeeper).GetField("parentRegion",BindingFlags.Instance|BindingFlags.NonPublic);
            var shop=typeof(Shopkeeper).GetField("shop",BindingFlags.Instance|BindingFlags.NonPublic);
            return region?.GetValue(keeper) is Region && ReferenceEquals(shop?.GetValue(keeper),area);
        }
    }
}
