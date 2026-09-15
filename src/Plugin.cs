using System;
using System.Collections.Generic;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SailwindRadio.Input;
using SailwindRadio.Physical;
using UnityEngine;

namespace SailwindRadio
{
    [BepInPlugin(Id, "Sailwind Radio", Version)]
    [BepInProcess("Sailwind.exe")]
    public sealed class Plugin : BaseUnityPlugin
    {
        public const string Id = "local.sailwind.radio";
        public const string Version = "0.1.1";
        private static Plugin instance;
        private Harmony harmony;
        private RadioWorldService world;
        private ConfigEntry<string> musicFile;
        private ShortcutSetting spawnKey;
        private readonly Dictionary<RadioItemController, RadioPlayback> playback = new Dictionary<RadioItemController, RadioPlayback>();
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
                musicFile = Config.Bind("Music", "MusicFile", "",
                    "Absolute path to one local MP3, OGG or WAV file for the first radio test. New radios use this file.");
                spawnKey = new ShortcutSetting(Config, "Development", "SpawnRadio", new KeyboardShortcut(KeyCode.F7),
                    "Create a test radio in front of the player during normal gameplay. Use key names such as O, F7 or Mouse4. " +
                    "Chords such as LeftShift + Mouse4 also work. Key names ignore case and spaces. Leave blank or use None to disable.", Warn);
                harmony = new Harmony(Id);
                world = new RadioWorldService(harmony, () => musicFile.Value, Warn);
                world.BeforeSave += CapturePositions;
                InstallSuspendBoundary(typeof(StartMenu), "GameToSettings");
                InstallSuspendBoundary(typeof(Sleep), "FallAsleep");
                ready = true;
                Logger.LogInfo("Sailwind Radio " + Version + " ready. Configure MusicFile, then press " + spawnKey.Value.Serialize() + " to create a test radio.");
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
            if (instance && instance.ready) instance.SuspendNow();
        }

        private bool Suspended => !GameState.playing || GameState.currentlyLoading || GameState.loadingScenes > 0 ||
            GameState.sleeping || Time.timeScale <= 0f || AudioListener.pause || applicationPaused || (!focused && !Application.runInBackground);

        private void Update()
        {
            if (!ready) return;
            try
            {
                world.Tick();
                SynchronizePlayback();
                bool suspended = Suspended;
                foreach (var pair in playback)
                {
                    if (!pair.Key) continue;
                    pair.Value.Tick(pair.Key.VisualAudioPosition, suspended);
                    pair.Key.SetPlaybackDisplay(pair.Value.TrackLabel, pair.Value.Status);
                }
                if (!suspended && !GameState.inCursorMenu && HotkeyInput.IsDown(spawnKey.Value,
                    focused && Application.isFocused, UnityEngine.Input.GetKeyDown, UnityEngine.Input.GetKey)) SpawnRadio();
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
                if (!item) continue;
                seen.Add(item);
                if (playback.ContainsKey(item)) continue;
                var engine = new RadioPlayback(this, item.State, Warn);
                playback.Add(item, engine);
                item.CapturePosition = engine.CapturePosition;
            }
            removed.Clear();
            foreach (var pair in playback)
            {
                if (pair.Key && seen.Contains(pair.Key)) continue;
                pair.Value.Dispose();
                if (pair.Key) pair.Key.CapturePosition = null;
                removed.Add(pair.Key);
            }
            foreach (var item in removed) playback.Remove(item);
        }

        private void SpawnRadio()
        {
            if (!Refs.observerMirror) return;
            Transform player = Refs.observerMirror.transform;
            Vector3 forward = Vector3.ProjectOnPlane(player.forward, Vector3.up).normalized;
            if (forward.sqrMagnitude < 0.1f) forward = Vector3.forward;
            Vector3 position = player.position + Vector3.up * 0.8f + forward * 1.2f;
            bool created = world.TrySpawn(position, Quaternion.LookRotation(forward, Vector3.up), out string message);
            if (created) Logger.LogInfo(message);
            else Warn(message);
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
                if (pair.Key) pair.Key.CapturePosition = null;
                try { pair.Value.Dispose(); }
                catch (Exception error) { Warn("Could not release a radio player: " + error.Message); }
            }
            playback.Clear();
            try { world?.Dispose(); }
            catch (Exception error) { Warn("Could not release radio item hooks: " + error.Message); }
            world = null;
            try { harmony?.UnpatchSelf(); }
            catch (Exception error) { Warn("Could not remove radio hooks: " + error.Message); }
            harmony = null;
            if (instance == this) instance = null;
        }
    }
}
