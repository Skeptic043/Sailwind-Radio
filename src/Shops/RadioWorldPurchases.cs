using System;
using System.Collections.Generic;
using SailwindRadio.Persistence;
using SailwindRadio.Shops;
using UnityEngine;

namespace SailwindRadio.Physical
{
    // The native RegisterToSave lifecycle is shared by purchases, spawning and starting supplies.
    public sealed partial class RadioWorldService
    {
        private sealed class RegistrationAttempt
        {
            internal int Id;
            internal RadioState State;
            internal SaveLoadManager Manager;
        }

        private static bool RegisterPrefix(SaveablePrefab __instance, ref RegistrationAttempt __state)
        {
            __state = null;
            if (active == null || active.disposed) return true;
            if (active.loading && !active.loadingPrefabs.Contains(__instance))
            {
                var marker = __instance ? __instance.GetComponent<RadioTemplateMarker>() : null;
                var item = __instance ? __instance.GetComponent<ShipItem>() : null;
                if (marker && item && item.sold && __instance.GetComponent<RadioItemController>())
                {
                    if (!active.pendingRegistrations.Contains(__instance)) active.pendingRegistrations.Add(__instance);
                    return false;
                }
            }
            if (active.loading) return true;
            try { return active.PrepareRegistration(__instance, out __state); }
            catch (Exception error) { active.warn("Radio item registration refused: " + error.Message); return false; }
        }

        private static void RegisterPostfix(SaveablePrefab __instance)
        {
            Guard(() => active.CompleteRegistration(__instance));
        }

        private static Exception RegisterFinalizer(SaveablePrefab __instance, RegistrationAttempt __state, Exception __exception)
        {
            if (__state != null && active != null && !active.disposed &&
                active.manager == __state.Manager && __state.Manager == SaveLoadManager.instance)
            {
                // A later callback can fail after the native manager took ownership. Keep that
                // item and its record. Cancel only this attempt's still-unowned canonical state.
                bool registered = active.manager && active.manager == SaveLoadManager.instance &&
                    active.manager.GetCurrentPrefabs().Contains(__instance);
                if (!registered && active.arbiter.TryGet(__state.Id, out var state) && ReferenceEquals(state, __state.State) &&
                    active.store.TryGet(__state.Id, RadioSaveStore.ItemIndex(state.Kind), out _) &&
                    !active.NativeOwnerPresent(__state.Id))
                {
                    SaveablePrefab.existingInstanceIds?.Remove(__state.Id);
                    active.store.Remove(__state.Id);
                    active.arbiter.Remove(__state.Id);
                    if (__instance && __instance.instanceId == __state.Id) __instance.instanceId = 0;
                }
            }
            return __exception;
        }

        private bool PrepareRegistration(SaveablePrefab prefab, out RegistrationAttempt created)
        {
            created = null;
            if (!prefab || loadingPrefabs.Contains(prefab) || !RadioSaveStore.IsItemIndex(prefab.prefabIndex)) return true;
            var marker = prefab.GetComponent<RadioTemplateMarker>();
            var controller = prefab.GetComponent<RadioItemController>();
            var item = prefab.GetComponent<ShipItem>();
            if (!marker) return true;
            if (!controller || controller.State == null || !item || !item.sold || marker.Kind != controller.State.Kind ||
                prefab.prefabIndex != RadioSaveStore.ItemIndex(marker.Kind) ||
                !IsTemplateInstanceName(prefab.gameObject.name, marker.Kind)) return false;
            if (manager != SaveLoadManager.instance) Tick();
            if (!manager || !store.Writable) return false;
            // Native registration can be requested again. Never allocate a replacement identity
            // for an already registered object, even when its Radio data cannot be validated.
            if (manager.GetCurrentPrefabs().Contains(prefab))
            {
                CompleteRegistration(prefab);
                return false;
            }
            // A controller already managed by this world keeps its established identity if
            // native code requests registration again after removing its current membership.
            if (items.Contains(controller))
                return store.TryGet(prefab.instanceId, prefab.prefabIndex, out var existing) && existing.Kind == marker.Kind &&
                    arbiter.TryGet(prefab.instanceId, out var known) && ReferenceEquals(known, controller.State) &&
                    !NativeOwnerPresent(prefab.instanceId);
            int id = prefab.instanceId;
            if (id <= 0 || IdUsed(id))
            {
                id = 0;
                for (int attempt = 0; attempt < 64; attempt++)
                {
                    int candidate = UnityEngine.Random.Range(1, int.MaxValue);
                    if (!IdUsed(candidate)) { id = candidate; break; }
                }
                if (id == 0) return false;
            }
            prefab.instanceId = id;
            try
            {
                store.Put(RadioRecord.Capture(id, prefab.prefabIndex, controller.State));
                store.Save();
                arbiter.Register(id, controller.State);
                created = new RegistrationAttempt { Id = id, State = controller.State, Manager = manager };
                return true;
            }
            catch
            {
                store.Remove(id);
                arbiter.Remove(id);
                prefab.instanceId = 0;
                throw;
            }
        }

