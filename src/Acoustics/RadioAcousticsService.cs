using System;
using UnityEngine;
using UnityEngine.SceneManagement;

namespace SailwindRadio.Acoustics
{
    internal sealed class RadioAcousticsService : IDisposable
    {
        private sealed class Volume
        {
            internal InteriorEffectsTrigger Trigger;
            internal Collider[] Colliders;
            internal bool BoatOwned;
            internal BoatDamage Boat;
            internal Transform Model, Walk;
            internal readonly RayCache[] Rays = new RayCache[32];
            internal int NextRay;
        }

        private struct RayCache
        {
            internal bool Valid, Blocked;
            internal Vector3 Source, Listener;
            internal float Until;
        }

        private readonly RaycastHit[] hits = new RaycastHit[64];

        private Volume[] volumes = Array.Empty<Volume>();
        private AudioListener[] listeners = Array.Empty<AudioListener>();
        private float nextVolumes;
        private float nextListeners;
        private float previousTime = -1;
        private bool disposed;
        internal bool ListenerKnown { get; private set; }
        internal Vector3 ListenerPosition { get; private set; }

        internal RadioAcousticsService()
        {
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
        }

        internal void Invalidate() { nextVolumes = nextListeners = 0; }
        private void SceneLoaded(Scene scene, LoadSceneMode mode) { Invalidate(); }
        private void SceneUnloaded(Scene scene) { Invalidate(); }

        internal void Tick()
        {
            if (disposed) return;
            float now = Time.realtimeSinceStartup;
            if (now < previousTime) Invalidate();
            previousTime = now;
            if (now >= nextVolumes)
            {
                var native = UnityEngine.Object.FindObjectsOfType<InteriorEffectsTrigger>();
                volumes = new Volume[native.Length];
                for (int i = 0; i < native.Length; i++)
                {
                    volumes[i] = new Volume { Trigger = native[i], Colliders = native[i].GetComponents<Collider>() };
                    ResolveMapping(volumes[i]);
                }
                nextVolumes = now + 1f;
            }
            if (now >= nextListeners)
            {
                listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>();
                nextListeners = now + .25f;
            }
            AudioListener selected = null;
            int count = 0;
            foreach (var listener in listeners)
            {
                if (!listener || !listener.enabled || !listener.gameObject.activeInHierarchy) continue;
                selected = listener;
                count++;
            }
            ListenerKnown = count == 1;
            if (ListenerKnown)
            {
                ListenerPosition = selected.transform.position;
                ListenerKnown = Finite(ListenerPosition);
            }
        }

        internal float ObstructionAt(Vector3 sourcePosition, bool carriedOrPlayerInventory)
        {
            if (disposed || !ListenerKnown || carriedOrPlayerInventory || !Finite(sourcePosition)) return 0;
            float obstruction = 0;
            foreach (var volume in volumes)
            {
                if (!volume.Trigger || !volume.Trigger.enabled || !volume.Trigger.gameObject.activeInHierarchy) continue;
                if (!TryContains(volume.Colliders, sourcePosition, out bool sourceInside) ||
                    !TryContains(volume.Colliders, ListenerPosition, out bool listenerInside)) continue;
                // Native overlapping volumes can describe one cabin. A shared enclosure is clear,
                // rather than inventing an internal wall from an extra overlapping trigger.
                if (sourceInside && listenerInside) return 0;
                if (sourceInside != listenerInside && HasPhysicalBoundary(volume, sourcePosition))
                    obstruction = Math.Max(obstruction, BoundaryObstruction(volume.Trigger));
            }
            return obstruction;
        }

