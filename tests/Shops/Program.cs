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
        var correct=typeof(RadioShopService).GetMethod("CorrectRegion",BindingFlags.Static|BindingFlags.NonPublic);
        Check((bool)correct.Invoke(null,new object[]{new Region{portRegion=PortRegion.alankh},1}),"Gold Rock maps to Al'Ankh currency");
        Check((bool)correct.Invoke(null,new object[]{new Region{portRegion=PortRegion.emerald},9}),"Dragon Cliffs maps to Emerald currency");
        Check((bool)correct.Invoke(null,new object[]{new Region{portRegion=PortRegion.medi},15}),"Fort Aestrin maps to Aestrin currency");
        Check(!(bool)correct.Invoke(null,new object[]{new Region{portRegion=PortRegion.none},15}),"Gold or no-port region cannot stock a capital merchant");
        var nativeName=typeof(RadioShopService).GetMethod("NativeKeeperName",BindingFlags.Static|BindingFlags.NonPublic);
        Check((bool)nativeName.Invoke(null,new object[]{"shopkeeper (12)"}),"serialized built-in NPC name accepted");
        Check(!(bool)nativeName.Invoke(null,new object[]{"shopkeeper (12)(Clone)"}),"another mod's cloned NPC name is not used as native template");
        var findRegion=typeof(RadioShopService).GetMethod("FindRegionAt",BindingFlags.Static|BindingFlags.NonPublic);
        var regionA=new GameObject("Emerald region A");var emeraldA=regionA.AddComponent<Region>();emeraldA.portRegion=PortRegion.emerald;
        var regionB=new GameObject("Emerald region B");var emeraldB=regionB.AddComponent<Region>();emeraldB.portRegion=PortRegion.emerald;
        UnityEngine.Physics.Nearby=new[]{regionA.AddComponent<Collider>()};
        Check(ReferenceEquals(findRegion.Invoke(null,new object[]{new Vector3(),9}),emeraldA),"only a nearby matching region can supply currency");
        UnityEngine.Physics.Nearby=new[]{regionA.GetComponent<Collider>(),regionB.AddComponent<Collider>()};
        Check(findRegion.Invoke(null,new object[]{new Vector3(),9})==null,"ambiguous nearby same-currency regions cannot be chosen arbitrarily");
        UnityEngine.Physics.Nearby=Array.Empty<Collider>();
        Lifecycle();
        Console.WriteLine(checks+" production shop reservation, callback and restock checks passed. Native physics and buying UI remain live checks.");
    }
    static object Call(string method,params object[] args)=>typeof(RadioShopService).GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);
    static void Lifecycle()
    {
        var world=new RadioWorldService();var warnings=new System.Collections.Generic.List<string>();using(var service=new RadioShopService(world,warnings.Add))
        {
            var root=new GameObject("scenery");var areaGo=new GameObject("shop");areaGo.transform.parent=root.transform;var area=areaGo.AddComponent<ShopArea>();area.itemsForSale=new System.Collections.Generic.List<ShipItem>();
            var keeper=new GameObject().AddComponent<Shopkeeper>();area.Keeper=keeper;keeper.Register(area);
            var controller=world.CreateShopStock(root.transform,default,default,0);var item=controller.GetComponent<ShipItem>();var stock=controller.gameObject.AddComponent<RadioShopStock>();
            stock.Service=service;stock.Shop=area;stock.Controller=controller;stock.PurchaseEnabled=true;controller.SetShopStock(true);
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
            failedStock.Service=service;failedStock.Shop=area;failedStock.Controller=failed;failedStock.PurchaseEnabled=true;
            Check((bool)Call("BeforePurchase",keeper,failedItem),"second stock can reserve");
            failedItem.sold=true;Call("AfterPurchase",failedItem,new Exception("registration failed"));
            Check(!failedStock.Purchased&&world.Store.Records.Count()==1,"sold flag without native registration never promotes orphan record");
            var noOp=world.CreateShopStock(root.transform,default,default,0);var noOpItem=noOp.GetComponent<ShipItem>();var noOpStock=noOp.gameObject.AddComponent<RadioShopStock>();
            noOpStock.Service=service;noOpStock.Shop=area;noOpStock.Controller=noOp;noOpStock.PurchaseEnabled=true;
            Check((bool)Call("BeforePurchase",keeper,noOpItem),"no-op registration fixture reserves");noOpItem.sold=true;
            Exception cancellation=null;
            try{Call("AfterNativeSell",noOpItem);}catch(TargetInvocationException e){cancellation=e.InnerException;}
            Check(cancellation!=null&&!noOpStock.Purchased,"silent registration failure aborts before currency code");
            Check(Call("AfterPurchase",noOpItem,cancellation)==null,"controlled abort cleanup suppresses only own cancellation");
            var unrelated=new GameObject().AddComponent<ShipItem>();Check((bool)Call("BeforePurchase",keeper,unrelated),"unrelated native stock unchanged");
            Region.DefaultPortRegion=PortRegion.none;
            var scenery = root.AddComponent<IslandSceneryScene>();scenery.parentIslandIndex=9;
            var template=new GameObject("shopkeeper (3)");template.transform.SetParent(scenery.transform,false);
            var templateKeeper=template.AddComponent<Shopkeeper>();template.AddComponent<Renderer>();template.AddComponent<SphereCollider>().isTrigger=true;
            var templateChild=new GameObject("Modular NPC");templateChild.transform.SetParent(template.transform,false);templateChild.AddComponent<Collider>().isTrigger=true;
            var probeNpc=new GameObject("trigger hierarchy probe");var rootTrigger=probeNpc.AddComponent<SphereCollider>();rootTrigger.isTrigger=true;
            var childNpc=new GameObject("Modular NPC");childNpc.transform.SetParent(probeNpc.transform,false);
            var childTrigger=childNpc.AddComponent<Collider>();childTrigger.isTrigger=true;
            var solidNpc=new GameObject("solid body");solidNpc.transform.SetParent(probeNpc.transform,false);
            var solidCollider=solidNpc.AddComponent<Collider>();
            typeof(RadioShopService).GetMethod("ConfigureMerchantTriggers",BindingFlags.Static|BindingFlags.NonPublic)
                .Invoke(null,new object[]{probeNpc,9});
            Check(rootTrigger.enabled&&rootTrigger.radius==1.75f&&!childTrigger.enabled&&solidCollider.enabled,
                "owned NPC keeps counter resale trigger but closes large child trigger and preserves solid collision");
            var goldProbe=new GameObject("Gold Rock trigger probe");
            var goldRoot=goldProbe.AddComponent<SphereCollider>();goldRoot.isTrigger=true;
            var goldChild=new GameObject("Modular NPC");goldChild.transform.SetParent(goldProbe.transform,false);
            var goldChildTrigger=goldChild.AddComponent<Collider>();goldChildTrigger.isTrigger=true;
            typeof(RadioShopService).GetMethod("ConfigureMerchantTriggers",BindingFlags.Static|BindingFlags.NonPublic)
                .Invoke(null,new object[]{goldProbe,1});
            Check(goldRoot.enabled&&goldRoot.radius==2f&&!goldChildTrigger.enabled,
                "Gold Rock keeps native 2 m resale reach across its wider counter while closing broad child trigger");
            var distantTemplate=new GameObject("shopkeeper (12)");distantTemplate.transform.SetParent(scenery.transform,false);
            var distantKeeper=distantTemplate.AddComponent<Shopkeeper>();distantTemplate.AddComponent<Renderer>();
            UnityEngine.Object.Found.Add(scenery);Camera.main=new GameObject().AddComponent<Camera>();
            UnityEngine.Object.Found.Add(distantKeeper);UnityEngine.Object.Found.Add(templateKeeper);
            RadioStandLayout.TryAnchor(9,out var previewAnchor,out _);Camera.main.transform.position=previewAnchor;
            template.transform.position=previewAnchor+new Vector3(2,0,0);
            distantTemplate.transform.position=previewAnchor+new Vector3(20,0,0);
            template.SetActive(false); // Native ShopArea may close this template at night.
            var chooseTemplate=typeof(RadioShopService).GetMethod("FindNativeMerchant",BindingFlags.Static|BindingFlags.NonPublic);
            Check(ReferenceEquals(chooseTemplate.Invoke(null,new object[]{scenery,previewAnchor}),templateKeeper),
                "inactive nearby native NPC remains an eligible visual template");
            world.ReadyForShop=false;world.ReadyToStageShop=false;Time.unscaledTime=0;service.Tick();
            Check(RadioShopStand.Created==1,"one owned preview stand per capital scenery");
            Check(area.itemsForSale.Count==0,"owned merchant never stocks existing vendor");
            var ownArea=GameObject.All.Single(g=>g.name=="Radio merchant area").GetComponent<ShopArea>();
            Check(ownArea!=null&&ownArea.GetShopkeeper()!=keeper,"independent merchant and area created");
            var ownStand=ownArea.transform.parent.GetComponent<RadioShopStand>();
            Check(!ownStand.Visible,"stall is constructed hidden before native save readiness");
            Check(ownArea.itemsForSale!=null,"dynamically constructed native ShopArea gets an initialized stock list before trigger activation");
            var probe=new GameObject("trigger probe");probe.AddComponent<ShipItem>();var probeCollider=probe.AddComponent<Collider>();
            typeof(ShopArea).GetMethod("OnTriggerEnter",BindingFlags.Instance|BindingFlags.NonPublic).Invoke(ownArea,new object[]{probeCollider});
            Check(ownArea.itemsForSale.Count==1,"native shop trigger can inspect a non-null stock list");
            ownArea.itemsForSale.Clear();
            Check(ownArea.GetShopkeeper().transform.parent==scenery.transform,"native merchant is direct scenery child for economy Start");
            var ownNpc=ownArea.GetShopkeeper().gameObject;
            Check(ownNpc!=template&&ownNpc.GetComponent<Renderer>()!=null,"owned merchant clones the native NPC visual without moving the template");
            Check(!ownNpc.GetComponent<SphereCollider>().enabled&&ownNpc.GetComponent<SphereCollider>().radius==1.75f&&
                !ownNpc.GetComponentsInChildren<Collider>(true).Single(c=>c.transform!=ownNpc.transform).enabled&&
                template.GetComponent<SphereCollider>().enabled&&template.GetComponent<SphereCollider>().radius==2f,
                "hidden owned keeper closes resale trigger and large child trigger without changing native source");
            Check(!ownNpc.GetComponent<Renderer>().enabled,"owned merchant remains hidden until stock can be attempted");
            Time.unscaledTime=6;service.Tick();
            Check(!ownStand.Visible&&!ownNpc.GetComponent<Renderer>().enabled&&ownArea.itemsForSale.Count==0,
                "save-readiness delay keeps the prebuilt shop hidden instead of revealing it in stages");
            Check(template.transform.parent==scenery.transform&&!template.Destroyed&&!template.activeInHierarchy,
                "original night-closed native NPC remains untouched");
            Check(!(bool)Call("BeforeAreaEnter",area,ownNpc.GetComponent<Collider>()),"owned NPC cannot register with a nearby base-game shop area");
            Check(ownArea.itemsForSale.Count==0,"invalid native port region keeps owned shop unstocked");
            var nativeRegion=(Region)typeof(Shopkeeper).GetField("parentRegion",BindingFlags.Instance|BindingFlags.NonPublic).GetValue(ownArea.GetShopkeeper());
            nativeRegion.portRegion=PortRegion.emerald;
            ownNpc.GetComponentsInChildren<Collider>(true).Single(c=>c.transform!=ownNpc.transform).enabled=true;
            Sun.sun.localTime=12;RadioShopPlacement.FailSlot=6;
            world.ReadyToStageShop=true;Time.unscaledTime=12;service.Tick();Check(RadioShopStand.Created==1,"repeat scan deduplicates owned merchant");
            Check(!ownNpc.GetComponentsInChildren<Collider>(true).Single(c=>c.transform!=ownNpc.transform).enabled,
                "late native NPC initialization cannot reopen broad child sale trigger");
            Check(ownStand.Visible&&ownNpc.GetComponent<Renderer>().enabled&&ownNpc.GetComponent<SphereCollider>().enabled&&ownArea.itemsForSale.Count==6,
                "staged unsold stock and merchant appear together before full save readiness");
            Check(ownArea.itemsForSale[0].gameObject.activeInHierarchy&&
                !ownArea.itemsForSale[0].GetComponent<RadioShopStock>().PurchaseEnabled,
                "visible staged stock cannot be purchased before full save readiness");
            Sun.sun.localTime=22;Time.unscaledTime=13;service.Tick();
            Check(!ownArea.itemsForSale[0].gameObject.activeInHierarchy,"night stock stays closed after staging");
            RadioShopPlacement.FailSlot=-1;Sun.sun.localTime=12;world.ReadyForShop=true;Time.unscaledTime=18;service.Tick();
            Check(ownArea.itemsForSale.Count==7,"owned merchant receives all seven devices after native readiness; found "+ownArea.itemsForSale.Count+"; "+string.Join(" | ",warnings));
            Check(ownNpc.GetComponent<Renderer>().enabled,"complete seven-item display becomes visible together");
            var ownedStock=ownArea.itemsForSale[0];int recordsBeforeGate=world.Store.Records.Count();
            var ownedCollider=ownedStock.gameObject.AddComponent<Collider>();
            Check(!(bool)Call("BeforeAreaEnter",area,ownedCollider),"Radio stock cannot register with a nearby base-game shop area");
            Check(!(bool)Call("BeforeKeeperEnter",keeper,ownedCollider),"base-game shopkeeper cannot offer Radio stock");
            Check((bool)Call("BeforeAreaEnter",ownArea,ownedCollider)&&
                (bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),ownedCollider),
                "unsold Radio stock remains attached only to its owned shop");
            Check(ownedStock.GetComponent<RadioShopStock>().PurchaseEnabled,"native purchase preflight enables priced stock");
            MoneyNotification.instance=null;Time.unscaledTime=19;service.Tick();
            Check(!ownedStock.GetComponent<RadioShopStock>().PurchaseEnabled,"existing stock closes purchase while native transaction UI is missing");
            MoneyNotification.instance=new MoneyNotification();Time.unscaledTime=20;service.Tick();
            Check(ownedStock.GetComponent<RadioShopStock>().PurchaseEnabled,"existing stock reopens purchase when native transaction UI becomes ready");
            world.ReadyForShop=false;Time.unscaledTime=20.1f;service.Tick();
            Check(ownStand.Visible&&ownedStock.gameObject.activeInHierarchy&&
                !ownedStock.GetComponent<RadioShopStock>().PurchaseEnabled,
                "a temporary save-readiness transition keeps the display visible but closes purchases");
            world.ReadyForShop=true;Time.unscaledTime=20.2f;service.Tick();
            var foreign=new GameObject("foreign stock");foreign.AddComponent<ShipItem>();var foreignCollider=foreign.AddComponent<Collider>();
            Check(!(bool)Call("BeforeAreaEnter",ownArea,foreignCollider),"owned area excludes other merchants' stock despite overlaps");
            Check(!(bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),foreignCollider),"owned NPC excludes other merchants' stock despite overlaps");
            foreign.GetComponent<ShipItem>().sold=true;foreign.GetComponent<ShipItem>().held=true;
            Check((bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),foreignCollider),
                "owned merchant accepts native held-good resale at its counter");
            Check((bool)Call("BeforeKeeperEnter",keeper,foreignCollider),
                "base-game merchant keeps its held-good resale callback");
            var purchasedCollider=item.gameObject.AddComponent<Collider>();item.held=true;
            Check(stock.Purchased&&item.sold,"successful native Radio purchase retains a sold ownership marker");
            Check((bool)Call("BeforeAreaEnter",area,purchasedCollider)&&
                (bool)Call("BeforeAreaEnter",ownArea,purchasedCollider),
                "purchased Radio can enter either shop area as an ordinary sold item");
            Check((bool)Call("BeforeKeeperEnter",keeper,purchasedCollider)&&
                (bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),purchasedCollider),
                "purchased held Radio can open resale at native or owned merchant");
            item.held=false;
            Check(!(bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),purchasedCollider),
                "purchased Radio must be held for owned resale popup");
            var abortedCollider=failedItem.gameObject.AddComponent<Collider>();failedItem.held=true;
            Check(!(bool)Call("BeforeKeeperEnter",keeper,abortedCollider)&&
                !(bool)Call("BeforeAreaEnter",area,abortedCollider),
                "sold flag alone cannot turn aborted Radio stock into resellable goods");
            foreign.GetComponent<ShipItem>().held=false;
            Check(!(bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),foreignCollider),
                "owned merchant does not open resale for an unheld good");
            var foreignChild=new GameObject("foreign item child collider");foreignChild.transform.SetParent(foreign.transform,false);
            var foreignChildCollider=foreignChild.AddComponent<Collider>();
            Check(!(bool)Call("BeforeAreaEnter",ownArea,foreignChildCollider)&&
                !(bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),foreignChildCollider),
                "child colliders of ordinary goods cannot enter the Radio merchant");
            Camera.main.transform.position=previewAnchor+new Vector3(15,0,0);
            foreign.GetComponent<ShipItem>().held=true;
            Check(!(bool)Call("BeforeKeeperEnter",ownArea.GetShopkeeper(),foreignCollider),
                "owned resale callback remains local to its counter");
            Check(!(bool)Call("BeforePurchase",ownArea.GetShopkeeper(),ownedStock),"Radio purchase cannot be made three stalls away");
            Camera.main.transform.position=previewAnchor;
            Check((bool)Call("BeforePurchase",ownArea.GetShopkeeper(),ownedStock),"native merchant can purchase ready radio stock");
            Check(world.Store.Records.Count()==recordsBeforeGate+1&&!ownedStock.sold,"purchase reserves before native sale");
            Call("AfterPurchase",ownedStock,null);
            Check(warnings.Any(w=>w.Contains("Radio merchant at")),"merchant location reported");
            GameState.currentlyLoading=true;service.Tick();GameState.currentlyLoading=false;
            Check(GameObject.All.Where(g=>g.name=="test stand").All(g=>g.Destroyed),"world load cleans up unsold preview");
            Check(ownNpc.Destroyed,"world load cleans up detached owned merchant");
            Check(ownArea.itemsForSale.Count==0,"world load removes owned unsold stock");
            Check(item,"previously purchased item survives preview cleanup");
            UnityEngine.Object.Found.Clear();
            var streamingScene=new GameObject("streaming scenery").AddComponent<IslandSceneryScene>();
            streamingScene.parentIslandIndex=9;UnityEngine.Object.Found.Add(streamingScene);
            Camera.main.transform.position=previewAnchor;
            Time.unscaledTime=21;service.Tick();
            var streamingStand=GameObject.All.Last(g=>g.name=="test stand").GetComponent<RadioShopStand>();
            Check(!streamingStand.Visible,"native NPC template delay keeps whole stand hidden at first scan");
            Time.unscaledTime=26;service.Tick();
            Check(!streamingStand.Visible,"first merchant retry does not reveal stand alone");
            Time.unscaledTime=36;service.Tick();
            Check(streamingStand.Visible,"extended missing template reveals placement for diagnosis");
            UnityEngine.Object.Found.Clear();
        }
    }
}
