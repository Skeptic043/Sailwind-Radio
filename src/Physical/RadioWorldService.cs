using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;
using SailwindRadio.Shops;
using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed class RadioWorldService : IDisposable
    {
        public const string DonorName = "138 model ship junk (small)";
        private static RadioWorldService active;
        private readonly Harmony harmony;
        private readonly Action<string> warn;
        private readonly RadioSaveStore store = new RadioSaveStore();
        private readonly GlobalRadioArbiter arbiter = new GlobalRadioArbiter();
        private readonly List<RadioItemController> items = new List<RadioItemController>();
        private readonly List<KeyValuePair<MethodInfo, MethodInfo>> patches = new List<KeyValuePair<MethodInfo, MethodInfo>>();
        private static readonly FieldInfo BusyField = AccessTools.Field(typeof(SaveLoadManager), "busy");
        private SaveLoadManager manager;
        private bool loading;
        private bool disposed;
        private bool malformedHouseReported;
        internal static bool HookCompatible { get; private set; }
        public IReadOnlyList<RadioItemController> Items => items;
        public int ActiveRadioId => arbiter.ActiveId;
        public event Action BeforeSave;
        public event Action<RadioItemController, RadioAction> ActionRequested;
        public string PersistenceStatus => store.Writable ? "Native save ready" : "Radio data preserved: " + store.Problem;
        internal bool ReadyForShop => !disposed && manager && manager == SaveLoadManager.instance && !loading && store.Writable &&
            FloatingOriginManager.instance && GameState.playing && !GameState.currentlyLoading && !GameState.justStarted &&
            !GameState.loadingBoatLocalItems && !GameState.recovering && SaveLoadManager.readyToSave && !(bool)BusyField.GetValue(manager);

        // Unsold shop stock has instanceId 0 and is never registered with the
        // native save manager. It can be built once the native item world is
        // stable, before the stricter purchase/save readiness check passes.
        internal bool ReadyToStageShop => !disposed && manager && manager == SaveLoadManager.instance && !loading &&
            FloatingOriginManager.instance && GameState.playing && !GameState.currentlyLoading && !GameState.justStarted &&
            !GameState.loadingBoatLocalItems && !GameState.recovering && !(bool)BusyField.GetValue(manager) &&
            TryGetDonor(out _);

        internal RadioItemController CreateShopStock(Transform parent, Vector3 position, Quaternion rotation, int kind)
        {
            if (!ReadyToStageShop || !TryGetDonor(out var donor)) return null;
            GameObject instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(donor, position, rotation);
                instance.transform.SetParent(parent, true);
                instance.transform.localScale = Vector3.one;
                var item = instance.GetComponent<ShipItem>();
                var saveable = instance.GetComponent<SaveablePrefab>();
                item.sold = false;
                saveable.instanceId = 0;
                saveable.currentCrateId = 0;
                saveable.SetParentObject(-1);
                saveable.Start();
                var controller = instance.AddComponent<RadioItemController>();
                controller.Initialize(this, new RadioState
                {
                    Kind = kind,
                    Volume = kind == 0 ? .5f : .75f,
                    TrackPath = ""
                });
                return controller;
            }
            catch
            {
                if (instance) UnityEngine.Object.Destroy(instance);
                throw;
            }
        }

        internal bool ReservePurchase(RadioItemController controller)
        {
            if (!ReadyForShop || !controller || items.Contains(controller)) return false;
            var item = controller.GetComponent<ShipItem>();
            var saveable = controller.GetComponent<SaveablePrefab>();
            if (!item || item.sold || !saveable || saveable.instanceId != 0 || !TryGetDonor(out _) ||
                saveable.prefabIndex != RadioSaveStore.DonorIndex || saveable.GetType() != typeof(SaveablePrefab)) return false;
            try
            {
                // Native RegisterToSave accepts a preassigned ID. Exclude both its live/cached IDs and
                // retained mod records. Nothing is registered as a native owned item before Sell.
                saveable.instanceId = ShopPurchaseReservation.Reserve(store, arbiter, controller.State,
                    () => UnityEngine.Random.Range(1, int.MaxValue),
                    candidate => SaveablePrefab.existingInstanceIds != null && SaveablePrefab.existingInstanceIds.Contains(candidate));
                return true;
            }
            catch (Exception error)
            {
                warn("Radio purchase unavailable: " + error.Message);
                return false;
            }
        }

        internal bool FinishPurchase(RadioItemController controller)
        {
            if (!controller) return false;
            var item = controller.GetComponent<ShipItem>();
            var prefab = controller.GetComponent<SaveablePrefab>();
            bool registered = manager && manager == SaveLoadManager.instance && manager.GetCurrentPrefabs().Contains(prefab);
            if (!item.sold || !registered)
            {
                ShopPurchaseReservation.Cancel(store, arbiter, prefab.instanceId);
                if (item.sold)
                {
                    // Sell marks sold before RegisterToSave. A failed registration is not ownership.
                    // Remove partial native registration rather than leave an item that cannot be saved.
                    prefab.Unregister();
                    UnityEngine.Object.Destroy(controller.gameObject);
                }
                prefab.instanceId = 0;
                return false;
            }
            // The state was fully validated and reserved before native Sell. Keep ownership even if
            // an unrelated native UI callback throws after registration, rather than lose a paid item.
            controller.RefreshOwnership();
            if (!items.Contains(controller)) items.Add(controller);
            return true;
        }

        public RadioWorldService(Harmony harmony, Action<string> warn)
        {
            if (active != null)
                throw new InvalidOperationException("Radio world service already exists");
            this.harmony = harmony;
            this.warn = warn ?? (_ => { });
            active = this;
            try
            {
                Patch(typeof(SaveLoadManager), "SaveModData", nameof(SavePrefix), true);
                Patch(typeof(SaveLoadManager), "LoadGame", nameof(LoadPrefix), true);
                Patch(typeof(SaveLoadManager), "LoadModData", nameof(LoadPostfix), false);
                Patch(typeof(SaveablePrefab), "Load", nameof(PrefabPostfix), false);
                Patch(typeof(SaveablePrefab), "PrepareSaveData", nameof(CapturePrefix), true);
                TryPatchControlHover();
                TryPatchHeldItemControlTarget();
                TryPatchHammerNail();
                TryPatchNativeHooks();
                TryPatchHouseTriggers();
            }
            catch { Dispose(); throw; }
        }

        private void Patch(Type type, string name, string callback, bool prefix)
        {
            MethodInfo target = AccessTools.Method(type, name);
            MethodInfo patch = AccessTools.Method(typeof(RadioWorldService), callback);
            if (target == null || patch == null || BusyField == null)
                throw new MissingMethodException("Native radio save compatibility check failed");
            harmony.Patch(target, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
        }

        private void TryPatchControlHover()
        {
            try
            {
                var target = typeof(LookUI).GetMethod("ShowLookText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(GoPointerButton) }, null);
                if (target == null || target.ReturnType != typeof(void) || !RadioControlHover.Compatible)
                    throw new MissingMethodException("Native control hint layout changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(ControlHoverPostfix));
                harmony.Patch(target, postfix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
            }
            catch (Exception error) { warn("Radio control hint cleanup unavailable. Native hints remain visible. " + error.Message); }
        }

        private void TryPatchHeldItemControlTarget()
        {
            try
            {
                var target = typeof(GoPointer).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                var pointedAt = typeof(GoPointer).GetField("pointedAtButton", BindingFlags.Instance | BindingFlags.NonPublic);
                var lookDistance = typeof(GoPointer).GetField("currentLookDistance", BindingFlags.Instance | BindingFlags.NonPublic);
                if (target == null || target.ReturnType != typeof(void) || pointedAt?.FieldType != typeof(GoPointerButton) ||
                    lookDistance?.FieldType != typeof(float))
                    throw new MissingMethodException("Native pointer target layout changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(HeldItemControlTargetPrefix));
                harmony.Patch(target, prefix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
            }
            catch (Exception error) { warn("Radio held-item control filtering unavailable. " + error.Message); }
        }

        private void TryPatchHammerNail()
        {
            try
            {
                var target = typeof(ShipItemHammer).GetMethod("CanNail", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(ShipItem) }, null);
                if (target == null || target.ReturnType != typeof(bool))
                    throw new MissingMethodException("Native hammer item check changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(HammerCanNailPostfix));
                harmony.Patch(target, postfix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
            }
            catch (Exception error) { warn("Radio hammer locking unavailable. " + error.Message); }
        }

        private static void HammerCanNailPostfix(ShipItem item, ref bool __result)
        {
            if (__result || !item || !item.sold || active == null || active.disposed)
                return;
            var radio = item.GetComponent<RadioItemController>();
            if (radio && radio.State != null && radio.State.Kind == 0 && active.items.Contains(radio))
                __result = true;
        }

        private void TryPatchNativeHooks()
        {
            int first = patches.Count;
            try
            {
                if (!RadioNativeHooks.Compatible)
                    throw new MissingFieldException("Native HangableItem.currentHook contract changed");
                PatchNativeHook(typeof(ShipItem), "OnPickup", Type.EmptyTypes, nameof(HookReleasePostfix), false);
                PatchNativeHook(typeof(ShipItem), "OnEnterInventory", Type.EmptyTypes, nameof(HookInventoryPostfix), false);
                PatchNativeHook(typeof(HangableItem), "LateUpdate", Type.EmptyTypes, nameof(HookPositionPrefix), true);
                PatchNativeHook(typeof(HangableItem), "LateUpdate", Type.EmptyTypes, nameof(HookPositionPostfix), false);
                HookCompatible = true;
            }
            catch (Exception error)
            {
                for (int i = patches.Count - 1; i >= first; i--)
                {
                    harmony.Unpatch(patches[i].Key, patches[i].Value);
                    patches.RemoveAt(i);
                }
                HookCompatible = false;
                warn("Radio hook placement unavailable on this game version. " + error.Message);
            }
        }

        private void TryPatchHouseTriggers()
        {
            int first = patches.Count;
            try
            {
                PatchNativeHook(typeof(ShipItem), "OnTriggerEnter", new[] { typeof(Collider) }, nameof(HouseTriggerPrefix), true);
                PatchNativeHook(typeof(ShipItem), "OnTriggerExit", new[] { typeof(Collider) }, nameof(HouseTriggerPrefix), true);
            }
            catch (Exception error)
            {
                for (int i = patches.Count - 1; i >= first; i--)
                {
                    harmony.Unpatch(patches[i].Key, patches[i].Value);
                    patches.RemoveAt(i);
                }
                warn("Radio house-trigger cleanup unavailable. " + error.Message);
            }
        }

        private void PatchNativeHook(Type type, string name, Type[] arguments, string callback, bool prefix)
        {
            var target = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, arguments, null);
            var patch = AccessTools.Method(typeof(RadioWorldService), callback);
            if (target == null || target.ReturnType != typeof(void) || patch == null)
                throw new MissingMethodException("Native radio hook contract changed: " + type.Name + "." + name);
            harmony.Patch(target, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
        }

        private static void HookReleasePostfix(ShipItem __instance) => Guard(() => { RadioNativeHooks.Release(__instance); });
        private static void HookInventoryPostfix(ShipItem __instance) => Guard(() => RadioNativeHooks.EnterInventory(__instance));
        private static void HookPositionPrefix(HangableItem __instance) => Guard(() => RadioNativeHooks.RejectPhantomHook(__instance));
        private static void HookPositionPostfix(HangableItem __instance) => Guard(() => RadioNativeHooks.PositionBelowHook(__instance));
        private static bool HouseTriggerPrefix(ShipItem __instance, Collider other)
        {
            if (active == null || active.disposed) return true;
            try
            {
                if (RadioNativeHooks.AllowHouseTrigger(__instance, other)) return true;
                if (!active.malformedHouseReported)
                {
                    active.malformedHouseReported = true;
                    active.warn("Radio ignored a House trigger without a native SaveableObject");
                }
                return false;
            }
            catch (Exception error)
            {
                active.warn("Radio house-trigger compatibility check failed: " + error.Message);
                return true;
            }
        }

        public void Tick()
        {
            if (disposed)
                return;
            if (manager != SaveLoadManager.instance)
            {
                items.Clear();
                manager = SaveLoadManager.instance;
                loading = false;
                store.Load(null);
                arbiter.Reset(Array.Empty<RadioRecord>());
                // A new-game scene can retain static modData from the prior session. Native LoadGame sets
                // manager itself in our prefix, so a completed native load never passes through this branch.
                if (manager && manager.loaded)
                    ReadNativeData();
                else
                    GameState.modData?.Remove(RadioSaveStore.Key);
            }
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!items[i])
                    items.RemoveAt(i);
                else
                    items[i].Tick();
            }
        }

        private static bool TryGetDonor(out GameObject donor)
        {
            donor = null;
            var directory = PrefabsDirectory.instance;
            if (!directory || directory.directory == null || directory.directory.Length <= RadioSaveStore.DonorIndex)
                return false;
            donor = directory.directory[RadioSaveStore.DonorIndex];
            if (!donor || donor.name != DonorName || (donor.transform.localScale - Vector3.one).sqrMagnitude > .0001f)
                return false;
            var item = donor.GetComponent<ShipItem>();
            var save = donor.GetComponent<SaveablePrefab>();
            return item && item.GetType() == typeof(ShipItem) && !item.big && !item.wallAttachment && save &&
                save.GetType() == typeof(SaveablePrefab) && save.prefabIndex == RadioSaveStore.DonorIndex && donor.GetComponent<BoxCollider>() &&
                donor.GetComponent<MeshFilter>() && donor.GetComponent<Renderer>() && !donor.GetComponent<Good>();
        }

        private bool Convert(SaveablePrefab saveable, RadioState state)
        {
            if (!saveable)
                return false;
            var existing = saveable.GetComponent<RadioItemController>();
            if (existing)
                return items.Contains(existing);
            var item = saveable.GetComponent<ShipItem>();
            if (!TryGetDonor(out _) || !item || item.GetType() != typeof(ShipItem) ||
                !item.sold || saveable.GetType() != typeof(SaveablePrefab) || saveable.prefabIndex != RadioSaveStore.DonorIndex ||
                saveable.gameObject.name != DonorName + "(Clone)" || !saveable.GetComponent<BoxCollider>())
            {
                warn("A saved radio did not match its expected native item and was left unchanged");
                return false;
            }
            var controller = saveable.gameObject.AddComponent<RadioItemController>();
            controller.Initialize(this, arbiter.Register(saveable.instanceId, state));
            items.Add(controller);
            return true;
        }

        internal void Report(string message) => warn(message);

        internal void TogglePower(RadioItemController item)
        {
            if (disposed || loading || !store.Writable || !items.Contains(item) || manager != SaveLoadManager.instance)
                return;
            try
            {
                if (item.State.Kind != 0)
                {
                    item.State.SpeakerEnabled = !item.State.SpeakerEnabled;
                    WriteCanonical(item.InstanceId);
                    return;
                }
                arbiter.Toggle(item.InstanceId, CaptureAndStop);
                arbiter.WriteTo(store);
                ActionRequested?.Invoke(item, RadioAction.Power);
            }
            catch (Exception ex) { warn("Could not switch the active radio: " + ex.Message); }
        }

        internal void RequestAction(RadioItemController item, RadioAction action)
        {
            if (disposed || loading || !store.Writable || !items.Contains(item) ||
                manager != SaveLoadManager.instance || !item.IsPlacedForControls)
                return;
            if (action == RadioAction.Power)
            {
                TogglePower(item);
                return;
            }
            if (item.State.Kind != 0)
                return;
            ActionRequested?.Invoke(item, action);
        }

        private void CaptureAndStop(int id)
        {
            foreach (var item in items)
            {
                if (!item || item.InstanceId != id)
                    continue;
                item.CapturePosition?.Invoke();
                item.SuspendPlayback?.Invoke();
            }
        }

        internal void Forget(RadioItemController item)
        {
            // Streaming also destroys objects. Retain its record so native cached reload can reconstruct it.
            if (items.Contains(item) && !disposed && !loading && manager == SaveLoadManager.instance && store.Writable)
                try
                {
                    WriteCanonical(item.InstanceId);
                }
                catch (Exception ex) { warn("Could not capture radio state during unload: " + ex.Message); }
            items.Remove(item);
        }

        private void Save()
        {
            Tick();
            if (!store.Writable || loading)
                return;
            BeforeSave?.Invoke();
            foreach (var item in items)
            {
                if (!item)
                    continue;
                item.CapturePosition?.Invoke();
            }
            // Includes cached radios displaced while no live object existed. Never let an old DTO
            // reintroduce its former power state on the next native cached-item restoration.
            arbiter.WriteTo(store);
            string json = store.Save();
            if (GameState.modData == null)
                GameState.modData = new Dictionary<string, string>();
            GameState.modData[RadioSaveStore.Key] = json;
        }

        private void BeginLoad(SaveLoadManager source)
        {
            manager = source;
            loading = true;
            items.Clear();
            store.Load(null);
            arbiter.Reset(Array.Empty<RadioRecord>());
            // Native LoadGame does not clear a static dictionary when the old save has null modData.
            GameState.modData?.Remove(RadioSaveStore.Key);
        }

        private void ReadNativeData()
        {
            string json = null;
            GameState.modData?.TryGetValue(RadioSaveStore.Key, out json);
            loading = false;
            if (!store.Load(json))
            {
                arbiter.Reset(Array.Empty<RadioRecord>());
                warn(PersistenceStatus);
                return;
            }
            arbiter.Reset(store.Records);
            if (!manager)
                return;
            foreach (var prefab in manager.GetCurrentPrefabs())
                Restore(prefab);
        }

        private void Restore(SaveablePrefab prefab)
        {
            if (!loading && prefab && store.TryGet(prefab.instanceId, prefab.prefabIndex, out var record))
                Convert(prefab, record.ToState());
        }

        // Exceptions must never interrupt the game's save/load call or suppress its original body.
        private static void SavePrefix() => Guard(() => active.Save());
        private static void LoadPrefix(SaveLoadManager __instance) => Guard(() => active.BeginLoad(__instance));
        private static void LoadPostfix() => Guard(() => active.ReadNativeData());
        private static void PrefabPostfix(SaveablePrefab __instance) => Guard(() => active.Restore(__instance));
        private static void CapturePrefix(SaveablePrefab __instance) => Guard(() => active.Capture(__instance));
        private static void ControlHoverPostfix(LookUI __instance, GoPointerButton button) => Guard(() => RadioControlHover.ClearPickupHint(__instance, button));
        private static void HeldItemControlTargetPrefix(GoPointer __instance, ref GoPointerButton ___pointedAtButton,
            ref float ___currentLookDistance) =>
            RadioHeldItemTarget.Clear(__instance, ref ___pointedAtButton, ref ___currentLookDistance);
        private void Capture(SaveablePrefab prefab)
        {
            var item = prefab.GetComponent<RadioItemController>();
            if (!item || !items.Contains(item) || !store.Writable || loading)
                return;
            item.CapturePosition?.Invoke();
            WriteCanonical(item.InstanceId);
        }

        private void WriteCanonical(int id)
        {
            if (arbiter.TryGet(id, out var state))
                store.Put(RadioRecord.Capture(id, RadioSaveStore.DonorIndex, state));
        }
        private static void Guard(Action action)
        {
            if (active == null || active.disposed)
                return;
            try
            {
                action();
            }
            catch (Exception ex) { active.warn("Radio native integration failed: " + ex.Message); }
        }

        public void Dispose()
        {
            if (disposed)
                return;
            disposed = true;
            foreach (var item in items)
                if (item)
                {
                    item.CapturePosition = null;
                    item.SuspendPlayback = null;
                }
            items.Clear();
            foreach (var pair in patches)
                harmony.Unpatch(pair.Key, pair.Value);
            patches.Clear();
            if (active == this)
            {
                HookCompatible = false;
                active = null;
            }
        }
    }
}
