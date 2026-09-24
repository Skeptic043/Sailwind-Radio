using System;
using System.Reflection;
using SailwindRadio.Physical;
using UnityEngine;

internal static class Program
{
    private static int checks;
    private static void Check(bool value, string reason) { checks++; if (!value) throw new Exception(reason); }
    private static (RadioVolumeKnob knob, GoPointer pointer) Fresh()
    {
        Refs.charController = new Component(); Refs.ovrController = new Component(); Refs.mouseCrosshair = new GameObject();
        GameState.playing = true;
        GameState.currentlyLoading = GameState.sleeping = GameState.inBed = GameState.inCursorMenu = false;
        GameState.loadingScenes = 0;
        Application.isFocused = true; Time.timeScale = 1; BoatCamera.on = false; GameInput.Scroll = 0;
        return (new RadioVolumeKnob { Radio = new RadioItemController() }, new GoPointer { transform = new Transform { position = new Vector3(0, 0, 1) } });
    }
    private static void Callback(RadioVolumeKnob knob, string name, params object[] args) =>
        typeof(RadioVolumeKnob).GetMethod(name, BindingFlags.Instance | BindingFlags.NonPublic).Invoke(knob, args);

    public static void Main()
    {
        var (knob, pointer) = Fresh();
        GameInput.Scroll = .4f;
        knob.ExtraLateUpdate();
        Check(knob.Radio.State.Volume == .5f, "hover alone never adjusts volume");
        knob.OnActivate(pointer);
        Check(knob.IsStickyClicked() && pointer.Sticky == knob && !Refs.charController.enabled, "first click engages native sticky control");
        knob.OnUnactivate(pointer);
        Check(knob.IsStickyClicked(), "releasing first click does not disengage");
        knob.ExtraLateUpdate();
        Check(Math.Abs(knob.Radio.State.Volume - .6f) < .0001, "scroll adjusts volume while engaged");
        GameInput.Scroll = 100; knob.ExtraLateUpdate();
        Check(knob.Radio.State.Volume == 1, "upper volume bound");
        GameInput.Scroll = -100; knob.ExtraLateUpdate();
        Check(knob.Radio.State.Volume == 0, "lower volume bound");
        pointer.MainClick();
        Check(!knob.IsStickyClicked() && !pointer.Sticky && Refs.charController.enabled, "next main click releases without picking up underlying item");
        GameInput.Scroll = 1; knob.ExtraLateUpdate();
        Check(knob.Radio.State.Volume == 0, "scroll after release has no effect");

        (knob, pointer) = Fresh(); pointer.Held = new UnityEngine.Object(); knob.OnActivate(pointer);
        Check(!knob.IsStickyClicked(), "holding an item cannot enter knob interaction");
        (knob, pointer) = Fresh(); pointer.transform.position = new Vector3(0, 0, 2); knob.OnActivate(pointer);
        Check(!knob.IsStickyClicked(), "initial engagement respects native 1.8m surface reach");
        (knob, pointer) = Fresh(); knob.OnActivate(pointer); pointer.transform.position = new Vector3(0, 0, 2); knob.ExtraLateUpdate();
        Check(!knob.IsStickyClicked() && Refs.charController.enabled, "moving out of reach releases control");
        (knob, pointer) = Fresh(); knob.OnActivate(pointer); knob.Radio.IsPlacedForControls = false; knob.ExtraLateUpdate();
        Check(!knob.IsStickyClicked() && Refs.charController.enabled, "pickup or inventory transition releases control");

        foreach (string boundary in new[] { "sleep", "bed", "menu", "camera", "load", "scenes", "pause", "exit" })
        {
            (knob, pointer) = Fresh(); knob.OnActivate(pointer);
            switch (boundary)
            {
                case "sleep": GameState.sleeping = true; break;
                case "bed": GameState.inBed = true; break;
                case "menu": GameState.inCursorMenu = true; break;
                case "camera": BoatCamera.on = true; break;
                case "load": GameState.currentlyLoading = true; break;
                case "scenes": GameState.loadingScenes = 1; break;
                case "pause": Time.timeScale = 0; break;
                case "exit": GameState.playing = false; break;
            }
            GameInput.Scroll = 1; knob.ExtraLateUpdate();
            Check(!knob.IsStickyClicked() && !pointer.Sticky, "late " + boundary + " boundary detaches pointer");
            bool nativeOwnsControllers = boundary == "sleep" || boundary == "bed" || boundary == "load" || boundary == "exit";
            Check(Refs.charController.enabled == !nativeOwnsControllers && Refs.ovrController.enabled == !nativeOwnsControllers,
                "late " + boundary + " boundary restores only radio-owned controller state");
            Check(Refs.mouseCrosshair.activeSelf == !(nativeOwnsControllers || boundary == "camera"),
                "late " + boundary + " boundary respects separate crosshair ownership");
            Check(knob.Radio.State.Volume == .5f, "late boundary cannot change volume");
            if (boundary == "scenes" || boundary == "pause" || boundary == "menu")
            {
                GameState.loadingScenes = 0; Time.timeScale = 1; GameState.inCursorMenu = false;
                knob.ExtraLateUpdate();
                Check(Refs.charController.enabled && Refs.ovrController.enabled && Refs.mouseCrosshair.activeSelf && !knob.IsStickyClicked(),
                    boundary + " transition completes without leaving movement frozen");
            }
        }
        (knob, pointer) = Fresh(); knob.OnActivate(pointer); knob.ReleaseInteraction();
        GameState.inCursorMenu = true; Time.timeScale = 0; knob.ExtraLateUpdate();
        Check(Refs.charController.enabled && !knob.IsStickyClicked(), "release before menu entry does not leave native controllers locked on resume");
        foreach (string lifecycle in new[] { "OnDisable", "OnDestroy", "OnApplicationFocus", "OnApplicationPause" })
        {
            (knob, pointer) = Fresh(); knob.OnActivate(pointer);
            if (lifecycle == "OnApplicationFocus") Callback(knob, lifecycle, false);
            else if (lifecycle == "OnApplicationPause") Callback(knob, lifecycle, true);
            else Callback(knob, lifecycle);
            Check(!knob.IsStickyClicked() && !pointer.Sticky && Refs.charController.enabled, lifecycle + " detaches and restores control");
        }
        (knob, pointer) = Fresh(); knob.OnActivate(pointer);
        Refs.charController = new Component { enabled = false };
        Refs.ovrController = new Component { enabled = false };
        Refs.mouseCrosshair = new GameObject { activeSelf = false };
        knob.ReleaseInteraction();
        Check(!Refs.charController.enabled && !Refs.ovrController.enabled && !Refs.mouseCrosshair.activeSelf,
            "release cannot enable replacement scene references");
        (knob, pointer) = Fresh();
        Refs.charController.enabled = false; Refs.ovrController.enabled = false; Refs.mouseCrosshair.SetActive(false);
        knob.OnActivate(pointer); knob.ReleaseInteraction();
        Check(!knob.IsStickyClicked() && !Refs.charController.enabled && !Refs.ovrController.enabled && !Refs.mouseCrosshair.activeSelf,
            "already disabled controllers cannot be acquired and later reenabled by native release");
        (knob, pointer) = Fresh(); knob.OnActivate(pointer);
        Refs.charController = null; Refs.ovrController = null; Refs.mouseCrosshair = null; GameState.playing = false;
        Callback(knob, "OnDestroy");
        Check(!knob.IsStickyClicked() && !pointer.Sticky, "teardown detaches pointer after native UI and controllers disappear");
        (knob,pointer)=Fresh(); knob.AdjustBass=true; knob.Radio.State.Kind=3;
        knob.OnActivate(pointer); GameInput.Scroll=1; knob.ExtraLateUpdate();
        Check(knob.Radio.State.Bass==.75f && knob.Radio.State.Volume==.5f,"woofer bass knob changes bass independently from local volume");
        GameInput.Scroll=100; knob.ExtraLateUpdate(); Check(knob.Radio.State.Bass==1,"bass clamps upper bound");
        GameInput.Scroll=-100; knob.ExtraLateUpdate(); Check(knob.Radio.State.Bass==0,"bass clamps lower bound");
        pointer.MainClick(); GameInput.Scroll=1; knob.ExtraLateUpdate();
        Check(knob.Radio.State.Bass==0 && Refs.charController.enabled,"second click releases bass without leaking scroll");
        (knob,pointer)=Fresh();knob.Mode=RadioKnobMode.Local;knob.OnActivate(pointer);GameInput.Scroll=-1;knob.ExtraLateUpdate();
        Check(knob.Radio.State.LocalVolume==.75f&&knob.Radio.State.Volume==.5f&&knob.Radio.State.Bass==.5f,"radio local knob never changes master or bass");
        pointer.MainClick();
        (knob,pointer)=Fresh();knob.Mode=RadioKnobMode.Master;knob.OnActivate(pointer);GameInput.Scroll=1;knob.ExtraLateUpdate();
        Check(knob.Radio.State.Volume==.75f&&knob.Radio.State.LocalVolume==1,"master knob changes shared volume independently");
        pointer.MainClick();
        (knob, pointer) = Fresh();
        var heldRadio = new RadioItemController();
        var next = new RadioActionButton { Radio = heldRadio, Action = RadioAction.Next };
        var power = new RadioPowerButton { Radio = heldRadio };
        pointer.Held = new UnityEngine.Object();
        GoPointerButton target = next;
        float lookDistance = 1.4f;
        RadioHeldItemTarget.Clear(pointer, ref target, ref lookDistance);
        Check(target == null && lookDistance == 0f, "held item clears radio button target before native drop handling");
        next.OnActivate(pointer);
        power.OnActivate();
        power.OnActivate(pointer);
        Check(heldRadio.RequestedCount == 0, "held item cannot skip a track or toggle power even if native invokes activation");
        target = knob; lookDistance = 1.4f;
        RadioHeldItemTarget.Clear(pointer, ref target, ref lookDistance);
        Check(target == null && lookDistance == 0f, "held item clears knob target before native drop handling");
        target = new GoPointerButton(); lookDistance = 1.4f;
        RadioHeldItemTarget.Clear(pointer, ref target, ref lookDistance);
        Check(target != null && lookDistance == 1.4f, "held item keeps unrelated native pointer targets untouched");
        pointer.Held = null;
        target = next; lookDistance = 1.4f;
        RadioHeldItemTarget.Clear(pointer, ref target, ref lookDistance);
        next.OnActivate(pointer);
        power.OnActivate(); power.OnActivate(pointer);
        Check(target == next && lookDistance == 1.4f && heldRadio.RequestedCount == 2 &&
            heldRadio.LastAction == RadioAction.Power && heldRadio.RememberedPointer == pointer,
            "unheld pointed action and power controls still activate once each");

        // Model the post-raycast state produced by PreferSittingItemLook: the
        // hit remains a radio child control, while the target becomes its root item.
        foreach (GoPointerButton control in new GoPointerButton[] { power, next, knob })
        {
            (knob, pointer) = Fresh();
            var radio = new RadioItemController();
            if (control is RadioPowerButton p) p.Radio = radio;
            else if (control is RadioActionButton a) a.Radio = radio;
            else ((RadioVolumeKnob)control).Radio = radio;
            var root = new PickupableItem { TestRadio = radio };
            var hit = new RaycastHit { collider = new Collider { TestButton = control }, distance = 1.25f };
            target = root; lookDistance = 1.4f;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == control && root.Unlooked && control.LookedAtBy == pointer && lookDistance == 1.25f,
                "placed purchased control restores its own direct hit before native click");

            // With Dizzy disabled, native DoRaycast already selected the control.
            // The postfix must not make another Look/Unlook transition each tick.
            root.Unlooked = false; control.LookedAtBy = null;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == control && !root.Unlooked && control.LookedAtBy == null,
                "already selected control keeps its native hover state");

            target = root; pointer.Held = new UnityEngine.Object();
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == root, "held item cannot retarget to a radio control");
            pointer.Held = null;
            target = new PickupableItem { TestRadio = new RadioItemController() };
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target != control, "unrelated pickup target is preserved");
            target = root; radio.IsPlacedForControls = false;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == root, "stock or unplaced radio cannot restore its controls");
            radio.IsPlacedForControls = true;
            target = null;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == null, "missing native target stays missing");
            target = new GoPointerButton();
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target != control, "another interactable target stays untouched");
            target = root; control.unclickable = true;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == root, "unclickable radio control stays unavailable");
            control.unclickable = false;
            hit.collider.enabled = false;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == root, "disabled shop control collider cannot be restored");
            hit.collider.enabled = true; GameState.inCursorMenu = true;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == root, "cursor menu cannot restore stale raycast control");
            GameState.inCursorMenu = false;
            hit.distance = 2f;
            RadioHeldItemTarget.Restore(pointer, hit, ref target, ref lookDistance);
            Check(target == root, "out-of-reach hit cannot restore radio control");
        }
        Console.WriteLine(checks + " production knob lifecycle checks passed with explicit native input doubles. Live click, scroll and menu behavior remain acceptance gates.");
    }
}
