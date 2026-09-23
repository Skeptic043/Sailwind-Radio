using UnityEngine;

namespace SailwindRadio.Shops
{
    internal static class RadioStandLayout
    {
        // Metres in the stand's frame, independent of the scenery's authored scale.
        internal static readonly Vector3 EnvelopeCenter = new Vector3(.4f, .82f, 0);
        internal static readonly Vector3 EnvelopeHalf = new Vector3(1.8f, .82f, .7f);
        internal static readonly Vector3[] Slots = {
            new Vector3(1.7f, 0, -.1f), // Wolfer beside the table, on the Dragon Cliffs planks.
            new Vector3(-.88f, .82f, -.04f), new Vector3(.88f, .82f, -.04f),
            new Vector3(-.32f, .82f, .30f), new Vector3(.32f, .82f, .30f),
            new Vector3(-.32f, .82f, -.27f), new Vector3(.32f, .82f, -.27f)
        };
        internal static Quaternion SlotRotation(int kind) => Quaternion.identity;
        internal static bool TryAnchor(int island, out Vector3 sceneryLocalPosition, out float yaw)
        {
            // Candidate scene-local locations inferred from the user's screenshots and native
            // scene landmarks. They do not follow a native shop, table, NPC or stall transform.
            // The player can inspect these even where scenery or another mod overlaps them.
            // Gold Rock is beside the empty waterfront stall. Fort is toward
            // the foreground path from its prior canopy, clear of the inn.
            yaw = island == 1 ? -90f : island == 9 ? 45f : 180f;
            sceneryLocalPosition = island == 1 ? new Vector3(1595f, 3.1f, -445.5f) :
                island == 9 ? new Vector3(-104.6f, 2.1f, -530f) : new Vector3(-119f, 2.3f, 37f);
            return island == 1 || island == 9 || island == 15;
        }
    }
}
