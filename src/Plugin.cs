using System;
using System.Collections.Generic;
using System.Threading.Tasks;
using BepInEx;
using BepInEx.Configuration;
using HarmonyLib;
using SailwindRadio.Acoustics;
using SailwindRadio.Audio;
using SailwindRadio.Library;
using SailwindRadio.Models;
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
        public const string Version = "1.5.13";
        private static Plugin instance;
        private Harmony harmony;
        private RadioWorldService world;
        private RadioAcousticsService acoustics;
        private RadioShopService shops;
        private ConfigEntry<string> musicFolders;
        private ConfigEntry<bool> continueWhileSleeping;
        private ConfigEntry<float> mechanismVolume;
        private ConfigEntry<bool> songHover;
        private ConfigEntry<bool> logDisplayTiming;
        private LibraryScanner library;
        private RadioMenus menus;
        private Task<List<AudioApplication>> applicationList;
        private RadioPlaybackSet<RadioItemController> radios;
        private readonly HashSet<RadioItemController> seen = new HashSet<RadioItemController>();
        private readonly List<RadioItemController> removed = new List<RadioItemController>();
        private bool ready;
        private bool applicationPaused;
        private bool focused = true;
        private bool onlyLoading;

        private void Awake()
        {
            instance = this;
            try
            {
                musicFolders = Config.Bind("Music", "MusicFolders", "",
                    "Music folders separated by |. Each folder is one collection including all its subfolders. Example: D:\\Music | E:\\Sailing Music");
                continueWhileSleeping = Config.Bind("Audio", "ContinueWhileSleeping", false,
                    "Keep music playing while the player sleeps. Loading still suspends playback.");
                var appVolume = Config.Bind("Audio", "AppVolume", 1f, new ConfigDescription(
                    "Volume multiplier for apps playing through a radio, such as Spotify or a browser. 1 plays an app as loud as its own volume allows. Raise it to bring an app up to your music without turning the app itself up. Loud peaks are smoothly limited instead of clipping.",
                    new AcceptableValueRange<float>(ApplicationRing.MinimumBoost, ApplicationRing.MaximumBoost)));
                ApplicationRing.Boost = appVolume.Value;
                appVolume.SettingChanged += (sender, args) => ApplicationRing.Boost = appVolume.Value;
                BindFalloff("RadioFalloff", "the radio's built-in speaker", RadioSpeakerProfile.BuiltIn);
                BindFalloff("SmallSpeakerFalloff", "Small Speakers", RadioSpeakerProfile.Small);
                BindFalloff("SpeakerFalloff", "Speakers", RadioSpeakerProfile.Normal);
                BindFalloff("WooferFalloff", "Woofers and Turbo Wolfers", RadioSpeakerProfile.TurboWoofer);
                mechanismVolume = Config.Bind("Display", "MechanismVolume", .2f, new ConfigDescription(
                    "Volume of the regional radio display mechanisms, from silent at 0 to full at 1. Game volume also applies.",
                    new AcceptableValueRange<float>(0f, 1f)));
                songHover = Config.Bind("Display", "ShowSongOnHover", false,
                    "Show the current song's title and artist when looking at a radio.");
                logDisplayTiming = Config.Bind("Development", "LogDisplayTiming", false,
                    "Log regional display timing, mesh updates and allocations every 10 seconds for performance checks.");
                RemoveRetiredSettings();
                harmony = new Harmony(Id);
                MediaFoundationSupport.Detect();
                LibraryPaths.MediaFoundationFormats = MediaFoundationSupport.Enabled;
                Logger.LogInfo(MediaFoundationSupport.Summary);
                ApplicationAudioSupport.Detect(System.IO.Path.Combine(Paths.ConfigPath, "SailwindRadio.held-volumes.txt"), Warn);
                Logger.LogInfo(ApplicationAudioSupport.Summary);
                acoustics = new RadioAcousticsService();
                library = new LibraryScanner();
                radios = new RadioPlaybackSet<RadioItemController>(
                    (item, state) => new RadioPlayback(this, state, Warn), AttachEngine,
                    item => { if (item && item.State.Powered) item.TogglePower(); }, Warn);
                menus = new RadioMenus();
                DisplayTiming.Configure(() => logDisplayTiming.Value, message => Logger.LogInfo(message));
                world = new RadioWorldService(harmony, Warn, new DisplaySoundBank(() => mechanismVolume.Value, Warn));
                world.BeforeSave += CapturePositions;
                world.ActionRequested += HandleAction;
                world.RadioReplaced += (from, to) => radios.Move(from, to);
                shops = new RadioShopService(world, Warn);
                menus.CollectionsChanged += SelectCollections;
                menus.ApplicationChosen += ChooseApplication;
                library.RequestScan(musicFolders.Value);
                InstallSuspendBoundary(typeof(StartMenu), "GameToSettings");
                InstallSuspendBoundary(typeof(Sleep), "FallAsleep");
                ready = true;
                // Count only. Folder paths are personal and players share this log in reports.
                int folderCount = 0;
                foreach (string folder in (musicFolders.Value ?? "").Split('|'))
                    if (!string.IsNullOrWhiteSpace(folder)) folderCount++;
                Logger.LogInfo(folderCount == 0
                    ? "Sailwind Radio " + Version + " ready. Set MusicFolders in the Radio config file to load music."
                    : "Sailwind Radio " + Version + " ready with " + folderCount + (folderCount == 1 ? " music folder." : " music folders."));
            }
            catch (Exception error)
            {
                Logger.LogError("Radio initialization failed: " + error);
                Shutdown();
                enabled = false;
            }
        }

        private void BindFalloff(string key, string output, RadioSpeakerProfile profile)
        {
            var entry = Config.Bind("Audio", key, profile.DefaultRadius, new ConfigDescription(
                "How far " + output + " can be heard, in metres." + (profile == RadioSpeakerProfile.BuiltIn ? "" :
                " They join a playing radio up to " + DeviceVessels.MaximumLinkRange + " m away whatever this is set to."),
                new AcceptableValueRange<float>(RadioSpeakerProfile.MinimumRadius, RadioSpeakerProfile.MaximumRadius)));
            profile.SetRadius(entry.Value);
            entry.SettingChanged += (sender, args) => profile.SetRadius(entry.Value);
        }

        private void RemoveRetiredSettings()
        {
            // BepInEx 5 preserves unbound values as orphaned entries. Consume each
            // retired key before removing it so existing config files lose the lines.
            var oldSpawn = Config.Bind("Development", "SpawnRadio", "", "Retired developer spawn shortcut.");
            Config.Remove(oldSpawn.Definition);
            var oldSpawnRevision = Config.Bind("Internal", "SpawnDefaultRevision", 0, "Retired spawn shortcut migration marker.");
            Config.Remove(oldSpawnRevision.Definition);
            var oldMusicFile = Config.Bind("Music", "MusicFile", "", "Retired single-file music setting.");
            bool legacyMusicOnly = !string.IsNullOrWhiteSpace(oldMusicFile.Value) && string.IsNullOrWhiteSpace(musicFolders.Value);
            Config.Remove(oldMusicFile.Definition);
            var oldStormEnabled = Config.Bind("Audio", "StormInterferenceEnabled", true, "Retired interference setting.");
            Config.Remove(oldStormEnabled.Definition);
            var oldStormStrength = Config.Bind("Audio", "StormInterferenceStrength", .35f, "Retired interference setting.");
            Config.Remove(oldStormStrength.Definition);
            var oldContinueWhilePaused = Config.Bind("Audio", "ContinueWhilePaused", false, "Retired pause setting.");
            Config.Remove(oldContinueWhilePaused.Definition);
            // Regional displays no longer page long text.
            var oldPageHold = Config.Bind("Display", "PageHoldSeconds", 4f, "Retired page hold setting.");
            Config.Remove(oldPageHold.Definition);
            Config.Save();
            if (legacyMusicOnly) Warn("MusicFile has been retired. Set MusicFolders to a folder containing your music.");
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

        private bool Suspended => Loading || Paused;

        // Islands load while the player sails, so loading only silences a radio. Only a pause also pauses an application.
        private static bool Loading => !GameState.playing || GameState.currentlyLoading || GameState.loadingScenes > 0;

        private bool Paused => (GameState.sleeping && !continueWhileSleeping.Value) ||
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
                var previous = library.Snapshot;
                if (library.Poll())
                {
                    foreach (string warning in library.Snapshot.WarningsSince(previous)) Warn(warning);
                    radios.SetSnapshot(library.Snapshot);
                    if (menus.CollectionRadio) ShowCollections(menus.CollectionRadio);
                }
                menus.Tick();
                PollApplications();
                bool paused = Paused;
                onlyLoading = !paused && Loading;
                bool suspended = paused || onlyLoading;
                // Each radio's playback failure is contained by the set. Anything else stops the mod below.
                radios.Tick(Time.realtimeSinceStartup, world.ActiveRadioId, item => item ? item.InstanceId : 0,
                    TickPlayback, suspended);
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
                if (!item || item.State.Role != DeviceRole.Radio) continue;
                seen.Add(item);
                radios.Add(item, item.State);
            }
            removed.Clear();
            foreach (var item in radios.Keys)
                if (!item || !seen.Contains(item)) removed.Add(item);
            foreach (var item in removed) radios.Remove(item);
        }

        // Native hooks reach the current engine through the set, so a replaced engine is never called.
        private void AttachEngine(RadioItemController item, RadioPlayback engine)
        {
            if (!item) return;
            if (engine == null)
            {
                item.CapturePosition = null;
                item.SuspendPlayback = null;
                item.ForgetPlayback();
                return;
            }
            item.CapturePosition = () => radios.Capture(item);
            item.SuspendPlayback = () => radios.Run(item, (current, queue) => current.Tick(item.VisualAudioPosition, true), true);
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

        // In the boat camera, a radio on the player's own boat and its speakers, which share its vessel, are
        // heard from the player's head. They play flat because the moving camera listener cannot pan them.
        private bool HeardFromHead(DeviceVessel vessel) => acoustics.HeadKnown && DeviceVessels.OnPlayerBoat(vessel);

        // An output that can't be heard keeps its last cabin muffling instead of asking again. It is silent
        // when its radio isn't playing, its volume is zero, or it is beyond its falloff distance.
        private bool ShouldMuffle(bool audible, float volume, Vector3 position, RadioSpeakerProfile profile, bool fromHead)
        {
            if (!audible || volume <= 0 || profile == null) return false;
            Vector3 listening = fromHead && acoustics.HeadKnown ? acoustics.HeadPosition : acoustics.ListenerPosition;
            return (position - listening).sqrMagnitude <= profile.Radius * profile.Radius;
        }

        // Without a known listening point nothing is muffled, as the acoustics service rules.
        private float Muffling(RadioItemController device, bool audible, float volume, Vector3 position, bool carried,
            RadioSpeakerProfile profile, bool fromHead) =>
            !acoustics.ListenerKnown ? 0
                : ShouldMuffle(audible, volume, position, profile, fromHead) ? acoustics.ObstructionAt(device, position, carried, fromHead)
                : acoustics.LastObstruction(device);

        private void TickPlayback(RadioItemController item, RadioPlayback engine, RadioQueue queue, bool suspended)
        {
            if (!item) return;
            var place = item.Place();
            Vector3 position = place.SoundPosition;
            bool fromHead = HeardFromHead(place.Vessel);
            if (place.Carried && acoustics.ListenerKnown) position = acoustics.ListenerPosition;
            bool audible = engine.Audible(suspended);
            float obstruction = Muffling(item, audible, item.State.Volume * item.State.LocalVolume, position, place.Carried,
                RadioSpeakerProfile.BuiltIn, fromHead);
            item.HearFromHead(fromHead, acoustics.HeadPosition);
            engine.SetAcoustics(fromHead ? acoustics.HeadPosition : acoustics.ListenerPosition, acoustics.ListenerKnown, obstruction, fromHead);
            engine.SetCarried(place.Carried);
            engine.BeginEndpoints();
            if (item.InstanceId == world.ActiveRadioId && item.State.Powered)
                ConnectSpeakers(place.Vessel, engine, place.VisualPosition, fromHead, audible, item.State.Volume);
            engine.EndEndpoints();
            engine.Tick(position, suspended, suspended && !onlyLoading);
            if (!suspended && item.State.Powered && !item.State.Paused)
            {
                bool changed = engine.LoadFailed ? queue.TrackFailed(engine.FailedPath) :
                    engine.TrackEnded && queue.TrackEnded();
                if (changed) engine.SetTrack(item.State.TrackPath);
            }
            engine.PreloadTrack(item.InstanceId == world.ActiveRadioId && item.State.Powered
                ? queue.PeekNext() : "");
            var application = engine.PlayingApplication;
            if (application != null)
            {
                // The application's own track when its media session names one, otherwise the application.
                var track = engine.ApplicationTrack;
                bool named = track != null && track.Title.Length > 0;
                item.SetTrackInfo(named ? track.Title : application.Label, named ? track.Artist : "", named ? track.Album : "",
                    item.State.Powered && songHover.Value);
            }
            else
            {
                var info = library.Snapshot.GetTrackInfo(item.State.TrackPath);
                item.SetTrackInfo(info.Title, info.Artist, info.Album, engine.NamesTrack && songHover.Value);
            }
        }

        private void ConnectSpeakers(DeviceVessel sourceVessel, RadioPlayback engine, Vector3 devicePosition, bool fromHead,
            bool audible, float masterVolume)
        {
            foreach (var speaker in world.Items)
            {
                if (!speaker || speaker.State.Role == DeviceRole.Radio || !speaker.State.SpeakerEnabled) continue;
                var place = speaker.Place();
                if (!DeviceVessels.CanConnect(sourceVessel, place.Vessel, devicePosition, place.VisualPosition)) continue;
                Vector3 speakerPosition = place.Carried && acoustics.ListenerKnown ? acoustics.ListenerPosition : place.SoundPosition;
                float localVolume = speaker.State.Role == DeviceRole.Woofer ? 1f : speaker.State.Volume;
                float obstruction = Muffling(speaker, audible, masterVolume * localVolume, speakerPosition, place.Carried,
                    RadioSpeakerProfile.ForKind(speaker.State.Kind), fromHead);
                engine.SetEndpoint(speaker.InstanceId, speakerPosition, place.Carried, speaker.State.Kind,
                    localVolume, speaker.State.Bass, obstruction);
            }
        }

        private void HandleAction(RadioItemController item, RadioAction action)
        {
            if (!ready || !item || item.State.Role != DeviceRole.Radio) return;
            if (action == RadioAction.Power)
            {
                if (item.State.Powered) library.RequestScan(musicFolders.Value);
                return;
            }
            if (action == RadioAction.Collections)
            {
                ShowCollections(item);
                ListApplications(item);
                return;
            }
            // Only the power button turns a radio on.
            if (action == RadioAction.PlayPause && !item.State.Powered) return;
            radios.Run(item, (engine, queue) =>
            {
                bool changed = false;
                // An application's own media session takes the playback buttons. One without a session can still be paused by the radio.
                bool application = engine.PlayingApplication != null;
                switch (action)
                {
                    case RadioAction.PlayPause:
                        if (application && !item.State.Paused && engine.SendToApplication(MediaControls.Command.PlayPause)) break;
                        engine.CapturePosition();
                        item.State.Paused = !item.State.Paused;
                        if (item.State.Paused) item.SuspendPlayback?.Invoke();
                        break;
                    case RadioAction.Previous:
                        if (application) engine.SendToApplication(MediaControls.Command.Previous);
                        else changed = queue.Previous();
                        break;
                    case RadioAction.Next:
                        if (application) engine.SendToApplication(MediaControls.Command.Next);
                        else changed = queue.Next();
                        break;
                    case RadioAction.Shuffle: changed = queue.SetShuffle(!item.State.Shuffle); break;
                }
                if (changed) engine.SetTrack(item.State.TrackPath);
            });
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
            if (!item) return;
            radios.Run(item, (engine, queue) => { if (queue.SetCollections(library.Snapshot, selected)) engine.SetTrack(item.State.TrackPath); });
        }

        // Listing touches Windows audio and every process, so it runs off the Unity thread.
        private void ListApplications(RadioItemController item)
        {
            if (!ApplicationAudioSupport.Enabled)
            {
                menus.ShowApplications(PlayingApplication(item), null, ApplicationAudioSupport.Summary);
                return;
            }
            menus.ShowApplications(PlayingApplication(item), null, "Looking for applications playing sound");
            if (applicationList == null)
                applicationList = Task.Run(() => { using (new ComCalls.Apartment()) return AudioApplications.List(); });
        }

        private void PollApplications()
        {
            if (applicationList == null || !applicationList.IsCompleted) return;
            var finished = applicationList;
            applicationList = null;
            var item = menus.CollectionRadio;
            if (finished.IsFaulted)
            {
                Warn("Could not list applications: " + finished.Exception.GetBaseException().Message);
                if (item) menus.ShowApplications(PlayingApplication(item), null, "Could not list applications");
            }
            else if (item)
                menus.ShowApplications(PlayingApplication(item), finished.Result, finished.Result.Count == 0
                    ? "No other application is playing sound. Start your music app and open this menu again." : null);
        }

        private AudioApplication PlayingApplication(RadioItemController item)
        {
            AudioApplication playing = null;
            radios.Run(item, (engine, queue) => playing = engine.PlayingApplication);
            return playing;
        }

        private void ChooseApplication(RadioItemController item, AudioApplication application)
        {
            if (item) radios.Run(item, (engine, queue) => engine.SetApplication(application));
        }

        private void OnGUI() { if (ready) menus?.Draw(); }

        private void CapturePositions()
        {
            radios?.ForEachEngine((item, engine) => engine.CapturePosition(), "Could not capture a radio position");
        }

        private void SuspendNow()
        {
            bool paused = Paused;
            radios?.ForEachEngine((item, engine) => { if (item) engine.Tick(item.VisualAudioPosition, true, paused); }, "Could not suspend a radio");
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
            radios?.Clear();
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
