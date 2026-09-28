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
        internal static Vector3 Slot(int island, int index, bool nativeGoldCounter)
        {
            var position = Slots[index];
            if (island == 1)
            {
                // Gold Rock's Wolfer stands clear of the wider native counter,
                // 4 cm lower than the stall so its feet meet the visible ground.
                if (index == 0) { position.x = 1.9f; position.y = -.04f; }
                else if (nativeGoldCounter)
                {
                    // Installed mesh triangles put the top at .916-.931 m
                    // across these slots. The native top runs from roughly
                    // -.29 to +.34 m in Z after the 180-degree visual flip.
                    position.y = index == 1 ? .93f : index == 2 ? .94f :
                        index == 3 ? .945f : index == 4 ? .95f :
                        index == 5 ? .93f : .925f;
                    if (index == 3 || index == 4) position.z = .20f;
                    if (index == 5 || index == 6) position.z = -.17f;
                }
            }
            return position;
        }
        internal static Quaternion SlotRotation(int kind) => Quaternion.identity;
        internal static bool TryAnchor(int island, out Vector3 sceneryLocalPosition, out float yaw)
        {
            // Scenery-local stand anchors for Gold Rock (1), Dragon Cliffs (9) and Fort Aestrin (15).
            // Gold Rock's scenery has half scale, so one world metre is two local metres there.
            yaw = island == 1 ? 58.4f : island == 9 ? 45f : 180f;
            sceneryLocalPosition = island == 1 ? new Vector3(1596.6110f, 3.1f, -436.3066f) :
                island == 9 ? new Vector3(-104.6f, 2.1f, -530f) : new Vector3(-136.97f, 2.3f, 43.95f);
            return island == 1 || island == 9 || island == 15;
        }
    }
}
