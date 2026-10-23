using System;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;
using UnityEngine;

namespace SailwindRadio.Physical
{
    // Right click with a device in hand sets it on top of the placed device it looks at, and right click with empty
    // hands takes a stack's top off. Stacking moves the held device's state object into its base's saved state and
    // destroys the held item. Unstacking spawns a fresh native item that takes that same state object back. Each
    // change is in-memory work on the main thread within one frame, so a native save sees the stack whole before or
    // after, never a device twice or not at all.
    public sealed partial class RadioWorldService
    {
        // Past the held item, which hangs about a metre in front of the player.
        private const float StackReach = 3f;
        // Native GoPointer.DoRaycast's layer mask. It leaves out the item physics copies on layer 2, which sit on a
        // placed item ashore and belong to no ShipItem.
        private const int PointerMask = -604165;
        internal static bool StacksCompatible { get; private set; }

        // The look prompt on a placed stack.
        internal static string StackHint(GoPointerButton button, GoPointer pointer)
        {
            if (active == null || active.disposed || !StacksCompatible || !pointer || pointer.GetHeldItem()) return "";
            var device = DeviceOf(button);
            bool unstackable = device && device.RecordOwner.State.Stacked != null && device.RecordOwner.IsPlacedForControls &&
                (!RadioControls.IsOwnControl(button) || device.IsStackTop);
            return unstackable ? "right click to remove top" : "";
        }

        // The hold prompt. Nothing is pointed while a big item is held, so this looks for the target itself.
        internal static string HoldHint(PickupableItem held)
        {
            // Native ShowHoldText leaves its prompts out when the player turns control text off.
            if (active == null || active.disposed || !StacksCompatible || !Settings.controlsTextEnabled || !(held is ShipItem item) || !item.held) return "";
            var top = item.GetComponent<RadioItemController>();
            return top && active.CanStack(StackTarget(item.held, item), top) ? "right click to stack" : "";
        }

        internal void AddStackTop(RadioItemController top)
        {
            if (!items.Contains(top)) items.Add(top);
        }

        // The state at the given height in a base's saved chain, counting the base as 0 and from the top when negative.
        private static RadioState InChain(RadioItemController root, int height)
        {
            var above = root.State.Above();
            above.Insert(0, root.State);
            return above[height < 0 ? above.Count + height : height];
        }

        // The held device, or the whole stack it carries, goes on top of whatever stack the player looks at, so they
        // build it in the order they hold. Saved chains are counted, not built devices, which a failed build can leave
        // short. A Radio can only be highest, so the held part brings the stack's only Radio if it has one.
        internal bool CanStack(RadioItemController target, RadioItemController held)
        {
            if (!target || !held || held.IsStackTop) return false;
            var root = target.RecordOwner;
            return root != held && root.State.Above().Count + 1 + held.State.Above().Count + 1 <= DeviceKind.MaximumStack &&
                DeviceKind.CanStack(InChain(root, -1).Kind, held.State.Kind) &&
                root.IsPlacedForControls && held.Item && held.Item.held && held.Item.sold && CanAct(root) && CanAct(held);
        }

        // The Radio among a base and its stacked devices, whose player moves when its controller is replaced.
        private static RadioItemController RadioIn(RadioItemController root)
        {
            if (root.State.Role == DeviceRole.Radio) return root;
            foreach (var device in root.Stack)
                if (device.State.Role == DeviceRole.Radio) return device;
            return null;
        }

        // The first item along the player's look past the held one, if it is a device.
        private static RadioItemController StackTarget(GoPointer pointer, ShipItem held)
        {
            var hits = Physics.RaycastAll(pointer.transform.position, pointer.transform.forward, StackReach, PointerMask, QueryTriggerInteraction.Collide);
            Array.Sort(hits, (a, b) => a.distance.CompareTo(b.distance));
            foreach (var hit in hits)
            {
                var collider = hit.collider;
                if (!collider || collider == Refs.charController || (held.itemRigidbodyC && collider.transform.IsChildOf(held.itemRigidbodyC.transform)))
                    continue;
                var item = collider.GetComponentInParent<ShipItem>();
                if (item == held)
                    continue;
                return item ? item.GetComponent<RadioItemController>() : null;
            }
            return null;
        }

