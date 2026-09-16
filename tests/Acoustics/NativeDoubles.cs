// Behavioral doubles for native geometry and scene discovery. These tests execute the
// production service, not Unity physics. Installed native collider coverage is separately
// established by the retained serialized-scene inspection and real-reference build.
using System;
using System.Collections.Generic;
using System.Linq;
namespace UnityEngine
{
    public class Object
    {
        public static readonly List<Object> Registry = new();
        public static int Searches;
        public bool Destroyed;
        public static implicit operator bool(Object value) => value != null && !value.Destroyed;
        public static T[] FindObjectsOfType<T>() where T : Component
        { Searches++; return Registry.OfType<T>().Where(x => x && x.gameObject.activeInHierarchy).ToArray(); }
    }
    public class GameObject : Object { public bool activeInHierarchy = true; }
    public class Transform : Object
    {
        public Component Owner;
        public Transform parent;
        public readonly List<Component> Children = new();
        public Vector3 position, lossyScale = Vector3.one;
        public bool IsChildOf(Transform candidate)
        { for(var current=this; current!=null;current=current.parent) if(current==candidate)return true; return false; }
        public Vector3 TransformPoint(Vector3 p) => p + position;
        public Vector3 InverseTransformPoint(Vector3 p) => p - position;
    }
    public class Component : Object
    {
        public GameObject gameObject = new();
        public Transform transform;
        public Component() { transform=new Transform {Owner=this}; }
        public Collider[] Colliders = Array.Empty<Collider>();
        public T[] GetComponents<T>() => Colliders.OfType<T>().ToArray();
        public T GetComponent<T>() where T : Component => this as T;
        public T GetComponentInParent<T>() where T : Component
        { for(var current=transform;current!=null;current=current.parent) if(current.Owner is T match)return match; return null; }
        public T[] GetComponentsInChildren<T>() where T : Component => transform.Children.OfType<T>().ToArray();
    }
    public class Behaviour : Component { public bool enabled = true; }
    public class AudioListener : Behaviour { }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y=0, float z=0) { this.x=x; this.y=y; this.z=z; }
        public float sqrMagnitude => x*x+y*y+z*z;
        public float magnitude => (float)Math.Sqrt(sqrMagnitude);
        public Vector3 normalized => new(x/magnitude,y/magnitude,z/magnitude);
        public static Vector3 one => new(1,1,1);
        public static Vector3 operator +(Vector3 a,Vector3 b) => new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static Vector3 operator -(Vector3 a,Vector3 b) => new(a.x-b.x,a.y-b.y,a.z-b.z);
    }
    public struct Bounds
    {
        public Vector3 center;
        public float Half;
        public Vector3 Closest(Vector3 p) => new(Math.Clamp(p.x,center.x-Half,center.x+Half),
            Math.Clamp(p.y,center.y-Half,center.y+Half),Math.Clamp(p.z,center.z-Half,center.z+Half));
        public float SqrDistance(Vector3 p) => (Closest(p)-p).sqrMagnitude;
    }
    public class Collider : Component
    {
        public bool enabled = true, isTrigger = true;
        public float Half = 2;
        public int Queries;
        public Func<Vector3, Vector3> Geometry;
        public Bounds bounds => new() { center=transform.position, Half=Half };
        public Vector3 ClosestPoint(Vector3 point) { Queries++; return Geometry == null ? bounds.Closest(point) : Geometry(point); }
    }
    public class BoxCollider : Collider { }
    public class CapsuleCollider : Collider { }
    public class Mesh : Object { }
    public class MeshCollider : Collider { public bool convex=true; public Mesh sharedMesh=new(); }
    public static class Time { public static float realtimeSinceStartup; }
    public struct RaycastHit { public Collider collider; }
    public enum QueryTriggerInteraction { Ignore }
    public static class Physics
    {
        public static RaycastHit[] Hits = new[] {new RaycastHit {collider=new BoxCollider {isTrigger=false}}};
        public static int Queries;
        public static Vector3 Origin, Direction;
        public static int RaycastNonAlloc(Vector3 origin,Vector3 direction,RaycastHit[] hits,float distance,int mask,QueryTriggerInteraction query)
        {
            Queries++; Origin=origin;Direction=direction;
            int count=Math.Min(hits.Length,Hits.Length); Array.Copy(Hits,hits,count); return count;
        }
    }
}
namespace UnityEngine.SceneManagement
{
    public struct Scene { }
    public enum LoadSceneMode { Additive }
    public static class SceneManager
    {
        public static event Action<Scene,LoadSceneMode> sceneLoaded;
        public static event Action<Scene> sceneUnloaded;
        public static void Load() => sceneLoaded?.Invoke(new(),LoadSceneMode.Additive);
        public static void Unload() => sceneUnloaded?.Invoke(new());
        public static int Subscribers => (sceneLoaded?.GetInvocationList().Length ?? 0)+(sceneUnloaded?.GetInvocationList().Length ?? 0);
    }
}
public class GPButtonTrapdoor : UnityEngine.Object { public bool Open; public bool IsOpen() => Open; }
public class GPButtonHouseDoor : UnityEngine.Object { public bool Open; public bool IsOpen() => Open; }
public class InteriorEffectsTrigger : UnityEngine.Behaviour
{
    public bool semiIndoor;
    public GPButtonTrapdoor[] doors;
    public GPButtonHouseDoor houseDoor;
    public GPButtonHouseDoor[] houseDoors;
}
public class BoatDamage : UnityEngine.Component { }
public class ShipItem : UnityEngine.Component { }
public class PlayerControllerMirror : UnityEngine.Component { }
public class BoatEmbarkCollider : UnityEngine.Component
{
    public UnityEngine.Transform Model, Root, walkCollider;
    public UnityEngine.Transform GetMeshParent()=>Model;
    public UnityEngine.Transform GetTopmostBoatParent()=>Root;
}
public static class Refs { public static UnityEngine.Collider charController; }
