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
        public SailwindRadio.Physical.RadioItemController TestRadio;
        public T GetComponent<T>() where T : class => (TestRadio as T) ?? (TestCollider as T);
    }
    public class Collider : Object
    {
        public bool enabled = true;
        public GoPointerButton TestButton;
        public T GetComponent<T>() where T : class => TestButton as T;
        public Vector3 ClosestPoint(Vector3 _) => new Vector3();
    }
    public struct RaycastHit { public Collider collider; public float distance; }
    public class GameObject : Object { public bool activeSelf = true; public void SetActive(bool value) { activeSelf = value; } }
    public static class Application { public static bool isFocused = true; }
}
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
    public bool unclickable;
    public GoPointer LookedAtBy;
    public bool Unlooked;
    public void Look(GoPointer pointer) { LookedAtBy = pointer; }
    public void ForceUnlook() { Unlooked = true; LookedAtBy = null; }
    protected GoPointer stickyClickedBy;
    public virtual void OnActivate() { }
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
public class PickupableItem : GoPointerButton { }
namespace SailwindRadio.Physical
{
    public enum RadioAction { Power, PlayPause, Previous, Next, Shuffle, Collections }
    public sealed class RadioItemController : Object
    {
        public bool IsPlacedForControls = true;
        public bool EnsureOwned() => true;
        public RadioState State = new RadioState { Volume = .5f };
        public int RequestedCount;
        public RadioAction LastAction;
        public GoPointer RememberedPointer;
        public void RequestAction(RadioAction action) { RequestedCount++; LastAction = action; }
        public void RememberControl(GoPointer pointer, Collider control) { RememberedPointer = pointer; }
    }
}
