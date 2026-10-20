using UnityEngine;

namespace SailwindRadio.Physical
{
    public enum RadioAction
    {
        Power, PlayPause, Previous, Next, Shuffle, Collections
    }

    public readonly struct DeviceVessel
    {
        public readonly bool Known;
        public readonly Transform Root;
        public DeviceVessel(bool known, Transform root)
        {
            Known = known;
            Root = root;
        }
    }

    public static class RadioDevice
    {
        public const float InventoryYaw = 180f;
        private static readonly string[] Regions = { "Walnut", "Al'Ankh", "Aestrin", "Emerald" };
        public static string Region(int family) => Regions[family];
        public static string Name(int kind)
        {
            var role = DeviceKind.Role(kind);
            if (DeviceKind.IsWalnut(kind))
                return role == DeviceRole.SmallSpeaker ? "Small Speaker" : role == DeviceRole.Speaker ? "Speaker" : role == DeviceRole.Woofer ? "Turbo Wolfer" : "Radio";
            return Regions[DeviceKind.Family(kind)] + " " + (role == DeviceRole.SmallSpeaker ? "Small Speaker" : role == DeviceRole.Speaker ? "Speaker" :
                role == DeviceRole.Woofer ? "Woofer" : "Radio");
        }
        // Item menus such as New Beginnings' gear list add the object name when it differs from the item name,
        // so regional templates carry their item name. Walnut kinds keep their released object names.
        // Saves key on the item index, never this name.
        public static string TemplateName(int kind, int itemIndex) =>
            DeviceKind.IsWalnut(kind) ? "Sailwind Radio item " + itemIndex : Name(kind);
        // Walnut kinds 0-3 keep these authored constants. Regional kinds use their model bounds below.
        public static Vector3 Size(int kind) => kind == DeviceKind.WalnutSmallSpeaker ? new Vector3(.14f, .20f, .12f) :
            kind == DeviceKind.WalnutSpeaker ? new Vector3(.367f, .75f, .4f) : kind == DeviceKind.WalnutWoofer ? new Vector3(.9f, .9f, .75f) :
            new Vector3(.6f, .38f, .2f);
        public static Vector3 Center(int kind)
        {
            var size = Size(kind);
            return new Vector3(0, size.y * .5f, kind == DeviceKind.WalnutSmallSpeaker ? -size.z * .5f : 0);
        }
        public static Vector3 SoundOrigin(int kind) => kind == DeviceKind.WalnutRadio ? new Vector3(-.166f, .19f, -.12f) :
            kind == DeviceKind.WalnutSmallSpeaker ? new Vector3(0, .123f, -.14f) : kind == DeviceKind.WalnutSpeaker ? new Vector3(0, .44f, -.22f) :
            new Vector3(0, .52f, -.40f);

        public static Vector3 Center(DeviceRole role, Vector3 min, Vector3 max)
        {
            var center = (min + max) * .5f;
            // The small speaker's back stays on the native wall attachment plane.
            if (role == DeviceRole.SmallSpeaker)
                center.z = -(max.z - min.z) * .5f;
            return center;
        }
        // Front face centre at 55% of the cabinet height.
        public static Vector3 SoundOrigin(Vector3 min, Vector3 max) => new Vector3(0, min.y + .55f * (max.y - min.y), min.z);
    }
}
