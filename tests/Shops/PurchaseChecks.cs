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
        stock.Controller=controller;stock.Shop=area;stock.Service=service;stock.Anchor=controller.transform.parent;
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
        // Walnut kinds plus one regional kind of each speaker role and region.
        foreach(int kind in new[]{0,1,2,3,5,10,15})
        {
            foreach(bool wrapper in new[]{true,false})
            {
                ResetNative();var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
                var stock=Stock(world,service,kind);var item=stock.GetComponent<ShipItem>();var prefab=stock.GetComponent<SaveablePrefab>();
                int gold=PlayerGold.currency[0];
                Check(!item.sold&&prefab.instanceId==0&&world.Store.Records.Count()==0&&stock.Controller.IsStock,"unsold stock has no owned record");
                Trade(stock,wrapper);
                int id=prefab.instanceId;
                Check(id>0&&prefab.registered&&world.Store.Records.Count()==0,"native purchase allocates identity without a Radio registration callback");
                Check(item.OnBuyCount==1&&PlayerGold.currency[0]==gold-100,"native OnBuy and caller payment retained");
                Check(world.EnsureOwned(stock.Controller),"first interaction binds kind "+kind+" wrapper "+wrapper);
                Check(stock.Purchased&&!stock.Controller.IsStock&&world.Items.Count==1,"ownership releases stock controls");
                Check(world.Store.TryGet(id,prefab.prefabIndex,out var record)&&record.Kind==kind,"owned record matches native identity");
                Check(world.Arbiter.TryGet(id,out var state)&&ReferenceEquals(state,stock.Controller.State),"controller uses canonical state");
                prefab.RegisterToSave();prefab.RegisterToSave();world.Tick();
                Check(world.Store.Records.Count()==1&&world.Items.Count==1&&prefab.instanceId==id&&SaveLoadManager.instance.Prefabs.Count==1&&SaveablePrefab.existingInstanceIds.Count==1,"repeated native registration and binding retain identity");
                state.Volume=.61f;state.Shuffle=true;state.LocalVolume=.37f;state.Bass=.48f;state.TrackPath="track.ogg";state.PositionSeconds=27;
                var saved=new RadioSaveStore();Check(saved.Load(world.Save())&&saved.TryGet(id,prefab.prefabIndex,out var restored)&&restored.Volume==.61f&&restored.LocalVolume==.37f&&restored.Bass==.48f&&restored.PositionSeconds==27&&restored.TrackPath=="track.ogg","player settings and track position roundtrip");
                stock.Purchased=false;stock.Controller.SetShopStock(true);stock.Controller.State=new RadioState{Kind=kind};world.Tick();
                Check(stock.Purchased&&!stock.Controller.IsStock&&ReferenceEquals(state,stock.Controller.State)&&state.Volume==.61f,"managed old shop marker rebinds existing canonical state");
                SaveLoadManager.instance.Prefabs.Remove(prefab);prefab.RegisterToSave();
                Check(prefab.instanceId==id&&SaveLoadManager.instance.Prefabs.Count==0&&!world.EnsureOwned(stock.Controller),"native private registered flag survives unregister without Radio reallocation");
            }
            foreach(bool captureFirst in new[]{true,false})
            {
                ResetNative();var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});var stock=Stock(world,service,kind);
                Trade(stock,false);
                if(captureFirst)world.Capture(stock.Controller);
                string json=world.Save();var copy=new RadioSaveStore();
                Check(copy.Load(json)&&copy.TryGet(stock.GetComponent<SaveablePrefab>().instanceId,RadioSaveStore.ItemIndex(kind),out _)&&world.Items.Count==1,"immediate native capture/save binds never-activated device "+captureFirst);
            }
            foreach(string failure in new[]{"before-register","id-added","after-register"})
            {
                ResetNative();var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});var stock=Stock(world,service,kind);var prefab=stock.GetComponent<SaveablePrefab>();
                var original=new Exception(failure);
                if(failure=="before-register")SaveablePrefab.BeforeNative=_=>throw original;
                if(failure=="id-added")SaveablePrefab.AfterIdAdded=_=>throw original;
                if(failure=="after-register")SaveablePrefab.AfterNative=_=>throw original;
                Check(ReferenceEquals(Error(()=>Trade(stock,false)),original),"Radio never substitutes native registration exception "+failure);
                world.Tick();bool owned=failure=="after-register";
                Check(stock.Purchased==owned&&world.Store.Records.Count()==(owned?1:0)&&!stock.gameObject.Destroyed,"postownership binding observes native result without rollback "+failure);
                if(failure=="id-added")Check(prefab.instanceId>0&&SaveablePrefab.existingInstanceIds.Contains(prefab.instanceId),"native partial registration ID is never removed by Radio");
            }
            ResetNative();
            {
                var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});var stock=Stock(world,service,kind);
                world.ReadyForShop=false;world.Store.Load("{\"Schema\":999,\"Radios\":[]}");
                int gold=PlayerGold.currency[0];Trade(stock,false);world.Tick();
                Check(stock.GetComponent<ShipItem>().sold&&stock.GetComponent<SaveablePrefab>().registered&&PlayerGold.currency[0]==gold-100,"read-only Radio data cannot veto native sale or payment");
                Check(!world.Store.Writable&&world.Store.Records.Count()==0&&!world.EnsureOwned(stock.Controller),"read-only data remains preserved without defaults");
            }
            ResetNative();
            {
                var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});var stock=Stock(world,service,kind);
                world.Loading(true);Trade(stock,false);world.Tick();
                Check(stock.GetComponent<SaveablePrefab>().registered&&world.Store.Records.Count()==0,"starting supplies register natively while Radio document is loading");
                world.Loading(false);world.Tick();Check(stock.Purchased&&world.Store.Records.Count()==1,"fresh loading-time acquisition binds after document readiness");
                var loaded=Stock(world,service,kind);world.Loaded(loaded.Controller);Trade(loaded,false);world.Tick();
                Check(!world.EnsureOwned(loaded.Controller)&&world.Store.Records.Count()==1,"loaded missing individual record never creates fresh defaults");
                int id=loaded.GetComponent<SaveablePrefab>().instanceId;
                var state=new RadioState{Kind=kind,Volume=.23f};world.Store.Put(RadioRecord.Capture(id,RadioSaveStore.ItemIndex(kind),state));world.Arbiter.Register(id,state);
                Check(!world.EnsureOwned(loaded.Controller),"matching record cannot bypass loaded-device restoration deferrals");
                world.Managed(loaded.Controller);Check(world.EnsureOwned(loaded.Controller)&&ReferenceEquals(state,loaded.Controller.State),"established restoration reuses canonical saved state");
            }
            ResetNative();
            {
                var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});var stock=Stock(world,service,kind);Trade(stock,false);
                int id=stock.GetComponent<SaveablePrefab>().instanceId;int otherKind=(kind+1)%4;
                var other=new RadioState{Kind=otherKind,Volume=.22f};world.Store.Put(RadioRecord.Capture(id,RadioSaveStore.ItemIndex(otherKind),other));world.Arbiter.Register(id,other);
                Check(!world.EnsureOwned(stock.Controller)&&world.Store.TryGet(id,RadioSaveStore.ItemIndex(otherKind),out var retained)&&retained.Volume==.22f,"record kind mismatch preserves foreign state and native ID");
                Check(stock.GetComponent<SaveablePrefab>().instanceId==id&&stock.GetComponent<SaveablePrefab>().registered,"record mismatch never changes native ownership");
            }
        }
        ResetNative();
        {
            var world=new RadioWorldService();using var service=new RadioShopService(world,_=>{});
            SaveablePrefab.existingInstanceIds.Add(10);var first=Stock(world,service,0);var second=Stock(world,service,1);Trade(first,false);Trade(second,false);world.Tick();
            Check(first.Controller.InstanceId==11&&second.Controller.InstanceId==12&&world.Store.Records.Count()==2,"native allocator alone skips native registry IDs for independent devices");
            var donor=new GameObject(RadioWorldService.DonorName+"(Clone)");var item=donor.AddComponent<ShipItem>();item.sold=true;
            var prefab=donor.AddComponent<SaveablePrefab>();prefab.instanceId=300;prefab.prefabIndex=RadioSaveStore.ItemIndex(0);SaveLoadManager.instance.Prefabs.Add(prefab);
            var controller=donor.AddComponent<RadioItemController>();var legacy=new RadioState{Kind=0,Volume=.29f,PositionSeconds=31};controller.State=legacy;
            world.Store.Put(RadioRecord.Capture(300,prefab.prefabIndex,legacy));world.Arbiter.Register(300,legacy);world.Managed(controller);
            Check(world.EnsureOwned(controller)&&controller.InstanceId==300&&ReferenceEquals(legacy,controller.State),"managed migrated donor retains controls and canonical state without template marker");
            controller.State.PositionSeconds=32;world.Capture(controller);Check(world.Store.TryGet(300,prefab.prefabIndex,out var record)&&record.PositionSeconds==32,"migrated donor captures updated playback position");
            var native=new GameObject("native fishing rod");native.AddComponent<ShipItem>();var ordinary=native.AddComponent<SaveablePrefab>();ordinary.prefabIndex=80;
            native.GetComponent<ShipItem>().Sell();world.Tick();Check(ordinary.registered&&ordinary.instanceId>0&&world.Store.Records.Count()==3,"unmarked native item remains outside Radio state binding");
        }
        ResetNative();
    }
}
