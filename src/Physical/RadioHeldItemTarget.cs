using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioHeldItemTarget
    {
        internal static void Restore(GoPointer pointer, RaycastHit hit, ref GoPointerButton target, ref float lookDistance)
        {
            // Dizzy Fixes' PreferSittingItemLook can replace a child control with its
            // sold PickupableItem after the native raycast. Restore within the same
            // DoRaycast call so its root highlight never persists until LateUpdate.
            if (!pointer || pointer.GetHeldItem() || !(target is PickupableItem) || !hit.collider ||
                !hit.collider.enabled || hit.distance < 0f || hit.distance > RadioControlReach.Distance ||
                !GameState.playing || GameState.currentlyLoading || GameState.loadingScenes != 0 ||
                GameState.sleeping || GameState.inBed || GameState.inCursorMenu || BoatCamera.on ||
                !Application.isFocused || Time.timeScale <= 0f)
                return;

            var control = hit.collider.GetComponent<GoPointerButton>();
            var radio = RadioControls.RadioOf(control);
            if (!control || control.unclickable || !radio || !radio.IsPlacedForControls || !radio.EnsureOwned() ||
                target.GetComponent<RadioItemController>() != radio)
                return;

            target.ForceUnlook();
            target = control;
            target.Look(pointer);
            lookDistance = hit.distance;
        }

        internal static void Clear(GoPointer pointer, ref GoPointerButton target, ref float lookDistance)
        {
            // Native GoPointer drops a ShipItem on pickup-key release only when its
            // pointedAtButton is null. Clear child controls before native LateUpdate
            // handles the click, including a target left from an earlier FixedUpdate.
            if (RadioControls.IsOwnControl(target) &&
                pointer && pointer.GetHeldItem())
            {
                target = null;
                lookDistance = 0f;
            }
        }
    }
}
