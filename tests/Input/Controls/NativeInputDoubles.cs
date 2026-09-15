// Small native API doubles. The production knob is compiled unchanged, but Unity input/physics is not executed.
using UnityEngine;

namespace UnityEngine
{
    public class Object { public static implicit operator bool(Object value) => value != null; }
    public class Transform : Object { public Vector3 position; }
    public class Component : Object
    {
        public bool enabled = true;
        public Transform transform = new Transform();
        public Collider TestCollider = new Collider();
        public T GetComponent<T>() where T : class => TestCollider as T;
    }
    public class Collider : Object { public Vector3 ClosestPoint(Vector3 _) => new Vector3(); }
    public class GameObject : Object { public bool activeSelf = true; public void SetActive(bool value) { activeSelf = value; } }
    public struct Vector3
    {
        public float x, y, z;
        public Vector3(float x, float y, float z) { this.x = x; this.y = y; this.z = z; }
        public static float Distance(Vector3 a, Vector3 b) => (float)System.Math.Sqrt(
            (a.x - b.x) * (a.x - b.x) + (a.y - b.y) * (a.y - b.y) + (a.z - b.z) * (a.z - b.z));
    }
    public static class Application { public static bool isFocused = true; }
    public static class Time { public static float timeScale = 1; }
    public static class Mathf
    {
        public static float Clamp01(float value) => System.Math.Clamp(value, 0, 1);
        public static int RoundToInt(float value) => (int)System.Math.Round(value);
    }
}
public static class Refs
{
    public static Component charController = new Component();
    public static Component ovrController = new Component();
    public static GameObject mouseCrosshair = new GameObject();
}
public static class GameState
{
    public static bool playing = true, currentlyLoading, sleeping, inBed, inCursorMenu;
    public static int loadingScenes;
}
public static class BoatCamera { public static bool on; }
public static class GameInput { public static float Scroll; public static float GetScrollAxis() => Scroll; }
public class GoPointer : Component
{
    public Object Held;
    public GoPointerButton Sticky;
    public Object GetHeldItem() => Held;
    public void StickyClick(GoPointerButton button) { Sticky = button; }
    public void UnStickyClick() { Sticky = null; }
    // Relevant observed GoPointer.LateUpdate behavior. It consumes the next main click.
    public void MainClick() { if (Sticky) Sticky.UnStickyClick(); }
}
public class GoPointerButton : Component
{
    public string lookText;
    protected GoPointer stickyClickedBy;
    public virtual void OnActivate(GoPointer pointer) { }
    public virtual void OnUnactivate(GoPointer pointer) { }
    public virtual void ExtraLateUpdate() { }
    public bool IsStickyClicked() => stickyClickedBy;
    public void StickyClick(GoPointer pointer)
    {
        stickyClickedBy = pointer;
        pointer.StickyClick(this);
        Refs.charController.enabled = false;
        Refs.ovrController.enabled = false;
        Refs.mouseCrosshair.SetActive(false);
    }
    public void UnStickyClick()
    {
        if (!stickyClickedBy) return;
        stickyClickedBy.UnStickyClick();
        stickyClickedBy = null;
        Refs.charController.enabled = true;
        Refs.ovrController.enabled = true;
        Refs.mouseCrosshair.SetActive(true);
    }
}
namespace SailwindRadio.Physical
{
    public sealed class RadioItemController : Object
    {
        public bool IsPlacedForControls = true;
        public RadioState State = new RadioState { Volume = .5f };
    }
}
