using System.Reflection;
using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioNativeHooks
    {
        private static readonly FieldInfo CurrentHook = typeof(HangableItem).GetField("currentHook",
            BindingFlags.Instance | BindingFlags.NonPublic);
        internal static bool Compatible => CurrentHook != null && CurrentHook.FieldType == typeof(Collider);

        private static bool IsRadio(Component item)
        {
            var controller = item ? item.GetComponent<RadioItemController>() : null;
            return controller && controller.State != null && controller.State.Kind == 0;
        }

        private static bool HasLiveJoint(HangableItem hangable, ShipItem item)
        {
            if (!hangable || !item || !hangable.IsHanging() || !Compatible) return false;
            var hook = CurrentHook.GetValue(hangable) as Collider;
            var hookItem = hook ? hook.GetComponent<ShipItemLampHook>() : null;
            var hookBody = hookItem ? hookItem.itemRigidbodyC : null;
            var joint = hookBody ? hookBody.GetComponent<ConfigurableJoint>() : null;
            var radioBody = item.itemRigidbodyC ? item.itemRigidbodyC.GetBody() : null;
            if (joint && radioBody && joint.connectedBody == radioBody) return true;
            // Native ConnectJoint assigns currentHook before CreateJoint. An occupied hook
            // returns no joint, but IsHanging then reports true. Clear only that stale marker.
            CurrentHook.SetValue(hangable, null);
            return false;
        }

        internal static void RejectPhantomHook(HangableItem hangable)
        {
            if (hangable && IsRadio(hangable))
                HasLiveJoint(hangable, hangable.GetComponent<ShipItem>());
        }

        internal static bool Release(ShipItem item)
        {
            if (!IsRadio(item)) return false;
            var hangable = item.GetComponent<HangableItem>();
            if (HasLiveJoint(hangable, item))
            {
                hangable.DisconnectJoint();
                // Native DisconnectJoint leaves this restriction enabled. Restore normal boat
                // affiliation only after the radio's hook joint has actually been released.
                item.ToggleDisallowDisembarking(false);
                return true;
            }
            return false;
        }

        internal static void EnterInventory(ShipItem item)
        {
            if (!IsRadio(item) || !item.GetComponent<HangableItem>()) return;
            // ExitBoat may already have disconnected an unheld item during native
            // OnEnterInventory. Inventory ownership must clear the stale restriction.
            if (!Release(item)) item.ToggleDisallowDisembarking(false);
        }

        internal static void PositionBelowHook(HangableItem hangable)
        {
            if (!hangable || !IsRadio(hangable) || !HasLiveJoint(hangable, hangable.GetComponent<ShipItem>())) return;
            // Native HangableItem.LateUpdate places the item's base at the hook. Move the
            // radio's base below it so the top edge becomes the attachment point.
            hangable.transform.position -= Vector3.up * RadioDevice.Size(0).y;
        }

        internal static bool AllowHouseTrigger(ShipItem item, Collider other) =>
            !IsRadio(item) || !other || !other.CompareTag("House") || other.GetComponent<SaveableObject>();
    }
}
