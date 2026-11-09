using System;
using System.Collections.Generic;
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
            // A boat cabin's walls live in the boat's separate walk-space copy of the hull.
            internal bool BoatOwned;
            internal BoatDamage Boat;
            internal Transform Model, Walk;
        }

        // The cabins one listening point or output is in, rechecked about once a second. An output's
        // placement also holds the cabins whose wall it confirmed between it and the listening point.
        private sealed class Placement
        {
            internal readonly List<InteriorEffectsTrigger> Inside = new List<InteriorEffectsTrigger>();
            internal readonly List<InteriorEffectsTrigger> Walled = new List<InteriorEffectsTrigger>();
            internal bool UsedHead;
            internal float RefreshAt = float.NegativeInfinity;
            internal float AskedAt;
            internal float Last;
        }

        // Scene loads and unloads rescan at once, which is when island buildings bring their cabins.
        // Boat cabins exist from the start, so the cabin search between them is only a safety net.
        private const float VolumeScanSeconds = 30f;
        private const float ListenerScanSeconds = 2f;
        // Native Sailwind takes two seconds to change its own interior mix, and outputs ease
        // muffling over a quarter second, so cabin membership only needs checking about once a second.
        private const float PlacementSeconds = 1f;
        // Outputs first seen together drift apart by up to this much, so their checks don't line up.
        private const float PlacementSpread = .3f;
        private const float ForgetSeconds = 5f;

        private Volume[] volumes = Array.Empty<Volume>();
        private readonly Dictionary<InteriorEffectsTrigger, Volume> volumeOf = new Dictionary<InteriorEffectsTrigger, Volume>();
        private readonly RaycastHit[] hits = new RaycastHit[64];
        private AudioListener[] listeners = Array.Empty<AudioListener>();
        private readonly Placement listenerPlacement = new Placement();
        private readonly Placement headPlacement = new Placement();
        private readonly Dictionary<Component, Placement> outputs = new Dictionary<Component, Placement>();
        private readonly List<Component> forgotten = new List<Component>();
        private float nextVolumes;
        private float nextListeners;
        private float nextSweep;
        private float previousTime = -1;
        private bool placementsStale;
        private bool disposed;
        private Vector3 headOffset;
        private bool headTracked;
        internal bool ListenerKnown { get; private set; }
        internal Vector3 ListenerPosition { get; private set; }
        // The player's head while the boat camera has carried the native listener away from it.
        internal bool HeadKnown { get; private set; }
        internal Vector3 HeadPosition { get; private set; }

        internal RadioAcousticsService()
        {
            SceneManager.sceneLoaded += SceneLoaded;
            SceneManager.sceneUnloaded += SceneUnloaded;
        }

        // Scene changes add and remove cabins in bulk, so every placement is worked out again.
        internal void Invalidate()
        {
            nextVolumes = nextListeners = 0;
            placementsStale = true;
        }
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
                volumeOf.Clear();
                for (int i = 0; i < native.Length; i++)
                {
                    volumes[i] = new Volume { Trigger = native[i], Colliders = native[i].GetComponents<Collider>() };
                    ResolveMapping(volumes[i]);
                    volumeOf[native[i]] = volumes[i];
                }
                nextVolumes = now + VolumeScanSeconds;
            }
            if (placementsStale)
            {
                placementsStale = false;
                outputs.Clear();
                listenerPlacement.RefreshAt = headPlacement.RefreshAt = float.NegativeInfinity;
            }
            if (now >= nextListeners)
            {
                listeners = UnityEngine.Object.FindObjectsOfType<AudioListener>();
                nextListeners = now + ListenerScanSeconds;
            }
            AudioListener selected = null;
            int count = 0;
            foreach (var listener in listeners)
            {
                if (!listener || !listener.enabled || !listener.gameObject.activeInHierarchy) continue;
                selected = listener;
                count++;
            }
            bool listenerWasKnown = ListenerKnown, headWasKnown = HeadKnown;
            ListenerKnown = count == 1;
            // No single usable listener can mean one appeared or changed since the last search,
            // so search again at the original quarter-second pace until there is one.
            if (!ListenerKnown) nextListeners = Math.Min(nextListeners, now + .25f);
            if (ListenerKnown)
            {
                ListenerPosition = selected.transform.position;
                ListenerKnown = Finite(ListenerPosition);
            }
            TrackHead();
            // A listening point that has just become known is placed at once.
            if (ListenerKnown && (!listenerWasKnown || now >= listenerPlacement.RefreshAt))
                Place(listenerPlacement, ListenerPosition, now, 0);
            if (HeadKnown && (!headWasKnown || now >= headPlacement.RefreshAt))
                Place(headPlacement, HeadPosition, now, 0);
            if (now >= nextSweep) Sweep(now);
        }

        // Native BoatCamera reparents the listener's eye anchor to its own rig and leaves the player's
        // camera rig in place. The listener's last place in that rig is the head while the camera is on.
        private void TrackHead()
        {
            HeadKnown = false;
            Transform rig = Refs.ovrCameraRig;
            if (!rig || !ListenerKnown) return;
            if (!BoatCamera.on)
            {
                headOffset = rig.InverseTransformPoint(ListenerPosition);
                headTracked = true;
                return;
            }
            if (!headTracked) return;
            HeadPosition = rig.TransformPoint(headOffset);
            HeadKnown = Finite(HeadPosition);
        }

        // Follows Sailwind's own interior mix. Sharing any cabin volume with the listening point is clear.
        // Otherwise a cabin volume holding only one of them muffles at that cabin's native level (partly
        // indoors with a door open, fully with every door closed) when a ray between them meets a real
        // wall of it, and the strongest one applies. Membership and walls are rechecked about once a
        // second, while door state is read on every call.
        internal float ObstructionAt(Component output, Vector3 sourcePosition, bool carriedOrPlayerInventory, bool fromHead = false)
        {
            if (disposed || !ListenerKnown || carriedOrPlayerInventory || !Finite(sourcePosition) || !output) return 0;
            float now = Time.realtimeSinceStartup;
            if (!outputs.TryGetValue(output, out var placement))
            {
                placement = new Placement();
                outputs.Add(output, placement);
            }
            bool useHead = fromHead && HeadKnown;
            var listening = useHead ? headPlacement : listenerPlacement;
            // Switching between the head and the native listener changes which walls lie between.
            if (now >= placement.RefreshAt || placement.UsedHead != useHead)
            {
                Place(placement, sourcePosition, now, Spread(output));
                ConfirmWalls(placement, sourcePosition, listening, useHead ? HeadPosition : ListenerPosition);
                placement.UsedHead = useHead;
            }
            placement.AskedAt = now;
            placement.Last = Obstruction(listening.Inside, placement.Inside, placement.Walled);
            return placement.Last;
        }

        // An output that can't be heard keeps its last level, so muffling never eases in from a wrong one.
        // Asking for it counts as being heard from, so the level isn't forgotten while the output is silent.
        internal float LastObstruction(Component output)
        {
            if (!output || !outputs.TryGetValue(output, out var placement)) return 0;
            placement.AskedAt = Time.realtimeSinceStartup;
            return placement.Last;
        }

        // A confirmed wall only counts while its cabin still holds exactly one of the two, so walking out of
        // a cabin never keeps a stale wall until the output's next check.
        private static float Obstruction(List<InteriorEffectsTrigger> listening, List<InteriorEffectsTrigger> source,
            List<InteriorEffectsTrigger> walled)
        {
            foreach (var trigger in source)
                if (Live(trigger) && listening.Contains(trigger)) return 0;
            float obstruction = 0;
            foreach (var trigger in walled)
                if (Live(trigger) && source.Contains(trigger) != listening.Contains(trigger))
                    obstruction = Math.Max(obstruction, BoundaryObstruction(trigger));
            return obstruction;
        }

        // Casts one ray per cabin volume that holds only one of the two. A shared volume is clear, so it casts none.
        private void ConfirmWalls(Placement placement, Vector3 source, Placement listening, Vector3 listener)
        {
            placement.Walled.Clear();
            foreach (var trigger in placement.Inside)
                if (Live(trigger) && listening.Inside.Contains(trigger)) return;
            foreach (var trigger in placement.Inside)
                if (Walled(trigger, source, listener)) placement.Walled.Add(trigger);
            foreach (var trigger in listening.Inside)
                if (!placement.Inside.Contains(trigger) && Walled(trigger, source, listener)) placement.Walled.Add(trigger);
        }

        private bool Walled(InteriorEffectsTrigger trigger, Vector3 source, Vector3 listener) =>
            Live(trigger) && volumeOf.TryGetValue(trigger, out var volume) && HasPhysicalBoundary(volume, source, listener);

        private bool HasPhysicalBoundary(Volume volume, Vector3 source, Vector3 listener)
        {
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
            Vector3 delta = listener - source;
            if (delta.sqrMagnitude <= .0001f) return false;
            int count = Physics.RaycastNonAlloc(source, delta.normalized, hits, delta.magnitude,
                ~(1 << 5), QueryTriggerInteraction.Ignore);
            if (count >= hits.Length) return true; // Truncation cannot establish a clear ray.
            for (int i = 0; i < count; i++)
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
                return true;
            }
            return false;
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

        private void Place(Placement placement, Vector3 position, float now, float spread)
        {
            placement.Inside.Clear();
            placement.RefreshAt = now + PlacementSeconds + spread;
            if (!Finite(position)) return;
            foreach (var volume in volumes)
            {
                // The precise test only runs for colliders whose current bounds hold the point,
                // so distant boats and buildings cost one bounds check each.
                if (Live(volume.Trigger) && TryContains(volume.Colliders, position, out bool inside) && inside)
                    placement.Inside.Add(volume.Trigger);
            }
        }

        private static float Spread(Component output) => (output.GetInstanceID() & 0xFF) / 255f * PlacementSpread;

        private static bool Live(InteriorEffectsTrigger trigger) =>
            trigger && trigger.enabled && trigger.gameObject.activeInHierarchy;

        // Devices that are destroyed or have gone unheard for a while release their placements.
        private void Sweep(float now)
        {
            nextSweep = now + PlacementSeconds;
            forgotten.Clear();
            foreach (var pair in outputs)
                if (!pair.Key || now - pair.Value.AskedAt > ForgetSeconds) forgotten.Add(pair.Key);
            foreach (var output in forgotten) outputs.Remove(output);
            forgotten.Clear();
        }

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
            volumeOf.Clear();
            listeners = Array.Empty<AudioListener>();
            outputs.Clear();
            listenerPlacement.Inside.Clear();
            headPlacement.Inside.Clear();
            ListenerKnown = HeadKnown = false;
        }
    }
}