        internal bool TryStack(ShipItem heldItem, GoPointer pointer)
        {
            var held = heldItem.GetComponent<RadioItemController>();
            var target = held ? StackTarget(pointer, heldItem) : null;
            if (!CanStack(target, held)) return false;
            var root = target.RecordOwner;
            int heldId = held.InstanceId, radioId = GlobalRadioArbiter.RadioId(heldId, held.State);
            // The held Radio's state must exist and the stack's Radio ID be free, so nothing can fail after the destroy.
            if (radioId != 0 && (!arbiter.TryGet(radioId, out _) || arbiter.TryGet(GlobalRadioArbiter.StackedId(root.InstanceId), out _)))
                return false;
            var heldRadio = RadioIn(held);
            // Bundle Up found that a dropped item's physics body can strike the moving hull before Destroy
            // completes, and the boat plays an impact. Settle it first.
            var body = heldItem.itemRigidbodyC ? heldItem.itemRigidbodyC.GetBody() : null;
            bool collides = body && body.detectCollisions, kinematic = body && body.isKinematic;
            try
            {
                if (body)
                {
                    body.detectCollisions = false;
                    body.isKinematic = true;
                }
                pointer.DropItem();
                heldItem.DestroyItem();
            }
            catch (Exception error)
            {
                // Nothing else has changed, so the held item keeps its identity, state and physics.
                if (body)
                {
                    body.detectCollisions = collides;
                    body.isKinematic = kinematic;
                }
                warn("Could not stack the device: " + error.Message);
                return false;
            }
            // In-memory changes from here, within the same frame as the Destroy request. The held chain's state
            // objects move onto the stack. A Radio among them is renamed to the stack's ID, so it stays the playing
            // radio. Stacked speakers need no power state.
            InChain(root, -1).Stacked = held.State;
            if (radioId != 0) arbiter.Rename(radioId, GlobalRadioArbiter.StackedId(root.InstanceId));
            if (held.State.Role != DeviceRole.Radio) arbiter.Remove(heldId);
            store.Remove(heldId);
            items.Remove(held);
            foreach (var device in held.Stack) items.Remove(device);
            WriteCanonical(root.InstanceId);
            root.SyncStack();
            if (heldRadio) RadioReplaced?.Invoke(heldRadio, RadioIn(root));
            return true;
        }

        internal bool TryUnstack(RadioItemController root, GoPointer pointer)
        {
            if (!root || root.IsStackTop || root.State.Stacked == null || !root.IsPlacedForControls ||
                !pointer || pointer.GetHeldItem() || !CanAct(root) || !ReadyForShop || !TryGetTemplate(InChain(root, -1).Kind, out var template))
                return false;
            var below = InChain(root, -2);
            var stacked = below.Stacked;
            bool radio = stacked.Role == DeviceRole.Radio;
            int stackedId = GlobalRadioArbiter.StackedId(root.InstanceId), id = 0;
            bool moved = false;
            var place = root.StackTop ? root.StackTop.transform : root.transform;
            GameObject instance = null;
            RadioItemController controller = null;
            ShipItem item;
            try
            {
                instance = UnityEngine.Object.Instantiate(template, place.position, place.rotation);
                item = instance.GetComponent<ShipItem>();
                var saveable = instance.GetComponent<SaveablePrefab>();
                controller = instance.GetComponent<RadioItemController>();
                if (!item || !saveable || !controller || controller.State == null || controller.State.Kind != stacked.Kind)
                    throw new InvalidOperationException("Radio item template did not initialize its clone");
                item.sold = true;
                // As native ShipItem.Sell places a bought item. Native EnterBoat sets its boat when it enters one.
                if (FloatingOriginManager.instance)
                    instance.transform.SetParent(FloatingOriginManager.instance.transform, true);
                saveable.Start();
                saveable.RegisterToSave();
                id = saveable.instanceId;
                // The new item takes the stacked state object itself. Binding then writes its record from it, and a
                // Radio keeps the arbiter state it had in the stack.
                moved = radio && arbiter.Rename(stackedId, id);
                if (radio && !moved)
                    throw new InvalidOperationException("the stacked state could not move to the new item");
                controller.ApplyRestoredState(this, stacked);
                if (!EnsureOwned(controller))
                    throw new InvalidOperationException("the new device could not be bound to the save");
                below.Stacked = null;
                WriteCanonical(root.InstanceId);
            }
            catch (Exception error)
            {
                below.Stacked = stacked;
                if (id > 0)
                {
                    if (moved) arbiter.Rename(id, stackedId);
                    else if (!radio) arbiter.Remove(id);
                    store.Remove(id);
                }
                if (instance)
                {
                    if (controller)
                    {
                        items.Remove(controller);
                        pendingBindings.Remove(controller);
                    }
                    var saveable = instance.GetComponent<SaveablePrefab>();
                    if (saveable) saveable.Unregister();
                    UnityEngine.Object.Destroy(instance);
                }
                warn("Could not take the top device off: " + error.Message);
                return false;
            }
            var stackedRadio = radio ? root.StackTop : null;
            root.SyncStack();
            if (stackedRadio) RadioReplaced?.Invoke(stackedRadio, controller);
            // A failed pickup leaves the device standing where the top was.
            try { pointer.PickUpItem(item); }
            catch (Exception error) { warn("The unstacked device stays in place: " + error.Message); }
            return true;
        }
    }
}
