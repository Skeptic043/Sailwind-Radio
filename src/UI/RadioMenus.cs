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

    public sealed class RadioMenus : IDisposable
    {
        private readonly MenuInputLease lease = new MenuInputLease();
        private readonly List<CollectionChoice> choices = new List<CollectionChoice>();
        private readonly Dictionary<string, bool> selected = new Dictionary<string, bool>(StringComparer.OrdinalIgnoreCase);
        private readonly List<AudioApplication> applications = new List<AudioApplication>();
        private AudioApplication playing;
        private string applicationNote;
        private Vector2 scroll;
        private bool disposed;
        public bool IsOpen => lease.Owned;
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
                GUILayout.Label("Choose which music collections this radio plays");
                scroll = GUILayout.BeginScrollView(scroll, GUILayout.Height(Mathf.Min(370, Screen.height - 160)));
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
                    Close();
                    CollectionsChanged?.Invoke(radio, roots.ToArray());
                }
            }
            if (GUILayout.Button("Close")) Close();
        }

        private void DrawApplications()
        {
            GUILayout.Space(12);
            GUILayout.Label(playing != null ? "Playing " + playing.Label + " through the radio" : "Or play an application through the radio");
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
