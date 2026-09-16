using SailwindRadio.Physical;
using UnityEngine;

namespace SailwindRadio.Shops
{
    // Ownership marker is never serialized and exists only on stock created by this service.
    public sealed class RadioShopStock : MonoBehaviour
    {
        internal RadioShopService Service;
        internal ShopArea Shop;
        internal RadioItemController Controller;
        internal Transform Anchor;
        internal bool Reserved;
        internal bool Purchased;
    }
}