        private bool HasPhysicalBoundary(Volume volume, Vector3 source)
        {
            Vector3 listener = ListenerPosition;
            Transform walk = null;
            if (volume.BoatOwned)
            {
                Transform model = volume.Model;
                walk = volume.Walk;
                // Native cabin membership is the conservative fallback when a vessel's
                // real collision space cannot be identified unambiguously.
                if (!volume.Boat || !model || !walk || !UnitScale(model) || !UnitScale(walk)) return true;
                source = walk.TransformPoint(model.InverseTransformPoint(source));
                listener = walk.TransformPoint(model.InverseTransformPoint(listener));
            }
            if (!Finite(source) || !Finite(listener)) return true;
            float now = Time.realtimeSinceStartup;
            for (int i = 0; i < volume.Rays.Length; i++)
            {
                RayCache cached = volume.Rays[i];
                if (cached.Valid && now <= cached.Until && (cached.Source - source).sqrMagnitude < .0001f &&
                    (cached.Listener - listener).sqrMagnitude < .0001f) return cached.Blocked;
            }
            Vector3 delta = listener - source;
            bool blocked = false;
            if (delta.sqrMagnitude > .0001f)
            {
                int count = Physics.RaycastNonAlloc(source, delta.normalized, hits, delta.magnitude,
                    ~(1 << 5), QueryTriggerInteraction.Ignore);
                blocked = count >= hits.Length; // Truncation cannot establish a clear ray.
                for (int i = 0; i < count && i < hits.Length; i++)
                {
                    Collider collider = hits[i].collider;
                    if (!collider || collider.isTrigger || !collider.enabled || !collider.gameObject.activeInHierarchy) continue;
                    if (collider.GetComponentInParent<ShipItem>() || collider == Refs.charController ||
                        collider.GetComponentInParent<PlayerControllerMirror>()) continue;
                    if (walk)
                    {
                        if (!collider.transform.IsChildOf(walk)) continue;
                    }
                    else if (collider is CapsuleCollider && collider.GetComponent<BoatDamage>()) continue;
                    blocked = true;
                    break;
                }
            }
            volume.Rays[volume.NextRay] = new RayCache { Valid = true, Source = source, Listener = listener, Until = now + .15f, Blocked = blocked };
            volume.NextRay = (volume.NextRay + 1) % volume.Rays.Length;
            return blocked;
        }

        private static void ResolveMapping(Volume volume)
        {
            volume.Boat = volume.Trigger.GetComponentInParent<BoatDamage>();
            volume.BoatOwned = volume.Boat;
            if (!volume.Boat) return;
            foreach (var embark in volume.Boat.GetComponentsInChildren<BoatEmbarkCollider>())
            {
                if (!embark || embark.GetTopmostBoatParent() != volume.Boat.transform) continue;
                Transform candidate = embark.GetMeshParent();
                if (!candidate || !volume.Trigger.transform.IsChildOf(candidate) || !embark.walkCollider) continue;
                if (volume.Model && (volume.Model != candidate || volume.Walk != embark.walkCollider))
                {
                    volume.Model = volume.Walk = null;
                    return;
                }
                volume.Model = candidate;
                volume.Walk = embark.walkCollider;
            }
        }

        private static bool UnitScale(Transform value) => (value.lossyScale - Vector3.one).sqrMagnitude < .0001f;

        internal static bool TryContains(Collider[] colliders, Vector3 position, out bool inside)
        {
            inside = false;
            if (!Finite(position) || colliders == null || colliders.Length == 0) return false;
            bool known = false;
            foreach (var collider in colliders)
            {
                if (!collider || !collider.enabled || !collider.gameObject.activeInHierarchy || !collider.isTrigger) continue;
                // The inspected beta uses visual-model/land BoxColliders and convex MeshColliders.
                // Do not reinterpret a fixed walk-space copy or approximate unsupported mesh geometry.
                if (!(collider is BoxCollider) && !(collider is MeshCollider mesh && mesh.convex && mesh.sharedMesh)) return false;
                known = true;
                if (collider.bounds.SqrDistance(position) > .000001f) continue;
                if ((collider.ClosestPoint(position) - position).sqrMagnitude <= .000001f) inside = true;
            }
            return known;
        }

        private static bool Finite(Vector3 point) =>
            !float.IsNaN(point.x) && !float.IsInfinity(point.x) &&
            !float.IsNaN(point.y) && !float.IsInfinity(point.y) &&
            !float.IsNaN(point.z) && !float.IsInfinity(point.z);

        internal static float BoundaryObstruction(InteriorEffectsTrigger trigger)
        {
            bool open = trigger.semiIndoor;
            if (trigger.doors != null && trigger.doors.Length > 0)
            {
                open = false;
                foreach (var door in trigger.doors)
                {
                    if (!door) return 0;
                    if (door.IsOpen()) open = true;
                }
            }
            if (trigger.houseDoor) open = trigger.houseDoor.IsOpen();
            if (trigger.houseDoors != null && trigger.houseDoors.Length > 0)
            {
                open = false;
                foreach (var door in trigger.houseDoors)
                {
                    if (!door) return 0;
                    if (door.IsOpen()) open = true;
                }
            }
            return open ? .35f : 1f;
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            SceneManager.sceneLoaded -= SceneLoaded;
            SceneManager.sceneUnloaded -= SceneUnloaded;
            volumes = Array.Empty<Volume>();
            listeners = Array.Empty<AudioListener>();
            ListenerKnown = false;
        }
    }
}
