using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioControlReach
    {
        internal static bool Contains(Transform origin, Collider target) => origin && target && target.enabled &&
            Vector3.Distance(origin.position,target.ClosestPoint(origin.position)) <= 1.8f;
    }
}
