using System;
using System.Collections.Generic;
using SailwindRadio.Audio;
using SailwindRadio.Physical;
using UnityEngine;

namespace SailwindRadio.UI
{
    public sealed class CollectionChoice
    {
        public readonly string Id;
        public readonly string Label;
        public readonly bool Selected;
        public CollectionChoice(string id, string label, bool selected) { Id = id; Label = label; Selected = selected; }
    }

    // The two settings the radio menu shows, read and written through the plugin's config. App volume is a percentage.
    public sealed class MenuSettings
    {
        public float MinimumAppVolume, MaximumAppVolume;
        public Func<float> GetAppVolume;
        // Heard at once while the slider moves. Saved once, when the menu closes.
        public Action<float> HearAppVolume, SaveAppVolume;
        public Func<string> GetMusicFolders;
        public Action<string> SaveMusicFolders;
        public string MusicFoldersHelp;
    }

    public sealed class RadioMenus : IDisposable
    {
        private readonly MenuInputLease lease = new MenuInputLease();
        private readonly MenuSettings settings;
        private float volumeDraft, savedVolume;
        private string foldersDraft;
        private readonly List<CollectionChoice> choices = new List<CollectionChoice>();
        private readonly Dictionary<string, bool> selected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AudioApplication> applications = new List<AudioApplication>();
        private AudioApplication playing;
        private string applicationNote;
        private Vector2 scroll;
        private bool disposed;
        public bool IsOpen => lease.Owned;

        public RadioMenus(MenuSettings settings = null)
        {
            this.settings = settings;
        }

        private bool HasVolume => settings?.GetAppVolume != null && settings.MinimumAppVolume > 0 && settings.MaximumAppVolume > settings.MinimumAppVolume;
        private bool HasFolders => settings?.GetMusicFolders != null && settings.SaveMusicFolders != null;
        public RadioItemController CollectionRadio { get; private set; }
        public event Action<RadioItemController, string[]> CollectionsChanged;
        // Null returns the radio to music.
        public event Action<RadioItemController, AudioApplication> ApplicationChosen;

        // The application section. A note replaces the list while it loads or when playback is unavailable.
        public void ShowApplications(AudioApplication current, IReadOnlyList<AudioApplication> available, string note)
        {
            playing = current;
            applicationNote = note;
            applications.Clear();
            if (available != null) applications.AddRange(available);
        }

        public void ShowCollections(RadioItemController radio, IReadOnlyList<CollectionChoice> available)
        {
            if (disposed || !radio || radio.State.Role != DeviceRole.Radio) return;
            bool refresh = IsOpen && CollectionRadio == radio;
            if (!refresh)
            {
                Close();
                radio.ReleaseControls();
                if (!lease.Acquire()) return;
                selected.Clear();
                scroll = Vector2.zero;
                if (HasVolume) volumeDraft = Mathf.Clamp(settings.GetAppVolume(), settings.MinimumAppVolume, settings.MaximumAppVolume);
                savedVolume = volumeDraft;
                foldersDraft = HasFolders ? settings.GetMusicFolders() ?? "" : "";
            }
            CollectionRadio = radio;
            choices.Clear();
            if (available != null)
                foreach (var choice in available)
                {
                    if (choice == null || string.IsNullOrEmpty(choice.Id)) continue;
                    choices.Add(choice);
                    if (!selected.ContainsKey(choice.Id)) selected.Add(choice.Id, choice.Selected);
                }
        }

        public void Tick()
        {
            if (!IsOpen) return;
            if (!MenuInputLease.GameplayAvailable || !Application.isFocused || !GameState.inCursorMenu ||
                UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            if (CollectionRadio && (!CollectionRadio.IsPlacedForControls || !CollectionRadio.EnsureOwned() || !CollectionRadio.WithinMenuReach)) Close();
            else if (!CollectionRadio) Close();
        }

        public void Draw()
        {
            if (!IsOpen) return;
            float width = Mathf.Min(440, Screen.width - 24);
            float height = Mathf.Min(520, Screen.height - 24);
            GUILayout.Window(194043, new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height),
                DrawWindow, "Radio music");
        }

        private void DrawWindow(int id)
        {
            if (CollectionRadio)
            {
                if (HasFolders)
                {
                    GUILayout.Label("Music folders");
                    // The same explanation Configuration Manager shows for the setting.
                    if (!string.IsNullOrEmpty(settings.MusicFoldersHelp)) GUILayout.Label(settings.MusicFoldersHelp);
                    foldersDraft = GUILayout.TextField(foldersDraft ?? "");
                    GUILayout.Space(8);
                }
                GUILayout.Label("Choose which music collections this radio plays");
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(320, Screen.height - 210)));
                if (choices.Count == 0) GUILayout.Label("No collections available yet");
                foreach (var choice in choices)
                    selected[choice.Id] = GUILayout.Toggle(selected[choice.Id], choice.Label ?? choice.Id);
                DrawApplications();
                GUILayout.EndScrollView();
                if (GUILayout.Button("Apply"))
                {
                    var radio = CollectionRadio;
                    var roots = new List<string>();
                    foreach (var choice in choices) if (selected[choice.Id] && !roots.Contains(choice.Id)) roots.Add(choice.Id);
                    string folders = foldersDraft;
                    Close();
                    CollectionsChanged?.Invoke(radio, roots.ToArray());
                    // Saving the setting rescans, and the new collections appear the next time the menu opens.
                    if (HasFolders && folders != null && folders != settings.GetMusicFolders()) settings.SaveMusicFolders(folders);
                }
            }
            if (GUILayout.Button("Close")) Close();
        }

        private void DrawApplications()
        {
            GUILayout.Space(12);
            GUILayout.Label(playing != null ? "Playing " + playing.Label : "Or play an application through the radio");
            if (HasVolume)
            {
                GUILayout.Label("App volume " + Mathf.RoundToInt(volumeDraft) + "%");
                float value = Mathf.Round(GUILayout.HorizontalSlider(volumeDraft, settings.MinimumAppVolume, settings.MaximumAppVolume));
                value = Mathf.Clamp(value, settings.MinimumAppVolume, settings.MaximumAppVolume);
                if (Mathf.Abs(value - volumeDraft) > 0.0001f) { volumeDraft = value; settings.HearAppVolume?.Invoke(value); }
            }
            bool chosen = playing != null && GUILayout.Button("Back to music");
            AudioApplication choice = null;
            if (applicationNote != null) GUILayout.Label(applicationNote);
            foreach (var application in applications)
                if (!application.Same(playing) && GUILayout.Button(application.Label)) { chosen = true; choice = application; }
            if (!chosen) return;
            var radio = CollectionRadio;
            Close();
            ApplicationChosen?.Invoke(radio, choice);
        }

        public void Close()
        {
            // The slider is heard while it moves. The setting is written once, when the menu closes.
            if (lease.Owned && HasVolume && Mathf.Abs(savedVolume - volumeDraft) > 0.0001f) settings.SaveAppVolume?.Invoke(volumeDraft);
            lease.Release();
            CollectionRadio = null;
            choices.Clear();
            selected.Clear();
            applications.Clear();
            playing = null;
            applicationNote = null;
        }

        public void Dispose() { if (disposed) return; Close(); disposed = true; }
    }
}
