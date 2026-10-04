using System;
using System.Linq;
using System.Reflection;
using SailwindRadio;
using SailwindRadio.Physical;
using SailwindRadio.Persistence;
using SailwindRadio.Shops;
using UnityEngine;

static partial class Program
{
    static void ResetNative()
    {
        SaveLoadManager.instance=new SaveLoadManager();
        SaveablePrefab.existingInstanceIds.Clear();
        SaveablePrefab.SkipNative=false;
        SaveablePrefab.BeforeNative=null;SaveablePrefab.AfterIdAdded=null;SaveablePrefab.AfterNative=null;
        ShipItem.SkipSell=false;ShipItem.BeforeBody=null;ShipItem.AfterOwnership=null;ShipItem.AfterRegister=null;
        UnityEngine.Random.Next=10;UnityEngine.Random.Source=null;
    }
    static RadioShopStock Stock(RadioWorldService world,RadioShopService service,int kind)
    {
        var area=new GameObject("test area").AddComponent<ShopArea>();area.itemsForSale=new();
        area.Keeper=new GameObject("keeper").AddComponent<Shopkeeper>();area.Keeper.RegisterShop(area);
        var controller=world.CreateShopStock(new GameObject("stand").transform,default,default,kind);
        var stock=controller.gameObject.AddComponent<RadioShopStock>();
        stock.Controller=controller;stock.Shop=area;stock.Service=service;stock.PurchaseEnabled=true;stock.Anchor=controller.transform.parent;
        controller.SetShopStock(true);controller.GetComponent<ShipItem>().AddToShop(area);
        return stock;
    }
    static Exception Error(Action action)
    {
        try{action();return null;}
        catch(TargetInvocationException e){return e.InnerException;}
        catch(Exception e){return e;}
    }
    static void Trade(RadioShopStock stock,bool wrapper)
    {
        var item=stock.GetComponent<ShipItem>();
        stock.Shop.itemsForSale.Remove(item);
        if(wrapper)
            typeof(Shopkeeper).GetMethod("SellItem",BindingFlags.Instance|BindingFlags.NonPublic)
                .Invoke(stock.Shop.Keeper,new object[]{item,100,0});
        else
        {
            // EconomyOverhaul's observed bypass: no Shopkeeper.SellItem callback.
            item.Sell();
            PlayerGold.currency[0]-=100;
        }
    }
    static void PurchaseChecks()
    {
        for(int kind=0;kind<4;kind++)
        {
            foreach(bool wrapper in new[]{true,false})
            {
                ResetNative();var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
                var stock=Stock(world,service,kind);var item=stock.GetComponent<ShipItem>();var prefab=stock.GetComponent<SaveablePrefab>();
                int gold=PlayerGold.currency[0];
                Check(!item.sold&&prefab.instanceId==0&&world.Store.Records.Count()==0&&stock.Controller.IsStock,"unsold stock has no owned state");
                Trade(stock,wrapper);
                int id=prefab.instanceId;
                Check(stock.Purchased&&!stock.Controller.IsStock&&!stock.Selling&&id>0,"native purchase enables kind "+kind+" wrapper "+wrapper);
                Check(item.OnBuyCount==1&&PlayerGold.currency[0]==gold-100,"native OnBuy and caller payment retained");
                Check(world.Store.TryGet(id,prefab.prefabIndex,out var record)&&record.Kind==kind&&world.Items.Count==1,"one canonical owned record");
                Check(world.Arbiter.TryGet(id,out var state)&&ReferenceEquals(state,stock.Controller.State),"registered controller uses canonical state");
                prefab.RegisterToSave();prefab.RegisterToSave();
                Check(world.Store.Records.Count()==1&&world.Items.Count==1&&prefab.instanceId==id&&SaveLoadManager.instance.Prefabs.Count==1&&SaveablePrefab.existingInstanceIds.Count==1,"repeated registration preserves unique identity");
                var duplicate=Error(()=>Trade(stock,wrapper));
                Check(duplicate!=null&&PlayerGold.currency[0]==gold-100&&item.OnBuyCount==1,"repeat sale cannot reach another caller charge");
                var original=new Exception("post-sale callback failed");
                Check(ReferenceEquals(Callbacks.Shop("AfterNativeSell",item,true,original),original),"repeated finalizer preserves unrelated exception");
                SaveLoadManager.instance.Prefabs.Remove(prefab);
                prefab.RegisterToSave();
                Check(prefab.instanceId==id&&world.Store.Records.Count()==1&&SaveLoadManager.instance.Prefabs.Count==0,"native private registered flag no-op never reallocates known identity");
                SaveLoadManager.instance.Prefabs.Add(prefab);
                var saved=new RadioSaveStore();Check(saved.Load(world.Store.Save())&&saved.TryGet(id,prefab.prefabIndex,out var restored)&&restored.Kind==kind,"owned extension record roundtrips without schema change");
                // Reproduce an old purchase whose shop slot reference is already lost. Its
                // controller was registered, but the old shop marker kept controls disabled.
                state.Volume=.61f;state.Shuffle=true;state.Powered=kind==0;
                world.Store.Put(RadioRecord.Capture(id,prefab.prefabIndex,state));
                stock.Purchased=false;stock.Controller.SetShopStock(true);
                stock.Controller.State=new RadioState{Kind=kind};
                world.Tick();
                Check(stock.Purchased&&!stock.Controller.IsStock&&prefab.instanceId==id&&ReferenceEquals(state,stock.Controller.State),"bounded live reconciliation rebinds canonical state without new identity");
                Check(stock.Controller.State.Volume==.61f&&stock.Controller.State.Shuffle&&stock.Controller.State.Powered==(kind==0),"live recovery preserves canonical player settings");
            }

            foreach(string failure in new[]{"before-sold","after-sold","skip-sell","before-register","id-added","skip-register","after-register","post-registration-callback"})
            {
                ResetNative();var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
                var stock=Stock(world,service,kind);var item=stock.GetComponent<ShipItem>();var prefab=stock.GetComponent<SaveablePrefab>();
                var original=new Exception(failure);int gold=PlayerGold.currency[0];
                if(failure=="before-sold")ShipItem.BeforeBody=_=>throw original;
                if(failure=="after-sold")ShipItem.AfterOwnership=_=>throw original;
                if(failure=="skip-sell")ShipItem.SkipSell=true;
                if(failure=="before-register")SaveablePrefab.BeforeNative=_=>throw original;
                if(failure=="id-added")SaveablePrefab.AfterIdAdded=_=>throw original;
                if(failure=="skip-register")SaveablePrefab.SkipNative=true;
                if(failure=="after-register")SaveablePrefab.AfterNative=_=>throw original;
                if(failure=="post-registration-callback")ShipItem.AfterRegister=_=>throw original;
                var error=Error(()=>Trade(stock,false));
                Check(error!=null&&PlayerGold.currency[0]==gold&&!stock.Selling,"failed native call prevents later caller charge "+failure);
                if(failure!="skip-register"&&failure!="skip-sell")Check(ReferenceEquals(error,original),"native exception survives finalizers "+failure);
                bool owned=failure=="after-register"||failure=="post-registration-callback";
                Check(stock.Purchased==owned&&stock.Controller.IsStock!=owned&&!stock.gameObject.Destroyed,"only registered ownership promotes, object preserved "+failure);
                Check(world.Store.Records.Count()==(owned?1:0)&&SaveLoadManager.instance.Prefabs.Count==(owned?1:0)&&SaveablePrefab.existingInstanceIds.Count==(owned?1:0),"cleanup only unowned provisional state "+failure);
                if(!owned)Check(prefab.instanceId==0,"unregistered original provisional identity cleared");
                if(!item.sold)
                {
                    Check(stock.Shop.itemsForSale.Count==0,"native caller removed refused stock before Sell");
                    Callbacks.Shop("BeforePrice",item);Callbacks.Shop("BeforePrice",item);
                    Check(stock.Shop.itemsForSale.Count==1&&stock.Shop.itemsForSale[0]==item,"unsold stock maintenance restores native listing without duplicates");
                }
                SaveablePrefab.BeforeNative=null;SaveablePrefab.AfterIdAdded=null;SaveablePrefab.AfterNative=null;ShipItem.BeforeBody=null;ShipItem.AfterOwnership=null;ShipItem.AfterRegister=null;
                if(owned)
                {
                    int id=prefab.instanceId;prefab.RegisterToSave();
                    Check(prefab.instanceId==id&&SaveLoadManager.instance.Prefabs.Count==1&&SaveablePrefab.existingInstanceIds.Count==1,"partial native flag cannot duplicate registered ownership");
                }
            }

            ResetNative();
            {
                var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
                var stock=Stock(world,service,kind);var item=stock.GetComponent<ShipItem>();
                Exception inner=null;ShipItem.BeforeBody=_=>{ShipItem.BeforeBody=null;inner=Error(()=>item.Sell());};
                Trade(stock,false);
                Check(inner!=null&&stock.Purchased&&world.Store.Records.Count()==1&&!stock.Selling,"reentrant Sell cannot duplicate initialization");
            }
            foreach(string blocked in new[]{"not-ready","read-only","capacity","oversized-json","bad-state","id-exhaustion"})
            {
                ResetNative();var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
                var stock=Stock(world,service,kind);int gold=PlayerGold.currency[0];
                if(blocked=="not-ready")world.ReadyForShop=false;
                if(blocked=="read-only")world.Store.Load("{\"Schema\":999,\"Radios\":[]}");
                if(blocked=="capacity")
                {
                    for(int id=1;id<=RadioSaveStore.MaximumRecords;id++)world.Store.Put(RadioRecord.Capture(id,RadioSaveStore.ItemIndex(0),new RadioState()));
                    UnityEngine.Random.Next=3000;
                }
                if(blocked=="oversized-json")
                {
                    for(int id=1;id<=130;id++)world.Store.Put(RadioRecord.Capture(id,RadioSaveStore.ItemIndex(0),new RadioState{TrackPath=new string('x',32768)}));
                    UnityEngine.Random.Next=3000;
                }
                if(blocked=="bad-state")stock.Controller.State.Volume=float.NaN;
                if(blocked=="id-exhaustion"){SaveablePrefab.existingInstanceIds.Add(10);UnityEngine.Random.Source=()=>10;}
                int records=world.Store.Records.Count();var error=Error(()=>Trade(stock,false));
                Check(error!=null&&PlayerGold.currency[0]==gold&&!stock.Purchased&&stock.Controller.IsStock,"registration refusal propagates before following charge "+blocked);
                Check(world.Store.Records.Count()==records&&!stock.gameObject.Destroyed,"registration refusal preserves existing records and object "+blocked);
                if(blocked=="capacity")Check(world.Warnings.Any(w=>w.Contains("capacity")),"capacity fixture reaches actual store capacity refusal");
                if(blocked=="oversized-json")Check(world.Warnings.Any(w=>w.Contains("too large")),"JSON fixture reaches actual serializer size refusal");
            }
            ResetNative();
            {
                var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
                var stock=Stock(world,service,kind);var prefab=stock.GetComponent<SaveablePrefab>();
                var live=new GameObject().AddComponent<SaveablePrefab>();live.instanceId=10;SaveLoadManager.instance.Prefabs.Add(live);
                var nativeObject=new NativeObject{localItems=new LocalItems()};var cached=new SaveablePrefab{instanceId=11};nativeObject.localItems.Cached.Add(cached);SaveLoadManager.instance.Objects.Add(nativeObject);
                world.Store.Put(RadioRecord.Capture(12,RadioSaveStore.ItemIndex(0),new RadioState()));SaveablePrefab.existingInstanceIds.Add(13);
                Trade(stock,false);Check(prefab.instanceId==14,"allocation excludes live cached retained and native registry IDs");
                stock.Purchased=false;stock.Controller.SetShopStock(true);cached.instanceId=14;world.Tick();
                Check(!stock.Purchased&&stock.Controller.IsStock,"recovery refuses cached identity collision");cached.instanceId=11;
                world.Store.Remove(14);world.Tick();Check(!stock.Purchased&&stock.Controller.IsStock,"recovery does not manufacture missing Radio records");
            }
            ResetNative();
            {
                var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
                var controller=world.CreateShopStock(new GameObject().transform,default,default,kind);var item=controller.GetComponent<ShipItem>();var prefab=controller.GetComponent<SaveablePrefab>();
                item.sold=true;world.Loading(true);prefab.RegisterToSave();prefab.RegisterToSave();
                Check(world.PendingCount==1&&world.Store.Records.Count()==0&&!prefab.registered,"starting-supply registration defers once while loading");
                world.Loading(false);world.ReplayPending();Check(prefab.registered&&world.Items.Count==1&&world.Store.Records.Count()==1,"starting-supply pending registration uses shared native path");
                var loaded=world.CreateShopStock(new GameObject().transform,default,default,kind).GetComponent<SaveablePrefab>();loaded.GetComponent<ShipItem>().sold=true;
                loaded.instanceId=999;world.LoadingPrefab(loaded,true);loaded.RegisterToSave();
                Check(loaded.registered&&loaded.instanceId==999&&!world.Store.TryGetAny(999,out _),"native Load registration bypasses creation until existing restore path");world.LoadingPrefab(loaded,false);
            }
        }
        IdentityMutationChecks();
        ResetNative();
        {
            var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
            var native=new GameObject("native fishing rod");var item=native.AddComponent<ShipItem>();
            var prefab=native.AddComponent<SaveablePrefab>();prefab.prefabIndex=80;prefab.instanceId=500;
            item.Sell();Check(item.sold&&prefab.registered&&item.OnBuyCount==1&&world.Store.Records.Count()==0,"unmarked native item keeps purchase initialization without Radio state");
        }
        ResetNative();
    }
    static void IdentityMutationChecks()
    {
        ResetNative();var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
        var stock=Stock(world,service,0);var prefab=stock.GetComponent<SaveablePrefab>();
        var other=new GameObject().AddComponent<SaveablePrefab>();other.instanceId=50;SaveLoadManager.instance.Prefabs.Add(other);
        var prior=new RadioState{Kind=0,Volume=.3f};world.Store.Put(RadioRecord.Capture(50,RadioSaveStore.ItemIndex(0),prior));world.Arbiter.Register(50,prior);
        int attempted=0;var original=new Exception("ID mutated before failure");
        SaveablePrefab.BeforeNative=p=>{attempted=p.instanceId;p.instanceId=50;throw original;};
        Check(ReferenceEquals(Error(()=>Trade(stock,false)),original),"ID mutation preserves native error");
        Check(prefab.instanceId==50&&world.Store.TryGetAny(50,out _)&&world.Arbiter.TryGet(50,out var retained)&&ReferenceEquals(retained,prior),"cleanup never deletes mutated foreign ID state");
        Check(!world.Store.TryGetAny(attempted,out _)&&!world.Arbiter.TryGet(attempted,out _),"cleanup removes only original attempt state");
        SaveablePrefab.BeforeNative=null;
        var second=Stock(world,service,1);int before=world.Store.Records.Count();
        SaveablePrefab.BeforeNative=p=>{SaveLoadManager.instance=new SaveLoadManager();throw original;};
        Check(ReferenceEquals(Error(()=>Trade(second,false)),original),"session change preserves original registration failure");
        Check(world.Store.Records.Count()==before+1,"old attempt finalizer cannot clean another session");
        SaveablePrefab.BeforeNative=null;
    }
}
