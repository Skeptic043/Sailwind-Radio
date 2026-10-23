using System;
using System.Collections.Generic;
using SailwindRadio.Models;
using SailwindRadio.Playback;
using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed partial class RadioItemController : MonoBehaviour
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
        internal ShipItem Item => item;
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
        private bool trackNamed;
        private readonly Dictionary<RadioVolumeKnob, float> knobAngles = new Dictionary<RadioVolumeKnob, float>();
        private readonly Dictionary<RadioVolumeKnob, Vector3> knobAxes = new Dictionary<RadioVolumeKnob, Vector3>();
        private Vector3 soundOrigin, contactCenter, contactSize;
        // Cabinet height below a native hook.
        internal float Height { get; private set; }
        private Transform menuPointer;
        private Collider menuControl;
        private bool shopStock, inventorySlot;
        private readonly List<BoxCollider> bodyBoxes = new List<BoxCollider>();

        public DeviceVessel Vessel => DeviceVessels.Resolve(item, UsesPlayerPosition);
        // The display voices follow the music: heard flat from the player's head while the boat camera is on.
        internal void HearFromHead(bool fromHead, Vector3 head) => (display as RegionalTrackDisplay)?.Sounds.HearFrom(fromHead, head);
        // A released or quarantined engine no longer ticks, so its hover song and head listening end with it.
        internal void ForgetPlayback()
        {
            trackNamed = false;
            HearFromHead(false, Vector3.zero);
        }
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
            SyncStack();
        }

        internal void ApplyRestoredState(RadioWorldService service, RadioState state)
        {
            owner = service;
            item = GetComponent<ShipItem>();
            if (State == null || State.Kind != state.Kind)
                throw new InvalidOperationException("Registered radio template kind changed during restoration");
            State = state;
            RefreshOwnership();
            SyncStack();
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
            // Big items are carried in both hands and are what native cargo loading accepts.
            item.big = role == DeviceRole.Speaker || role == DeviceRole.Woofer;
            item.wallAttachment = role == DeviceRole.SmallSpeaker;
            item.category = role == DeviceRole.Radio || role == DeviceRole.SmallSpeaker ? TransactionCategory.toolsAndSupplies : TransactionCategory.otherItems;
            item.mass = Mass(role);
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

        internal static float Mass(DeviceRole role) => role == DeviceRole.SmallSpeaker ? .25f : role == DeviceRole.Speaker ? 3f :
            role == DeviceRole.Woofer ? 8f : .5f;

        // A stacked device has no native item, so its ID comes from its base.
        internal void RefreshOwnership()
        {
            InstanceId = stackBase ? StackedInstanceId : GetComponent<SaveablePrefab>().instanceId;
            foreach (var top in stack)
                top.RefreshOwnership();
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
            foreach (var top in stack)
            {
                top.shopStock = shopStock;
                top.inventorySlot = inventorySlot;
                top.ApplyControlColliders();
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
        // Hover text names the song while it is opening, playing, paused or suspended.
        internal string HoverTrack => !trackNamed || title == "" ? "" : artist == "" ? title : title + "\n" + artist;
        public void SetTrackInfo(string trackTitle, string trackArtist, string trackAlbum, bool named)
        {
            trackNamed = named;
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
        internal bool IsPlacedForControls => item && item.sold && !item.held && gameObject.activeInHierarchy && item.gameObject.layer != 5 &&
            item.itemRigidbodyC && !item.itemRigidbodyC.GetCurrentBox() && item.GetCurrentInventorySlot() < 0;
        // Binds or confirms this device's saved identity. It may tick the world service and write a record.
        internal bool EnsureOwned() => owner != null && owner.EnsureOwned(this);
        public void ReleaseControls()
        {
            foreach (var knob in knobs)
                if (knob)
                    knob.ReleaseInteraction();
            foreach (var top in stack)
                top.ReleaseControls();
        }

        internal void Tick()
        {
            if (parts == null)
                return;
            if (!physicsFitted && !stackBase)
                ApplyBoxes();
            // A stacked top renders on its base's native layer, such as the held-item layer.
            int layer = item ? item.gameObject.layer : gameObject.layer;
            if (renderedLayer != layer)
            {
                renderedLayer = layer;
                gameObject.layer = layer;
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
            bool regional = HasRegionalShape(State.Kind, out var min, out var max);
            soundOrigin = regional ? RadioDevice.SoundOrigin(min, max) : RadioDevice.SoundOrigin(State.Kind);
            Height = regional ? max.y - min.y : RadioDevice.Size(State.Kind).y;
            // A stacked top's cabinet is covered by its base's boxes, which it never owns.
            if (stackBase)
            {
                AddControls();
                return;
            }
            Contact(State.Kind, out contactCenter, out contactSize);
            // Native ItemRigidbody.Start copies each direct child tagged ItemSubcollider as a solid box, so the whole
            // regional body collides while the contact box stays behind the controls. It clones the whole object, so
            // this child holds only its box. Layer 2 keeps it out of pointer raycasts. Authored solid boxes follow the
            // model where items must rest on real tops. Walnut kinds have none.
            foreach (var box in DeviceModel.CollisionBoxes(State.Kind))
                AddBodyBox(box.Min, box.Max);
            AddControls();
        }

        // Regional kinds with both authored model bounds and an authored contact box.
        private static bool HasRegionalShape(int kind, out Vector3 min, out Vector3 max) =>
            Regional(kind, out min, out max) && DeviceModel.TryGetContact(kind, out _, out _);

        // One cabinet's pointer contact box in its own space.
        private static void Contact(int kind, out Vector3 center, out Vector3 size)
        {
            if (HasRegionalShape(kind, out _, out _) && DeviceModel.TryGetContact(kind, out var contactMin, out var contactMax))
            {
                // The authored contact box sits behind every control and on the wall plane for small speakers.
                center = (contactMin + contactMax) * .5f;
                size = contactMax - contactMin;
                return;
            }
            // Keep the radio's top at .33 for pointer raycasts.
            if (kind == DeviceKind.WalnutRadio)
            {
                center = new Vector3(0, .16f, 0);
                size = new Vector3(.6f, .34f, .2f);
                return;
            }
            WalnutCabinet(kind, out center, out size);
        }

        // A walnut cabinet's box, including each floor speaker's visible feet without raising its cabinet top.
        private static void WalnutCabinet(int kind, out Vector3 center, out Vector3 size)
        {
            center = RadioDevice.Center(kind);
            size = RadioDevice.Size(kind);
            float footDepth = kind == DeviceKind.WalnutSpeaker ? .012f : kind == DeviceKind.WalnutWoofer ? .015f : 0f;
            center.y -= footDepth * .5f;
            size.y += footDepth;
        }

        private void AddControls()
        {
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
