using System;
using SailwindRadio.UI;
using SailwindRadio.Physical;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool value,string message){checks++;if(!value)throw new Exception(message);}
    private static void Reset()
    {
        GameState.playing=true;GameState.currentlyLoading=GameState.justStarted=GameState.sleeping=GameState.inBed=GameState.inCursorMenu=false;
        GameState.loadingScenes=0;GameState.currentBoat=null;BoatCamera.on=false;Time.timeScale=1;Application.isFocused=true;Input.Escape=false;
        Refs.charController=new();Refs.ovrController=new();Refs.mouseCrosshair=new();Refs.observerMirror=new();
        Cursor.visible=false;Cursor.lockState=CursorLockMode.Locked;MouseLook.Enabled=true;
        GUILayout.Click=GUILayout.ToggleLabel=null;GUILayout.Labels.Clear();
        GPButtonInventorySlot.inventorySlots=Array.Empty<GPButtonInventorySlot>();
    }
    private static Transform Boat()
    {
        var root=new Transform(); root.Components.Add(typeof(BoatDamage),new BoatDamage()); root.Components.Add(typeof(SaveableObject),new SaveableObject());
        return new Transform{parent=root};
    }
    private static void Main()
    {
        Reset();var boatA=Boat();var boatB=Boat();var item=new ShipItem{currentActualBoat=boatA};
        var vessel=DeviceVessels.Resolve(item,false);
        Check(vessel.Known&&vessel.Root==boatA.parent,"native model normalized to stable root identity");
        GameState.currentBoat=boatB;
        Check(DeviceVessels.Resolve(item,true).Root==boatB.parent,"held or player-inventory device follows actual player boat over stale item association");
        GameState.currentBoat=null;
        Check(DeviceVessels.Resolve(item,true).Known&&DeviceVessels.Resolve(item,true).Root==null,"carried device becomes land association after disembark");
        item.held=true;Check(DeviceVessels.IsPlayerOwned(item),"held item recognized");item.held=false;
        GPButtonInventorySlot.inventorySlots=new[]{new GPButtonInventorySlot{currentItem=item}};
        Check(DeviceVessels.IsPlayerOwned(item),"native player inventory ownership recognized");
        GPButtonInventorySlot.inventorySlots=Array.Empty<GPButtonInventorySlot>();Check(!DeviceVessels.IsPlayerOwned(item),"unowned hidden item not assumed player inventory");
        var carrier=new ShipItem{currentActualBoat=boatB};var carrierBody=carrier.itemRigidbodyC;carrierBody.Item=carrier;
        item.itemRigidbodyC.Box=new Transform();item.itemRigidbodyC.Box.Components.Add(typeof(ItemRigidbody),carrierBody);
        Check(DeviceVessels.Resolve(item,false).Root==boatB.parent,"crate contents use carrier boat instead of own stale boat");
        carrier.held=true;GameState.currentBoat=boatA;
        Check(DeviceVessels.Resolve(item,false).Root==boatA.parent,"held crate contents use player's current boat");
        carrier.held=false;item.itemRigidbodyC.Box.Components.Clear();Check(!DeviceVessels.Resolve(item,false).Known,"unresolved crate carrier is unknown not land");
        item.itemRigidbodyC.Box=null;item.currentActualBoat=null;
        Check(DeviceVessels.Resolve(item,false).Known&&DeviceVessels.Resolve(item,false).Root==null,"settled land device is known land");
        item.transform.parent=boatA;Check(!DeviceVessels.Resolve(item,false).Known,"partial embark association fails closed");
        item.transform.parent=null;GameState.currentlyLoading=true;Check(!DeviceVessels.Resolve(item,false).Known,"native load association is unknown");GameState.currentlyLoading=false;
        var knownA=new DeviceVessel(true,boatA.parent);var knownB=new DeviceVessel(true,boatB.parent);var land=new DeviceVessel(true,null);
        Check(DeviceVessels.CanConnect(knownA,knownA,new(),new(50)),"same vessel at50metres connects");
        Check(!DeviceVessels.CanConnect(knownA,knownA,new(),new(50.01f)),"same vessel beyond range disconnects");
        Check(!DeviceVessels.CanConnect(knownA,knownB,new(),new()),"different touching boats cannot connect");
        Check(DeviceVessels.CanConnect(land,land,new(),new(20)),"land housing devices connect in range");
        Check(!DeviceVessels.CanConnect(knownA,land,new(),new()),"land and boat never connect across gangway");
        Check(!DeviceVessels.CanConnect(new(false,null),land,new(),new()),"unknown association never becomes land match");
        Check(!DeviceVessels.CanConnect(land,land,new(float.NaN),new()),"invalid positions cannot connect");
        Check(RadioDevice.Center(1).z+RadioDevice.Size(1).z*.5f==0,"wall speaker back ends at native hook contact plane");
        Check(RadioDevice.Size(1).x==.14f&&RadioDevice.Size(1).y==.20f&&RadioDevice.Size(1).z==.12f,"satellite is compact surround size");
        Check(RadioDevice.Size(2).x==.367f&&RadioDevice.Size(2).y==.75f&&RadioDevice.Size(2).z==.4f,"normal speaker is narrower and taller");
        Check(RadioDevice.Size(3).x<1&&RadioDevice.Size(3).y<1&&RadioDevice.Size(3).z<1,"woofer fits within one metre cargo volume");
        var hookRoot=new Transform{position=new Vector3(0,1,0)};
        var radioBody=new Rigidbody();
        var hookedRadio=new ShipItem{transform=hookRoot,nailed=true,itemRigidbodyC=new ItemRigidbody{Body=radioBody}};
        var hookController=new RadioItemController{transform=hookRoot,State=new SailwindRadio.RadioState{Kind=0}};
        var hangable=new HangableItem{transform=hookRoot};
        var hookCollider=new Collider();
        var hookBody=new ItemRigidbody();
        var hookJoint=new ConfigurableJoint{connectedBody=radioBody};
        hookBody.transform.Components.Add(typeof(ConfigurableJoint),hookJoint);
        hookCollider.transform.Components.Add(typeof(ShipItemLampHook),new ShipItemLampHook{transform=hookCollider.transform,itemRigidbodyC=hookBody});
        hangable.SetHook(hookCollider);
        hookRoot.Components.Add(typeof(ShipItem),hookedRadio);
        hookRoot.Components.Add(typeof(RadioItemController),hookController);
        hookRoot.Components.Add(typeof(HangableItem),hangable);
        RadioNativeHooks.PositionBelowHook(hangable);
        Check(MathF.Abs(hookRoot.position.y-(1-RadioDevice.Size(0).y))<.0001f,"radio hook meets cabinet top instead of base");
        RadioNativeHooks.Release(hookedRadio);
        Check(!hangable.IsHanging()&&hangable.Disconnects==1&&hookedRadio.nailed,
            "radio pickup releases native hook without changing hammer lock");
        Check(!hookedRadio.DisembarkRestricted&&hookedRadio.DisembarkChanges==2,
            "radio hook release undoes native stale disembark restriction");
        RadioNativeHooks.EnterInventory(hookedRadio);
        Check(hangable.Disconnects==1&&hookedRadio.DisembarkChanges==3&&!hookedRadio.DisembarkRestricted,
            "inventory callback after pickup cannot release hook twice or retain boat restriction");
        hangable.SetHook(hookCollider);hookController.State.Kind=1;
        RadioNativeHooks.Release(hookedRadio);
        Check(hangable.IsHanging()&&hangable.Disconnects==1,"speaker hook behavior remains native-owned");
        hookController.State.Kind=0;hangable.SetHook(null);hookRoot.position=new Vector3(0,2,0);
        RadioNativeHooks.PositionBelowHook(hangable);
        Check(hookRoot.position.y==2,"unhooked radio retains placement position");
        hangable.SetHook(hookCollider);hookJoint.connectedBody=new Rigidbody();
        RadioNativeHooks.RejectPhantomHook(hangable);
        Check(!hangable.IsHanging()&&hangable.Disconnects==1,"occupied hook marker is cleared without disconnecting another item's joint");
        hookRoot.position=new Vector3(0,2,0);hangable.SetHook(hookCollider);
        RadioNativeHooks.PositionBelowHook(hangable);
        Check(hookRoot.position.y==2&&!hangable.IsHanging(),"occupied hook cannot move radio to a phantom attachment");
        RadioNativeHooks.Release(hookedRadio);
        Check(hangable.Disconnects==1&&hookedRadio.DisembarkChanges==3,"phantom hook does not invoke native disconnect on absent joint");
        hookJoint.connectedBody=radioBody;hangable.SetHook(hookCollider);
        hangable.DisconnectJoint();
        Check(hookedRadio.DisembarkRestricted,"native unheld inventory path reproduces stale disembark restriction");
        RadioNativeHooks.EnterInventory(hookedRadio);
        Check(!hookedRadio.DisembarkRestricted&&hangable.Disconnects==2,
            "inventory callback clears native restriction when native ExitBoat already disconnected");
        var invalidHouse=new Collider{Tag="House"};
        var validHouse=new Collider{Tag="House"};
        validHouse.transform.Components.Add(typeof(SaveableObject),new SaveableObject());
        Check(!RadioNativeHooks.AllowHouseTrigger(hookedRadio,invalidHouse),"radio ignores malformed native House trigger");
        Check(RadioNativeHooks.AllowHouseTrigger(hookedRadio,validHouse),"valid House trigger still reaches native parent assignment");
        Check(RadioNativeHooks.AllowHouseTrigger(hookedRadio,new Collider{Tag="EmbarkCol"}),"boat embark trigger remains native-owned");
        Check(RadioNativeHooks.AllowHouseTrigger(new ShipItem(),invalidHouse),"ordinary vanilla donor item is unaffected");
        // Installed level24 PlayerNeedsUI PCParent and inventory_parent rotations, after
        // UpdateAnchor resets the UI root locally. The slots themselves have identity rotation.
        var pcParent=new System.Numerics.Quaternion(0,.840093613f,-.542441487f,0);
        var inventoryParent=new System.Numerics.Quaternion(-.707106829f,0,0,.707106829f);
        var slotBasis=System.Numerics.Quaternion.Multiply(pcParent,inventoryParent);
        var oldFront=System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ,slotBasis);
        Check(oldFront.Z>.9f,"native yaw zero reproduces backwards-facing custom radio in inventory");
        var inventoryCorrection=System.Numerics.Quaternion.CreateFromAxisAngle(System.Numerics.Vector3.UnitY,RadioDevice.InventoryYaw*MathF.PI/180);
        var corrected=System.Numerics.Quaternion.Multiply(slotBasis,inventoryCorrection);
        var front=System.Numerics.Vector3.Transform(-System.Numerics.Vector3.UnitZ,corrected);
        var right=System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitX,corrected);
        var up=System.Numerics.Vector3.Transform(System.Numerics.Vector3.UnitY,corrected);
        Check(front.Z<-.9f&&right.X>.99f&&up.Y>.9f,"native inventory correction faces camera with upright unmirrored controls");
        for(int kind=0;kind<4;kind++)Check(RadioDevice.SoundOrigin(kind).y>0.1f,"device sound originates above floor pivot");
        var visualState=new SailwindRadio.RadioState{Powered=true,Shuffle=true};
        Check(DeviceControlVisuals.IsLit(visualState,"power",false)&&!DeviceControlVisuals.IsLit(visualState,"playpause",false),"playing radio shows power but no pause light");
        visualState.Paused=true;Check(DeviceControlVisuals.IsLit(visualState,"power",false)&&DeviceControlVisuals.IsLit(visualState,"playpause",false),"pause keeps power light and illuminates pause");
        Check(DeviceControlVisuals.IsLit(visualState,"shuffle",false),"enabled shuffle remains indicated when paused");
        visualState.Powered=false;Check(!DeviceControlVisuals.IsLit(visualState,"power",false)&&!DeviceControlVisuals.IsLit(visualState,"shuffle",false)&&!DeviceControlVisuals.IsLit(visualState,"next",true),"power off extinguishes all state and momentary lights");
        visualState.Kind=3;visualState.SpeakerEnabled=true;Check(DeviceControlVisuals.IsLit(visualState,"power",false),"speaker uses its independent enabled state");
        visualState.SpeakerEnabled=false;Check(!DeviceControlVisuals.IsLit(visualState,"power",false),"disabled speaker power light extinguishes");
        var nearSurface=new Collider{Closest=new(1.75f),transform=new Transform{position=new(2.05f)}};
        Check(RadioControlReach.Contains(new Transform(),nearSurface),"front control inside native reach stays open even when radio pivot is farther away");
        nearSurface.Closest=new(1.81f);Check(!RadioControlReach.Contains(new Transform(),nearSurface),"surface outside native reach closes menu");
        Check(!RadioControlReach.Contains(null,nearSurface),"destroyed activating pointer closes menu");
        foreach(string boundary in new[]{"focus","scenes","pause","camera","sleep","load","exit"})
        {
            Reset();using var menus=new RadioMenus();menus.ShowSpawnChooser();
            Check(menus.IsOpen&&!Refs.charController.enabled&&!MouseLook.Enabled&&GameState.inCursorMenu,"menu acquires input and cursor");
            switch(boundary){case "focus":Application.isFocused=false;break;case "scenes":GameState.loadingScenes=1;break;
                case "pause":Time.timeScale=0;break;case "camera":BoatCamera.on=true;break;case "sleep":GameState.sleeping=true;break;
                case "load":GameState.currentlyLoading=true;break;case "exit":GameState.playing=false;break;}
            menus.Tick();Check(!menus.IsOpen,"menu closes at "+boundary);
            bool nativeOwner=boundary=="sleep"||boundary=="load"||boundary=="exit";
            Check(Refs.charController.enabled!=nativeOwner,"controller owner respected at "+boundary);
            if(!nativeOwner)Check(!GameState.inCursorMenu&&MouseLook.Enabled&&!Cursor.visible,"owned cursor restored at "+boundary);
        }
        Reset();using(var menus=new RadioMenus())
        {
            RadioDeviceKind? spawned=null;
            menus.SpawnRequested+=kind=>{Check(!menus.IsOpen&&!GameState.inCursorMenu&&Refs.charController.enabled,"spawn callback runs after native input release");spawned=kind;};
            menus.ShowSpawnChooser();GUILayout.Click="Turbo Wolfer";menus.Draw();
            Check(spawned==RadioDeviceKind.TurboWoofer,"spawn chooser routes correct fourth device kind");
            menus.SetMessage("Move to a clear spot");menus.Draw();Check(menus.IsOpen&&GUILayout.Labels.Contains("Move to a clear spot"),"spawn failure is visible in chooser");
            int markerCalls=0;
            menus.StandPositionRequested+=()=>{markerCalls++;menus.SetMessage("Radio stand marker: local test");};
            GUILayout.Click="Log stall position here";menus.Draw();
            Check(markerCalls==1&&menus.IsOpen&&GUILayout.Labels.Contains("Radio stand marker: local test"),
                "marker button reports once without closing or spawning");
            menus.Draw();Check(markerCalls==1,"marker does not repeat without another click");
            menus.Close();var radio=new RadioItemController();
            menus.ShowCollections(radio,new[]{new CollectionChoice("A","Ocean",true),new CollectionChoice("B","Local",false)});
            GUILayout.ToggleLabel="Local";menus.Draw();GUILayout.ToggleLabel=null;
            menus.ShowCollections(radio,new[]{new CollectionChoice("A","Ocean",true),new CollectionChoice("B","Local",false)});
            Check(radio.Releases==1&&menus.CollectionRadio==radio,"scan refresh keeps same lease and collection target");
            string[] roots=null;menus.CollectionsChanged+=(target,selection)=>{Check(target==radio&&!menus.IsOpen,"apply closes before callback");roots=selection;};
            GUILayout.Click="Apply";menus.Draw();Check(roots.Length==2,"scan refresh preserves user checkbox edits");
            menus.ShowCollections(radio,Array.Empty<CollectionChoice>());radio.Destroyed=true;menus.Tick();
            Check(!menus.IsOpen,"destroyed empty collection target never turns into spawn menu");
            radio=new RadioItemController();menus.ShowCollections(radio,Array.Empty<CollectionChoice>());radio.WithinMenuReach=false;menus.Tick();
            Check(!menus.IsOpen,"leaving native interaction reach closes collections");
            menus.ShowSpawnChooser();Input.Escape=true;menus.Tick();Check(!menus.IsOpen,"escape closes menu");Input.Escape=false;
            menus.ShowSpawnChooser();Refs.charController=new(){enabled=false};Refs.ovrController=new(){enabled=false};menus.Close();
            Check(!Refs.charController.enabled&&!Refs.ovrController.enabled,"lease cannot enable replacement scene controllers");
        }
        Reset();Refs.charController.enabled=false;using(var menus=new RadioMenus()){menus.ShowSpawnChooser();Check(!menus.IsOpen,"another native control owner prevents menu acquisition");}
        Reset();using(var menus=new RadioMenus()){menus.ShowSpawnChooser();menus.Dispose();Check(!GameState.inCursorMenu&&Refs.charController.enabled,"dispose returns owned input state");}
        Console.WriteLine(checks+" device association and menu lifecycle checks passed with production rules and native API doubles. Physical placement and UI remain live checks.");
    }
}
