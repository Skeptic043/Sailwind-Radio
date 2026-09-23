using UnityEngine;

namespace SailwindRadio.Shops
{
    internal static class RadioStandLayout
    {
        // Metres in the stand's frame, independent of the scenery's authored scale.
        internal static readonly Vector3 EnvelopeCenter = new Vector3(.4f, .82f, 0);
        internal static readonly Vector3 EnvelopeHalf = new Vector3(1.8f, .82f, .7f);
        // The cloned native keeper stands behind the owned counter. Keep this
        // offset shared with anchor calculations so marker positions locate the NPC.
        internal static readonly Vector3 MerchantOffset = new Vector3(0, 0, 1.04f);
        internal static readonly Vector3[] Slots = {
            new Vector3(1.7f, 0, -.1f), // Wolfer beside the table, on the Dragon Cliffs planks.
            new Vector3(-.88f, .82f, -.04f), new Vector3(.88f, .82f, -.04f),
            new Vector3(-.32f, .82f, .30f), new Vector3(.32f, .82f, .30f),
            new Vector3(-.32f, .82f, -.27f), new Vector3(.32f, .82f, -.27f)
        };
        internal static Quaternion SlotRotation(int kind) => Quaternion.identity;
        internal static bool TryAnchor(int island, out Vector3 sceneryLocalPosition, out float yaw)
        {
            // Gold Rock's old merchant position was local (1599.16, -434.19).
            // Face the player's logged -126.6 degree yaw, then move the NPC
            // .25 world metres forward and .25 left in that new facing. The
            // scenery's half scale makes each world metre two local metres.
            // Subtract the rotated 1.04 m keeper offset to locate the stand.
            // Fort keeps its approved facing and moves .30 m forward. Dragon
            // Cliffs keeps its approved location and facing.
            yaw = island == 1 ? 53.4f : island == 9 ? 45f : 180f;
            sceneryLocalPosition = island == 1 ? new Vector3(1597.39f, 3.1f, -436.13f) :
                island == 9 ? new Vector3(-104.6f, 2.1f, -530f) : new Vector3(-136.97f, 2.3f, 43.95f);
            return island == 1 || island == 9 || island == 15;
        }
    }
}
