using System;
using System.Linq;
using System.Reflection;
using SailwindRadio;
using SailwindRadio.Shops;
using SailwindRadio.Physical;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;
using UnityEngine;

static class Program
{
    static int checks;
    static void Check(bool value,string name){checks++;if(!value)throw new Exception(name);}
    static void Reject(Action action,string name){bool failed=false;try{action();}catch{failed=true;}Check(failed,name);}
    static int Main()
    {
        try { Run(); return 0; }
        catch(Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    static void Run()
    {
        var store=new RadioSaveStore();var arbiter=new GlobalRadioArbiter();
        store.Put(RadioRecord.Capture(10,138,new RadioState{Powered=true,PositionSeconds=22}));arbiter.Reset(store.Records);
        int sequence=8;
        int id=ShopPurchaseReservation.Reserve(store,arbiter,new RadioState{Kind=3,Bass=.8f},()=>sequence++,n=>n==8||n==9);
        Check(id==11,"reservation skips native and cached IDs");
        Check(arbiter.ActiveId==10,"unsold reservation cannot displace active radio");
        Check(store.TryGet(id,138,out var record)&&record.Kind==3&&record.Bass==.8f,"canonical state reserved before native charge");
        Check(new RadioSaveStore().Load(store.Save()),"reserved data readable");
        ShopPurchaseReservation.Cancel(store,arbiter,id);
        Check(!store.TryGet(id,138,out _)&&!arbiter.TryGet(id,out _),"cancel removes all provisional ownership");
        Check(store.TryGet(10,138,out var prior)&&prior.PositionSeconds==22,"cancel preserves existing cached state");
        int tries=0;Reject(()=>ShopPurchaseReservation.Reserve(store,arbiter,new RadioState(),()=>{tries++;return 10;},_=>false),"duplicate IDs bounded failure");
        Check(tries==64,"ID retry cap");
        Reject(()=>ShopPurchaseReservation.Reserve(store,arbiter,new RadioState{Volume=float.NaN},()=>12,_=>false),"invalid data fails precharge");
        Check(!store.TryGet(12,138,out _)&&!arbiter.TryGet(12,out _),"invalid reservation no residue");
        var future=new RadioSaveStore();future.Load("{\"Schema\":999,\"Radios\":[]}");
        Reject(()=>ShopPurchaseReservation.Reserve(future,new GlobalRadioArbiter(),new RadioState(),()=>1,_=>false),"future schema blocks purchase");
        Check(!future.Writable,"future data remains closed");
        var full=new RadioSaveStore();for(int i=1;i<=RadioSaveStore.MaximumRecords;i++)full.Put(RadioRecord.Capture(i,138,new RadioState()));
        Reject(()=>ShopPurchaseReservation.Reserve(full,new GlobalRadioArbiter(),new RadioState(),()=>9999,_=>false),"record capacity checked precharge");
        Check(full.Records.Count()==RadioSaveStore.MaximumRecords,"capacity rejection keeps existing records");
        var large=new RadioSaveStore();for(int i=1;i<=130;i++)large.Put(RadioRecord.Capture(i,138,new RadioState{TrackPath=new string('x',32768)}));
        Reject(()=>ShopPurchaseReservation.Reserve(large,new GlobalRadioArbiter(),new RadioState(),()=>9999,_=>false),"full JSON size checked precharge");
        Check(!large.TryGet(9999,138,out _),"oversized reservation rolled back");
        var clock=new ShopRestock();Check(clock.Ready,"new slot ready");clock.Spawned();Check(!clock.Ready,"occupied no duplicate");clock.Removed();clock.Removed();clock.Tick(119);Check(!clock.Ready,"restock waits120 simulationseconds");clock.Tick(float.NaN);clock.Tick(float.PositiveInfinity);clock.Tick(-1);Check(!clock.Ready,"badtime ignored");clock.Tick(1);Check(clock.Ready,"restock readyat120");
        var sale=typeof(Shopkeeper).GetMethod("SellItem",BindingFlags.Instance|BindingFlags.NonPublic);var sell=typeof(ShipItem).GetMethod("Sell");
        Check(NativeShopContract.Supports(sale,sell),"native order contract accepts sell before currency");
        Check(!NativeShopContract.Supports(typeof(Shopkeeper).GetMethod("UnsafeSale",BindingFlags.Instance|BindingFlags.NonPublic),sell),"native order contract rejects charge before sell");
        Lifecycle();
        Console.WriteLine(checks+" production shop reservation, callback and restock checks passed. Native physics and buying UI remain live checks.");
    }
    static object Call(string method,params object[] args)=>typeof(RadioShopService).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
    static void Lifecycle()
    {
        var world=new RadioWorldService();var warnings=new System.Collections.Generic.List<string>();using(var service=new RadioShopService(world,warnings.Add))
        {
            var root=new GameObject("scenery");var areaGo=new GameObject("shop");areaGo.transform.parent=root.transform;var area=areaGo.AddComponent<ShopArea>();
            var keeper=new GameObject().AddComponent<Shopkeeper>();area.Keeper=keeper;keeper.Register(area);
            var controller=world.CreateShopStock(root.transform,default,default,0);var item=controller.GetComponent<ShipItem>();var stock=controller.gameObject.AddComponent<RadioShopStock>();
            stock.Service=service;stock.Shop=area;stock.Controller=controller;controller.SetShopStock(true);
            stock.Anchor=new GameObject("price anchor").transform;item.transform.parent=stock.Anchor;
            Check(!(bool)Call("BeforeEnterBoat",item),"unsold stand stock cannot adopt boat parent");
            item.transform.parent=new GameObject("foreign parent").transform;Call("BeforePrice",item);
            Check(item.transform.parent==stock.Anchor,"native hover pricing sees repaired direct anchor");
            item.held=true;item.transform.position=new Vector3(3,0,0);Call("BeforePrice",item);
            Check(item.Returns==1&&!item.held,"held stock beyond1.8m uses native return");
            Check(!(bool)Call("BeforePurchase",new GameObject().AddComponent<Shopkeeper>(),item),"wrong keeper cannot purchase");
            world.ReadyForShop=false;Check(!(bool)Call("BeforePurchase",keeper,item),"loading blocks purchase");world.ReadyForShop=true;
            Check((bool)Call("BeforePurchase",keeper,item)&&stock.Reserved,"purchase reserves before native callback");
            Check(world.Store.Records.Count()==1&&!item.sold&&!item.GetComponent<SaveablePrefab>().registered,"unsold reservation not native registered");
            Check(!(bool)Call("BeforePurchase",keeper,item),"duplicate purchase blocked");
            Call("AfterPurchase",item,null);Check(!stock.Reserved&&world.Store.Records.Count()==0&&item.GetComponent<SaveablePrefab>().instanceId==0,"skipped native body cancels reservation");
            Check((bool)Call("BeforePurchase",keeper,item),"cancelled stock purchasable again");
            item.Sell();Call("AfterNativeSell",item);
            Check(stock.Purchased&&!controller.IsStock&&controller.InstanceId>0,"native sale promotes existing controller");
            Check((bool)Call("BeforeEnterBoat",item),"owned purchased item can board boat");
            Check(item.GetComponent<SaveablePrefab>().registered,"native sale owns stable registration");
            var error=new Exception("native UI failed");Check(ReferenceEquals(Call("AfterPurchase",item,error),error),"native exception preserved");
            Check(world.Store.Records.Count()==1&&!stock.Reserved,"post-registration error retains canonical owned item");
            Check(!(bool)Call("BeforePurchase",keeper,item),"sold item cannot be charged twice");
            var failed=world.CreateShopStock(root.transform,default,default,0);var failedItem=failed.GetComponent<ShipItem>();var failedStock=failed.gameObject.AddComponent<RadioShopStock>();
            failedStock.Service=service;failedStock.Shop=area;failedStock.Controller=failed;
            Check((bool)Call("BeforePurchase",keeper,failedItem),"second stock can reserve");
            failedItem.sold=true;Call("AfterPurchase",failedItem,new Exception("registration failed"));
            Check(!failedStock.Purchased&&world.Store.Records.Count()==1,"sold flag without native registration never promotes orphan record");
            var noOp=world.CreateShopStock(root.transform,default,default,0);var noOpItem=noOp.GetComponent<ShipItem>();var noOpStock=noOp.gameObject.AddComponent<RadioShopStock>();
            noOpStock.Service=service;noOpStock.Shop=area;noOpStock.Controller=noOp;
            Check((bool)Call("BeforePurchase",keeper,noOpItem),"no-op registration fixture reserves");noOpItem.sold=true;
            Exception cancellation=null;
            try{Call("AfterNativeSell",noOpItem);}catch(TargetInvocationException e){cancellation=e.InnerException;}
            Check(cancellation!=null&&!noOpStock.Purchased,"silent registration failure aborts before currency code");
            Check(Call("AfterPurchase",noOpItem,cancellation)==null,"controlled abort cleanup suppresses only own cancellation");
            var unrelated=new GameObject().AddComponent<ShipItem>();Check((bool)Call("BeforePurchase",keeper,unrelated),"unrelated native stock unchanged");
            UnityEngine.Object.Found.Add(area);Camera.main=new GameObject().AddComponent<Camera>();Time.unscaledTime=0;service.Tick();Time.unscaledTime=6;service.Tick();
            Check(area.itemsForSale.Count==7,"requested two radios two satellites two regular one woofer stocked");
            Check(RadioShopStand.Created==1,"exactly one display stand per vendor");
            Time.unscaledTime=12;service.Tick();Check(area.itemsForSale.Count==7,"repeat scan deduplicates vendor and stock");
            Sun.sun.localTime=20;service.Tick();Check(area.itemsForSale.All(x=>!x.gameObject.activeInHierarchy),"stock follows closed shop");
            Sun.sun.localTime=12;service.Tick();Check(area.itemsForSale.All(x=>x.gameObject.activeInHierarchy),"day reactivates same stock");
            var purchasedDisplay=area.itemsForSale[0];Check((bool)Call("BeforePurchase",keeper,purchasedDisplay),"display item purchasable");
            area.itemsForSale.Remove(purchasedDisplay);purchasedDisplay.Sell();Call("AfterNativeSell",purchasedDisplay);Call("AfterPurchase",purchasedDisplay,null);
            Check(purchasedDisplay.transform.parent==GameState.World,"native sale detaches from ephemeral stand before cleanup");
            GameState.currentlyLoading=true;service.Tick();Check(area.itemsForSale.Count==0,"world load removes only unsold stock from native list");GameState.currentlyLoading=false;
            Check(purchasedDisplay&&item,"purchased items survive stand and anchor destruction");
            RadioShopPlacement.FailSlot=3;Time.unscaledTime=20;service.Tick();Time.unscaledTime=26;service.Tick();
            Check(area.itemsForSale.Count==0,"failed fourth slot rolls back complete initial display");
            Check(GameObject.All.Where(g=>g.name=="test stand"||g.name=="Radio shop stock anchor").All(g=>g.Destroyed),"atomic rollback destroys stand and every partial anchor recursively");
            int warningCount=warnings.Count;Time.unscaledTime=32;service.Tick();Check(warnings.Count==warningCount,"unchanged blocked slot warning deduplicated across retries");
            RadioShopPlacement.FailSlot=-1;Time.unscaledTime=38;service.Tick();Check(area.itemsForSale.Count==7,"cleared blocker retries whole display successfully");
            UnityEngine.Object.Found.Clear();
        }
    }
}
