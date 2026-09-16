using UnityEngine;

namespace SailwindRadio.Physical
{
    public enum RadioDeviceKind
    {
        Radio, SmallSpeaker, Speaker, TurboWoofer
    }
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
        public static string Name(int kind) => kind == 1 ? "Small Speaker" : kind == 2 ? "Speaker" : kind == 3 ? "Turbo Wolfer" : "Sailwind Radio";
        public static Vector3 Size(int kind) => kind == 1 ? new Vector3(.14f, .20f, .12f) :
            kind == 2 ? new Vector3(.367f, .75f, .4f) : kind == 3 ? new Vector3(.9f, .9f, .75f) : new Vector3(.6f, .38f, .2f);
        public static Vector3 Center(int kind)
        {
            var size = Size(kind);
            return new Vector3(0, size.y * .5f, kind == 1 ? -size.z * .5f : 0);
        }
        public static Vector3 SoundOrigin(int kind) => kind == 0 ? new Vector3(-.166f, .19f, -.12f) :
            kind == 1 ? new Vector3(0, .123f, -.14f) : kind == 2 ? new Vector3(0, .44f, -.22f) : new Vector3(0, .52f, -.40f);
    }
}
