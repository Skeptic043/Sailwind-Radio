using System;
using System.Collections.Generic;
using SailwindRadio.Models;
using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed class RadioItemController : MonoBehaviour
    {
        public RadioState State
        {
            get; private set;
        }
        public int InstanceId
        {
            get; private set;
        }
        public Action CapturePosition
        {
            get; set;
        }
        public Action SuspendPlayback
        {
            get; set;
        }
        private ShipItem item;
        private RadioWorldService owner;
        private readonly List<UnityEngine.Object> assets = new List<UnityEngine.Object>();
        private readonly List<RadioVolumeKnob> knobs = new List<RadioVolumeKnob>();
        private Dictionary<string, GameObject> parts;
        private readonly Dictionary<string, float> pulses = new Dictionary<string, float>();
        private sealed class Glow
        {
            internal string Key;
            internal Material Material;
            internal Light Light;
            internal bool Active;
        }
        private readonly List<Glow> glows = new List<Glow>();
        private static readonly Color LitIconColor = new Color(.8f, .75f, .54f);
        private static readonly Color UnlitIconColor = new Color(.30f, .23f, .15f);
        private int renderedLayer = -1;
        private DotMatrixDisplay display;
        private Material screenMaterial;
        private bool screenLit;
        private string title = "", artist = "", album = "", displayContent = "", renderedText;
        private readonly Dictionary<RadioVolumeKnob, float> knobAngles = new Dictionary<RadioVolumeKnob, float>();
        private Transform menuPointer;
        private Collider menuControl;

        public DeviceVessel Vessel => DeviceVessels.Resolve(item, UsesPlayerPosition);
        internal bool WithinMenuReach => RadioControlReach.Contains(menuPointer, menuControl);
        internal void RememberControl(GoPointer pointer, Collider control)
        {
            menuPointer = pointer ? pointer.transform : null;
            menuControl = control;
        }
        public bool UsesPlayerPosition => DeviceVessels.IsPlayerOwned(item);

        public Vector3 VisualAudioPosition
        {
            get
            {
                if (UsesPlayerPosition && Refs.observerMirror)
                    return Refs.observerMirror.transform.position;
                if (item && item.itemRigidbodyC)
                {
                    Transform box = item.itemRigidbodyC.GetCurrentBox();
                    if (box)
                    {
                        var body = box.GetComponentInParent<ItemRigidbody>();
                        if (body && body.GetShipItem())
                            return body.GetShipItem().transform.position;
                        return box.position;
                    }
                }
                return transform.position;
            }
        }

        // Placed sound originates at the cone rather than the floor-level native item pivot.
        public Vector3 SoundPosition => UsesPlayerPosition || (item && item.itemRigidbodyC && item.itemRigidbodyC.GetCurrentBox())
            ? VisualAudioPosition : transform.TransformPoint(RadioDevice.SoundOrigin(State.Kind));

        internal void Initialize(RadioWorldService service, RadioState state)
        {
            owner = service;
            item = GetComponent<ShipItem>();
            RefreshOwnership();
            State = state;
            ConfigureItem(item, State.Kind);
            if (item.wallAttachment && item.itemRigidbodyC && !item.held)
                item.itemRigidbodyC.attached = true;
            BuildModel();
            // The native lamp hook accepts a HangableItem on the held object. It is rebuilt after
            // load alongside our model, while the saved native item keeps its original prefab ID.
            if (State.Kind == 0 && RadioWorldService.HookCompatible && !GetComponent<HangableItem>())
                gameObject.AddComponent<HangableItem>();
        }

        internal void ApplyRestoredState(RadioWorldService service, RadioState state)
        {
            owner = service;
            item = GetComponent<ShipItem>();
            if (State == null || State.Kind != state.Kind)
                throw new InvalidOperationException("Registered radio template kind changed during restoration");
            State = state;
            RefreshOwnership();
        }

        internal static void ConfigureItem(ShipItem item, int kind)
        {
            item.name = RadioDevice.Name(kind);
            item.description = "";
            item.big = kind >= 2;
            item.wallAttachment = kind == 1;
            item.category = kind < 2 ? TransactionCategory.toolsAndSupplies : TransactionCategory.otherItems;
            item.mass = kind == 1 ? .25f : kind == 2 ? 3f : kind == 3 ? 8f : .5f;
            // The inspected native PC inventory basis reverses X and faces +camera-Z at yaw zero.
            // Our models face -Z, so a local half-turn restores front-facing text and controls.
            item.inventoryRotation = RadioDevice.InventoryYaw;
            item.inventoryRotationX = 0;
            // Native small-item holding positions the root at the pointer. The
            // radio's root is at its base, so lower it by half its height to
            // put the face and controls near the player's sight line.
            item.holdHeight = kind == 0 ? -RadioDevice.Center(0).y : 0f;
            item.value = SailwindRadio.Shops.RadioShopCatalog.BasePrice(kind);
        }

        internal void RefreshOwnership()
        {
            InstanceId = GetComponent<SaveablePrefab>().instanceId;
        }

        internal void SetShopStock(bool stock)
        {
            if (stock)
                ReleaseControls();
            if (parts == null)
                return;
            foreach (var pair in parts)
            {
                if (!pair.Key.StartsWith("control_", StringComparison.Ordinal))
                    continue;
                var collider = pair.Value.GetComponent<Collider>();
                if (collider)
                    collider.enabled = !stock;
            }
        }

        public void TogglePower()
        {
            owner?.TogglePower(this);
        }
        public void RequestAction(RadioAction action)
        {
            if (!IsPlacedForControls)
                return;
            owner?.RequestAction(this, action);
            if (action != RadioAction.Power && action != RadioAction.PlayPause && action != RadioAction.Shuffle)
                pulses[action.ToString().ToLowerInvariant()] = Time.unscaledTime + .16f;
        }
        public void SetTrackInfo(string trackTitle, string trackArtist, string trackAlbum)
        {
            string newTitle = Clean(trackTitle, 128), newArtist = Clean(trackArtist, 128), newAlbum = Clean(trackAlbum, 128);
            if (title == newTitle && artist == newArtist && album == newAlbum)
                return;
            title = newTitle;
            artist = newArtist;
            album = newAlbum;
            displayContent = title + "\n" + artist + "\n" + album;
        }
        public void SetPlaybackDisplay(string track, string playbackStatus)
        {
            if (State != null && State.Kind == 0 && string.IsNullOrEmpty(title))
                SetTrackInfo(track, "", "");
        }
        private static string Clean(string value, int limit)
        {
            value = (value ?? "").Replace('\n', ' ').Replace('\r', ' ');
            if (value.Length <= limit)
                return value;
            int end = limit - 1;
            if (char.IsHighSurrogate(value[end - 1]))
                end--;
            return value.Substring(0, end) + "…";
        }
        internal bool IsPlacedForControls => item && item.sold && !item.held && gameObject.activeInHierarchy && gameObject.layer != 5 &&
            item.itemRigidbodyC && !item.itemRigidbodyC.GetCurrentBox() && item.GetCurrentInventorySlot() < 0;
        public void ReleaseControls()
        {
            foreach (var knob in knobs)
                if (knob)
                    knob.ReleaseInteraction();
        }

        internal void Tick()
        {
            if (parts == null)
                return;
            if (renderedLayer != gameObject.layer)
            {
                renderedLayer = gameObject.layer;
                foreach (var part in parts.Values)
                    if (part)
                        part.layer = renderedLayer;
                foreach (var glow in glows)
                    if (glow.Light)
                    {
                        glow.Light.gameObject.layer = renderedLayer;
                        glow.Light.cullingMask = 1 << renderedLayer;
                    }
                display?.SetLayer(renderedLayer);
            }
            foreach (var glow in glows)
            {
                string key = glow.Key;
                bool active = DeviceControlVisuals.IsLit(State, key, pulses.TryGetValue(key, out float until) && Time.unscaledTime < until);
                if (glow.Active != active)
                {
                    glow.Active = active;
                    // Diffuse contrast keeps the state legible in direct sunlight.
                    glow.Material.SetColor("_Color", active ? LitIconColor : UnlitIconColor);
                    glow.Material.SetColor("_EmissionColor", active ? new Color(.9f, .43f, .06f) : Color.black);
                    if (glow.Light)
                        glow.Light.enabled = active;
                }
            }
            foreach (var knob in knobs)
            {
                float level = knob.Mode == RadioKnobMode.Bass ? State.Bass :
                    knob.Mode == RadioKnobMode.Local ? State.LocalVolume : State.Volume;
                float angle = DeviceControlVisuals.KnobAngle(level);
                if (!knobAngles.TryGetValue(knob, out float previous) || previous != angle)
                {
                    knob.transform.localRotation = State.Kind == 0 &&
                        (knob.Mode == RadioKnobMode.Master || knob.Mode == RadioKnobMode.Local)
                        ? Quaternion.Euler(0, angle, 0) : Quaternion.Euler(0, 0, angle);
                    knobAngles[knob] = angle;
                }
            }
            if (screenMaterial && screenLit != State.Powered)
            {
                screenLit = State.Powered;
                screenMaterial.SetColor("_EmissionColor", screenLit ? new Color(.045f, .018f, .0025f) : Color.black);
            }
            if (display != null)
            {
                display.Tick();
                string text = State.Powered ? displayContent : "";
                if (text != renderedText)
                {
                    display.SetMetadata(State.Powered, title, artist, album);
                    renderedText = text;
                }
            }
        }
        private void BuildModel()
        {
            foreach (Transform child in transform)
                child.gameObject.SetActive(false);
            parts = DeviceModel.Build(gameObject, State.Kind, assets);
            foreach (var pair in parts)
            {
                if (pair.Key == "screen")
                    screenMaterial = pair.Value.GetComponent<Renderer>().sharedMaterial;
                else if (DeviceModel.HasOwnLightMaterial(pair.Key))
                {
                    var material = pair.Value.GetComponent<Renderer>().sharedMaterial;
                    material.SetColor("_Color", UnlitIconColor);
                    var halo = new GameObject("Local control glow");
                    halo.transform.SetParent(pair.Value.transform, false);
                    halo.transform.position = pair.Value.GetComponent<Renderer>().bounds.center +
                        pair.Value.transform.TransformDirection(Vector3.back) * .008f;
                    var light = halo.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.color = new Color(1f, .62f, .13f);
                    light.range = State.Kind == 0 || State.Kind == 1 ? .08f : .12f;
                    light.intensity = .12f;
                    light.shadows = LightShadows.None;
                    light.enabled = false;
                    glows.Add(new Glow { Key = pair.Key.Substring(5), Material = material, Light = light });
                }
            }
            var box = GetComponent<BoxCollider>();
            // Keep the radio's top at .33 for pointer raycasts; include each
            // floor speaker's visible feet without raising its cabinet top.
            var contactCenter = State.Kind == 0 ? new Vector3(0, .16f, 0) : RadioDevice.Center(State.Kind);
            var contactSize = State.Kind == 0 ? new Vector3(.6f, .34f, .2f) : RadioDevice.Size(State.Kind);
            float footDepth = State.Kind == 2 ? .012f : State.Kind == 3 ? .015f : 0f;
            if (footDepth > 0f)
            {
                contactCenter.y -= footDepth * .5f;
                contactSize.y += footDepth;
            }
            box.center = contactCenter;
            box.size = contactSize;
            if (item.itemRigidbodyC)
            {
                var physicsBox = item.itemRigidbodyC.GetComponent<BoxCollider>();
                if (physicsBox)
                {
                    physicsBox.center = box.center;
                    physicsBox.size = box.size;
                }
            }
            AddButton("power", RadioAction.Power);
            if (State.Kind == 0)
            {
                AddButton("playpause", RadioAction.PlayPause);
                AddButton("previous", RadioAction.Previous);
                AddButton("next", RadioAction.Next);
                AddButton("shuffle", RadioAction.Shuffle);
                AddButton("collections", RadioAction.Collections);
                AddKnob("master", RadioKnobMode.Master);
                AddKnob("local", RadioKnobMode.Local);
                BuildTrackDisplay();
            }
            else if (State.Kind == 3)
                AddKnob("bass", RadioKnobMode.Bass);
            else
                AddKnob("volume", RadioKnobMode.Volume);
        }
        private void AddButton(string name, RadioAction action)
        {
            if (!parts.TryGetValue("control_" + name, out var part))
                throw new InvalidOperationException("Missing device control: " + name);
            if (action == RadioAction.Power)
                part.AddComponent<RadioPowerButton>().Radio = this;
            else
            {
                var control = part.AddComponent<RadioActionButton>();
                control.Radio = this;
                control.Action = action;
            }
        }
        private void AddKnob(string name, RadioKnobMode mode)
        {
            if (!parts.TryGetValue("control_" + name, out var part))
                throw new InvalidOperationException("Missing device knob: " + name);
            var knob = part.AddComponent<RadioVolumeKnob>();
            knob.Radio = this;
            knob.Mode = mode;
            knobs.Add(knob);
        }
        private void BuildTrackDisplay()
        {
            // The installed player's Sprites/Default is depth-tested (LEqual), unlike GUI/Text Shader.
            var shader = Shader.Find("Sprites/Default");
            if (!shader)
            {
                owner?.Report("Depth-tested radio track display shader unavailable");
                return;
            }
            display = new DotMatrixDisplay(transform, shader, assets);
        }
        private void OnDestroy()
        {
            display?.Dispose();
            try
            {
                ReleaseControls();
                CapturePosition?.Invoke();
            }
            catch (Exception ex) { owner?.Report("Could not capture final radio position: " + ex.Message); }
            finally { try { owner?.Forget(this); } finally { foreach (var asset in assets) if (asset) Destroy(asset); } }
        }
    }
}