        private bool IdUsed(int id) => store.TryGetAny(id, out _) || arbiter.TryGet(id, out _) || NativeIdUsed(id);

        private bool NativeIdUsed(int id)
        {
            if (SaveablePrefab.existingInstanceIds != null && SaveablePrefab.existingInstanceIds.Contains(id)) return true;
            return NativeOwnerPresent(id);
        }

        private bool NativeOwnerPresent(int id)
        {
            if (!manager) return false;
            foreach (int existing in NativeOwnerIds()) if (existing == id) return true;
            return false;
        }

        internal bool CompleteRegistration(SaveablePrefab prefab)
        {
            // Native Load registers before the Load postfix binds persisted state. Leave those
            // objects to the existing restore path, including its duplicate-ID deferrals.
            if (loading || !prefab || loadingPrefabs.Contains(prefab) || !manager || manager != SaveLoadManager.instance ||
                !manager.GetCurrentPrefabs().Contains(prefab)) return false;
            var controller = prefab.GetComponent<RadioItemController>();
            var marker = prefab.GetComponent<RadioTemplateMarker>();
            var item = prefab.GetComponent<ShipItem>();
            if (!controller || controller.State == null || !marker || !item || !item.sold ||
                marker.Kind != controller.State.Kind || marker.Kind < 0 || marker.Kind > 3 ||
                prefab.prefabIndex != RadioSaveStore.ItemIndex(marker.Kind) ||
                !IsTemplateInstanceName(prefab.gameObject.name, marker.Kind) ||
                !store.TryGet(prefab.instanceId, prefab.prefabIndex, out var record) || record.Kind != marker.Kind ||
                !arbiter.TryGet(prefab.instanceId, out var canonical) || canonical.Kind != marker.Kind ||
                !RadioSaveStore.HasUniqueNativeOwner(prefab.instanceId, NativeOwnerIds(), out _)) return false;
            var stock = prefab.GetComponent<RadioShopStock>();
            if (stock && stock.Controller != controller) return false;
            if (!ReferenceEquals(controller.State, canonical)) controller.ApplyRestoredState(this, canonical);
            controller.RefreshOwnership();
            if (!items.Contains(controller)) items.Add(controller);
            if (stock)
            {
                controller.SetShopStock(false);
                stock.Purchased = true;
                stock.PurchaseEnabled = false;
            }
            return true;
        }

        private void ReconcileShopOwnership(RadioItemController controller)
        {
            // Old bypassed purchases already reached this world's registered item list. No
            // scene scan, new identity, missing-record adoption or payment inference is needed.
            var stock = controller.GetComponent<RadioShopStock>();
            if (stock && !stock.Purchased) CompleteRegistration(controller.GetComponent<SaveablePrefab>());
        }

        private IEnumerable<int> NativeOwnerIds()
        {
            foreach (var live in manager.GetCurrentPrefabs())
                if (live) yield return live.instanceId;
            foreach (var obj in manager.GetCurrentObjects())
                if (obj && obj.localItems && obj.localItems.HasLocalItems())
                    foreach (var cached in obj.localItems.GetCachedItems())
                        if (cached != null) yield return cached.instanceId;
        }
    }
}
