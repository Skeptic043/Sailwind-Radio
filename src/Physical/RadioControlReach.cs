using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioControlReach
    {
        internal const float Distance = 1.8f;
        internal static bool Contains(Transform origin, Collider target) => origin && target && target.enabled &&
            Vector3.Distance(origin.position,target.ClosestPoint(origin.position)) <= Distance;
    }
}
