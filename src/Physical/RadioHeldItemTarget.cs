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
                target.GetComponent<RadioItemController>() != radio.RecordOwner)
                return;

            target.ForceUnlook();
            target = control;
            target.Look(pointer);
            lookDistance = hit.distance;
        }

        // In cargo-loading mode a big output's controls stand for the whole item, so native loading can take it.
        // Native LateUpdate still requires a big, sold and unnailed item and a hired carrier.
        internal static bool CargoTarget(GoPointer pointer, RaycastHit hit, ref GoPointerButton target)
        {
            if (!CargoStorageUI.loadingCargoMode || !pointer || !hit.collider)
                return false;
            var control = hit.collider.GetComponent<GoPointerButton>();
            var radio = RadioControls.RadioOf(control);
            var item = radio ? radio.Item : null;
            if (!item || !item.big || (target != control && target != item))
                return false;
            if (target != item)
            {
                target.ForceUnlook();
                target = item;
                target.Look(pointer);
            }
            return true;
        }

        // Only the upward-facing flat top of a supported cabinet acts as a tabletop for small held items.
        // Looking at a speaker's front or sides clears the placement target while leaving hammer nailing untouched.
        internal static void FilterTabletop(GoPointer pointer, RaycastHit hit, ref GoPointerButton target, ref float lookDistance)
        {
            if (!pointer || !(target is ShipItem targetItem) || !targetItem.allowPlacingItems)
                return;
            var held = pointer.GetHeldItem() as ShipItem;
            if (!held || held.big || held is ShipItemHammer)
                return;
            var device = targetItem.GetComponent<RadioItemController>();
            if (!device)
                return;
            bool onTop = device.IsPlacedForControls && DeviceKind.HasTabletop(device.TopKind) &&
                hit.collider && Vector3.Dot(hit.normal, targetItem.transform.up) > 0.7f &&
                Vector3.Dot(hit.normal, Vector3.up) > 0.7f;
            if (!onTop)
            {
                target.ForceUnlook();
                target = null;
                lookDistance = 0f;
            }
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
