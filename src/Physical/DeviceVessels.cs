using UnityEngine;

namespace SailwindRadio.Physical
{
    public static class DeviceVessels
    {
        public const float MaximumLinkRange = 50f;
        public static bool CanConnect(DeviceVessel radio, DeviceVessel speaker, Vector3 source, Vector3 output)
        {
            if (!radio.Known || !speaker.Known || radio.Root != speaker.Root || !Finite(source) || !Finite(output)) return false;
            return (source-output).sqrMagnitude <= MaximumLinkRange*MaximumLinkRange;
        }

        private static bool Finite(Vector3 value) => !float.IsNaN(value.x) && !float.IsInfinity(value.x) &&
            !float.IsNaN(value.y) && !float.IsInfinity(value.y) && !float.IsNaN(value.z) && !float.IsInfinity(value.z);

        internal static bool IsPlayerOwned(ShipItem item)
        {
            if (!item) return false;
            if (item.held) return true;
            var slots=GPButtonInventorySlot.inventorySlots;
            if (slots!=null) foreach(var slot in slots) if(slot && slot.currentItem==item) return true;
            return false;
        }

        public static DeviceVessel Resolve(ShipItem item, bool usesPlayerPosition)
        {
            if (!item || !GameState.playing || GameState.currentlyLoading || GameState.justStarted) return new DeviceVessel(false,null);
            if (usesPlayerPosition) return Normalize(GameState.currentBoat);
            // Hidden crate contents inherit their actual carrier rather than stale item membership.
            for (int depth=0;depth<4;depth++)
            {
                if (!item.itemRigidbodyC) return new DeviceVessel(false,null);
                var box=item.itemRigidbodyC.GetCurrentBox();
                if (!box)
                {
                    if (item.currentActualBoat) return Normalize(item.currentActualBoat);
                    // A native boarding transition can have a boat parent before its body membership settles.
                    if (item.GetComponentInParent<BoatDamage>()) return new DeviceVessel(false,null);
                    return new DeviceVessel(true,null);
                }
                var body=box.GetComponentInParent<ItemRigidbody>();
                var carrier=body ? body.GetShipItem() : null;
                if (!carrier || carrier==item) return new DeviceVessel(false,null);
                if (IsPlayerOwned(carrier)) return Normalize(GameState.currentBoat);
                item=carrier;
            }
            return new DeviceVessel(false,null);
        }

        private static DeviceVessel Normalize(Transform model)
        {
            if (!model) return new DeviceVessel(true,null);
            var root=model.parent;
            return root && root.GetComponent<BoatDamage>() && root.GetComponent<SaveableObject>()
                ? new DeviceVessel(true,root) : new DeviceVessel(false,null);
        }
    }
}
