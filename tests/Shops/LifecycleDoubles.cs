// API doubles for production shop lifecycle and purchase callbacks. Not Unity physics acceptance.
using System;
using System.Collections.Generic;
using System.Reflection;
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
        public static GameObject Instantiate(GameObject source,Transform parent)
        {
            var clone=new GameObject(source.name+"(Clone)");
            clone.transform.SetParent(parent,false);
            if(source.GetComponent<global::Shopkeeper>() is global::Shopkeeper)clone.AddComponent<global::Shopkeeper>();
            if(source.GetComponent<Renderer>() is Renderer)clone.AddComponent<Renderer>();
            if(source.GetComponent<SphereCollider>() is SphereCollider sourceSphere)
            {
                var sphere=clone.AddComponent<SphereCollider>();sphere.isTrigger=sourceSphere.isTrigger;sphere.radius=sourceSphere.radius;
            }
            else if(source.GetComponent<Collider>() is Collider sourceCollider)
                clone.AddComponent<Collider>().isTrigger=sourceCollider.isTrigger;
            foreach(var child in GameObject.All.ToArray())
                if(child.transform.parent==source.transform)Instantiate(child,clone.transform);
            return clone;
        }
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
        public T GetComponentInChildren<T>() where T:class=>null;
        public T[] GetComponentsInChildren<T>(bool includeInactive=false) where T:class
        {
            var found=new List<T>();
            foreach(var candidate in GameObject.All)
                for(var parent=candidate.transform;parent!=null;parent=parent.parent)
                    if(parent==transform)
                    {
                        if((includeInactive||candidate.activeInHierarchy) && candidate.GetComponent<T>() is T component)
                            found.Add(component);
                        break;
                    }
            return found.ToArray();
        }
        public T GetComponentInParent<T>() where T:class
        {
            for(var parent=transform;parent!=null;parent=parent.parent)
                if(parent.gameObject.GetComponent<T>() is T component)return component;
            return null;
        }
    }
    public class MonoBehaviour:Component { public bool enabled=true; }
    public class Transform:Component
    {
        public Transform parent;
        public Vector3 position,localPosition,localScale=Vector3.one;
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
        public T GetComponent<T>() where T:class
        {
            if(parts.TryGetValue(typeof(T),out var value))return value as T;
            foreach(var part in parts.Values)if(part is T match)return match;
            return null;
        }
        public T[] GetComponentsInChildren<T>(bool includeInactive=false) where T:class=>transform.GetComponentsInChildren<T>(includeInactive);
        public void SetActive(bool value){activeInHierarchy=value;}
    }
    public class Renderer:MonoBehaviour { }
    public class MeshRenderer:Renderer { }
    public class Collider:Component { public bool enabled=true,isTrigger; }
    public class SphereCollider:Collider { public float radius=2f; }
    public class BoxCollider:Collider { public Vector3 center,size; }
    public class Rigidbody:Component { public bool isKinematic,useGravity; }
    public class MissingComponentException:Exception { public MissingComponentException(string message):base(message){} }
    public class Camera:Component { public static Camera main; }
    public struct Quaternion { public static Quaternion identity=>new(); public static Quaternion Euler(float x,float y,float z)=>new();public static Quaternion operator *(Quaternion a,Quaternion b)=>new(); }
    public static class Physics
    {
        public static void SyncTransforms(){}
    }
    public static class Random { public static int Next=10; public static Func<int> Source; public static int Range(int min,int max)=>Source!=null?Source():Next++; }
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
public class SaveLoadManager:UnityEngine.Object
{
    public static SaveLoadManager instance=new();
    public readonly List<SaveablePrefab> Prefabs=new();
    public readonly List<NativeObject> Objects=new();
    public List<SaveablePrefab> GetCurrentPrefabs()=>Prefabs;
    public List<NativeObject> GetCurrentObjects()=>Objects;
}
public class NativeObject:UnityEngine.Object { public LocalItems localItems; }
public class LocalItems:UnityEngine.Object
{
    public readonly List<SaveablePrefab> Cached=new();
    public bool HasLocalItems()=>Cached.Count>0;
    public List<SaveablePrefab> GetCachedItems()=>Cached;
}
public class IslandSceneryScene:UnityEngine.Component { public int parentIslandIndex; }
public static class GameState { public static bool currentlyLoading,playing=true; public static int loadingScenes; public static UnityEngine.Transform World=new UnityEngine.GameObject("world").transform; }
public class Sun:UnityEngine.Object { public static Sun sun=new();public float localTime=12; }
public class SaveablePrefab:UnityEngine.Component
{
    public static readonly List<int> existingInstanceIds=new();
    public static bool SkipNative;
    public static Action<SaveablePrefab> BeforeNative,AfterIdAdded,AfterNative;
    public int instanceId,prefabIndex;public bool registered;
    public void RegisterToSave()
    {
        BeforeNative?.Invoke(this);
        if(!SkipNative&&!registered)
        {
            if(instanceId==0)
                do { instanceId=UnityEngine.Random.Range(1,int.MaxValue); } while(existingInstanceIds.Contains(instanceId));
            existingInstanceIds.Add(instanceId);
            AfterIdAdded?.Invoke(this);
            SaveLoadManager.instance.Prefabs.Add(this);
            registered=true;
            AfterNative?.Invoke(this);
        }
    }
}
public static class Callbacks
{
    static object Call(Type type,string method,object[] args)
    {
        try{return type.GetMethod(method,BindingFlags.Static|BindingFlags.NonPublic).Invoke(null,args);}
        catch(TargetInvocationException e){throw e.InnerException;}
    }
    public static object Shop(string method,params object[] args)=>Call(typeof(SailwindRadio.Shops.RadioShopService),method,args);
    public static object World(string method,params object[] args)=>Call(typeof(SailwindRadio.Physical.RadioWorldService),method,args);
}
public static class PlayerGold { public static int[] currency={10000,10000,10000,10000}; }
public static class PlayerReputation { public static float[] retailDiscounts={0,0,0,0}; }
public class CurrencyMarket:UnityEngine.Object { public static CurrencyMarket instance=new(); }
public class UISoundPlayer:UnityEngine.Object { public static UISoundPlayer instance=new(); }
public class BuyItemUI:UnityEngine.Object { public static BuyItemUI instance=new(); }
public class DayLog:UnityEngine.Object { }
public class DayLogs:UnityEngine.Object { public static DayLogs instance=new();public DayLog[] dayLogs={new(),new(),new(),new()}; }
public class MoneyNotification:UnityEngine.Object { public static MoneyNotification instance=new(); }
public enum PortRegion { alankh=0,emerald=1,medi=2,none=3 }
public class Region:UnityEngine.Component { public static PortRegion DefaultPortRegion=PortRegion.emerald;public PortRegion portRegion=DefaultPortRegion; }
public class ShipItem:UnityEngine.Component
{
    public bool sold;
    public int value=100;
    public bool held;
    public int Returns;
    public void ReturnToShopPos(){held=false;Returns++;}
    private void EnterBoat(UnityEngine.Collider collider){}
    public static bool SkipSell;
    public static Action<ShipItem> BeforeBody,AfterOwnership,AfterRegister;
    public int OnBuyCount;
    public void Sell()
    {
        BeforeBody?.Invoke(this);
        if(!SkipSell)
        {
            sold=true;
            transform.SetParent(GameState.World,true);
            AfterOwnership?.Invoke(this);
            GetComponent<SaveablePrefab>().RegisterToSave();
            AfterRegister?.Invoke(this);
            OnBuyCount++;
        }
    }
    private ShopArea shopArea;
    public void AddToShop(ShopArea area){if(!shopArea){area.itemsForSale.Add(this);shopArea=area;}}
}
public class Shopkeeper:UnityEngine.Component
{
    private Region parentRegion=new();
    private ShopArea shop;
    private object economy=new();
    private UnityEngine.Transform homePos=new UnityEngine.GameObject("home").transform;
    internal void Register(ShopArea area){shop=area;}
    public void RegisterShop(ShopArea area){shop=area;}
    internal bool FieldsUsed()=>parentRegion!=null&&shop!=null&&homePos!=null;
    private void SellItem(ShipItem item,int price,int currency){item.Sell();PlayerGold.currency[currency]-=price;}
    private int GetPrice(ShipItem item)=>1;
    private void OnTriggerEnter(UnityEngine.Collider other){}
    internal void UnsafeSale(ShipItem item,int price,int currency){PlayerGold.currency[currency]-=price;item.Sell();}
}
public class ShopArea:UnityEngine.Component
{
    public bool openAtNight;
    private Shopkeeper keeper;
    public Shopkeeper Keeper { get=>keeper; set=>keeper=value; }
    // Unity does not call the managed field initializer of the installed native
    // ShopArea when the component is created dynamically.
    public List<ShipItem> itemsForSale;
    public Shopkeeper GetShopkeeper()=>keeper;
    private void OnTriggerEnter(UnityEngine.Collider other)
    {
        var item=other.GetComponent<ShipItem>();
        if(item!=null && !itemsForSale.Contains(item))item.AddToShop(this);
    }
}
public class ShopItemSpawner:UnityEngine.MonoBehaviour { public float priceMult; }
public class NotificationUi:UnityEngine.Object { public static NotificationUi instance=new();public void ShowNotification(string text){} }
namespace SailwindRadio.Physical
{
    public class RadioItemController:UnityEngine.Component
    {
        public RadioState State=new();public bool IsStock;public int InstanceId;
        public bool IsStackTop=>false;public RadioItemController RecordOwner=>this;
        public IReadOnlyList<RadioItemController> Stack=>System.Array.Empty<RadioItemController>();
        public void SetShopStock(bool value){IsStock=value;}
        public void RefreshOwnership(){InstanceId=GetComponent<SaveablePrefab>().instanceId;}
        public void ApplyRestoredState(RadioWorldService owner,RadioState state){State=state;RefreshOwnership();}
    }
    public class RadioTemplateMarker:UnityEngine.Component { public int Kind; }
    public sealed partial class RadioWorldService
    {
        private bool disposed=false;
        private bool loading=false;
        private readonly HashSet<SaveablePrefab> loadingPrefabs=new();
        private readonly List<RadioItemController> items=new();
        private SaveLoadManager manager=SaveLoadManager.instance;
        private readonly RadioSaveStore store=new();
        private readonly GlobalRadioArbiter arbiter=new();
        public readonly List<string> Warnings=new();
        private Action<string> warn;
        public bool ReadyForShop=true,ReadyToStageShop=true;
        public RadioSaveStore Store=>store;
        internal GlobalRadioArbiter Arbiter=>arbiter;
        public IReadOnlyList<RadioItemController> Items=>items;
        public int StockBudget=-1;
        internal const string DonorName="138 model ship junk (small)";
        public RadioWorldService(){warn=Warnings.Add;}
        public void Tick(){BindPending();foreach(var item in items.ToArray())ReconcileShopOwnership(item);}
        private static void Guard(Action action){try{action();}catch{}}
        internal static bool IsTemplateInstanceName(string name,int kind)=>name=="Sailwind Radio item "+RadioSaveStore.ItemIndex(kind);
        public void Loading(bool value){loading=value;}
        public void LoadingPrefab(SaveablePrefab prefab,bool value){if(value)loadingPrefabs.Add(prefab);else loadingPrefabs.Remove(prefab);}
        public void Loaded(RadioItemController controller){pendingBindings.Remove(controller);}
        public void Managed(RadioItemController controller){pendingBindings.Remove(controller);items.Add(controller);}
        public string Save(){Tick();arbiter.WriteTo(store);return store.Save();}
        public void Capture(RadioItemController controller){EnsureOwned(controller);if(items.Contains(controller))store.Put(RadioRecord.Capture(controller.InstanceId,controller.GetComponent<SaveablePrefab>().prefabIndex,controller.State));}
        public RadioItemController CreateShopStock(UnityEngine.Transform parent,UnityEngine.Vector3 pos,UnityEngine.Quaternion rot,int kind)
        {
            if(!ReadyToStageShop||StockBudget==0)return null;
            if(StockBudget>0)StockBudget--;
            var go=new UnityEngine.GameObject("Sailwind Radio item "+RadioSaveStore.ItemIndex(kind));go.transform.SetParent(parent,true);go.transform.position=pos;go.transform.rotation=rot;go.AddComponent<ShipItem>();go.AddComponent<UnityEngine.BoxCollider>();
            go.AddComponent<SaveablePrefab>().prefabIndex=RadioSaveStore.ItemIndex(kind);
            go.AddComponent<RadioTemplateMarker>().Kind=kind;
            var c=go.AddComponent<RadioItemController>();c.State.Kind=kind;pendingBindings.Add(c);return c;
        }
    }
}
namespace SailwindRadio.Shops
{
    internal static class RadioShopPlacement
    {
        internal static bool Clear=true;
        internal static bool TryStand(IslandSceneryScene a,out UnityEngine.Vector3 p,out UnityEngine.Quaternion r,out string reason){RadioStandLayout.TryAnchor(a.parentIslandIndex,out var anchor,out _);p=a.transform.TransformPoint(anchor);r=default;reason=Clear?"":"blocked";return Clear;}
    }
    internal sealed class RadioShopStand:UnityEngine.Component
    {
        internal static int Created;
        internal bool Visible=true;
        internal bool UsesNativeGoldRockCounter {get;set;}
        internal void SetVisible(bool visible){Visible=visible;}
        internal UnityEngine.GameObject VendorVisual { get; private set; }
        internal static RadioShopStand Create(UnityEngine.Transform scenery,UnityEngine.Vector3 position,UnityEngine.Quaternion rotation,int island)
        {
            Created++;var go=new UnityEngine.GameObject("test stand");go.transform.SetParent(scenery,false);go.transform.position=position;go.transform.rotation=rotation;
            var stand=go.AddComponent<RadioShopStand>();stand.VendorVisual=new UnityEngine.GameObject("vendor body");stand.VendorVisual.transform.SetParent(go.transform,false);stand.VendorVisual.AddComponent<UnityEngine.Collider>();return stand;
        }
    }
}
