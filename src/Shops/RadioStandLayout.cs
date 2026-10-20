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
            RadioStalls.TryGet(island, out var stall);
            if (stall != null && stall.WideCounter)
            {
                // Gold Rock's Wolfer stands clear of the wider native counter,
                // 4 cm lower than the stall so its feet meet the visible ground.
                if (index == 0) { position.x = 1.9f; position.y = -.04f; }
                else if (nativeGoldCounter)
                {
                    // The native table has three planks after the stand's half-scale, near-180-degree copy:
                    // front z -.62 to -.305 with its top at .89 m, middle -.305 to +.345 rising from .905
                    // to .935 m, and rear +.345 to +.69 at about .965 m. Each Al'Ankh device sits wholly
                    // on one plank: radios centred on the rear about 21 cm apart, large speakers about 9 cm
                    // apart on the middle and small speakers on the front.
                    if (index == 1 || index == 2) { position.x = index == 1 ? -.545f : .545f; position.y = index == 1 ? .93f : .94f; position.z = .02f; }
                    if (index == 3 || index == 4) { position.x = index == 3 ? -.36f : .36f; position.y = .975f; position.z = .515f; }
                    if (index == 5 || index == 6) { position.x = index == 5 ? -.34f : .34f; position.y = .90f; position.z = -.405f; }
                }
            }
            // Fort Aestrin's wider Aestrin radios stand 3 cm further apart, each moving outward along the shelf.
            else if (stall != null && stall.Family == DeviceKind.Aestrin && (index == 3 || index == 4))
                position.x += index == 3 ? -.03f : .03f;
            return position;
        }
        internal static bool TryAnchor(int island, out Vector3 sceneryLocalPosition, out float yaw)
        {
            bool found = RadioStalls.TryGet(island, out var stall);
            sceneryLocalPosition = found ? stall.SceneryAnchor : default;
            yaw = found ? stall.Yaw : 0f;
            return found;
        }
    }
}
