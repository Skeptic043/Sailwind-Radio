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
    private static void Main()
    {
        var listener=new AudioListener(); UObject.Registry.Add(listener);
        var inactive=new AudioListener(); inactive.gameObject.activeInHierarchy=false; UObject.Registry.Add(inactive);
        var mesh=new MeshCollider(); var room=Room(mesh);
        using(var service=new RadioAcousticsService())
        {
            service.Tick(); Check(service.ListenerKnown,"only enabled and hierarchy-active listener counts");
            Equal(service.ObstructionAt(new(1),false),0,"same convex cabin clear");
            Equal(service.ObstructionAt(new(5),false),1,"outside source inside listener muffled");
            listener.transform.position=new(5); service.Tick();
            Equal(service.ObstructionAt(new(0),false),1,"inside source outside listener muffled");
            Equal(service.ListenerPosition.x,5,"actual listener transform read every frame");
            Equal(service.ObstructionAt(new(6),false),0,"both outside clear");
            Equal(service.ObstructionAt(new(0),true),0,"carried and player-inventory source shares listener membership");
            var door=new GPButtonTrapdoor(); room.doors=new[]{door};
            door.Open=true; Equal(service.ObstructionAt(new(0),false),.35f,"door open changes without listener entering or native Refresh");
            door.Open=false; Equal(service.ObstructionAt(new(0),false),1,"door closing changes immediately");
            room.houseDoor=new GPButtonHouseDoor { Open=true };
            Equal(service.ObstructionAt(new(0),false),.35f,"houseDoor overrides trapdoor list");
            room.houseDoors=new[]{new GPButtonHouseDoor()};
            Equal(service.ObstructionAt(new(0),false),1,"houseDoors list overrides single houseDoor");
            room.houseDoors[0].Open=true; Equal(service.ObstructionAt(new(0),false),.35f,"houseDoors open reads live");
            room.houseDoors=new GPButtonHouseDoor[]{null}; Equal(service.ObstructionAt(new(0),false),0,"missing door fails open");
            room.houseDoors=null; room.houseDoor=null; room.doors=null; room.semiIndoor=true;
            Equal(service.ObstructionAt(new(0),false),.35f,"unbound semiIndoor fallback"); room.semiIndoor=false;
            mesh.convex=false; Equal(service.ObstructionAt(new(0),false),0,"unsupported nonconvex mesh fails open"); mesh.convex=true;
            mesh.sharedMesh=null; Equal(service.ObstructionAt(new(0),false),0,"missing geometry fails open"); mesh.sharedMesh=new();
            mesh.enabled=false; Equal(service.ObstructionAt(new(0),false),0,"disabled collider ignored"); mesh.enabled=true;
            room.enabled=false; Equal(service.ObstructionAt(new(0),false),0,"disabled trigger ignored"); room.enabled=true;
            mesh.isTrigger=false; Equal(service.ObstructionAt(new(0),false),0,"nontrigger collider not treated as interior"); mesh.isTrigger=true;
            mesh.Geometry=p=>new Vector3(10); Equal(service.ObstructionAt(new(0),false),0,"mesh narrow phase rejects point inside only its bounding box"); mesh.Geometry=null;
            mesh.transform.position=new(10); Equal(service.ObstructionAt(new(0),false),0,"moving vessel geometry uses current world transform");
            Equal(service.ObstructionAt(new(10),false),1,"moving cabin follows source"); mesh.transform.position=new(0);
            var second=Room(new BoxCollider { transform=new Transform { position=new(5) } });
            SceneManager.Load(); service.Tick(); Equal(service.ObstructionAt(new(0),false),1,"different closed rooms muffled");
            var common=Room(new BoxCollider { Half=10 }); SceneManager.Load(); service.Tick();
            Equal(service.ObstructionAt(new(0),false),0,"any common overlapping volume clears artificial internal boundary");
            common.gameObject.activeInHierarchy=false; second.gameObject.activeInHierarchy=false;
            Equal(service.ObstructionAt(new(0),false),1,"inactive volumes excluded immediately");
            int searches=UObject.Searches; for(int i=0;i<100;i++) service.Tick();
            Check(UObject.Searches==searches,"steady frames do not scan scene or allocate cache");
            listener.enabled=false; service.Tick(); Check(!service.ListenerKnown,"disabled cached listener immediately unknown");
            listener.enabled=true; service.Tick(); Check(service.ListenerKnown,"cached listener reenabled without scan");
            var extra=new AudioListener(); UObject.Registry.Add(extra);
            Time.realtimeSinceStartup=.3f; service.Tick(); Check(!service.ListenerKnown,"ambiguous active listeners fail muted");
            extra.enabled=false; service.Tick(); Check(service.ListenerKnown,"ambiguity resolved every frame");
            listener.Destroyed=true; service.Tick(); Check(!service.ListenerKnown,"destroyed listener immediately unknown");
            var replacement=new AudioListener { transform=new Transform { position=new(42) } }; UObject.Registry.Add(replacement);
            SceneManager.Unload(); service.Tick(); Check(service.ListenerKnown,"scene changes reacquire listener immediately");
            Equal(service.ListenerPosition.x,42,"replacement listener position supplied");
            replacement.transform.position=new(float.NaN); service.Tick();
            Check(!service.ListenerKnown,"invalid listener position fails muted without physics queries");
            replacement.transform.position=new(42); service.Tick();
            Equal(service.ObstructionAt(new(float.PositiveInfinity),false),0,"invalid source point never reaches native geometry");
            service.Dispose(); Check(!service.ListenerKnown&&SceneManager.Subscribers==0,"dispose clears listener and unregisters callbacks");
            searches=UObject.Searches; service.Tick(); Check(UObject.Searches==searches,"disposed service performs no discovery");
        }
        Check(!RadioAcousticsService.TryContains(new Collider[]{new BoxCollider(),new MeshCollider {convex=false}},new(0),out _),
            "one unsupported active collider makes whole volume unknown");
        CheckPhysicalBoundaries();
        Console.WriteLine(checks+" acoustics checks passed using production service and native geometry/lifecycle doubles. Unity spatial audio remains a live check.");
    }

    private static void CheckPhysicalBoundaries()
    {
        UObject.Registry.Clear();
        Time.realtimeSinceStartup=10;
        var listener=new AudioListener(); listener.transform.position=new(105); UObject.Registry.Add(listener);
        var boat=new BoatDamage();
        var model=new Transform {parent=boat.transform,position=new(100)};
        var walk=new Transform {position=new(1000)};
        var cabin=Room(new BoxCollider {transform=new Transform {position=new(100)}});
        cabin.transform.parent=model;
        var embark=new BoatEmbarkCollider {Model=model,Root=boat.transform,walkCollider=walk};
        boat.transform.Children.Add(embark);
        Physics.Hits=Array.Empty<RaycastHit>();
        using(var service=new RadioAcousticsService())
        {
            service.Tick();
            Equal(service.ObstructionAt(new(100),false),0,"deck trigger protrusion with clear mapped collision ray stays open air");
            Equal(Physics.Origin.x,1000,"both visual source and listener are mapped to the owning boat walk space");
            int queries=Physics.Queries;
            for(int i=0;i<100;i++) service.ObstructionAt(new(100),false);
            Check(Physics.Queries==queries,"stable source-listener ray uses bounded short-lived cache");
            var wall=new BoxCollider {isTrigger=false}; wall.transform.parent=walk;
            Physics.Hits=new[]{new RaycastHit{collider=wall}}; Time.realtimeSinceStartup+=.2f;
            Equal(service.ObstructionAt(new(100),false),1,"actual cabin wall in owning walk hierarchy retains muffling");
            cabin.doors=new[]{new GPButtonTrapdoor {Open=true}};
            Equal(service.ObstructionAt(new(100),false),.35f,"door attenuation updates immediately while barrier query remains cached");
            cabin.doors=null;
            var otherBoatWall=new BoxCollider {isTrigger=false}; otherBoatWall.transform.parent=new Transform();
            Physics.Hits=new[]{new RaycastHit{collider=otherBoatWall}}; Time.realtimeSinceStartup+=.2f;
            Equal(service.ObstructionAt(new(100),false),0,"another boat's nearby fixed collider cannot become this cabin's wall");
            var item=new ShipItem(); item.transform.parent=walk;
            var itemCollider=new BoxCollider {isTrigger=false}; itemCollider.transform.parent=item.transform;
            var avatar=new PlayerControllerMirror(); avatar.transform.parent=walk;
            var avatarCollider=new BoxCollider {isTrigger=false}; avatarCollider.transform.parent=avatar.transform;
            Physics.Hits=new[]{new RaycastHit{collider=itemCollider},new RaycastHit{collider=avatarCollider}};
            Time.realtimeSinceStartup+=.2f;
            Equal(service.ObstructionAt(new(100),false),0,"device cargo and player colliders cannot muffle their own endpoint");
            Physics.Hits=new[]{new RaycastHit{collider=wall}};
            Equal(service.ObstructionAt(new(100),true),0,"carried output bypasses cabin checks");
            walk.lossyScale=new(2,2,2);
            Equal(service.ObstructionAt(new(100),false),1,"unknown scaled collision mapping retains conservative native membership");
            walk.lossyScale=Vector3.one;
            boat.transform.Children.Add(new BoatEmbarkCollider {Model=model,Root=boat.transform,walkCollider=new Transform()});
            service.Invalidate();service.Tick(); Physics.Hits=Array.Empty<RaycastHit>();
            Equal(service.ObstructionAt(new(100),false),1,"ambiguous walk mappings do not establish clear deck space");
            boat.transform.Children.Clear(); service.Invalidate();service.Tick();
            Equal(service.ObstructionAt(new(100),false),1,"missing walk mapping preserves native cabin obstruction");
            cabin.transform.parent=null; service.Invalidate();service.Tick();
            Equal(service.ObstructionAt(new(100),false),0,"land trigger boundary without physical wall stays clear");
            Physics.Hits=new[]{new RaycastHit{collider=wall}}; Time.realtimeSinceStartup+=.2f;
            Equal(service.ObstructionAt(new(100),false),1,"land wall crossing retains existing cabin attenuation");
            Physics.Hits=new RaycastHit[64]; Time.realtimeSinceStartup+=.2f;
            Equal(service.ObstructionAt(new(100),false),1,"full ray buffer cannot falsely establish unobstructed space");
        }
    }
}
