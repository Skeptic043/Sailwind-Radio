using UnityEngine;

namespace SailwindRadio.Physical
{
    internal static class RadioPlacement
    {
        // Front controls project beyond the .2 metre body depth.
        private static readonly Vector3 HalfSize = new Vector3(.32f, .21f, .17f);

        internal static bool IsClear(Vector3 position, Quaternion rotation)
            => IsClear(position, rotation, HalfSize, new Vector3(0, .19f, 0));

        internal static bool IsClear(Vector3 position, Quaternion rotation, Vector3 halfSize, Vector3 localCenter)
        {
            Vector3 center = position + rotation * localCenter;
            Vector3 start = Refs.observerMirror ? Refs.observerMirror.transform.position + Vector3.up * .6f : center;
            Transform model = GameState.currentBoat;
            Transform walk = null;
            CapsuleCollider coarseHull = null;
            if (model)
            {
                // Native GameState.currentBoat is the visual model, not the root or fixed walk collider.
                Transform root = model.parent;
                if (!root || !root.GetComponent<BoatDamage>()) return false;
                foreach (var embark in model.GetComponentsInChildren<BoatEmbarkCollider>())
                {
                    if (!embark || embark.GetMeshParent() != model || embark.GetTopmostBoatParent() != root || !embark.walkCollider)
                        continue;
                    if (walk && walk != embark.walkCollider) return false;
                    walk = embark.walkCollider;
                }
                if (!walk || !UnitScale(model) || !UnitScale(walk)) return false;
                coarseHull = root.GetComponent<CapsuleCollider>();
                if (!coarseHull) return false;
                Vector3 walkCenter = walk.TransformPoint(model.InverseTransformPoint(center));
                Vector3 walkStart = walk.TransformPoint(model.InverseTransformPoint(start));
                Quaternion walkRotation = walk.rotation * Quaternion.Inverse(model.rotation) * rotation;
                // Only after the full walk-space volume and path pass can the exact owning root hull be ignored.
                if (!ClearVolumeAndPath(walkCenter, walkRotation, walkStart, null, halfSize)) return false;
            }
            return ClearVolumeAndPath(center, rotation, start, coarseHull, halfSize);
        }

        private static bool UnitScale(Transform transform) => (transform.lossyScale - Vector3.one).sqrMagnitude < .0001f;

        private static bool ClearVolumeAndPath(Vector3 center, Quaternion rotation, Vector3 start, Collider allowedHull, Vector3 halfSize)
        {
            foreach (var collider in Physics.OverlapBox(center, halfSize, rotation, ~(1 << 5), QueryTriggerInteraction.Ignore))
                if (!Ignored(collider, allowedHull)) return false;
            Vector3 delta = center - start;
            if (delta.sqrMagnitude > .0001f)
                foreach (var hit in Physics.RaycastAll(start, delta.normalized, delta.magnitude, ~(1 << 5), QueryTriggerInteraction.Ignore))
                    if (!Ignored(hit.collider, allowedHull)) return false;
            return true;
        }

        private static bool Ignored(Collider collider, Collider allowedHull) =>
            collider == allowedHull || collider == Refs.charController ||
            (Refs.observerMirror && collider && collider.GetComponent<PlayerControllerMirror>() == Refs.observerMirror);
    }
}
