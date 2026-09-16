using System;
using System.Reflection;
using SailwindRadio.Shops;
internal static class NativeContractChecks
{
    private static int Main()
    {
        try
        {
            var sale=typeof(Shopkeeper).GetMethod("SellItem",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(ShipItem),typeof(int),typeof(int)},null);
            var sell=typeof(ShipItem).GetMethod("Sell",Type.EmptyTypes);
            if(!NativeShopContract.Supports(sale,sell)) throw new Exception("Installed native ownership/charge order is unsupported");
            if(typeof(ShopItemSpawner).GetField("priceMult").FieldType!=typeof(float)) throw new Exception("Native price multiplier changed");
            if(typeof(SaveablePrefab).GetMethod("RegisterToSave",Type.EmptyTypes)==null)throw new Exception("Native registration unavailable");
            var enterBoat=typeof(ShipItem).GetMethod("EnterBoat",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(UnityEngine.Collider)},null);
            var price=typeof(Shopkeeper).GetMethod("GetPrice",BindingFlags.Instance|BindingFlags.NonPublic,null,new[]{typeof(ShipItem)},null);
            if(enterBoat==null||enterBoat.ReturnType!=typeof(void))throw new Exception("Native boat entry signature changed");
            if(price==null||price.ReturnType!=typeof(int))throw new Exception("Native pricing signature changed");
            Console.WriteLine("5 production contract checks passed against installed native assembly on Unity Mono. No game code executed.");
            return 0;
        }
        catch(Exception e){Console.Error.WriteLine(e);return 1;}
    }
}
