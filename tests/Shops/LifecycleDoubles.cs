// API doubles for production shop lifecycle and purchase callbacks. Not Unity physics acceptance.
using System;
using System.Collections.Generic;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;
using SailwindRadio.Shops;
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static implicit operator bool(Object x)=>x!=null&&!x.Destroyed;
        public static readonly List<Object> Found=new();
        public static T[] FindObjectsOfType<T>() where T:Object=>Found.FindAll(x=>x is T&&!x.Destroyed).ConvertAll(x=>(T)x).ToArray();
        public static void Destroy(Object x)
        {
            if(x==null||x.Destroyed)return;
            x.Destroyed=true;
            if(x is GameObject go)
            {
                go.DestroyComponents();
                foreach(var child in GameObject.All.ToArray())if(child.transform.parent==go.transform)Destroy(child);
            }
        }
    }
    public class Component:Object
    {
        public GameObject gameObject;
        public Transform transform=>gameObject.transform;
        public string name=>gameObject.name;
        public T GetComponent<T>() where T:class=>gameObject.GetComponent<T>();
    }
    public class MonoBehaviour:Component { public bool enabled=true; }
    public class Transform:Component
    {
        public Transform parent;
        public Vector3 position,localScale=Vector3.one;
        public Quaternion rotation;
        public Vector3 lossyScale=>localScale;
        public void SetParent(Transform value,bool world){parent=value;}
        public Vector3 TransformPoint(Vector3 value)=>position+value;
    }
    public class GameObject:Object
    {
        public string name;
        public static readonly List<GameObject> All=new();
        public bool activeInHierarchy=true;
        public Transform transform;
        private readonly Dictionary<Type,Component> parts=new();
        public GameObject(string name=""){this.name=name;transform=new Transform{gameObject=this};parts[typeof(Transform)]=transform;All.Add(this);}
        internal void DestroyComponents(){foreach(var part in parts.Values)part.Destroyed=true;}
        public T AddComponent<T>() where T:Component,new()
        {
            var value=new T{gameObject=this};parts[typeof(T)]=value;
            if(value is ShopItemSpawner) AddComponent<MeshRenderer>();
            return value;
        }
        public T GetComponent<T>() where T:class=>parts.TryGetValue(typeof(T),out var value)?value as T:null;
        public void SetActive(bool value){activeInHierarchy=value;}
    }
    public class MeshRenderer:MonoBehaviour { }
    public class Collider:Component { }
    public class MissingComponentException:Exception { public MissingComponentException(string message):base(message){} }
    public class Camera:Component { public static Camera main; }
    public struct Vector3
    {
        public float x,y,z;
        public Vector3(float x,float y,float z){this.x=x;this.y=y;this.z=z;}
        public static Vector3 one=>new(1,1,1);
        public static Vector3 operator +(Vector3 a,Vector3 b)=>new(a.x+b.x,a.y+b.y,a.z+b.z);
        public static float Distance(Vector3 a,Vector3 b)=>MathF.Sqrt((a.x-b.x)*(a.x-b.x)+(a.y-b.y)*(a.y-b.y)+(a.z-b.z)*(a.z-b.z));
    }
    public struct Quaternion { public static Quaternion identity=>new(); }
    public static class Physics { public static void SyncTransforms(){} }
    public static class Time { public static float unscaledTime,deltaTime; }
}
namespace HarmonyLib
{
    public class Harmony
    {
        public Harmony(string id){}
        public void Patch(System.Reflection.MethodInfo method,HarmonyMethod prefix=null,HarmonyMethod postfix=null,HarmonyMethod finalizer=null){}
        public void UnpatchSelf(){}
    }
    public class HarmonyMethod { public HarmonyMethod(Type type,string name){} }
}
public class SaveLoadManager:UnityEngine.Object { public static SaveLoadManager instance=new(); }
public static class GameState { public static bool currentlyLoading; public static UnityEngine.Transform World=new UnityEngine.GameObject("world").transform; }
public class Sun:UnityEngine.Object { public static Sun sun=new();public float localTime=12; }
public class SaveablePrefab:UnityEngine.Component { public int instanceId;public bool registered; public void RegisterToSave(){registered=true;} }
public static class PlayerGold { public static int[] currency={10000}; }
public class Region:UnityEngine.Object { }
public class ShipItem:UnityEngine.Component
{
    public bool sold;
    public bool held;
    public int Returns;
    public void ReturnToShopPos(){held=false;Returns++;}
    private void EnterBoat(UnityEngine.Collider collider){}
    public void Sell(){sold=true;transform.SetParent(GameState.World,true);GetComponent<SaveablePrefab>().RegisterToSave();}
    public void AddToShop(ShopArea area){area.itemsForSale.Add(this);}
}
public class Shopkeeper:UnityEngine.Component
{
    private Region parentRegion=new();
    private ShopArea shop;
    internal void Register(ShopArea area){shop=area;}
    internal bool FieldsUsed()=>parentRegion!=null&&shop!=null;
    private void SellItem(ShipItem item,int price,int currency){item.Sell();PlayerGold.currency[currency]-=price;}
    private int GetPrice(ShipItem item)=>1;
    internal void UnsafeSale(ShipItem item,int price,int currency){PlayerGold.currency[currency]-=price;item.Sell();}
}
public class ShopArea:UnityEngine.Component
{
    public bool openAtNight;
    public Shopkeeper Keeper;
    public List<ShipItem> itemsForSale=new();
    public Shopkeeper GetShopkeeper()=>Keeper;
}
public class ShopItemSpawner:UnityEngine.MonoBehaviour { public float priceMult; }
public class NotificationUi:UnityEngine.Object { public static NotificationUi instance=new();public void ShowNotification(string text){} }
namespace SailwindRadio.Physical
{
    public class RadioItemController:UnityEngine.Component
    {
        public RadioState State=new();public bool IsStock;public int InstanceId;
        public void SetShopStock(bool value){IsStock=value;}
    }
    public class RadioWorldService
    {
        public bool ReadyForShop=true;
        public RadioSaveStore Store=new();internal GlobalRadioArbiter Arbiter=new();public int NextId=10,Finishes;
        public RadioItemController CreateShopStock(UnityEngine.Transform parent,UnityEngine.Vector3 pos,UnityEngine.Quaternion rot,int kind)
        {
            var go=new UnityEngine.GameObject();go.transform.SetParent(parent,true);go.AddComponent<ShipItem>();go.AddComponent<SaveablePrefab>();
            var c=go.AddComponent<RadioItemController>();c.State.Kind=kind;return c;
        }
        public bool ReservePurchase(RadioItemController c)
        {
            if(!ReadyForShop||c.GetComponent<ShipItem>().sold)return false;
            try {c.GetComponent<SaveablePrefab>().instanceId=ShopPurchaseReservation.Reserve(Store,Arbiter,c.State,()=>NextId++,i=>false);return true;}catch{return false;}
        }
        public bool FinishPurchase(RadioItemController c)
        {
            Finishes++;
            if(c.GetComponent<ShipItem>().sold&&c.GetComponent<SaveablePrefab>().registered){c.InstanceId=c.GetComponent<SaveablePrefab>().instanceId;return true;}
            ShopPurchaseReservation.Cancel(Store,Arbiter,c.GetComponent<SaveablePrefab>().instanceId);c.GetComponent<SaveablePrefab>().instanceId=0;
            return false;
        }
    }
}
namespace SailwindRadio.Shops
{
    internal static class RadioShopCatalog
    {
        internal static readonly int[] StockKinds={3,2,2,0,0,1,1};
        internal static bool Matches(ShopArea area)=>true;
    }
    internal static class RadioShopPlacement
    {
        internal static bool Clear=true;
        internal static int FailSlot=-1;
        internal static bool TryStand(ShopArea a,out UnityEngine.Vector3 p,out UnityEngine.Quaternion r,out string reason){p=default;r=default;reason=Clear?"":"blocked";return Clear;}
        internal static bool SlotClear(RadioShopStand stand,int index,int kind,out string reason){reason="blocked";return index!=FailSlot;}
    }
    internal sealed class RadioShopStand:UnityEngine.Component
    {
        internal static int Created;
        internal static RadioShopStand Create(UnityEngine.Transform scenery,UnityEngine.Vector3 position,UnityEngine.Quaternion rotation)
        {
            Created++;var go=new UnityEngine.GameObject("test stand");go.transform.SetParent(scenery,false);go.transform.position=position;go.transform.rotation=rotation;
            return go.AddComponent<RadioShopStand>();
        }
    }
}
