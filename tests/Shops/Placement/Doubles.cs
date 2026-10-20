using System;
using System.Collections.Generic;
using NQ=System.Numerics.Quaternion;
using NV=System.Numerics.Vector3;
namespace UnityEngine
{
 public class Object{public static implicit operator bool(Object o)=>o!=null;}
 public class Component:Object
 {
  public Transform transform;
  public GameObject gameObject=>transform.gameObject;
  public string name=>transform.name;
  public T GetComponent<T>() where T:Component=>transform.GetComponent<T>();
  public T GetComponentInParent<T>() where T:Component=>transform.GetComponentInParent<T>();
 }
 public class Transform:Object
 {
  public string name="fixture";public Transform parent;public Vector3 position,localPosition,localScale=Vector3.one,lossyScale=Vector3.one;public Quaternion rotation=Quaternion.identity;
  public GameObject gameObject=new();
  public Vector3 up=>rotation*Vector3.up;public Vector3 forward=>rotation*Vector3.forward;
  public Dictionary<Type,Component> Components=new();
  public T Add<T>() where T:Component,new(){var c=new T{transform=this};Components[typeof(T)]=c;return c;}
  public T GetComponent<T>() where T:Component=>Components.TryGetValue(typeof(T),out var c)?(T)c:null;
  public T GetComponentInParent<T>() where T:Component=>GetComponent<T>()??parent?.GetComponentInParent<T>();
  public Vector3 TransformPoint(Vector3 v)=>position+rotation*Vector3.Scale(v,lossyScale);
  public Vector3 InverseTransformPoint(Vector3 v){var p=Quaternion.Inverse(rotation)*(v-position);return new(p.x/lossyScale.x,p.y/lossyScale.y,p.z/lossyScale.z);}
 }
 public class GameObject:Object{public bool activeInHierarchy=true;}
 public struct Quaternion
 {
  public NQ Value;public static Quaternion identity=>new(){Value=NQ.Identity};public static Quaternion Inverse(Quaternion q)=>new(){Value=NQ.Inverse(q.Value)};
  public static Quaternion LookRotation(Vector3 f,Vector3 up)=>new(){Value=NQ.CreateFromAxisAngle(NV.UnitY,MathF.Atan2(f.x,f.z))};
  public static Quaternion Euler(float x,float y,float z)=>new(){Value=NQ.CreateFromYawPitchRoll(y*MathF.PI/180,x*MathF.PI/180,z*MathF.PI/180)};
  public static Quaternion operator *(Quaternion a,Quaternion b)=>new(){Value=a.Value*b.Value};
  public static Vector3 operator *(Quaternion q,Vector3 v){var p=NV.Transform(new NV(v.x,v.y,v.z),q.Value);return new(p.X,p.Y,p.Z);}
 }
 public class Rigidbody:Object{}
 public class Collider:Component{public bool enabled=true,isTrigger;public Rigidbody attachedRigidbody;}
 public class BoxCollider:Collider{public Vector3 center,size=Vector3.one;}
 public struct RaycastHit{public Collider collider;public Vector3 point,normal;}
 public enum QueryTriggerInteraction{Ignore,Collide}
 public static class Physics
 {
  public static Collider Ground=new Transform().Add<Collider>();
  public static Func<Vector3,float,RaycastHit?> Support=(p,d)=>p.y>=0&&p.y-d<=0?new RaycastHit{collider=Ground,point=new Vector3(p.x,0,p.z),normal=Vector3.up}:null;
  public static Func<Vector3,Collider[]> Overlap=_=>Array.Empty<Collider>();public static RaycastHit[] Path=Array.Empty<RaycastHit>();public static int Rays;
  public static bool Raycast(Vector3 p,Vector3 dir,out RaycastHit hit,float d,int mask,QueryTriggerInteraction q){Rays++;var h=Support(p,d);hit=h??default;return h.HasValue;}
  public static Collider[] OverlapBox(Vector3 p,Vector3 h,Quaternion r,int m,QueryTriggerInteraction q)=>Overlap(p);
  public static RaycastHit[] RaycastAll(Vector3 p,Vector3 d,float length,int m,QueryTriggerInteraction q)=>Path;
 }
}
public class IslandSceneryScene:UnityEngine.Component{public int parentIslandIndex;}
public class Shopkeeper:UnityEngine.Component{}
public class ShopArea:UnityEngine.Component{public Shopkeeper Keeper;public Shopkeeper GetShopkeeper()=>Keeper;}
public class ShipItem:UnityEngine.Component{}
namespace SailwindRadio.Shops{public class RadioShopStock:UnityEngine.Component{}}
