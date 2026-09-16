using SailwindRadio.Physical;
using UnityEngine;

namespace SailwindRadio.Shops
{
    internal static class RadioShopPlacement
    {
        internal static bool TryStand(ShopArea area, out Vector3 position, out Quaternion rotation, out string reason)
        {
            position = default;
            rotation = Quaternion.identity;
            reason = "vendor geometry no longer matches";
            if (!RadioShopCatalog.Matches(area)) return false;
            int island = area.transform.parent.GetComponent<IslandSceneryScene>().parentIslandIndex;
            if (!RadioStandLayout.TryAnchor(island, out var offset, out float yaw)) return false;
            Vector3 expected = area.transform.position + area.transform.rotation * offset;
            rotation = area.transform.rotation * Quaternion.Euler(0, yaw, 0);
            if (!Ground(expected, out float height)) { reason = "no level static ground at the authored anchor"; return false; }
            position = new Vector3(expected.x, height + .02f, expected.z);
            foreach (float x in new[] { -1.1f, 1.1f, 1.39f, 2.21f })
                foreach (float z in new[] { -.39f, .39f })
                {
                    Vector3 foot = position + rotation * new Vector3(x, 0, z);
                    if (!Ground(foot, out float y) || Mathf.Abs(y - height) > .045f)
                    { reason = "uneven or missing ground beneath the display"; return false; }
                }
            string blocked = Blocker(position + rotation * RadioStandLayout.EnvelopeCenter, RadioStandLayout.EnvelopeHalf, rotation, null);
            if (blocked != null) { reason = "display footprint blocked by " + blocked; return false; }
            // Check the customer's approach in front, rather than a line through the native counter
            // from the merchant standing behind it.
            blocked = Blocker(position + rotation * new Vector3(.55f, .91f, -1.02f), new Vector3(1.8f, .85f, .38f), rotation, null);
            if (blocked != null) { reason = "customer approach blocked by " + blocked; return false; }
            reason = "";
            return true;
        }

        internal static bool SlotClear(RadioShopStand stand, int index, int kind, out string reason)
        {
            var position = stand.transform.TransformPoint(RadioStandLayout.Slots[index]);
            Vector3 half = RadioDevice.Size(kind) * .5f + new Vector3(.02f, .008f, .025f);
            reason = Blocker(position + stand.transform.rotation * RadioDevice.Center(kind), half, stand.transform.rotation, stand.transform);
            return reason == null;
        }

        private static bool Ground(Vector3 expected, out float height)
        {
            height = 0;
            if (!Physics.Raycast(expected + Vector3.up * .6f, Vector3.down, out var hit, 1.2f, ~0, QueryTriggerInteraction.Ignore) ||
                !hit.collider || hit.normal.y < .98f || hit.collider.attachedRigidbody || hit.collider.GetComponentInParent<ShipItem>() ||
                Mathf.Abs(hit.point.y - expected.y) > .22f) return false;
            height = hit.point.y;
            return true;
        }

        private static string Blocker(Vector3 center, Vector3 half, Quaternion rotation, Transform ownStand)
        {
            foreach (var collider in Physics.OverlapBox(center, half, rotation, ~0, QueryTriggerInteraction.Collide))
            {
                if (!collider || !collider.enabled) continue;
                bool item = collider.GetComponentInParent<ShipItem>();
                if (ownStand && collider.transform.IsChildOf(ownStand) && !item) continue;
                if (!collider.isTrigger || item) return collider.name;
            }
            return null;
        }
    }
}
