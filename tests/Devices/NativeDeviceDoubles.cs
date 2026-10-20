// Explicit Unity/native doubles execute production menu ownership and vessel rules.
// They do not establish IMGUI appearance or native item/physics acceptance.
using System;
using System.Collections.Generic;
namespace UnityEngine
{
    public class Object
    {
        public bool Destroyed;
        public static implicit operator bool(Object value)=>value!=null&&!value.Destroyed;
    }
    public class Transform:Object
    {
        public Transform parent;
        public Vector3 position;
        public readonly Dictionary<Type,Object> Components=new();
        public T GetComponent<T>() where T:class => Components.TryGetValue(typeof(T),out var result) ? result as T : null;
        public T GetComponentInParent<T>() where T:class => GetComponent<T>() ?? parent?.GetComponentInParent<T>();
    }
    public class Component:Object
    {
        public bool enabled=true;
        public Transform transform=new();
        public T GetComponent<T>() where T:class=>transform.GetComponent<T>();
        public T GetComponentInParent<T>() where T:class=>transform.GetComponentInParent<T>();
    }
    public class GameObject:Object { public bool activeSelf=true; public void SetActive(bool value){activeSelf=value;} }
    public class Collider:Component
    {
        public Vector3 Closest;
        public string Tag;
        public bool CompareTag(string value)=>Tag==value;
        public Vector3 ClosestPoint(Vector3 value)=>Closest;
    }
    public struct Vector2 { public static Vector2 zero=>new(); }
    public struct Rect { public Rect(float x,float y,float w,float h){} }
    public enum CursorLockMode { None,Locked }
    public static class Cursor { public static bool visible; public static CursorLockMode lockState=CursorLockMode.Locked; }
    public static class Application { public static bool isFocused=true; }
    public enum KeyCode { Escape }
    public static class Input { public static bool Escape; public static bool GetKeyDown(KeyCode key)=>Escape; }
    public static class Screen { public static int width=1920,height=1080; }
    public class GUILayoutOption { }
    public static class GUILayout
    {
        public static string Click,ToggleLabel;
        public static readonly List<string> Labels=new();
        public static Rect Window(int id,Rect rect,Action<int> body,string title){Labels.Add(title);body(id);return rect;}
        public static void Label(string label){Labels.Add(label);}
        public static bool Button(string text,params GUILayoutOption[] options){if(Click!=text)return false;Click=null;return true;}
        public static bool Toggle(bool value,string label)=>ToggleLabel==label?!value:value;
        public static GUILayoutOption Height(float value)=>new();
        public static Vector2 BeginScrollView(Vector2 value,params GUILayoutOption[] options)=>value;
        public static void EndScrollView(){}
    }
}
public static class MouseLook
{
    public static bool Enabled=true;
    public static bool MouseLookIsEnabled()=>Enabled;
    public static void ToggleMouseLook(bool enabled){Enabled=enabled;}
    public static void ToggleMouseLookAndCursor(bool enabled)
    {
        GameState.inCursorMenu=!enabled; UnityEngine.Cursor.visible=!enabled;
        UnityEngine.Cursor.lockState=enabled?UnityEngine.CursorLockMode.Locked:UnityEngine.CursorLockMode.None;
    }
}
public class BoatDamage:UnityEngine.Component { }
public class SaveableObject:UnityEngine.Component { }
public class ShipItem:UnityEngine.Component
{
    public bool held;
    public bool nailed;
    public bool DisembarkRestricted;
    public int DisembarkChanges;
    public void ToggleDisallowDisembarking(bool value){DisembarkRestricted=value;DisembarkChanges++;}
    public ItemRigidbody itemRigidbodyC=new();
    public UnityEngine.Transform currentActualBoat;
    public ItemRigidbody GetItemRigidbody()=>itemRigidbodyC;
}
public class HangableItem:UnityEngine.Component
{
    private UnityEngine.Collider currentHook;
    public int Disconnects;
    public void SetHook(UnityEngine.Collider hook)=>currentHook=hook;
    public bool IsHanging()=>currentHook;
    public void DisconnectJoint()
    {
        currentHook=null;Disconnects++;
        transform.GetComponent<ShipItem>()?.ToggleDisallowDisembarking(true);
    }
}
public class ShipItemLampHook:ShipItem { }
namespace UnityEngine
{
    public class Rigidbody:Component { }
    public class ConfigurableJoint:Component { public Rigidbody connectedBody; }
}
public class ItemRigidbody:UnityEngine.Component
{
    public UnityEngine.Transform Box;
    public ShipItem Item;
    public UnityEngine.Rigidbody Body;
    public UnityEngine.Rigidbody GetBody()=>Body;
    public UnityEngine.Transform GetCurrentBox()=>Box;
    public ShipItem GetShipItem()=>Item;
}
public class GPButtonInventorySlot:UnityEngine.Object
{
    public static GPButtonInventorySlot[] inventorySlots=Array.Empty<GPButtonInventorySlot>();
    public ShipItem currentItem;
}
namespace SailwindRadio.Physical
{
    public sealed class RadioItemController:UnityEngine.Component
    {
        public RadioState State=new();
        public bool IsPlacedForControls=true;
        public bool EnsureOwned()=>true;
        public bool WithinMenuReach=true;
        public UnityEngine.Vector3 VisualAudioPosition;
        public float Height;
        public int Releases;
        public void ReleaseControls(){Releases++;}
        public bool? InventorySlot;
        public void SetInventorySlot(bool value){InventorySlot=value;}
    }
}
