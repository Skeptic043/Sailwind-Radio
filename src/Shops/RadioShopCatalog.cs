namespace SailwindRadio.Shops
{
    internal static class RadioShopCatalog
    {
        internal static readonly DeviceRole[] StockRoles =
        {
            DeviceRole.Woofer, DeviceRole.Speaker, DeviceRole.Speaker, DeviceRole.Radio, DeviceRole.Radio, DeviceRole.SmallSpeaker, DeviceRole.SmallSpeaker
        };
        internal static int StallFamily(int island) => RadioStalls.TryGet(island, out var stall) ? stall.Family : DeviceKind.Walnut;
        // Each regional stall sells its own family's radio and speakers. An island without a regional family gets the walnut set.
        internal static int StockKind(int island, int slot) => DeviceKind.Of(StallFamily(island), StockRoles[slot]);
        internal static int BasePrice(int kind)
        {
            if (!DeviceKind.IsValid(kind))
                return 0;
            var role = DeviceKind.Role(kind);
            return role == DeviceRole.Radio ? 789 : role == DeviceRole.SmallSpeaker ? 526 : role == DeviceRole.Speaker ? 1579 : 2632;
        }
    }
}
