using System;
using System.Collections.Generic;
using System.IO;
using System.Reflection;
using HarmonyLib;
using SailwindRadio.Models;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;
using SailwindRadio.Shops;
using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed partial class RadioWorldService : IDisposable
    {
        public const string DonorName = "138 model ship junk (small)";
        private static RadioWorldService active;
        private readonly Harmony harmony;
        private readonly Action<string> warn;
        internal DisplaySoundBank DisplaySounds { get; }
        private readonly RadioSaveStore store = new RadioSaveStore();
        private readonly GlobalRadioArbiter arbiter = new GlobalRadioArbiter();
        private readonly List<RadioItemController> items = new List<RadioItemController>();
        private readonly GameObject[] templates = new GameObject[DeviceKind.Count];
        // Kinds registered in the current directory. Integrity, length, lookup and cleanup all follow it.
        private int[] registeredKinds = Array.Empty<int>();
        private AssetBundle templateBundle;
        private Shader worldText;
        private bool worldTextAttempted;
        private bool bundleAttempted;
        private bool buildingTemplates;
        private readonly HashSet<SaveablePrefab> loadingPrefabs = new HashSet<SaveablePrefab>();
        private readonly HashSet<SaveablePrefab> pendingRestores = new HashSet<SaveablePrefab>();
        private readonly RestoreDeferrals deferrals = new RestoreDeferrals();
        private PrefabsDirectory templateDirectory;
        private static readonly FieldInfo BusyField = AccessTools.Field(typeof(SaveLoadManager), "busy");
        private SaveLoadManager manager;
        private bool loading;
        private bool missingRadioData;
        private bool disposed;
        private bool malformedHouseReported;
        private string reportedTemplateProblem;
        private float nextRestoreRetry;
        internal static bool HookCompatible { get; private set; }
        internal static bool CargoCompatible { get; private set; }
        public IReadOnlyList<RadioItemController> Items => items;
        public int ActiveRadioId => arbiter.ActiveId;
        public event Action BeforeSave;
        public event Action<RadioItemController, RadioAction> ActionRequested;
        // A Radio's controller replaced by stacking or unstacking, whose player should carry on with the new one.
        public event Action<RadioItemController, RadioItemController> RadioReplaced;
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

        internal RadioWorldService(Harmony harmony, Action<string> warn, DisplaySoundBank displaySounds)
        {
            if (active != null)
                throw new InvalidOperationException("Radio world service already exists");
            this.harmony = harmony;
            this.warn = warn ?? (_ => { });
            DisplaySounds = displaySounds;
            active = this;
            try
            {
                InstallPatches();
                EnsureTemplates();
            }
            catch { Dispose(); throw; }
        }

        public void Tick()
        {
            if (disposed)
                return;
            EnsureTemplates();
            if (manager != SaveLoadManager.instance)
            {
                items.Clear();
                pendingBindings.Clear();
                pendingRestores.Clear();
                deferrals.Clear();
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
            BindPending();
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!items[i])
                    items.RemoveAt(i);
                else
                {
                    ReconcileShopOwnership(items[i]);
                    items[i].Tick();
                }
            }
        }

        // The native item that walnut devices from before Radio 1.2.0 were saved as, needed to migrate them.
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

        internal static string TemplateName(int kind) => RadioDevice.TemplateName(kind, RadioSaveStore.ItemIndex(kind));
        internal static bool IsTemplateInstanceName(string name, int kind) =>
            name == TemplateName(kind) || name == TemplateName(kind) + "(Clone)";

        private bool TryGetTemplate(int kind, out GameObject template)
        {
            template = null;
            if (!EnsureTemplates() || Array.IndexOf(registeredKinds, kind) < 0) return false;
            template = templates[kind];
            return template;
        }

        private bool EnsureTemplates(PrefabsDirectory directory = null)
        {
            if (!directory) directory = PrefabsDirectory.instance;
            if (disposed || !directory || directory.directory == null) return false;
            if (templateDirectory == directory && templates[0] && directory.directory.Length >= RadioSaveStore.DirectoryLength(registeredKinds))
            {
                bool intact = true;
                for (int i = 0; i < registeredKinds.Length; i++)
                {
                    int kind = registeredKinds[i];
                    intact &= directory.directory[RadioSaveStore.ItemIndex(kind)] == templates[kind] &&
                        directory.shipItems != null && directory.shipItems.Length > RadioSaveStore.ItemIndex(kind) &&
                        directory.shipItems[RadioSaveStore.ItemIndex(kind)] == templates[kind].GetComponent<ShipItem>();
                }
                if (intact) return true;
                ReportTemplateProblem("Radio item directory entries were replaced after registration");
                return false;
            }
            var skipped = new List<string>();
            var kinds = RadioSaveStore.ClaimItemKinds(DeviceModel.Kinds, index =>
                (directory.directory.Length > index && directory.directory[index]) ||
                (directory.shipItems != null && directory.shipItems.Length > index && directory.shipItems[index]),
                (family, slot) => skipped.Add("Radio item ID " + slot + " is occupied. " + RadioDevice.Region(family) + " devices were not registered"),
                out int collision);
            if (kinds == null)
            {
                ReportTemplateProblem("Radio item ID " + collision + " is occupied. Radio templates were not registered");
                return false;
            }
            var created = new GameObject[DeviceKind.Count];
            buildingTemplates = true;
            try
            {
                if (!TryLoadTemplateBundle()) return false;
                // The bundle may hold prefabs for kinds without a model or skipped kinds. Those stay unregistered.
                foreach (int kind in kinds)
                {
                    var template = templateBundle.LoadAsset<GameObject>("assets/radioitems/radioitem" + RadioSaveStore.ItemIndex(kind) + ".prefab");
                    if (!template || template.scene.IsValid())
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
                }
                int length = RadioSaveStore.DirectoryLength(kinds);
                var newDirectory = new GameObject[Math.Max(directory.directory.Length, length)];
                Array.Copy(directory.directory, newDirectory, directory.directory.Length);
                var newShipItems = new ShipItem[Math.Max(directory.shipItems?.Length ?? 0, length)];
                if (directory.shipItems != null) Array.Copy(directory.shipItems, newShipItems, directory.shipItems.Length);
                foreach (int kind in kinds)
                {
                    int index = RadioSaveStore.ItemIndex(kind);
                    newDirectory[index] = created[kind];
                    newShipItems[index] = created[kind].GetComponent<ShipItem>();
                }
                Array.Copy(created, templates, templates.Length);
                registeredKinds = kinds;
                directory.directory = newDirectory;
                directory.shipItems = newShipItems;
                templateDirectory = directory;
                foreach (string message in skipped) warn(message);
                return true;
            }
            finally { buildingTemplates = false; }
        }

        // Templates are checked every frame, so a lasting problem is logged once.
        private void ReportTemplateProblem(string message)
        {
            if (message == reportedTemplateProblem) return;
            reportedTemplateProblem = message;
            warn(message);
        }

        private bool TryLoadTemplateBundle()
        {
            if (templateBundle) return true;
            if (bundleAttempted) return false;
            bundleAttempted = true;
            // The bundle is embedded at build time, so a missing resource means a broken build.
            using (var source = typeof(RadioWorldService).Assembly.GetManifestResourceStream("SailwindRadio.Physical.radio-items.assets"))
            using (var bytes = new MemoryStream())
            {
                if (source == null) throw new InvalidDataException("Radio item assets are missing from the plugin");
                source.CopyTo(bytes);
                templateBundle = AssetBundle.LoadFromMemory(bytes.ToArray());
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
            if (buildingTemplates || !marker || marker.GetComponent<RadioItemController>()) return;
            if (manager != SaveLoadManager.instance) Tick();
            if (!marker || marker.GetComponent<RadioItemController>()) return;
            int kind = marker.Kind;
            var item = marker.GetComponent<ShipItem>();
            var prefab = marker.GetComponent<SaveablePrefab>();
            if (!item || !prefab || !IsRegisteredClone(prefab, kind)) return;
            item.enabled = true;
            prefab.enabled = true;
            var controller = marker.gameObject.AddComponent<RadioItemController>();
            controller.Initialize(this, RadioState.NewDevice(kind));
            pendingBindings.Add(controller);
        }

        private bool Convert(SaveablePrefab saveable, RadioState state, bool migratedLegacy = false)
        {
            if (!saveable)
                return false;
            var existing = saveable.GetComponent<RadioItemController>();
            if (existing)
            {
                var existingMarker = saveable.GetComponent<RadioTemplateMarker>();
                bool registered = IsRegisteredClone(saveable, state.Kind);
                // A live donor migrated earlier in this load has no template marker. It
                // is already under our management and retains the donor object name.
                bool migratedDonor = !existingMarker && items.Contains(existing) && existing.State != null &&
                    existing.State.Kind == state.Kind && IsDonorClone(saveable) &&
                    (saveable.prefabIndex == RadioSaveStore.DonorIndex ||
                     saveable.prefabIndex == RadioSaveStore.ItemIndex(state.Kind));
                if (!registered && !migratedDonor) return false;
                existing.ApplyRestoredState(this, arbiter.Register(saveable.instanceId, state));
                if (!items.Contains(existing)) items.Add(existing);
                pendingBindings.Remove(existing);
                return true;
            }
            var item = saveable.GetComponent<ShipItem>();
            bool nativeTemplate = IsRegisteredClone(saveable, state.Kind);
            bool nativeLegacy = saveable.prefabIndex == RadioSaveStore.DonorIndex && IsDonorClone(saveable) && TryGetDonor(out _);
            bool retaggedLegacy = migratedLegacy && IsDonorClone(saveable) && saveable.prefabIndex == RadioSaveStore.ItemIndex(state.Kind);
            if (!IsPlainNativeItem(saveable, item) || !item.sold || !(nativeLegacy || nativeTemplate || retaggedLegacy))
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
        // Depth-tested display text that colours Alpha8 font atlases. It ships in the item bundle,
        // so it is available once templates are built. Null after a reported failure.
        internal Shader WorldTextShader
        {
            get
            {
                if (!worldTextAttempted && templateBundle)
                {
                    worldTextAttempted = true;
                    worldText = templateBundle.LoadAsset<Shader>("assets/radioitems/worldtext.shader");
                    if (!worldText || !worldText.isSupported)
                    {
                        worldText = null;
                        warn("Radio world text shader unavailable. Display text uses Sprites/Default instead");
                    }
                }
                return worldText;
            }
        }

        // The one ownership and readiness guard for every device action.
        private bool CanAct(RadioItemController item) => EnsureOwned(item) && !disposed && !loading && store.Writable &&
            items.Contains(item) && manager == SaveLoadManager.instance;

        internal void TogglePower(RadioItemController item)
        {
            if (CanAct(item))
                SwitchPower(item);
        }

        // Returns whether a physical control's action was accepted.
        internal bool RequestAction(RadioItemController item, RadioAction action)
        {
            if (!CanAct(item) || !item.IsPlacedForControls)
                return false;
            if (action == RadioAction.Power)
                SwitchPower(item);
            else if (item.State.Role == DeviceRole.Radio)
                ActionRequested?.Invoke(item, action);
            return true;
        }

        private void SwitchPower(RadioItemController item)
        {
            try
            {
                if (item.State.Role != DeviceRole.Radio)
                {
                    item.State.SpeakerEnabled = !item.State.SpeakerEnabled;
                    WriteCanonical(item.RecordOwner.InstanceId);
                    return;
                }
                arbiter.Toggle(item.InstanceId, CaptureAndStop);
                arbiter.WriteTo(store);
                ActionRequested?.Invoke(item, RadioAction.Power);
            }
            catch (Exception ex) { warn("Could not switch the active radio: " + ex.Message); }
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
            pendingBindings.Remove(item);
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
            pendingBindings.Clear();
            pendingRestores.Clear();
            deferrals.Clear();
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
                pendingBindings.Clear();
                arbiter.Reset(Array.Empty<RadioRecord>());
                warn(PersistenceStatus);
                return;
            }
            arbiter.Reset(store.Records);
            if (!manager)
                return;
            MigrateLegacyItems();
            foreach (var prefab in manager.GetCurrentPrefabs())
            {
                var controller = prefab ? prefab.GetComponent<RadioItemController>() : null;
                if (!controller || !pendingBindings.Contains(controller)) Restore(prefab);
            }
            BindPending();
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
                if (!IsPlainNativeItem(prefab, item) || !item.sold || !IsDonorClone(prefab)) continue;
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
            // Only the exact SaveablePrefab is required before adoption. Convert then applies the full native item rule.
            if (!marker || !IsRegisteredClone(prefab, marker.Kind) || prefab.GetType() != typeof(SaveablePrefab) ||
                !manager.GetCurrentPrefabs().Contains(prefab)) return;
            if (!UniqueNativeOwner(prefab)) return;
            pendingRestores.Remove(prefab);
            var state = RadioState.NewDevice(marker.Kind);
            if (store.TryAdoptMissing(prefab.instanceId, prefab.prefabIndex, state, missingRadioData))
                Convert(prefab, state);
        }

        private bool UniqueNativeOwner(SaveablePrefab prefab)
        {
            int id = prefab.instanceId;
            if (id <= 0 || !manager) return false;
            if (RadioSaveStore.HasUniqueNativeOwner(id, NativeOwnerIds(), out _))
            {
                deferrals.Restored(id);
                return true;
            }
            pendingRestores.Add(prefab);
            if (deferrals.Defer(id, Time.unscaledTime, GameState.loadingBoatLocalItems || GameState.currentlyLoading))
                warn("A saved radio device was not restored because another item uses its item ID " + id);
            return false;
        }

        private void Capture(SaveablePrefab prefab)
        {
            var item = prefab.GetComponent<RadioItemController>();
            if (!EnsureOwned(item) || !items.Contains(item) || !store.Writable || loading)
                return;
            item.CapturePosition?.Invoke();
            WriteCanonical(item.InstanceId);
        }

        private void WriteCanonical(int id)
        {
            if (arbiter.TryGet(id, out var state) && store.TryGetAny(id, out var record))
                store.Put(RadioRecord.Capture(id, record.PrefabIndex, state));
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
            pendingBindings.Clear();
            foreach (var pair in patches)
                harmony.Unpatch(pair.Key, pair.Value);
            patches.Clear();
            if (templateDirectory)
                foreach (int kind in registeredKinds)
                {
                    int index = RadioSaveStore.ItemIndex(kind);
                    if (templates[kind] && templateDirectory.directory != null && templateDirectory.directory.Length > index &&
                        templateDirectory.directory[index] == templates[kind]) templateDirectory.directory[index] = null;
                    if (templates[kind] && templateDirectory.shipItems != null && templateDirectory.shipItems.Length > index &&
                        templateDirectory.shipItems[index] == templates[kind].GetComponent<ShipItem>()) templateDirectory.shipItems[index] = null;
                }
            if (templateBundle) templateBundle.Unload(false);
            if (active == this)
            {
                HookCompatible = CargoCompatible = StacksCompatible = false;
                active = null;
            }
        }
    }
}
