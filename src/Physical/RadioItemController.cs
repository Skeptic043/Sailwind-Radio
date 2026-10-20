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
        private ITrackDisplay display;
        private static bool fontReported;
        private Material screenMaterial;
        private bool screenLit;
        private string title = "", artist = "", album = "", displayContent = "", renderedText;
        private readonly Dictionary<RadioVolumeKnob, float> knobAngles = new Dictionary<RadioVolumeKnob, float>();
        private readonly Dictionary<RadioVolumeKnob, Vector3> knobAxes = new Dictionary<RadioVolumeKnob, Vector3>();
        private Vector3 soundOrigin;
        // Cabinet height below a native hook.
        internal float Height { get; private set; }
        private Transform menuPointer;
        private Collider menuControl;
        private bool shopStock, inventorySlot;
        private readonly List<BoxCollider> bodyBoxes = new List<BoxCollider>();

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
            ? VisualAudioPosition : transform.TransformPoint(soundOrigin);

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
            if (RadioNativeHooks.CanHang(State.Kind) && RadioWorldService.HookCompatible && !GetComponent<HangableItem>())
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

        // Walnut kinds keep their tuned constants. Regional kinds derive their shape from model bounds.
        private static bool Regional(int kind, out Vector3 min, out Vector3 max)
        {
            min = max = Vector3.zero;
            return !DeviceKind.IsWalnut(kind) && DeviceModel.TryGetBounds(kind, out min, out max);
        }

        internal static void ConfigureItem(ShipItem item, int kind)
        {
            var role = DeviceKind.Role(kind);
            item.name = RadioDevice.Name(kind);
            item.description = "";
            item.big = role == DeviceRole.Speaker || role == DeviceRole.Woofer;
            item.wallAttachment = role == DeviceRole.SmallSpeaker;
            item.category = role == DeviceRole.Radio || role == DeviceRole.SmallSpeaker ? TransactionCategory.toolsAndSupplies : TransactionCategory.otherItems;
            item.mass = role == DeviceRole.SmallSpeaker ? .25f : role == DeviceRole.Speaker ? 3f : role == DeviceRole.Woofer ? 8f : .5f;
            // The inspected native PC inventory basis reverses X and faces +camera-Z at yaw zero.
            // Our models face -Z, so a local half-turn restores front-facing text and controls.
            item.inventoryRotation = RadioDevice.InventoryYaw;
            item.inventoryRotationX = 0;
            // Native small-item holding positions the root at the pointer. The
            // radio's root is at its base, so lower it by half its height to
            // put the face and controls near the player's sight line.
            item.holdHeight = role != DeviceRole.Radio ? 0f :
                -(Regional(kind, out var min, out var max) ? RadioDevice.Center(role, min, max) : RadioDevice.Center(kind)).y;
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
            shopStock = stock;
            ApplyControlColliders();
        }

        // Native EnterInventorySlot disables only the item's root collider. Live control triggers in a
        // slot would still reach ShipItem.OnTriggerEnter and board the item onto the player's boat.
        internal void SetInventorySlot(bool slotted)
        {
            inventorySlot = slotted;
            ApplyControlColliders();
        }

        private void ApplyControlColliders()
        {
            if (parts == null)
                return;
            // The body subcollider is also a live trigger on the item. Its native solid copy was made
            // at ItemRigidbody.Start, so disabling the original in a slot leaves collision untouched.
            foreach (var bodyBox in bodyBoxes)
                if (bodyBox)
                    bodyBox.enabled = !inventorySlot;
            foreach (var pair in parts)
            {
                if (!pair.Key.StartsWith("control_", StringComparison.Ordinal))
                    continue;
                var collider = pair.Value.GetComponent<Collider>();
                if (collider)
                    collider.enabled = !shopStock && !inventorySlot;
            }
        }

        public void TogglePower()
        {
            owner?.TogglePower(this);
        }
        public void RequestAction(RadioAction action)
        {
            if (!IsPlacedForControls || owner == null || !owner.RequestAction(this, action))
                return;
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
        // Display mechanisms fall silent while paused, asleep, slotted, boxed or unsold.
        private bool MechanismAudible => !shopStock && !inventorySlot && Time.timeScale > 0f && !GameState.sleeping &&
            !(item && item.itemRigidbodyC && item.itemRigidbodyC.GetCurrentBox());
        internal bool IsPlacedForControls => item && item.sold && !item.held && gameObject.activeInHierarchy && gameObject.layer != 5 &&
            item.itemRigidbodyC && !item.itemRigidbodyC.GetCurrentBox() && item.GetCurrentInventorySlot() < 0;
        // Binds or confirms this device's saved identity. It may tick the world service and write a record.
        internal bool EnsureOwned() => owner != null && owner.EnsureOwned(this);
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
                float angle = DeviceControlVisuals.KnobAngle(State.KnobLevel(knob.Mode));
                if (!knobAngles.TryGetValue(knob, out float previous) || previous != angle)
                {
                    // An authored axis points out of the knob's face. Turning about its reverse matches
                    // the walnut front knobs, whose faces point along -Z, as seen from in front of that face.
                    knob.transform.localRotation = knobAxes.TryGetValue(knob, out var axis) ? Quaternion.AngleAxis(angle, -axis) :
                        State.Kind == DeviceKind.WalnutRadio && (knob.Mode == RadioKnobMode.Master || knob.Mode == RadioKnobMode.Local)
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
                    // Regional icons are flat glyphs, so their first normal is the face they sit on.
                    var face = DeviceKind.IsWalnut(State.Kind) ? Vector3.back : pair.Value.GetComponent<MeshFilter>().sharedMesh.normals[0];
                    halo.transform.position = pair.Value.GetComponent<Renderer>().bounds.center +
                        pair.Value.transform.TransformDirection(face) * .008f;
                    var light = halo.AddComponent<Light>();
                    light.type = LightType.Point;
                    light.color = new Color(1f, .62f, .13f);
                    light.range = State.Role == DeviceRole.Radio || State.Role == DeviceRole.SmallSpeaker ? .08f : .12f;
                    light.intensity = .12f;
                    light.shadows = LightShadows.None;
                    light.enabled = false;
                    glows.Add(new Glow { Key = pair.Key.Substring(5), Material = material, Light = light });
                }
            }
            var box = GetComponent<BoxCollider>();
            Vector3 contactCenter, contactSize;
            if (Regional(State.Kind, out var min, out var max) && DeviceModel.TryGetContact(State.Kind, out var contactMin, out var contactMax))
            {
                // The authored contact box sits behind every control and on the wall plane for small speakers.
                contactCenter = (contactMin + contactMax) * .5f;
                contactSize = contactMax - contactMin;
                soundOrigin = RadioDevice.SoundOrigin(min, max);
                Height = max.y - min.y;
            }
            else
            {
                // Keep the radio's top at .33 for pointer raycasts; include each
                // floor speaker's visible feet without raising its cabinet top.
                contactCenter = State.Kind == DeviceKind.WalnutRadio ? new Vector3(0, .16f, 0) : RadioDevice.Center(State.Kind);
                contactSize = State.Kind == DeviceKind.WalnutRadio ? new Vector3(.6f, .34f, .2f) : RadioDevice.Size(State.Kind);
                float footDepth = State.Kind == DeviceKind.WalnutSpeaker ? .012f : State.Kind == DeviceKind.WalnutWoofer ? .015f : 0f;
                if (footDepth > 0f)
                {
                    contactCenter.y -= footDepth * .5f;
                    contactSize.y += footDepth;
                }
                soundOrigin = RadioDevice.SoundOrigin(State.Kind);
                Height = RadioDevice.Size(State.Kind).y;
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
            if (Regional(State.Kind, out var bodyMin, out var bodyMax))
            {
                // Native ItemRigidbody.Start copies each direct child tagged ItemSubcollider as a solid box,
                // so the whole regional body collides while the contact box stays behind the controls.
                // It clones the whole object, so this child holds only its box. Layer 2 keeps it out of pointer raycasts.
                // Authored solid boxes replace the bounds where items must rest on real tops between raised ends.
                var solids = DeviceModel.Solids(State.Kind);
                if (solids.Count == 0)
                    AddBodyBox(bodyMin, bodyMax);
                foreach (var solid in solids)
                    AddBodyBox(new Vector3(solid.min[0], solid.min[1], solid.min[2]), new Vector3(solid.max[0], solid.max[1], solid.max[2]));
            }
            AddButton("power", RadioAction.Power);
            if (State.Role == DeviceRole.Radio)
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
            else if (State.Role == DeviceRole.Woofer)
                AddKnob("bass", RadioKnobMode.Bass);
            else
                AddKnob("volume", RadioKnobMode.Volume);
        }
        private void AddBodyBox(Vector3 min, Vector3 max)
        {
            var body = new GameObject("Radio body subcollider") { tag = "ItemSubcollider", layer = 2 };
            body.transform.SetParent(transform, false);
            var box = body.AddComponent<BoxCollider>();
            box.center = (min + max) * .5f;
            box.size = max - min;
            box.isTrigger = true;
            bodyBoxes.Add(box);
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
            if (DeviceModel.TryGetAxis(State.Kind, "control_" + name, out var axis))
                knobAxes[knob] = axis;
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
            var textShader = owner?.WorldTextShader;
            if (!textShader)
                textShader = shader;
            if (DeviceKind.IsWalnut(State.Kind))
            {
                display = new DotMatrixDisplay(transform, shader, textShader, assets);
                return;
            }
            Font font = null;
            foreach (var candidate in Resources.FindObjectsOfTypeAll<Font>())
                if (candidate.name == "IMMORTAL")
                    font = candidate;
            if (!font)
            {
                if (!fontReported)
                    owner?.Report("Game font IMMORTAL unavailable for regional radio displays. Arial is used instead.");
                fontReported = true;
                font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            }
            // Leaves, drum brass and the stylus share the shader DeviceModel chose for the cabinet.
            parts.TryGetValue("display_carriage", out var carriage);
            display = new RegionalTrackDisplay(transform, DeviceModel.Displays(State.Kind), GetComponent<Renderer>().sharedMaterial.shader,
                textShader, font, () => MechanismAudible, owner.DisplaySounds,
                carriage ? carriage.transform : null, message => owner?.Report(message), assets);
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
