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
            // Ground and overlap problems are returned in reason without refusing the stand.
            bool supported = Ground(expected, out float height);
            // Gold Rock and Fort Aestrin settle onto static support up to .30 m below the
            // authored height. A prop above the anchor cannot lift them.
            bool groundedCapital = (scenery.parentIslandIndex == 1 || scenery.parentIslandIndex == 15) && supported &&
                height <= expected.y + .02f && height >= expected.y - .30f;
            // Gold Rock's visible sand and the keeper's feet sit .10 m below the static
            // dock support this ray finds.
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
            string blocked = Blocker(position + rotation * RadioStandLayout.EnvelopeCenter, envelopeHalf, rotation);
            if (blocked != null) diagnostics = Append(diagnostics, "display footprint overlaps " + blocked);
            // Check the customer's approach in front, rather than a line through the native counter
            // from the merchant standing behind it.
            blocked = Blocker(position + rotation * new Vector3(.55f, .91f, -1.02f), new Vector3(1.8f, .85f, .38f), rotation);
            if (blocked != null) diagnostics = Append(diagnostics, "customer approach overlaps " + blocked);
            reason = diagnostics;
            return true;
        }

        private static string Append(string previous, string message) => previous.Length == 0 ? message : previous + ", " + message;

        private static bool Ground(Vector3 expected, out float height)
        {
            height = 0;
            if (!Physics.Raycast(expected + Vector3.up, Vector3.down, out var hit, 3f, ~0, QueryTriggerInteraction.Ignore) ||
                !hit.collider || hit.normal.y < .5f || hit.collider.attachedRigidbody || hit.collider.GetComponentInParent<ShipItem>() ||
                Mathf.Abs(hit.point.y - expected.y) > 1.5f) return false;
            height = hit.point.y;
            return true;
        }

        private static string Blocker(Vector3 center, Vector3 half, Quaternion rotation)
        {
            foreach (var collider in Physics.OverlapBox(center, half, rotation, ~0, QueryTriggerInteraction.Collide))
            {
                if (!collider || !collider.enabled) continue;
                if (!collider.isTrigger || collider.GetComponentInParent<ShipItem>()) return collider.name;
            }
            return null;
        }
    }
}
