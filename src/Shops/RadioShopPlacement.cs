using SailwindRadio.Physical;
using UnityEngine;

namespace SailwindRadio.Shops
{
    internal static class RadioShopPlacement
    {
        internal static bool TryStand(IslandSceneryScene scenery, out Vector3 position, out Quaternion rotation, out string reason)
        {
            position = default;
            rotation = Quaternion.identity;
            reason = "not an authored capital scene";
            if (!scenery || !RadioStandLayout.TryAnchor(scenery.parentIslandIndex, out var scenePosition, out float yaw)) return false;
            Vector3 expected = scenery.transform.TransformPoint(scenePosition);
            rotation = scenery.transform.rotation * Quaternion.Euler(0, yaw, 0);
            // The authored location is a preview until a player checks it in the live scene.
            // Ground and overlap problems must be visible rather than hiding the display.
            bool supported = Ground(expected, out float height);
            // Fort's last live log found static support .197 m below its authored
            // height. Gold Rock's feet also float above the nearby ground. Set
            // these stalls and their keepers down only on support slightly below
            // the authored height. A prop above the anchor cannot lift them.
            bool groundedCapital = (scenery.parentIslandIndex == 1 || scenery.parentIslandIndex == 15) && supported &&
                height <= expected.y + .02f && height >= expected.y - .30f;
            // Gold Rock's visual sand and the NPC's feet sit below the static
            // dock support that this ray sees. The player's latest live check
            // still found a gap at -.05 m. Lower only this complete stall;
            // Fort's approved grounding remains exact.
            float goldVisualOffset = scenery.parentIslandIndex == 1 ? -.10f : 0f;
            position = new Vector3(expected.x, (groundedCapital ? height : expected.y + .02f) + goldVisualOffset, expected.z);
            string diagnostics = supported ? "" : "no level static ground at the authored anchor";
            if (supported && !groundedCapital && Mathf.Abs(height - expected.y) > .15f)
                diagnostics = Append(diagnostics, "nearby support height differs from authored height by " + (height - expected.y));
            bool uneven = false;
            foreach (float x in new[] { -1.1f, 1.1f, 1.39f, 2.21f })
                foreach (float z in new[] { -.39f, .39f })
                {
                    Vector3 foot = position + rotation * new Vector3(x, 0, z);
                    if (!Ground(foot, out float y) || Mathf.Abs(y - position.y) > .045f)
                        uneven = true;
                }
            if (uneven) diagnostics = Append(diagnostics, "uneven or missing ground beneath the display");
            var envelopeHalf = RadioStandLayout.EnvelopeHalf;
            if (scenery.parentIslandIndex == 1) envelopeHalf.x += .15f; // Gold Wolfer reaches farther from the counter.
            string blocked = Blocker(position + rotation * RadioStandLayout.EnvelopeCenter, envelopeHalf, rotation, null);
            if (blocked != null) diagnostics = Append(diagnostics, "display footprint overlaps " + blocked);
            // Check the customer's approach in front, rather than a line through the native counter
            // from the merchant standing behind it.
            blocked = Blocker(position + rotation * new Vector3(.55f, .91f, -1.02f), new Vector3(1.8f, .85f, .38f), rotation, null);
            if (blocked != null) diagnostics = Append(diagnostics, "customer approach overlaps " + blocked);
            reason = diagnostics;
            return true;
        }

        internal static bool SlotClear(RadioShopStand stand, int index, int kind, out string reason)
        {
            var scenery = stand.GetComponentInParent<IslandSceneryScene>();
            var position = stand.transform.TransformPoint(RadioStandLayout.Slot(scenery ? scenery.parentIslandIndex : -1, index, stand.UsesNativeGoldRockCounter));
            Vector3 half = RadioDevice.Size(kind) * .5f + new Vector3(.02f, .008f, .025f);
            var rotation = stand.transform.rotation * RadioStandLayout.SlotRotation(kind);
            reason = Blocker(position + rotation * RadioDevice.Center(kind), half, rotation, stand.transform);
            return true;
        }

        private static string Append(string previous, string message) => previous.Length == 0 ? message : previous + "; " + message;

        private static bool Ground(Vector3 expected, out float height)
        {
            height = 0;
            if (!Physics.Raycast(expected + Vector3.up, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore) ||
                !hit.collider || hit.normal.y < .5f || hit.collider.attachedRigidbody || hit.collider.GetComponentInParent<ShipItem>() ||
                Mathf.Abs(hit.point.y - expected.y) > 1.5f) return false;
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
