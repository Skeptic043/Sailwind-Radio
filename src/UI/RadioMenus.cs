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

    // The settings the radio menu shows, read and written through the plugin's config. App volume is a percentage.
    public sealed class MenuSettings
    {
        public float MinimumAppVolume, MaximumAppVolume;
        public Func<float> GetAppVolume;
        // Heard at once while the slider moves. Saved once, when the menu closes.
        public Action<float> HearAppVolume, SaveAppVolume;
        public Func<string> GetMusicFolders;
        public Action<string> SaveMusicFolders;
        public string MusicFoldersHelp;
        public Func<IReadOnlyList<string>> GetExcludedApps;
        public Action<AudioApplication> ExcludeApp;
        public Action<string> RestoreExcludedApp;
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
        private GUISkin layoutSkin;
        private GUIStyle wrappedLabel, clippedToggle, clippedButton;
        private bool showHiddenApps;
        private bool disposed;
        public bool IsOpen => lease.Owned;

        public RadioMenus(MenuSettings settings = null)
        {
            this.settings = settings;
        }

        private bool HasVolume => settings?.GetAppVolume != null && settings.MinimumAppVolume > 0 && settings.MaximumAppVolume > settings.MinimumAppVolume;
        private bool HasFolders => settings?.GetMusicFolders != null && settings.SaveMusicFolders != null;
        private bool HasExclusion => settings?.GetExcludedApps != null && settings.ExcludeApp != null && settings.RestoreExcludedApp != null;
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

        private static float WindowWidth => Mathf.Max(1f, Mathf.Min(440, Screen.width - 24));

        public void Draw()
        {
            if (!IsOpen) return;
            float width = WindowWidth;
            float height = Mathf.Max(1f, Mathf.Min(520, Screen.height - 24));
            GUILayout.Window(194043, new Rect((Screen.width - width) * .5f, (Screen.height - height) * .5f, width, height),
                DrawWindow, "Radio music", GUILayout.Width(width), GUILayout.Height(height));
        }

        private void DrawWindow(int id)
        {
            if (CollectionRadio)
            {
                var skin = GUI.skin;
                if (layoutSkin != skin)
                {
                    layoutSkin = skin;
                    wrappedLabel = new GUIStyle(skin.label) { wordWrap = true, clipping = TextClipping.Clip };
                    clippedToggle = new GUIStyle(skin.toggle) { wordWrap = false, clipping = TextClipping.Clip };
                    clippedButton = new GUIStyle(skin.button) { wordWrap = false, clipping = TextClipping.Clip };
                }

                // Keep room for the vertical scrollbar even as the list changes length.
                float bodyWidth = InsetWidth(WindowWidth, skin.window.padding, skin.scrollView.margin)
                    - skin.verticalScrollbar.fixedWidth - skin.verticalScrollbar.margin.left;
                var padding = skin.scrollView.padding;
                var labelWidth = GUILayout.Width(InsetWidth(bodyWidth, padding, wrappedLabel.margin));
                var toggleWidth = GUILayout.Width(InsetWidth(bodyWidth, padding, clippedToggle.margin));
                var buttonWidth = GUILayout.Width(InsetWidth(bodyWidth, padding, clippedButton.margin));

                // The scroll view takes the space left by the window padding and the two footer buttons.
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.ExpandHeight(true));
                if (HasFolders)
                {
                    GUILayout.Label("Music folders", wrappedLabel, labelWidth);
                    // The same explanation Configuration Manager shows for the setting.
                    if (!string.IsNullOrEmpty(settings.MusicFoldersHelp)) GUILayout.Label(settings.MusicFoldersHelp, wrappedLabel, labelWidth);
                    foldersDraft = GUILayout.TextField(foldersDraft ?? "", GUILayout.Width(InsetWidth(bodyWidth, padding, skin.textField.margin)));
                    GUILayout.Space(8);
                }
                GUILayout.Label("Choose which music collections this radio plays", wrappedLabel, labelWidth);
                if (choices.Count == 0) GUILayout.Label("No collections available yet", wrappedLabel, labelWidth);
                foreach (var choice in choices)
                    selected[choice.Id] = GUILayout.Toggle(selected[choice.Id], choice.Label ?? choice.Id, clippedToggle, toggleWidth);
                DrawApplications(bodyWidth, padding, labelWidth, buttonWidth);
                GUILayout.EndScrollView();
                if (GUILayout.Button("Apply", GUILayout.ExpandHeight(false)))
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
            if (GUILayout.Button("Close", GUILayout.ExpandHeight(false))) Close();
        }

        private static float InsetWidth(float width, RectOffset padding, RectOffset margin)
            => Mathf.Max(1f, width - Mathf.Max(padding.left, margin.left) - Mathf.Max(padding.right, margin.right));

        private const float ActionButtonWidth = 56f;

        private void DrawApplications(float bodyWidth, RectOffset padding, GUILayoutOption labelWidth, GUILayoutOption buttonWidth)
        {
            GUILayout.Space(12);
            GUILayout.Label(playing != null ? "Playing " + playing.Label : "Or play an application through the radio", wrappedLabel, labelWidth);
            if (HasVolume)
            {
                GUILayout.Label("App volume " + Mathf.RoundToInt(volumeDraft) + "%", wrappedLabel, labelWidth);
                float value = Mathf.Round(GUILayout.HorizontalSlider(volumeDraft, settings.MinimumAppVolume, settings.MaximumAppVolume));
                value = Mathf.Clamp(value, settings.MinimumAppVolume, settings.MaximumAppVolume);
                if (Mathf.Abs(value - volumeDraft) > 0.0001f) { volumeDraft = value; settings.HearAppVolume?.Invoke(value); }
            }
            float rowWidth = InsetWidth(bodyWidth, padding, clippedButton.margin);
            float mainWidth = Mathf.Max(1f, rowWidth - ActionButtonWidth - 4f);
            var mainOption = GUILayout.Width(mainWidth);
            var actionOption = GUILayout.Width(ActionButtonWidth);
            bool chosen = playing != null && GUILayout.Button("Back to music", clippedButton, buttonWidth);
            AudioApplication choice = null;
            AudioApplication hideApp = null;
            if (applicationNote != null) GUILayout.Label(applicationNote, wrappedLabel, labelWidth);
            foreach (var application in applications)
            {
                if (application.Same(playing)) continue;
                if (HasExclusion)
                {
                    GUILayout.BeginHorizontal();
                    if (GUILayout.Button(application.Label, clippedButton, mainOption)) { chosen = true; choice = application; }
                    if (GUILayout.Button("Hide", clippedButton, actionOption)) hideApp = application;
                    GUILayout.EndHorizontal();
                }
                else if (GUILayout.Button(application.Label, clippedButton, buttonWidth)) { chosen = true; choice = application; }
            }
            if (HasExclusion)
            {
                var hidden = settings.GetExcludedApps();
                string restoreEntry = null;
                if (hidden != null && hidden.Count > 0)
                {
                    GUILayout.Space(8);
                    string toggleLabel = showHiddenApps ? "Hide hidden apps" : "Show hidden apps (" + hidden.Count + ")";
                    if (GUILayout.Button(toggleLabel, clippedButton, buttonWidth)) showHiddenApps = !showHiddenApps;
                    if (showHiddenApps)
                    {
                        foreach (string entry in hidden)
                        {
                            GUILayout.BeginHorizontal();
                            GUILayout.Label(entry, wrappedLabel, mainOption);
                            if (GUILayout.Button("Show", clippedButton, actionOption)) restoreEntry = entry;
                            GUILayout.EndHorizontal();
                        }
                    }
                }
                else
                {
                    showHiddenApps = false;
                }
                if (hideApp != null) settings.ExcludeApp(hideApp);
                else if (restoreEntry != null) settings.RestoreExcludedApp(restoreEntry);
            }
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
            showHiddenApps = false;
        }

        public void Dispose() { if (disposed) return; Close(); disposed = true; }
    }
}
