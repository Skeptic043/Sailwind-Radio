using System;
using System.Collections.Generic;
using System.IO;
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
        private readonly GameObject[] templates = new GameObject[4];
        private AssetBundle templateBundle;
        private bool bundleAttempted;
        private readonly HashSet<SaveablePrefab> loadingPrefabs = new HashSet<SaveablePrefab>();
        private readonly List<SaveablePrefab> pendingRegistrations = new List<SaveablePrefab>();
        private readonly HashSet<SaveablePrefab> pendingRestores = new HashSet<SaveablePrefab>();
        private readonly HashSet<int> reportedAmbiguousIds = new HashSet<int>();
        private PrefabsDirectory templateDirectory;
        private readonly List<KeyValuePair<MethodInfo, MethodInfo>> patches = new List<KeyValuePair<MethodInfo, MethodInfo>>();
        private static readonly FieldInfo BusyField = AccessTools.Field(typeof(SaveLoadManager), "busy");
        private SaveLoadManager manager;
        private bool loading;
        private bool missingRadioData;
        private bool disposed;
        private bool malformedHouseReported;
        private float nextRestoreRetry;
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
            TryGetTemplate(0, out _);

        internal RadioItemController CreateShopStock(Transform parent, Vector3 position, Quaternion rotation, int kind)
        {
            if (!ReadyToStageShop || !TryGetTemplate(kind, out var template)) return null;
            GameObject instance = null;
            try
            {
                instance = UnityEngine.Object.Instantiate(template, position, rotation);
                instance.transform.SetParent(parent, true);
                instance.transform.localScale = Vector3.one;
                var item = instance.GetComponent<ShipItem>();
                var saveable = instance.GetComponent<SaveablePrefab>();
                item.sold = false;
                saveable.instanceId = 0;
                saveable.currentCrateId = 0;
                saveable.SetParentObject(-1);
                saveable.Start();
                var controller = instance.GetComponent<RadioItemController>();
                if (!controller || controller.State == null || controller.State.Kind != kind)
                    throw new InvalidOperationException("Radio item template did not initialize its clone");
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
            if (!item || item.sold || !saveable || saveable.instanceId != 0 || !TryGetTemplate(controller.State.Kind, out _) ||
                saveable.prefabIndex != RadioSaveStore.ItemIndex(controller.State.Kind) || saveable.GetType() != typeof(SaveablePrefab)) return false;
            try
            {
                // Native RegisterToSave accepts a preassigned ID. Exclude both its live/cached IDs and
                // retained mod records. Nothing is registered as a native owned item before Sell.
                saveable.instanceId = ShopPurchaseReservation.Reserve(store, arbiter, controller.State,
                    () => UnityEngine.Random.Range(1, int.MaxValue),
                    IdUsed);
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
                Patch(typeof(SaveablePrefab), "Load", nameof(PrefabLoadPrefix), true);
                Patch(typeof(SaveablePrefab), "Load", nameof(PrefabPostfix), false);
                Patch(typeof(SaveablePrefab), "PrepareSaveData", nameof(CapturePrefix), true);
                PatchDirectoryAndRegistration();
                TryPatchControlHover();
                TryPatchHeldItemControlTarget();
                TryPatchHammerNail();
                TryPatchNativeHooks();
                TryPatchHouseTriggers();
                EnsureTemplates();
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

        private void PatchDirectoryAndRegistration()
        {
            var start = AccessTools.DeclaredMethod(typeof(PrefabsDirectory), "Start");
            var register = AccessTools.DeclaredMethod(typeof(SaveablePrefab), "RegisterToSave");
            if (start == null || register == null)
                throw new MissingMethodException("Native item registration lifecycle changed");
            var directoryPatch = AccessTools.Method(typeof(RadioWorldService), nameof(DirectoryPrefix));
            var prefix = AccessTools.Method(typeof(RadioWorldService), nameof(RegisterPrefix));
            var postfix = AccessTools.Method(typeof(RadioWorldService), nameof(RegisterPostfix));
            var finalizer = AccessTools.Method(typeof(RadioWorldService), nameof(RegisterFinalizer));
            harmony.Patch(start, prefix: new HarmonyMethod(directoryPatch) { priority = Priority.First });
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(start, directoryPatch));
            harmony.Patch(register, prefix: new HarmonyMethod(prefix), postfix: new HarmonyMethod(postfix),
                finalizer: new HarmonyMethod(finalizer));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(register, prefix));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(register, postfix));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(register, finalizer));
        }

        private static void DirectoryPrefix(PrefabsDirectory __instance) => Guard(() => active.EnsureTemplates(__instance));

        private static bool RegisterPrefix(SaveablePrefab __instance, ref bool __state)
        {
            __state = false;
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

        private static Exception RegisterFinalizer(SaveablePrefab __instance, bool __state, Exception __exception)
        {
            if (__exception != null && __state && active != null && !active.disposed)
            {
                try { __instance.Unregister(); } catch { }
                active.store.Remove(__instance.instanceId);
                active.arbiter.Remove(__instance.instanceId);
                __instance.instanceId = 0;
            }
            return __exception;
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
                var lateUpdate = typeof(GoPointer).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                var pointedAt = typeof(GoPointer).GetField("pointedAtButton", BindingFlags.Instance | BindingFlags.NonPublic);
                var lookDistance = typeof(GoPointer).GetField("currentLookDistance", BindingFlags.Instance | BindingFlags.NonPublic);
                if (lateUpdate == null || lateUpdate.ReturnType != typeof(void) ||
                    pointedAt?.FieldType != typeof(GoPointerButton) || lookDistance?.FieldType != typeof(float))
                    throw new MissingMethodException("Native pointer target layout changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(HeldItemControlClearPrefix));
                harmony.Patch(lateUpdate, prefix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(lateUpdate, patch));
            }
            catch (Exception error) { warn("Radio held-item pointer cleanup unavailable. " + error.Message); }

            try
            {
                var raycast = typeof(GoPointer).GetMethod("DoRaycast", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                var pointedAt = typeof(GoPointer).GetField("pointedAtButton", BindingFlags.Instance | BindingFlags.NonPublic);
                var lookDistance = typeof(GoPointer).GetField("currentLookDistance", BindingFlags.Instance | BindingFlags.NonPublic);
                var hit = typeof(GoPointer).GetField("hit", BindingFlags.Instance | BindingFlags.NonPublic);
                if (raycast == null || raycast.ReturnType != typeof(void) ||
                    pointedAt?.FieldType != typeof(GoPointerButton) || lookDistance?.FieldType != typeof(float) ||
                    hit?.FieldType != typeof(RaycastHit))
                    throw new MissingMethodException("Native pointer raycast layout changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(ControlRaycastPostfix));
                var orderedPatch = new HarmonyMethod(patch) { after = new[] { "com.dizzy.sailwind.fixes" } };
                harmony.Patch(raycast, postfix: orderedPatch);
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(raycast, patch));
            }
            catch (Exception error) { warn("Radio pointer target correction unavailable. " + error.Message); }
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
            EnsureTemplates();
            if (manager != SaveLoadManager.instance)
            {
                items.Clear();
                pendingRestores.Clear();
                reportedAmbiguousIds.Clear();
                nextRestoreRetry = 0f;
                manager = SaveLoadManager.instance;
                loading = false;
                missingRadioData = false;
                store.Load(null);
                arbiter.Reset(Array.Empty<RadioRecord>());
                // A new-game scene can retain static modData from the prior session. Native LoadGame sets
                // manager itself in our prefix, so a completed native load never passes through this branch.
                if (manager && manager.loaded)
                    ReadNativeData();
                else
                    GameState.modData?.Remove(RadioSaveStore.Key);
            }
            if (pendingRestores.Count > 0 && !GameState.loadingBoatLocalItems && Time.unscaledTime >= nextRestoreRetry)
            {
                nextRestoreRetry = Time.unscaledTime + .5f;
                foreach (var deferred in new List<SaveablePrefab>(pendingRestores))
                {
                    if (deferred) Restore(deferred);
                    else pendingRestores.Remove(deferred);
                }
            }
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!items[i])
                    items.RemoveAt(i);
                else
                    items[i].Tick();
            }
        }

        private static bool TryGetDonor(out GameObject donor, PrefabsDirectory directory = null)
        {
            donor = null;
            if (!directory) directory = PrefabsDirectory.instance;
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

        internal static string TemplateName(int kind) => "Sailwind Radio item " + RadioSaveStore.ItemIndex(kind);
        internal static bool IsTemplateInstanceName(string name, int kind) =>
            name == TemplateName(kind) || name == TemplateName(kind) + "(Clone)";

        private bool TryGetTemplate(int kind, out GameObject template)
        {
            template = null;
            if (kind < 0 || kind > 3 || !EnsureTemplates()) return false;
            template = templates[kind];
            return template;
        }

        private bool EnsureTemplates(PrefabsDirectory directory = null)
        {
            if (!directory) directory = PrefabsDirectory.instance;
            if (disposed || !directory || directory.directory == null) return false;
            if (templateDirectory == directory && templates[0] && directory.directory.Length > RadioSaveStore.ItemIndex(3))
            {
                bool intact = true;
                for (int kind = 0; kind < 4; kind++)
                    intact &= directory.directory[RadioSaveStore.ItemIndex(kind)] == templates[kind] &&
                        directory.shipItems != null && directory.shipItems.Length > RadioSaveStore.ItemIndex(kind) &&
                        directory.shipItems[RadioSaveStore.ItemIndex(kind)] == templates[kind].GetComponent<ShipItem>();
                if (intact) return true;
                warn("Radio item directory entries were replaced after registration");
                return false;
            }
            if (!RadioSaveStore.CanClaimItemSlots(index =>
                (directory.directory.Length > index && directory.directory[index]) ||
                (directory.shipItems != null && directory.shipItems.Length > index && directory.shipItems[index]), out int collision))
            {
                warn("Radio item ID " + collision + " is occupied. Radio templates were not registered");
                return false;
            }
            var created = new GameObject[4];
            try
            {
                bool assetBacked = TryLoadTemplateBundle();
                GameObject donor = null;
                if (!assetBacked && !TryGetDonor(out donor, directory)) return false;
                for (int kind = 0; kind < 4; kind++)
                {
                    var template = assetBacked ? templateBundle.LoadAsset<GameObject>("assets/radioitems/radioitem" +
                        RadioSaveStore.ItemIndex(kind) + ".prefab") :
                        UnityEngine.Object.Instantiate(donor, new Vector3(0f, -100000f, 0f), Quaternion.identity);
                    if (!template || (assetBacked && template.scene.IsValid()))
                        throw new InvalidDataException("Radio item asset is missing or scene-backed: " + kind);
                    created[kind] = template;
                    template.name = TemplateName(kind);
                    var item = template.GetComponent<ShipItem>() ?? template.AddComponent<ShipItem>();
                    var prefab = template.GetComponent<SaveablePrefab>() ?? template.AddComponent<SaveablePrefab>();
                    prefab.prefabIndex = RadioSaveStore.ItemIndex(kind);
                    prefab.instanceId = 0;
                    prefab.currentCrateId = 0;
                    prefab.SetParentObject(-1);
                    item.sold = false;
                    RadioItemController.ConfigureItem(item, kind);
                    item.holdDistance = 1.15f;
                    item.furniturePlaceHeight = .15f;
                    item.inventoryScale = 1f;
                    item.floaterHeight = 1.6f;
                    (template.GetComponent<RadioTemplateMarker>() ?? template.AddComponent<RadioTemplateMarker>()).Configure(kind);
                    if (!assetBacked)
                    {
                        template.SetActive(true);
                        // The fallback scene clone has already run ShipItem.Awake. Strip its
                        // generated physics and LOD so subsequent clones build exactly one set.
                        foreach (var lod in template.GetComponents<LODGroup>()) UnityEngine.Object.DestroyImmediate(lod);
                        if (item.itemRigidbodyC) UnityEngine.Object.DestroyImmediate(item.itemRigidbodyC.gameObject);
                        item.itemRigidbodyC = null;
                        AccessTools.Field(typeof(ShipItem), "itemRigidbody")?.SetValue(item, null);
                        item.enabled = false;
                        prefab.enabled = false;
                    }
                }
                int length = RadioSaveStore.ItemIndex(3) + 1;
                var newDirectory = new GameObject[Math.Max(directory.directory.Length, length)];
                Array.Copy(directory.directory, newDirectory, directory.directory.Length);
                var newShipItems = new ShipItem[Math.Max(directory.shipItems?.Length ?? 0, length)];
                if (directory.shipItems != null) Array.Copy(directory.shipItems, newShipItems, directory.shipItems.Length);
                for (int kind = 0; kind < 4; kind++)
                {
                    int index = RadioSaveStore.ItemIndex(kind);
                    newDirectory[index] = created[kind];
                    newShipItems[index] = created[kind].GetComponent<ShipItem>();
                    templates[kind] = created[kind];
                }
                directory.directory = newDirectory;
                directory.shipItems = newShipItems;
                templateDirectory = directory;
                return true;
            }
            catch
            {
                foreach (var template in created) if (template && template.scene.IsValid()) UnityEngine.Object.Destroy(template);
                throw;
            }
        }

        private bool TryLoadTemplateBundle()
        {
            if (templateBundle) return true;
            if (bundleAttempted) return false;
            bundleAttempted = true;
            using (var source = typeof(RadioWorldService).Assembly.GetManifestResourceStream("SailwindRadio.Physical.radio-items.assets"))
            {
                if (source == null)
                {
                    warn("Radio item assets are unavailable; scene templates remain available for direct item spawning");
                    return false;
                }
                using (var bytes = new MemoryStream())
                {
                    source.CopyTo(bytes);
                    templateBundle = AssetBundle.LoadFromMemory(bytes.ToArray());
                }
            }
            if (!templateBundle) throw new InvalidDataException("Radio item AssetBundle could not be loaded");
            return true;
        }

        internal static void AttachTemplateClone(RadioTemplateMarker marker)
        {
            if (active == null || active.disposed)
                return;
            active.AttachFresh(marker);
        }

        private void AttachFresh(RadioTemplateMarker marker)
        {
            if (!marker || marker.GetComponent<RadioItemController>()) return;
            int kind = marker.Kind;
            var item = marker.GetComponent<ShipItem>();
            var prefab = marker.GetComponent<SaveablePrefab>();
            if (!item || !prefab || prefab.prefabIndex != RadioSaveStore.ItemIndex(kind)) return;
            item.enabled = true;
            prefab.enabled = true;
            var controller = marker.gameObject.AddComponent<RadioItemController>();
            controller.Initialize(this, new RadioState { Kind = kind, Volume = kind == 0 ? .5f : .75f, TrackPath = "" });
        }

        private bool PrepareRegistration(SaveablePrefab prefab, out bool created)
        {
            created = false;
            if (!prefab || loadingPrefabs.Contains(prefab) || !RadioSaveStore.IsItemIndex(prefab.prefabIndex)) return true;
            var marker = prefab.GetComponent<RadioTemplateMarker>();
            var controller = prefab.GetComponent<RadioItemController>();
            var item = prefab.GetComponent<ShipItem>();
            if (!marker) return true;
            if (!controller || controller.State == null || !item || !item.sold || marker.Kind != controller.State.Kind ||
                prefab.prefabIndex != RadioSaveStore.ItemIndex(marker.Kind) ||
                !IsTemplateInstanceName(prefab.gameObject.name, marker.Kind)) return false;
            if (items.Contains(controller)) return true;
            if (!store.Writable) return false;
            if (manager != SaveLoadManager.instance) Tick();
            var stock = prefab.GetComponent<RadioShopStock>();
            if (prefab.instanceId > 0 && stock && stock.Reserved && stock.Controller == controller &&
                store.TryGet(prefab.instanceId, prefab.prefabIndex, out _)) return true;
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
                created = true;
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

        private bool IdUsed(int id)
        {
            if (store.TryGetAny(id, out _) || arbiter.TryGet(id, out _) ||
                (SaveablePrefab.existingInstanceIds != null && SaveablePrefab.existingInstanceIds.Contains(id))) return true;
            if (!manager) return false;
            foreach (var live in manager.GetCurrentPrefabs()) if (live && live.instanceId == id) return true;
            foreach (var obj in manager.GetCurrentObjects())
                if (obj && obj.localItems && obj.localItems.HasLocalItems())
                    foreach (var cached in obj.localItems.GetCachedItems()) if (cached.instanceId == id) return true;
            return false;
        }

        private void CompleteRegistration(SaveablePrefab prefab)
        {
            // SaveablePrefab.Load calls RegisterToSave before our Load postfix can bind
            // the controller to its persisted arbiter state.
            if (loading || !prefab || loadingPrefabs.Contains(prefab) || !manager ||
                !manager.GetCurrentPrefabs().Contains(prefab)) return;
            var controller = prefab.GetComponent<RadioItemController>();
            if (!controller || controller.State == null || !RadioSaveStore.IsItemIndex(prefab.prefabIndex) ||
                prefab.prefabIndex != RadioSaveStore.ItemIndex(controller.State.Kind) ||
                !store.TryGet(prefab.instanceId, prefab.prefabIndex, out _)) return;
            controller.RefreshOwnership();
            if (!items.Contains(controller)) items.Add(controller);
        }

        private bool Convert(SaveablePrefab saveable, RadioState state, bool migratedLegacy = false)
        {
            if (!saveable)
                return false;
            var existing = saveable.GetComponent<RadioItemController>();
            if (existing)
            {
                var existingMarker = saveable.GetComponent<RadioTemplateMarker>();
                bool registered = existingMarker && existingMarker.Kind == state.Kind &&
                    saveable.prefabIndex == RadioSaveStore.ItemIndex(state.Kind) &&
                    IsTemplateInstanceName(saveable.gameObject.name, state.Kind);
                // A live donor migrated earlier in this load has no template marker. It
                // is already under our management and retains the donor object name.
                bool migratedDonor = !existingMarker && items.Contains(existing) && existing.State != null &&
                    existing.State.Kind == state.Kind && saveable.gameObject.name == DonorName + "(Clone)" &&
                    (saveable.prefabIndex == RadioSaveStore.DonorIndex ||
                     saveable.prefabIndex == RadioSaveStore.ItemIndex(state.Kind));
                if (!registered && !migratedDonor) return false;
                existing.ApplyRestoredState(this, arbiter.Register(saveable.instanceId, state));
                if (!items.Contains(existing)) items.Add(existing);
                return true;
            }
            var item = saveable.GetComponent<ShipItem>();
            bool nativeTemplate = saveable.GetComponent<RadioTemplateMarker>() is RadioTemplateMarker marker &&
                marker.Kind == state.Kind && IsTemplateInstanceName(saveable.gameObject.name, state.Kind);
            bool nativeLegacy = saveable.prefabIndex == RadioSaveStore.DonorIndex &&
                saveable.gameObject.name == DonorName + "(Clone)" && TryGetDonor(out _);
            bool retaggedLegacy = migratedLegacy && saveable.gameObject.name == DonorName + "(Clone)";
            if (!item || item.GetType() != typeof(ShipItem) || !item.sold || saveable.GetType() != typeof(SaveablePrefab) ||
                !(nativeLegacy || (saveable.prefabIndex == RadioSaveStore.ItemIndex(state.Kind) && (nativeTemplate || retaggedLegacy))) ||
                !saveable.GetComponent<BoxCollider>())
            {
                warn("A saved radio did not match its expected native item and was left unchanged");
                return false;
            }
            item.enabled = true;
            saveable.enabled = true;
            var controller = saveable.gameObject.AddComponent<RadioItemController>();
            try
            {
                controller.Initialize(this, arbiter.Register(saveable.instanceId, state));
                items.Add(controller);
                return true;
            }
            catch
            {
                UnityEngine.Object.Destroy(controller);
                throw;
            }
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
            EnsureTemplates();
            pendingRegistrations.Clear();
            pendingRestores.Clear();
            reportedAmbiguousIds.Clear();
            nextRestoreRetry = 0f;
            manager = source;
            loading = true;
            missingRadioData = false;
            items.Clear();
            store.Load(null);
            arbiter.Reset(Array.Empty<RadioRecord>());
            // Native LoadGame does not clear a static dictionary when the old save has null modData.
            GameState.modData?.Remove(RadioSaveStore.Key);
        }

        private void ReadNativeData()
        {
            string json = null;
            missingRadioData = GameState.modData == null || !GameState.modData.TryGetValue(RadioSaveStore.Key, out json);
            loading = false;
            if (!store.Load(json))
            {
                pendingRegistrations.Clear();
                arbiter.Reset(Array.Empty<RadioRecord>());
                warn(PersistenceStatus);
                return;
            }
            arbiter.Reset(store.Records);
            if (!manager)
                return;
            MigrateLegacyItems();
            foreach (var prefab in manager.GetCurrentPrefabs()) Restore(prefab);
            foreach (var prefab in pendingRegistrations)
                if (prefab && prefab.GetComponent<ShipItem>().sold)
                    try { prefab.RegisterToSave(); }
                    catch (Exception error) { warn("Radio startup item registration failed: " + error.Message); }
            pendingRegistrations.Clear();
        }

        private void MigrateLegacyItems()
        {
            if (!TryGetDonor(out _)) return;
            var owners = new Dictionary<int, int>();
            foreach (var prefab in manager.GetCurrentPrefabs())
                if (prefab && store.TryGet(prefab.instanceId, RadioSaveStore.DonorIndex, out _))
                    owners[prefab.instanceId] = owners.TryGetValue(prefab.instanceId, out int count) ? count + 1 : 1;
            foreach (var obj in manager.GetCurrentObjects())
                if (obj && obj.localItems && obj.localItems.HasLocalItems())
                    foreach (var cached in obj.localItems.GetCachedItems())
                        if (cached != null && store.TryGet(cached.instanceId, RadioSaveStore.DonorIndex, out _))
                            owners[cached.instanceId] = owners.TryGetValue(cached.instanceId, out int count) ? count + 1 : 1;
            foreach (var prefab in manager.GetCurrentPrefabs())
            {
                if (!prefab || prefab.prefabIndex != RadioSaveStore.DonorIndex || !owners.TryGetValue(prefab.instanceId, out int count) || count != 1 ||
                    !store.TryGet(prefab.instanceId, RadioSaveStore.DonorIndex, out var record)) continue;
                var item = prefab.GetComponent<ShipItem>();
                if (!item || !item.sold || prefab.GetType() != typeof(SaveablePrefab) || item.GetType() != typeof(ShipItem) ||
                    prefab.gameObject.name != DonorName + "(Clone)" || !prefab.GetComponent<BoxCollider>()) continue;
                if (!TryGetTemplate(record.Kind, out _) || !store.MigrateLegacy(prefab.instanceId, record.Kind)) continue;
                try
                {
                    prefab.prefabIndex = RadioSaveStore.ItemIndex(record.Kind);
                    if (!Convert(prefab, record.ToState(), true)) throw new InvalidOperationException("legacy device conversion failed");
                }
                catch (Exception error)
                {
                    prefab.prefabIndex = RadioSaveStore.DonorIndex;
                    store.RevertLegacyMigration(prefab.instanceId, record.Kind);
                    warn("Radio legacy item migration deferred: " + error.Message);
                }
            }
            foreach (var obj in manager.GetCurrentObjects())
                if (obj && obj.localItems && obj.localItems.HasLocalItems())
                    foreach (var cached in obj.localItems.GetCachedItems())
                    {
                        if (cached == null || !cached.isSold || cached.prefabIndex != RadioSaveStore.DonorIndex ||
                            !owners.TryGetValue(cached.instanceId, out int count) || count != 1 ||
                            !store.TryGet(cached.instanceId, RadioSaveStore.DonorIndex, out var record) ||
                            !TryGetTemplate(record.Kind, out _) || !store.MigrateLegacy(cached.instanceId, record.Kind)) continue;
                        cached.prefabIndex = RadioSaveStore.ItemIndex(record.Kind);
                    }
        }

        private void Restore(SaveablePrefab prefab)
        {
            if (loading || !prefab) return;
            if (store.TryGet(prefab.instanceId, prefab.prefabIndex, out var record))
            {
                if (!UniqueNativeOwner(prefab)) return;
                pendingRestores.Remove(prefab);
                Convert(prefab, record.ToState());
                return;
            }
            var nativeItem = prefab.GetComponent<ShipItem>();
            if (!missingRadioData || !store.Writable || prefab.instanceId <= 0 || !nativeItem || !nativeItem.sold)
                return;
            var marker = prefab.GetComponent<RadioTemplateMarker>();
            if (!marker || marker.Kind < 0 || marker.Kind > 3 || prefab.prefabIndex != RadioSaveStore.ItemIndex(marker.Kind) ||
                !IsTemplateInstanceName(prefab.gameObject.name, marker.Kind) ||
                prefab.GetType() != typeof(SaveablePrefab) ||
                !manager.GetCurrentPrefabs().Contains(prefab)) return;
            if (!UniqueNativeOwner(prefab)) return;
            pendingRestores.Remove(prefab);
            var state = new RadioState { Kind = marker.Kind, Volume = marker.Kind == 0 ? .5f : .75f, TrackPath = "" };
            if (store.TryAdoptMissing(prefab.instanceId, prefab.prefabIndex, state, missingRadioData))
                Convert(prefab, state);
        }

        private bool UniqueNativeOwner(SaveablePrefab prefab)
        {
            int id = prefab.instanceId;
            if (id <= 0 || !manager) return false;
            if (RadioSaveStore.HasUniqueNativeOwner(id, NativeOwnerIds(), out int count)) return true;
            pendingRestores.Add(prefab);
            if (reportedAmbiguousIds.Add(id))
                warn("Radio restoration deferred because native item ID " + id + " has " + count + " owners");
            return false;
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

        // Exceptions must never interrupt the game's save/load call or suppress its original body.
        private static void SavePrefix() => Guard(() => active.Save());
        private static void LoadPrefix(SaveLoadManager __instance) => Guard(() => active.BeginLoad(__instance));
        private static void LoadPostfix() => Guard(() => active.ReadNativeData());
        private static void PrefabLoadPrefix(SaveablePrefab __instance) => Guard(() => active.loadingPrefabs.Add(__instance));
        private static void PrefabPostfix(SaveablePrefab __instance) => Guard(() => { active.loadingPrefabs.Remove(__instance); active.Restore(__instance); });
        private static void CapturePrefix(SaveablePrefab __instance) => Guard(() => active.Capture(__instance));
        private static void ControlHoverPostfix(LookUI __instance, GoPointerButton button) => Guard(() => RadioControlHover.ClearPickupHint(__instance, button));
        private static void HeldItemControlClearPrefix(GoPointer __instance,
            ref GoPointerButton ___pointedAtButton, ref float ___currentLookDistance)
        {
            if (active == null || active.disposed) return;
            try { RadioHeldItemTarget.Clear(__instance, ref ___pointedAtButton, ref ___currentLookDistance); }
            catch (Exception ex) { active.warn("Radio held-item pointer cleanup failed: " + ex.Message); }
        }
        private static void ControlRaycastPostfix(GoPointer __instance, RaycastHit ___hit,
            ref GoPointerButton ___pointedAtButton, ref float ___currentLookDistance)
        {
            if (active == null || active.disposed) return;
            try { RadioHeldItemTarget.Restore(__instance, ___hit, ref ___pointedAtButton, ref ___currentLookDistance); }
            catch (Exception ex) { active.warn("Radio pointer target correction failed: " + ex.Message); }
        }
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
            if (arbiter.TryGet(id, out var state) && store.TryGetAny(id, out var record))
                store.Put(RadioRecord.Capture(id, record.PrefabIndex, state));
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
            if (templateDirectory)
                for (int kind = 0; kind < 4; kind++)
                {
                    int index = RadioSaveStore.ItemIndex(kind);
                    if (templates[kind] && templateDirectory.directory != null && templateDirectory.directory.Length > index &&
                        templateDirectory.directory[index] == templates[kind]) templateDirectory.directory[index] = null;
                    if (templates[kind] && templateDirectory.shipItems != null && templateDirectory.shipItems.Length > index &&
                        templateDirectory.shipItems[index] == templates[kind].GetComponent<ShipItem>()) templateDirectory.shipItems[index] = null;
                    if (templates[kind] && templates[kind].scene.IsValid()) UnityEngine.Object.Destroy(templates[kind]);
                }
            if (templateBundle) templateBundle.Unload(false);
            if (active == this)
            {
                HookCompatible = false;
                active = null;
            }
        }
    }
}
