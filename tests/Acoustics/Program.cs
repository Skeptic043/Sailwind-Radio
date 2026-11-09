using System;
using SailwindRadio.Acoustics;
using UnityEngine;
using UnityEngine.SceneManagement;
using UObject = UnityEngine.Object;

internal static class Program
{
    private static int checks;
    private static void Check(bool value,string message) { checks++; if(!value) throw new Exception(message); }
    private static void Equal(float actual,float expected,string message) => Check(Math.Abs(actual-expected)<.00001f,message);
    private static InteriorEffectsTrigger Room(Collider collider)
    {
        var room=new InteriorEffectsTrigger { Colliders=new[]{collider} };
        UObject.Registry.Add(room); return room;
    }
    // Cabin membership is rechecked about once a second, so membership changes need time to pass.
    private static void Later(RadioAcousticsService service) { Time.realtimeSinceStartup+=1.5f; service.Tick(); }
    private static void Main()
    {
        UObject.Registry.Clear();
        Time.realtimeSinceStartup=10;
        var listener=new AudioListener(); UObject.Registry.Add(listener);
        var inactive=new AudioListener(); inactive.gameObject.activeInHierarchy=false; UObject.Registry.Add(inactive);
        var mesh=new MeshCollider(); var room=Room(mesh);
        var radio=new Component(); var speaker=new Component();
        using(var service=new RadioAcousticsService())
        {
            service.Tick(); Check(service.ListenerKnown,"only enabled and hierarchy-active listener counts");
            Equal(service.ObstructionAt(radio,new(1),false),0,"same convex cabin clear");
            Equal(service.ObstructionAt(speaker,new(5),false),1,"outside source inside listener muffled");
            listener.transform.position=new(5); Later(service);
            Equal(service.ObstructionAt(radio,new(0),false),1,"inside source outside listener muffled");
            var rig=new UnityEngine.Transform{position=new(4)}; Refs.ovrCameraRig=rig;
            service.Tick(); Check(!service.HeadKnown,"first-person listener is the head");
            BoatCamera.on=true; rig.position=new(-.5f); listener.transform.position=new(30); service.Tick();
            Equal(service.HeadPosition.x,.5f,"boat camera keeps the head at its place in the player's rig");
            Equal(service.ObstructionAt(radio,new(0),false,true),0,"boat camera head is placed at once and hears its cabin's source clear");
            Equal(service.ObstructionAt(radio,new(0),false),1,"camera listener outside the cabin still hears it muffled");
            BoatCamera.on=false; Refs.ovrCameraRig=null; listener.transform.position=new(5); service.Tick();
            Check(!service.HeadKnown,"leaving the boat camera returns to the native listener");
            Equal(service.ListenerPosition.x,5,"actual listener transform read every frame");
            Equal(service.ObstructionAt(new Component(),new(6),false),0,"both outside clear");
            Equal(service.ObstructionAt(radio,new(0),true),0,"carried and player-inventory source shares listener membership");
            Equal(service.ObstructionAt(null,new(0),false),0,"an output without a device is not muffled");

            var door=new GPButtonTrapdoor(); room.doors=new[]{door};
            door.Open=true; Equal(service.ObstructionAt(radio,new(0),false),.35f,"door open changes without waiting for a membership check");
            door.Open=false; Equal(service.ObstructionAt(radio,new(0),false),1,"door closing changes immediately");
            room.houseDoor=new GPButtonHouseDoor { Open=true };
            Equal(service.ObstructionAt(radio,new(0),false),.35f,"houseDoor overrides trapdoor list");
            room.houseDoors=new[]{new GPButtonHouseDoor()};
            Equal(service.ObstructionAt(radio,new(0),false),1,"houseDoors list overrides single houseDoor");
            room.houseDoors[0].Open=true; Equal(service.ObstructionAt(radio,new(0),false),.35f,"houseDoors open reads live");
            room.houseDoors=new GPButtonHouseDoor[]{null}; Equal(service.ObstructionAt(radio,new(0),false),0,"missing door fails open");
            room.houseDoors=null; room.houseDoor=null; room.doors=null; room.semiIndoor=true;
            Equal(service.ObstructionAt(radio,new(0),false),.35f,"unbound semiIndoor fallback"); room.semiIndoor=false;
            Equal(service.LastObstruction(radio),.35f,"the last level is kept for an output that stops asking");
            Equal(service.LastObstruction(new Component()),0,"an output never asked about has no level");
            room.enabled=false; Equal(service.ObstructionAt(radio,new(0),false),0,"disabled trigger ignored immediately"); room.enabled=true;

            mesh.convex=false; Later(service); Equal(service.ObstructionAt(radio,new(0),false),0,"unsupported nonconvex mesh fails open"); mesh.convex=true;
            mesh.sharedMesh=null; Later(service); Equal(service.ObstructionAt(radio,new(0),false),0,"missing geometry fails open"); mesh.sharedMesh=new();
            mesh.enabled=false; Later(service); Equal(service.ObstructionAt(radio,new(0),false),0,"disabled collider ignored"); mesh.enabled=true;
            mesh.isTrigger=false; Later(service); Equal(service.ObstructionAt(radio,new(0),false),0,"nontrigger collider not treated as interior"); mesh.isTrigger=true;
            mesh.Geometry=p=>new Vector3(10); Later(service); Equal(service.ObstructionAt(radio,new(0),false),0,"mesh narrow phase rejects point inside only its bounding box"); mesh.Geometry=null;
            mesh.transform.position=new(10); Later(service);
            Equal(service.ObstructionAt(radio,new(0),false),0,"a moving vessel's cabin is found at its current position");
            Equal(service.ObstructionAt(new Component(),new(10),false),1,"moving cabin follows source");
            mesh.transform.position=new(0); Later(service);

            var second=Room(new BoxCollider { transform=new Transform { position=new(5) } });
            SceneManager.Load(); service.Tick(); Equal(service.ObstructionAt(radio,new(0),false),1,"different closed rooms muffled, and a scene change rechecks at once");
            second.doors=new[]{new GPButtonTrapdoor {Open=true}};
            Equal(service.ObstructionAt(radio,new(0),false),1,"between two cabins the stronger level applies");
            room.semiIndoor=true; Equal(service.ObstructionAt(radio,new(0),false),.35f,"two open cabins stay partly muffled");
            room.semiIndoor=false; second.doors=null;
            var common=Room(new BoxCollider { Half=10 }); SceneManager.Load(); service.Tick();
            Equal(service.ObstructionAt(radio,new(0),false),0,"any common overlapping volume clears artificial internal boundary");
            common.gameObject.activeInHierarchy=false; second.gameObject.activeInHierarchy=false;
            Later(service); Equal(service.ObstructionAt(radio,new(0),false),1,"inactive volumes are excluded, and the wall they hid is checked at the next check");

            int queries=mesh.Queries;
            for(int i=0;i<100;i++) service.ObstructionAt(radio,new(0),false);
            Check(mesh.Queries==queries,"no containment tests between membership checks");
            Time.realtimeSinceStartup+=1.5f; service.ObstructionAt(radio,new(0),false);
            Check(mesh.Queries>queries,"membership is checked again after about a second");
            queries=mesh.Queries; service.ObstructionAt(new Component(),new(0),false);
            Check(mesh.Queries>queries,"a new output is placed at once");
            listener.enabled=false; service.Tick(); listener.transform.position=new(0); listener.enabled=true; service.Tick();
            Equal(service.ObstructionAt(radio,new(0),false),0,"a listener that becomes known is placed at once");
            listener.transform.position=new(5); Later(service);

            int searches=UObject.Searches; for(int i=0;i<100;i++) service.Tick();
            Check(UObject.Searches==searches,"steady frames do not scan scene or allocate cache");
            int cabins=UObject.CabinSearches; Time.realtimeSinceStartup+=20; service.Tick();
            Check(UObject.CabinSearches==cabins,"between scene loads the cabin search is only a 30-second safety net");
            SceneManager.Load(); service.Tick(); Check(UObject.CabinSearches==cabins+1,"a scene load searches for cabins at once");
            listener.enabled=false; service.Tick(); Check(!service.ListenerKnown,"disabled cached listener immediately unknown");
            Equal(service.ObstructionAt(radio,new(0),false),0,"unknown listener is never muffled");
            listener.enabled=true; service.Tick(); Check(service.ListenerKnown,"cached listener reenabled without scan");
            var extra=new AudioListener(); UObject.Registry.Add(extra);
            Time.realtimeSinceStartup=.3f; service.Tick(); Check(!service.ListenerKnown,"ambiguous active listeners fail muted");
            extra.enabled=false; service.Tick(); Check(service.ListenerKnown,"ambiguity resolved every frame");
            listener.Destroyed=true; service.Tick(); Check(!service.ListenerKnown,"destroyed listener immediately unknown");
            var replacement=new AudioListener { transform=new Transform { position=new(42) } }; UObject.Registry.Add(replacement);
            SceneManager.Unload(); service.Tick(); Check(service.ListenerKnown,"scene changes reacquire listener immediately");
            Equal(service.ListenerPosition.x,42,"replacement listener position supplied");
            replacement.transform.position=new(float.NaN); service.Tick();
            Check(!service.ListenerKnown,"invalid listener position fails muted without geometry tests");
            replacement.transform.position=new(42); service.Tick();
            Equal(service.ObstructionAt(radio,new(float.PositiveInfinity),false),0,"invalid source point never reaches native geometry");
            service.Dispose(); Check(!service.ListenerKnown&&SceneManager.Subscribers==0,"dispose clears listener and unregisters callbacks");
            searches=UObject.Searches; service.Tick(); Check(UObject.Searches==searches,"disposed service performs no discovery");
        }
        Check(!RadioAcousticsService.TryContains(new Collider[]{new BoxCollider(),new MeshCollider {convex=false}},new(0),out _),
            "one unsupported active collider makes whole volume unknown");
        CheckPhysicalBoundaries();
        Console.WriteLine(checks+" acoustics checks passed using production service and native geometry/lifecycle doubles. Unity spatial audio remains a live check.");
    }
    // 1.5.9's wall checks, at the once-a-second cabin check: a cabin volume holding only one of the two
    // muffles only when a ray between them meets a real wall of it.
    private static void CheckPhysicalBoundaries()
    {
        UObject.Registry.Clear();
        Time.realtimeSinceStartup=100;
        var listener=new AudioListener(); listener.transform.position=new(105); UObject.Registry.Add(listener);
        var boat=new BoatDamage();
        var model=new Transform {parent=boat.transform,position=new(100)};
        var walk=new Transform {position=new(1000)};
        var cabin=Room(new BoxCollider {transform=new Transform {position=new(100)}});
        cabin.transform.parent=model;
        var embark=new BoatEmbarkCollider {Model=model,Root=boat.transform,walkCollider=walk};
        boat.transform.Children.Add(embark);
        var radio=new Component();
        Physics.Hits=Array.Empty<RaycastHit>();
        using(var service=new RadioAcousticsService())
        {
            service.Tick();
            Equal(service.ObstructionAt(radio,new(100),false),0,"deck trigger protrusion with clear mapped collision ray stays open air");
            Equal(Physics.Origin.x,1000,"the ray starts at the output, mapped into the owning boat's walk space");
            Check(Physics.Direction.x>0,"the ray runs from the output toward the listener");
            int queries=Physics.Queries;
            for(int i=0;i<100;i++) service.ObstructionAt(radio,new(100),false);
            Check(Physics.Queries==queries,"rays are cast only at the once-a-second check");
            var wall=new BoxCollider {isTrigger=false}; wall.transform.parent=walk;
            Physics.Hits=new[]{new RaycastHit{collider=wall}}; Later(service);
            Equal(service.ObstructionAt(radio,new(100),false),1,"actual cabin wall in owning walk hierarchy retains muffling");
            cabin.doors=new[]{new GPButtonTrapdoor {Open=true}};
            Equal(service.ObstructionAt(radio,new(100),false),.35f,"door attenuation updates immediately between wall checks");
            cabin.doors=null;
            var otherBoatWall=new BoxCollider {isTrigger=false}; otherBoatWall.transform.parent=new Transform();
            Physics.Hits=new[]{new RaycastHit{collider=otherBoatWall}}; Later(service);
            Equal(service.ObstructionAt(radio,new(100),false),0,"another boat's nearby fixed collider cannot become this cabin's wall");
            var item=new ShipItem(); item.transform.parent=walk;
            var itemCollider=new BoxCollider {isTrigger=false}; itemCollider.transform.parent=item.transform;
            var avatar=new PlayerControllerMirror(); avatar.transform.parent=walk;
            var avatarCollider=new BoxCollider {isTrigger=false}; avatarCollider.transform.parent=avatar.transform;
            Physics.Hits=new[]{new RaycastHit{collider=itemCollider},new RaycastHit{collider=avatarCollider}}; Later(service);
            Equal(service.ObstructionAt(radio,new(100),false),0,"device cargo and player colliders cannot muffle their own endpoint");
            Physics.Hits=new[]{new RaycastHit{collider=wall}};
            Equal(service.ObstructionAt(radio,new(100),true),0,"carried output bypasses cabin checks");
            walk.lossyScale=new(2,2,2); Later(service);
            Equal(service.ObstructionAt(radio,new(100),false),1,"unknown scaled collision mapping keeps the cabin muffled");
            walk.lossyScale=Vector3.one;
            boat.transform.Children.Add(new BoatEmbarkCollider {Model=model,Root=boat.transform,walkCollider=new Transform()});
            service.Invalidate();service.Tick(); Physics.Hits=Array.Empty<RaycastHit>();
            Equal(service.ObstructionAt(radio,new(100),false),1,"ambiguous walk mappings do not establish clear deck space");
            boat.transform.Children.Clear(); service.Invalidate();service.Tick();
            Equal(service.ObstructionAt(radio,new(100),false),1,"missing walk mapping keeps the cabin muffled");
            cabin.transform.parent=null; service.Invalidate();service.Tick();
            Equal(service.ObstructionAt(radio,new(100),false),0,"land trigger boundary without physical wall stays clear");
            Physics.Hits=new[]{new RaycastHit{collider=wall}}; Later(service);
            Equal(service.ObstructionAt(radio,new(100),false),1,"land wall crossing retains existing cabin attenuation");
            Physics.Hits=new RaycastHit[64]; Later(service);
            Equal(service.ObstructionAt(radio,new(100),false),1,"full ray buffer cannot falsely establish unobstructed space");

            Physics.Hits=Array.Empty<RaycastHit>(); Later(service);
            Equal(service.ObstructionAt(radio,new(100),false),0,"a wall that is no longer there clears at the next check");
            Physics.Hits=new[]{new RaycastHit{collider=wall}};
            var rig=new Transform{position=new(105)}; Refs.ovrCameraRig=rig; service.Tick();
            BoatCamera.on=true; listener.transform.position=new(300); service.Tick();
            queries=Physics.Queries;
            Equal(service.ObstructionAt(radio,new(100),false,true),1,"switching to the head checks its walls at once");
            Check(Physics.Queries==queries+1,"a switch casts one ray");
            service.ObstructionAt(radio,new(100),false,true);
            Check(Physics.Queries==queries+1,"staying on the head casts no more until the next check");
            BoatCamera.on=false; Refs.ovrCameraRig=null; listener.transform.position=new(105); service.Tick();
        }
        Physics.Hits=new[] {new RaycastHit {collider=new BoxCollider {isTrigger=false}}};
    }
}
