using UnityEngine;

namespace SailwindRadio.Shops
{
    internal static class RadioStandLayout
    {
        // Metres in the stand's frame, independent of the scenery's authored scale.
        internal static readonly Vector3 EnvelopeCenter = new Vector3(.55f, .8f, 0);
        internal static readonly Vector3 EnvelopeHalf = new Vector3(1.8f, .8f, .56f);
        internal static readonly Vector3[] Slots = {
            new Vector3(1.8f, 0, 0), // Ground woofer beside the stand.
            new Vector3(-.64f, .18f, 0), new Vector3(.64f, .18f, 0),
            new Vector3(-.5f, 1.07f, 0), new Vector3(.5f, 1.07f, 0),
            new Vector3(-.99f, 1.07f, 0), new Vector3(.99f, 1.07f, 0)
        };
        internal static bool TryAnchor(int island, out Vector3 offset, out float yaw)
        {
            // Inspected scene collider geometry provides these anchors. Runtime queries still
            // reject moved scenery, player items or other mods occupying the display footprint.
            yaw = island == 15 ? -90 : 180;
            offset = island == 1 ? new Vector3(-.55f, -1.449f, 2.4f) :
                island == 9 ? new Vector3(.35f, -1.04f, 2.35f) : new Vector3(4, -2.05f, .7f);
            return island == 1 || island == 9 || island == 15;
        }
    }
}
