namespace SailwindRadio.Shops
{
    internal static class RadioShopCatalog
    {
        internal static readonly int[] StockKinds = { 3, 2, 2, 0, 0, 1, 1 };
        internal static int BasePrice(int kind) => kind == 0 ? 789 : kind == 1 ? 526 : kind == 2 ? 1579 : kind == 3 ? 2632 : 0;
    }
}
