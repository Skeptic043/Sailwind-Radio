using System.Collections.Generic;
using SailwindRadio.Persistence;
using SailwindRadio.Shops;

namespace SailwindRadio.Physical
{
    public sealed partial class RadioWorldService
    {
        // Loaded clones leave this set before Load restores their identity, so
        // missing saved records are never invented by the fresh acquisition path.
        private readonly HashSet<RadioItemController> pendingBindings = new HashSet<RadioItemController>();

        internal bool EnsureOwned(RadioItemController controller)
        {
            if (disposed || !controller) return false;
            if (manager != SaveLoadManager.instance) Tick();
            try { return BindOwned(controller.GetComponent<SaveablePrefab>()); }
            catch (System.Exception error) { warn("Radio state binding deferred: " + error.Message); return false; }
        }

        private void BindPending()
        {
            foreach (var controller in new List<RadioItemController>(pendingBindings))
                if (!controller) pendingBindings.Remove(controller);
                else EnsureOwned(controller);
        }

        private bool BindOwned(SaveablePrefab prefab)
        {
            if (loading || GameState.currentlyLoading || !store.Writable || !prefab || loadingPrefabs.Contains(prefab) ||
                !manager || manager != SaveLoadManager.instance || prefab.instanceId <= 0 ||
                !manager.GetCurrentPrefabs().Contains(prefab)) return false;
            var controller = prefab.GetComponent<RadioItemController>();
            var marker = prefab.GetComponent<RadioTemplateMarker>();
            var item = prefab.GetComponent<ShipItem>();
            if (!controller || controller.State == null || !item || !item.sold ||
                (!items.Contains(controller) && !pendingBindings.Contains(controller))) return false;
            // Migrated live donors intentionally retain their native name and lack a marker.
            if (!marker && items.Contains(controller) && prefab.gameObject.name == DonorName + "(Clone)" &&
                store.TryGet(prefab.instanceId, prefab.prefabIndex, out var legacy) && legacy.Kind == controller.State.Kind &&
                arbiter.TryGet(prefab.instanceId, out var managed) && managed.Kind == legacy.Kind)
            {
                if (!ReferenceEquals(controller.State, managed)) controller.ApplyRestoredState(this, managed);
                controller.RefreshOwnership();
                return true;
            }
            if (!marker ||
                marker.Kind != controller.State.Kind || marker.Kind < 0 || marker.Kind > 3 ||
                prefab.prefabIndex != RadioSaveStore.ItemIndex(marker.Kind) ||
                !IsTemplateInstanceName(prefab.gameObject.name, marker.Kind)) return false;
            var stock = prefab.GetComponent<RadioShopStock>();
            if (stock && stock.Controller != controller) return false;
            int id = prefab.instanceId;
            if (arbiter.TryGet(id, out var canonical) && canonical.Kind != marker.Kind) return false;
            if (store.TryGetAny(id, out var record))
            {
                if (record.PrefabIndex != prefab.prefabIndex || record.Kind != marker.Kind) return false;
                if (canonical == null) canonical = arbiter.Register(id, record.ToState());
            }
            else
            {
                if (!pendingBindings.Contains(controller)) return false;
                var state = canonical ?? controller.State;
                store.Put(RadioRecord.Capture(id, prefab.prefabIndex, state));
                canonical = arbiter.Register(id, state);
            }
            if (!ReferenceEquals(controller.State, canonical)) controller.ApplyRestoredState(this, canonical);
            controller.RefreshOwnership();
            if (!items.Contains(controller)) items.Add(controller);
            pendingBindings.Remove(controller);
            if (stock)
            {
                controller.SetShopStock(false);
                stock.Purchased = true;
            }
            return true;
        }

        private void ReconcileShopOwnership(RadioItemController controller)
        {
            var stock = controller.GetComponent<RadioShopStock>();
            if (stock && !stock.Purchased) EnsureOwned(controller);
        }

        // Restoration retains its existing transient live/cache duplicate handling.
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
