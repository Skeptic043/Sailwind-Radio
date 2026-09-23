using System;
using System.Collections.Generic;
using System.Globalization;
using UnityEngine;

namespace SailwindRadio.Shops
{
    internal static class RadioShopPositionMarker
    {
        private const float MaxAnchorDistance = 100f;

        internal static bool TryDescribe(Transform observer, IEnumerable<IslandSceneryScene> scenes, out string message)
        {
            message = "Stand marker unavailable. Enter a loaded capital island and try again";
            if (!observer)
            {
                message = "Stand marker unavailable. Wait for the player to load and try again";
                return false;
            }
            if (scenes == null) return false;

            IslandSceneryScene closest = null;
            float bestDistance = float.PositiveInfinity;
            foreach (var scene in scenes)
            {
                if (!scene || !scene.gameObject.activeInHierarchy ||
                    !RadioStandLayout.TryAnchor(scene.parentIslandIndex, out var anchor, out _)) continue;
                Vector3 delta = scene.transform.TransformPoint(anchor) - observer.position;
                float distance = delta.sqrMagnitude;
                if (float.IsNaN(distance) || float.IsInfinity(distance) ||
                    distance > MaxAnchorDistance * MaxAnchorDistance || distance >= bestDistance) continue;
                closest = scene;
                bestDistance = distance;
            }
            if (!closest) return false;

            Vector3 localPosition = closest.transform.InverseTransformPoint(observer.position);
            Vector3 worldForward = Vector3.ProjectOnPlane(observer.forward, Vector3.up);
            if (worldForward.sqrMagnitude < .001f) worldForward = Vector3.forward;
            Vector3 localForward = Quaternion.Inverse(closest.transform.rotation) * worldForward.normalized;
            float localYaw = Mathf.Atan2(localForward.x, localForward.z) * Mathf.Rad2Deg;
            // The display, device faces and customer approach all use local -Z as front.
            // A player facing the intended customer side therefore needs the opposite
            // authored stand yaw, while the player's own facing remains useful evidence.
            float standYaw = localYaw + 180f;
            if (standYaw >= 180f) standYaw -= 360f;
            if (float.IsNaN(localPosition.x) || float.IsNaN(localPosition.y) || float.IsNaN(localPosition.z) ||
                float.IsInfinity(localPosition.x) || float.IsInfinity(localPosition.y) || float.IsInfinity(localPosition.z) ||
                float.IsNaN(localYaw) || float.IsInfinity(localYaw))
            {
                message = "Stand marker unavailable. Island coordinates are invalid";
                return false;
            }
            string island = closest.parentIslandIndex == 1 ? "Gold Rock City" :
                closest.parentIslandIndex == 9 ? "Dragon Cliffs" : "Fort Aestrin";
            message = string.Format(CultureInfo.InvariantCulture,
                "Radio stand marker: {0} ({1}) local ({2:F2}, {3:F2}, {4:F2}) facing yaw {5:F1}, stand yaw {6:F1}",
                island, closest.parentIslandIndex, localPosition.x, localPosition.y, localPosition.z, localYaw, standYaw);
            return true;
        }
    }
}
