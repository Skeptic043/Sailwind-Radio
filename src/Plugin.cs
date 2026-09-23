using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SailwindRadio.Acoustics;
using SailwindRadio.Input;
using SailwindRadio.Library;
using SailwindRadio.Physical;
using SailwindRadio.Playback;
using SailwindRadio.Shops;
using SailwindRadio.UI;
using UnityEngine;

namespace SailwindRadio
{
    [BepInPlugin(Id, "Sailwind Radio", Version)]
    [BepInProcess("Sailwind.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.sailwind.radio";
        public const string Version = "0.5.5";
        private static Plugin instance;
        private Harmony harmony;
        private RadioWorldService world;
        private RadioAcousticsService acoustics;
        private RadioShopService shops;
        private ConfigEntry<string> musicFolders;
        private ConfigEntry<bool> continueWhileSleeping;
        private LibraryScanner library;
        private RadioMenus menus;
        private bool libraryReady;
        private ShortcutSetting spawnKey;
        private readonly Dictionary<RadioItemController, RadioPlayback> playback = new Dictionary<RadioItemController, RadioPlayback>();
        private readonly Dictionary<RadioItemController, RadioQueue> queues = new Dictionary<RadioItemController, RadioQueue>();
        private readonly HashSet<RadioItemController> seen = new HashSet<RadioItemController>();
        private readonly List<RadioItemController> removed = new List<RadioItemController>();
        private bool ready;
        private bool applicationPaused;
        private bool focused = true;

        private void Awake()
        {
            instance = this;
            try
            {
                musicFolders = Config.Bind("Music", "MusicFolders", "",
                    "Music folders separated by |. Each folder is one collection including all its subfolders. Example: D:\\Music | E:\\Sailing Music");
                continueWhileSleeping = Config.Bind("Audio", "ContinueWhileSleeping", false,
                    "Keep music playing while the player sleeps. Loading still suspends playback.");
                spawnKey = SpawnShortcut.Bind(Config, Warn);
                // BepInEx 5 preserves unbound values as orphaned entries. Consume each
                // retired key before removing it so existing config files lose the lines.
                var oldMusicFile = Config.Bind("Music", "MusicFile", "", "Retired single-file music setting.");
                bool legacyMusicOnly = !string.IsNullOrWhiteSpace(oldMusicFile.Value) && string.IsNullOrWhiteSpace(musicFolders.Value);
                Config.Remove(oldMusicFile.Definition);
                var oldStormEnabled = Config.Bind("Audio", "StormInterferenceEnabled", true, "Retired interference setting.");
                Config.Remove(oldStormEnabled.Definition);
                var oldStormStrength = Config.Bind("Audio", "StormInterferenceStrength", .35f, "Retired interference setting.");
                Config.Remove(oldStormStrength.Definition);
                var oldContinueWhilePaused = Config.Bind("Audio", "ContinueWhilePaused", false, "Retired pause setting.");
                Config.Remove(oldContinueWhilePaused.Definition);
                Config.Save();
                if (legacyMusicOnly) Warn("MusicFile has been retired. Set MusicFolders to a folder containing your music.");
                harmony = new Harmony(Id);
                acoustics = new RadioAcousticsService();
                library = new LibraryScanner();
                menus = new RadioMenus();
                world = new RadioWorldService(harmony, () => "", Warn);
                world.BeforeSave += CapturePositions;
                world.ActionRequested += HandleAction;
                shops = new RadioShopService(world, Warn);
                menus.SpawnRequested += SpawnDevice;
                menus.StandPositionRequested += LogStandPosition;
                menus.CollectionsChanged += SelectCollections;
                library.RequestScan(musicFolders.Value);
                InstallSuspendBoundary(typeof(StartMenu), "GameToSettings");
                InstallSuspendBoundary(typeof(Sleep), "FallAsleep");
                ready = true;
                Logger.LogInfo("Sailwind Radio " + Version + " ready. Configure MusicFolders, then press " + spawnKey.Value.Serialize() + " for the spawn menu.");
            }
            catch (Exception error)
            {
                Logger.LogError("Radio initialization failed: " + error);
                Shutdown();
                enabled = false;
            }
        }

        private void InstallSuspendBoundary(Type type, string name)
        {
            var method = AccessTools.DeclaredMethod(type, name, Type.EmptyTypes);
            if (method == null)
            {
                Warn("Immediate radio pause hook unavailable for " + type.Name + "." + name + ". Game state pause checks remain active.");
                return;
            }
            harmony.Patch(method, prefix: new HarmonyMethod(typeof(Plugin), nameof(ReleaseControlBoundary)),
                postfix: new HarmonyMethod(typeof(Plugin), nameof(SuspendBoundary)));
        }

        private static void ReleaseControlBoundary()
        {
            if (instance && instance.ready) instance.ReleaseRadioControls();
        }

        private static void SuspendBoundary()
        {
            if (instance && instance.ready && instance.Suspended) instance.SuspendNow();
        }

        private bool Suspended => !GameState.playing || GameState.currentlyLoading || GameState.loadingScenes > 0 ||
            (GameState.sleeping && !continueWhileSleeping.Value) ||
            (Time.timeScale <= 0f && !(GameState.sleeping && continueWhileSleeping.Value)) ||
            AudioListener.pause || applicationPaused || (!focused && !Application.runInBackground);

        private void Update()
        {
            if (!ready) return;
            try
            {
                acoustics.Tick();
                world.Tick();
                TickShops();
                SynchronizePlayback();
                if (library.Poll())
                {
                    libraryReady = true;
                    foreach (string warning in library.Snapshot.Warnings) Warn(warning);
                    foreach (var pair in queues)
                        if (pair.Value.ApplySnapshot(library.Snapshot)) playback[pair.Key].SetTrack(pair.Key.State.TrackPath);
                    if (menus.CollectionRadio) ShowCollections(menus.CollectionRadio);
                }
                menus.Tick();
                bool suspended = Suspended;
                PlaybackOrder.Tick(playback, world.ActiveRadioId, pair => pair.Key ? pair.Key.InstanceId : 0,
                    TickPlayback, suspended);
                if (!suspended && (menus.IsOpen || !GameState.inCursorMenu) && HotkeyInput.IsDown(spawnKey.Value,
                    focused && Application.isFocused, UnityEngine.Input.GetKeyDown, UnityEngine.Input.GetKey))
                {
                    if (menus.IsOpen) menus.Close();
                    else { ReleaseRadioControls(); menus.ShowSpawnChooser(); }
                }
            }
            catch (Exception error)
            {
                Logger.LogError("Radio stopped after a runtime error: " + error);
                Shutdown();
                enabled = false;
            }
        }

        private void SynchronizePlayback()
        {
            seen.Clear();
            var items = world.Items;
            for (int index = 0; index < items.Count; index++)
            {
                var item = items[index];
                if (!item || item.State.Kind != 0) continue;
                seen.Add(item);
                if (playback.ContainsKey(item)) continue;
                var queue = new RadioQueue(item.State);
                if (libraryReady) queue.ApplySnapshot(library.Snapshot);
                queues.Add(item, queue);
                var engine = new RadioPlayback(this, item.State, Warn) { RepeatTrack = false };
                playback.Add(item, engine);
                item.CapturePosition = engine.CapturePosition;
                item.SuspendPlayback = () => engine.Tick(item.VisualAudioPosition, true);
            }
            removed.Clear();
            foreach (var pair in playback)
            {
                if (pair.Key && seen.Contains(pair.Key)) continue;
                pair.Value.Dispose();
                if (pair.Key) { pair.Key.CapturePosition = null; pair.Key.SuspendPlayback = null; }
                removed.Add(pair.Key);
            }
            foreach (var item in removed) { playback.Remove(item); queues.Remove(item); }
        }

        private void TickShops()
        {
            if (shops == null) return;
            try { shops.Tick(); }
            catch (Exception error)
            {
                Warn("Radio shops stopped after an error: " + error.Message);
                var failed = shops;
                shops = null;
                try { failed.Dispose(); }
                catch (Exception cleanup) { Warn("Could not release radio shops: " + cleanup.Message); }
            }
        }

        private void TickPlayback(KeyValuePair<RadioItemController, RadioPlayback> pair, bool suspended)
        {
            if (!pair.Key) return;
            Vector3 devicePosition = pair.Key.VisualAudioPosition;
            Vector3 position = pair.Key.SoundPosition;
            bool carried = pair.Key.UsesPlayerPosition;
            if (carried && acoustics.ListenerKnown) position = acoustics.ListenerPosition;
            float obstruction = pair.Key.State.Powered ? acoustics.ObstructionAt(position, carried) : 0;
            pair.Value.SetAcoustics(acoustics.ListenerPosition, acoustics.ListenerKnown, obstruction);
            pair.Value.SetCarried(carried);
            pair.Value.BeginEndpoints();
            if (pair.Key.InstanceId == world.ActiveRadioId && pair.Key.State.Powered)
            {
                var sourceVessel = pair.Key.Vessel;
                foreach (var speaker in world.Items)
                {
                    if (!speaker || speaker.State.Kind == 0 || !speaker.State.SpeakerEnabled) continue;
                    var speakerVessel = speaker.Vessel;
                    bool speakerCarried = speaker.UsesPlayerPosition;
                    Vector3 speakerDevicePosition = speaker.VisualAudioPosition;
                    if (!DeviceVessels.CanConnect(sourceVessel, speakerVessel, devicePosition, speakerDevicePosition)) continue;
                    Vector3 speakerPosition = speakerCarried && acoustics.ListenerKnown ? acoustics.ListenerPosition : speaker.SoundPosition;
                    pair.Value.SetEndpoint(speaker.InstanceId, speakerPosition, speakerCarried, speaker.State.Kind,
                        speaker.State.Kind == 3 ? 1f : speaker.State.Volume, speaker.State.Bass, acoustics.ObstructionAt(speakerPosition, speakerCarried));
                }
            }
            pair.Value.EndEndpoints();
            pair.Value.Tick(position, suspended);
            if (!suspended && pair.Key.State.Powered && !pair.Key.State.Paused)
            {
                RadioQueue queue = queues[pair.Key];
                bool changed = pair.Value.LoadFailed ? queue.TrackFailed(pair.Value.FailedPath) :
                    pair.Value.TrackEnded && queue.TrackEnded();
                if (changed) pair.Value.SetTrack(pair.Key.State.TrackPath);
            }
            pair.Value.PreloadTrack(pair.Key.InstanceId == world.ActiveRadioId && pair.Key.State.Powered
                ? queues[pair.Key].PeekNext() : "");
            var info = library.Snapshot.GetTrackInfo(pair.Key.State.TrackPath);
            pair.Key.SetTrackInfo(info.Title, info.Artist, info.Album);
        }

        private void HandleAction(RadioItemController item, RadioAction action)
        {
            if (!ready || !item || item.State.Kind != 0) return;
            if (action == RadioAction.Power)
            {
                if (item.State.Powered) library.RequestScan(musicFolders.Value);
                return;
            }
            if (!playback.TryGetValue(item, out var engine) || !queues.TryGetValue(item, out var queue)) return;
            bool changed = false;
            switch (action)
            {
                case RadioAction.PlayPause:
                    if (!item.State.Powered) item.TogglePower();
                    else
                    {
                        engine.CapturePosition();
                        item.State.Paused = !item.State.Paused;
                        if (item.State.Paused) item.SuspendPlayback?.Invoke();
                    }
                    break;
                case RadioAction.Previous: changed = queue.Previous(); break;
                case RadioAction.Next: changed = queue.Next(); break;
                case RadioAction.Shuffle: changed = queue.SetShuffle(!item.State.Shuffle); break;
                case RadioAction.Collections: ShowCollections(item); break;
            }
            if (changed) engine.SetTrack(item.State.TrackPath);
        }

        private void ShowCollections(RadioItemController item)
        {
            if (!item) return;
            var choices = new List<CollectionChoice>();
            var selected = new HashSet<string>(item.State.SelectedCollections ?? Array.Empty<string>(), StringComparer.OrdinalIgnoreCase);
            foreach (var collection in library.Snapshot.Collections)
                choices.Add(new CollectionChoice(collection.Id, collection.Label, selected.Contains(collection.Id)));
            menus.ShowCollections(item, choices);
        }

        private void SelectCollections(RadioItemController item, string[] selected)
        {
            if (!item || !queues.TryGetValue(item, out var queue)) return;
            if (queue.SetCollections(library.Snapshot, selected)) playback[item].SetTrack(item.State.TrackPath);
        }

        private void OnGUI() { if (ready) menus?.Draw(); }

        private void SpawnDevice(RadioDeviceKind kind)
        {
            if (!Refs.observerMirror) return;
            Transform player = Refs.observerMirror.transform;
            Vector3 forward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.1f) forward = Vector3.forward;
            Vector3 position = player.position + Vector3.up * 0.8f + forward * 1.2f;
            bool created = world.TrySpawn(position, Quaternion.LookRotation(forward, Vector3.up), kind, out string message);
            if (created) Logger.LogInfo(message);
            else { Warn(message); menus.SetMessage(message); }
        }

        private void LogStandPosition()
        {
            var observer = Refs.observerMirror ? Refs.observerMirror.transform : null;
            bool found = RadioShopPositionMarker.TryDescribe(observer,
                UnityEngine.Object.FindObjectsOfType<IslandSceneryScene>(), out string message);
            if (found) Logger.LogInfo(message);
            menus.SetMessage(message);
        }

        private void CapturePositions()
        {
            foreach (var engine in playback.Values)
                try { engine.CapturePosition(); }
                catch (Exception error) { Warn("Could not capture a radio position: " + error.Message); }
        }

        private void SuspendNow()
        {
            foreach (var pair in playback)
            {
                if (!pair.Key) continue;
                try { pair.Value.Tick(pair.Key.VisualAudioPosition, true); }
                catch (Exception error) { Warn("Could not suspend a radio: " + error.Message); }
            }
        }

        private void ReleaseRadioControls()
        {
            menus?.Close();
            foreach (var item in world?.Items ?? Array.Empty<RadioItemController>())
                if (item)
                    try { item.ReleaseControls(); }
                    catch (Exception error) { Warn("Could not release radio controls: " + error.Message); }
        }

        private void OnApplicationPause(bool pause)
        {
            applicationPaused = pause;
            if (pause) ReleaseRadioControls();
            if (pause && ready) SuspendNow();
        }

        private void OnApplicationFocus(bool focus)
        {
            focused = focus;
            acoustics?.Invalidate();
            if (!focus) ReleaseRadioControls();
            if (!focus && !Application.runInBackground && ready) SuspendNow();
        }

        private void Warn(string message) { Logger.LogWarning(message); }
        private void OnDisable() { Shutdown(); }
        private void OnDestroy() { Shutdown(); }

        private void Shutdown()
        {
            ready = false;
            ReleaseRadioControls();
            CapturePositions();
            foreach (var pair in playback)
            {
                if (pair.Key) { pair.Key.CapturePosition = null; pair.Key.SuspendPlayback = null; }
                try { pair.Value.Dispose(); }
                catch (Exception error) { Warn("Could not release a radio player: " + error.Message); }
            }
            playback.Clear();
            queues.Clear();
            library?.Dispose();
            library = null;
            menus?.Dispose();
            menus = null;
            try { shops?.Dispose(); }
            catch (Exception error) { Warn("Could not release radio shops: " + error.Message); }
            shops = null;
            try { world?.Dispose(); }
            catch (Exception error) { Warn("Could not release radio item hooks: " + error.Message); }
            world = null;
            acoustics?.Dispose();
            acoustics = null;
            try { harmony?.UnpatchSelf(); }
            catch (Exception error) { Warn("Could not remove radio hooks: " + error.Message); }
            harmony = null;
            if (instance == this) instance = null;
        }
    }
}
