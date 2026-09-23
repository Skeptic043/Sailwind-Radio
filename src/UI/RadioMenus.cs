using System;
using System.Collections.Generic;
using SailwindRadio.Physical;
using UnityEngine;

namespace SailwindRadio.UI
{
    public sealed class CollectionChoice
    {
        public readonly string Id;
        public readonly string Label;
        public readonly bool Selected;
        public CollectionChoice(string id, string label, bool selected) { Id=id; Label=label; Selected=selected; }
    }

    public sealed class RadioMenus : IDisposable
    {
        private readonly MenuInputLease lease = new MenuInputLease();
        private readonly List<CollectionChoice> choices = new List<CollectionChoice>();
        private readonly Dictionary<string,bool> selected = new Dictionary<string,bool>(StringComparer.OrdinalIgnoreCase);
        private Vector2 scroll;
        private bool disposed;
        public bool IsOpen => lease.Owned;
        public RadioItemController CollectionRadio { get; private set; }
        public event Action<RadioItemController,string[]> CollectionsChanged;

        public void ShowCollections(RadioItemController radio, IReadOnlyList<CollectionChoice> available)
        {
            if (disposed || !radio || radio.State.Kind != 0) return;
            bool refresh = IsOpen && CollectionRadio == radio;
            if (!refresh)
            {
                Close();
                radio.ReleaseControls();
                if (!lease.Acquire()) return;
                selected.Clear();
                scroll=Vector2.zero;
            }
            CollectionRadio=radio;
            choices.Clear();
            if (available != null)
                foreach (var choice in available)
                {
                    if (choice == null || string.IsNullOrEmpty(choice.Id)) continue;
                    choices.Add(choice);
                    if (!selected.ContainsKey(choice.Id)) selected.Add(choice.Id,choice.Selected);
                }
        }

        public void Tick()
        {
            if (!IsOpen) return;
            if (!MenuInputLease.GameplayAvailable || !Application.isFocused || !GameState.inCursorMenu ||
                UnityEngine.Input.GetKeyDown(KeyCode.Escape)) { Close(); return; }
            if (CollectionRadio && (!CollectionRadio.IsPlacedForControls || !CollectionRadio.WithinMenuReach)) Close();
            else if (!CollectionRadio) Close();
        }

        public void Draw()
        {
            if (!IsOpen) return;
            float width=Mathf.Min(440,Screen.width-24);
            float height=Mathf.Min(430,Screen.height-24);
            GUILayout.Window(194043,new Rect((Screen.width-width)*.5f,(Screen.height-height)*.5f,width,height),
                DrawWindow,"Radio collections");
        }

        private void DrawWindow(int id)
        {
            if (CollectionRadio)
            {
                GUILayout.Label("Choose which music collections this radio plays");
                scroll=GUILayout.BeginScrollView(scroll,GUILayout.Height(Mathf.Min(280,Screen.height-160)));
                if (choices.Count==0) GUILayout.Label("No collections available yet");
                foreach (var choice in choices)
                    selected[choice.Id]=GUILayout.Toggle(selected[choice.Id],choice.Label ?? choice.Id);
                GUILayout.EndScrollView();
                if (GUILayout.Button("Apply"))
                {
                    var radio=CollectionRadio;
                    var roots=new List<string>();
                    foreach(var choice in choices) if(selected[choice.Id] && !roots.Contains(choice.Id)) roots.Add(choice.Id);
                    Close();
                    CollectionsChanged?.Invoke(radio,roots.ToArray());
                }
            }
            if (GUILayout.Button("Close")) Close();
        }

        public void Close()
        {
            lease.Release();
            CollectionRadio=null;
            choices.Clear();
            selected.Clear();
        }

        public void Dispose() { if (disposed) return; Close(); disposed=true; }
    }
}
