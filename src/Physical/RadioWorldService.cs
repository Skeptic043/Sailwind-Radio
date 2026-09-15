using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using SailwindRadio.Persistence;
using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed class RadioWorldService : IDisposable
    {
        public const string DonorName = "138 model ship junk (small)";
        private static RadioWorldService active;
        private readonly Harmony harmony;
        private readonly Func<string> configuredTrack;
        private readonly Action<string> warn;
        private readonly RadioSaveStore store = new RadioSaveStore();
        private readonly List<RadioItemController> items = new List<RadioItemController>();
        private readonly List<KeyValuePair<MethodInfo, MethodInfo>> patches = new List<KeyValuePair<MethodInfo, MethodInfo>>();
        private static readonly FieldInfo BusyField = AccessTools.Field(typeof(SaveLoadManager), "busy");
        private SaveLoadManager manager;
        private bool loading;
        private bool disposed;
        public IReadOnlyList<RadioItemController> Items => items;
        public event Action BeforeSave;
        public string PersistenceStatus => store.Writable ? "Native save ready" : "Radio data preserved: " + store.Problem;

        public RadioWorldService(Harmony harmony, Func<string> configuredTrack, Action<string> warn)
        {
            if (active != null) throw new InvalidOperationException("Radio world service already exists");
            this.harmony = harmony;
            this.configuredTrack = configuredTrack;
            this.warn = warn ?? (_ => { });
            active = this;
            try
            {
                Patch(typeof(SaveLoadManager), "SaveModData", nameof(SavePrefix), true);
                Patch(typeof(SaveLoadManager), "LoadGame", nameof(LoadPrefix), true);
                Patch(typeof(SaveLoadManager), "LoadModData", nameof(LoadPostfix), false);
                Patch(typeof(SaveablePrefab), "Load", nameof(PrefabPostfix), false);
                Patch(typeof(SaveablePrefab), "PrepareSaveData", nameof(CapturePrefix), true);
            }
            catch { Dispose(); throw; }
        }

        private void Patch(Type type, string name, string callback, bool prefix)
        {
            MethodInfo target = AccessTools.Method(type, name);
            MethodInfo patch = AccessTools.Method(typeof(RadioWorldService), callback);
            if (target == null || patch == null || BusyField == null) throw new MissingMethodException("Native radio save compatibility check failed");
            harmony.Patch(target, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
        }

        public void Tick()
        {
            if (disposed) return;
            if (manager != SaveLoadManager.instance)
            {
                items.Clear();
                manager = SaveLoadManager.instance;
                loading = false;
                store.Load(null);
                // A new-game scene can retain static modData from the prior session. Native LoadGame sets
                // manager itself in our prefix, so a completed native load never passes through this branch.
                if (manager && manager.loaded) ReadNativeData();
                else GameState.modData?.Remove(RadioSaveStore.Key);
            }
            for (int i = items.Count - 1; i >= 0; i--)
            {
                if (!items[i]) items.RemoveAt(i);
                else items[i].Tick();
            }
        }

        public bool TrySpawn(Vector3 position, Quaternion rotation, out string message)
        {
            Tick();
            if (disposed || !manager || !FloatingOriginManager.instance || !GameState.playing || GameState.currentlyLoading ||
                GameState.justStarted || GameState.loadingBoatLocalItems || GameState.recovering || loading ||
                !SaveLoadManager.readyToSave || (bool)BusyField.GetValue(manager))
            { message = "Wait until the game finishes loading or saving"; return false; }
            if (!store.Writable) { message = PersistenceStatus; return false; }
            if (!TryGetDonor(out GameObject donor))
            { message = "The installed game's radio item reference did not match"; return false; }
            if (!RadioPlacement.IsClear(position, rotation))
            { message = "Move to a clear spot before spawning a radio"; return false; }
            GameObject instance = null;
            int createdId = 0;
            try
            {
                instance = UnityEngine.Object.Instantiate(donor, position, rotation);
                instance.transform.localScale = Vector3.one;
                var item = instance.GetComponent<ShipItem>();
                var saveable = instance.GetComponent<SaveablePrefab>();
                item.sold = true;
                saveable.instanceId = 0;
                saveable.currentCrateId = 0;
                saveable.SetParentObject(-1);
                // A native save coroutine may already be resuming at this frame's end. Initialize its
                // private component cache before adding this instance to the collection it enumerates.
                saveable.Start();
                saveable.RegisterToSave();
                createdId = saveable.instanceId;
                var state = new RadioState { TrackPath = configuredTrack?.Invoke() ?? "" };
                store.Put(RadioRecord.Capture(saveable.instanceId, saveable.prefabIndex, state));
                if (!Convert(saveable, state)) throw new InvalidOperationException("The native radio could not be initialized");
                message = "Radio spawned. Click its power button to play";
                return true;
            }
            catch (Exception ex)
            {
                if (instance)
                {
                    var controller = instance.GetComponent<RadioItemController>();
                    if (controller) items.Remove(controller);
                    store.Remove(createdId);
                    instance.GetComponent<SaveablePrefab>()?.Unregister();
                    UnityEngine.Object.Destroy(instance);
                }
                warn("Radio spawn failed: " + ex.Message);
                message = "Radio could not be spawned. See the BepInEx log";
                return false;
            }
        }

        private static bool TryGetDonor(out GameObject donor)
        {
            donor = null;
            var directory = PrefabsDirectory.instance;
            if (!directory || directory.directory == null || directory.directory.Length <= RadioSaveStore.DonorIndex) return false;
            donor = directory.directory[RadioSaveStore.DonorIndex];
            if (!donor || donor.name != DonorName || (donor.transform.localScale - Vector3.one).sqrMagnitude > .0001f) return false;
            var item = donor.GetComponent<ShipItem>();
            var save = donor.GetComponent<SaveablePrefab>();
            return item && item.GetType() == typeof(ShipItem) && !item.big && !item.wallAttachment && save &&
                save.GetType() == typeof(SaveablePrefab) && save.prefabIndex == RadioSaveStore.DonorIndex && donor.GetComponent<BoxCollider>() &&
                donor.GetComponent<MeshFilter>() && donor.GetComponent<Renderer>() && !donor.GetComponent<Good>();
        }

        private bool Convert(SaveablePrefab saveable, RadioState state)
        {
            if (!saveable) return false;
            var existing = saveable.GetComponent<RadioItemController>();
            if (existing) return items.Contains(existing);
            var item = saveable.GetComponent<ShipItem>();
            if (!TryGetDonor(out _) || !item || item.GetType() != typeof(ShipItem) ||
                !item.sold || saveable.GetType() != typeof(SaveablePrefab) || saveable.prefabIndex != RadioSaveStore.DonorIndex ||
                saveable.gameObject.name != DonorName + "(Clone)" || !saveable.GetComponent<BoxCollider>())
            { warn("A saved radio did not match its expected native item and was left unchanged"); return false; }
            var controller = saveable.gameObject.AddComponent<RadioItemController>();
            controller.Initialize(this, state);
            items.Add(controller);
            return true;
        }

        internal void Report(string message) => warn(message);

        internal void Forget(RadioItemController item)
        {
            // Streaming also destroys objects. Retain its record so native cached reload can reconstruct it.
            if (items.Contains(item) && !disposed && !loading && manager == SaveLoadManager.instance && store.Writable)
                try { store.Put(RadioRecord.Capture(item.InstanceId, RadioSaveStore.DonorIndex, item.State)); }
                catch (Exception ex) { warn("Could not capture radio state during unload: " + ex.Message); }
            items.Remove(item);
        }

        private void Save()
        {
            Tick();
            if (!store.Writable || loading) return;
            BeforeSave?.Invoke();
            foreach (var item in items)
            {
                if (!item) continue;
                item.CapturePosition?.Invoke();
                store.Put(RadioRecord.Capture(item.InstanceId, RadioSaveStore.DonorIndex, item.State));
            }
            string json = store.Save();
            if (GameState.modData == null) GameState.modData = new Dictionary<string, string>();
            GameState.modData[RadioSaveStore.Key] = json;
        }

        private void BeginLoad(SaveLoadManager source)
        {
            manager = source;
            loading = true;
            items.Clear();
            store.Load(null);
            // Native LoadGame does not clear a static dictionary when the old save has null modData.
            GameState.modData?.Remove(RadioSaveStore.Key);
        }

        private void ReadNativeData()
        {
            string json = null;
            GameState.modData?.TryGetValue(RadioSaveStore.Key, out json);
            loading = false;
            if (!store.Load(json)) { warn(PersistenceStatus); return; }
            if (!manager) return;
            foreach (var prefab in manager.GetCurrentPrefabs()) Restore(prefab);
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
        private void Capture(SaveablePrefab prefab)
        {
            var item = prefab.GetComponent<RadioItemController>();
            if (!item || !items.Contains(item) || !store.Writable || loading) return;
            item.CapturePosition?.Invoke();
            store.Put(RadioRecord.Capture(item.InstanceId, prefab.prefabIndex, item.State));
        }
        private static void Guard(Action action)
        {
            if (active == null || active.disposed) return;
            try { action(); } catch (Exception ex) { active.warn("Radio native integration failed: " + ex.Message); }
        }

        public void Dispose()
        {
            if (disposed) return;
            disposed = true;
            foreach (var item in items) if (item) item.CapturePosition = null;
            items.Clear();
            foreach (var pair in patches) harmony.Unpatch(pair.Key, pair.Value);
            patches.Clear();
            if (active == this) active = null;
        }
    }
}
