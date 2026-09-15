using System;
using System.Collections.Generic;
using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed class RadioItemController : MonoBehaviour
    {
        public RadioState State { get; private set; }
        public int InstanceId { get; private set; }
        public Action CapturePosition { get; set; }
        private ShipItem item;
        private RadioWorldService owner;
        private readonly List<UnityEngine.Object> assets = new List<UnityEngine.Object>();
        private readonly List<Transform> controls = new List<Transform>();
        private TextMesh display;
        private RadioVolumeKnob volumeKnob;
        private string trackLabel = "";
        private string status = "Off";
        private string renderedTrack;
        private string renderedStatus;
        private int renderedVolume = -1;
        private bool renderedPower;

        public Vector3 VisualAudioPosition
        {
            get
            {
                // Native inventory transforms are UI space. Require actual slot ownership, never just layer 5.
                var slots = GPButtonInventorySlot.inventorySlots;
                if (slots != null)
                    foreach (var slot in slots)
                        if (slot && slot.currentItem == item && Refs.observerMirror)
                            return Refs.observerMirror.transform.position;
                if (item && item.held && Refs.observerMirror) return Refs.observerMirror.transform.position;
                // A hidden crate item follows the crate's visual item, not the player's inventory.
                if (item && item.itemRigidbodyC)
                {
                    Transform box = item.itemRigidbodyC.GetCurrentBox();
                    if (box)
                    {
                        var body = box.GetComponentInParent<ItemRigidbody>();
                        if (body && body.GetShipItem()) return body.GetShipItem().transform.position;
                        return box.position;
                    }
                }
                return transform.position;
            }
        }

        internal void Initialize(RadioWorldService service, RadioState state)
        {
            owner = service;
            item = GetComponent<ShipItem>();
            InstanceId = GetComponent<SaveablePrefab>().instanceId;
            State = state;
            item.name = "Sailwind Radio";
            item.description = "Place the radio and click its power button. Click the volume knob, scroll to adjust, then click to release";
            item.big = false;
            item.mass = 0.5f;
            item.value = 0;
            BuildPlaceholder();
        }

        public void TogglePower()
        {
            CapturePosition?.Invoke();
            State.Powered = !State.Powered;
            if (State.Powered) State.Paused = false;
        }

        public void SetPlaybackDisplay(string track, string playbackStatus)
        {
            trackLabel = track ?? "";
            status = playbackStatus ?? "";
        }

        internal bool IsPlacedForControls => item && !item.held && gameObject.activeInHierarchy &&
            gameObject.layer != 5 && item.itemRigidbodyC && !item.itemRigidbodyC.GetCurrentBox() && item.GetCurrentInventorySlot() < 0;

        public void ReleaseControls()
        {
            if (volumeKnob) volumeKnob.ReleaseInteraction();
        }

        internal void Tick()
        {
            foreach (Transform child in controls)
                if (child) child.gameObject.layer = gameObject.layer;
            int volume = Mathf.RoundToInt(State.Volume * 100);
            if (display && (renderedTrack != trackLabel || renderedStatus != status || renderedPower != State.Powered || renderedVolume != volume))
            {
                renderedTrack = trackLabel;
                renderedStatus = status;
                renderedPower = State.Powered;
                renderedVolume = volume;
                string title = trackLabel.Replace('\n', ' ').Replace('\r', ' ');
                if (title.Length > 25) title = title.Substring(0, 22) + "...";
                display.text = "SAILWIND RADIO\n" + (State.Powered ? status : "Off") + "  " + volume + "%\n" + title;
            }
        }

        private void BuildPlaceholder()
        {
            // Only this runtime instance is modified. The donor prefab and its directory entry stay intact.
            foreach (Transform child in transform) child.gameObject.SetActive(false);
            var temporary = GameObject.CreatePrimitive(PrimitiveType.Cube);
            temporary.SetActive(false);
            Mesh mesh = Instantiate(temporary.GetComponent<MeshFilter>().sharedMesh);
            var vertices = mesh.vertices;
            for (int i = 0; i < vertices.Length; i++) vertices[i] = Vector3.Scale(vertices[i], new Vector3(.6f, .38f, .2f)) + new Vector3(0, .19f, 0);
            mesh.vertices = vertices;
            mesh.RecalculateBounds();
            GetComponent<MeshFilter>().sharedMesh = mesh;
            assets.Add(mesh);
            Destroy(temporary);
            var bodyMaterial = MakeMaterial(new Color(.20f, .105f, .045f));
            GetComponent<Renderer>().sharedMaterials = new[] { bodyMaterial };
            var box = GetComponent<BoxCollider>();
            box.center = new Vector3(0, .19f, 0);
            box.size = new Vector3(.6f, .38f, .2f);
            // Loaded objects may already have their separate native collision body.
            if (item.itemRigidbodyC)
            {
                var physicsBox = item.itemRigidbodyC.GetComponent<BoxCollider>();
                if (physicsBox) { physicsBox.center = box.center; physicsBox.size = box.size; }
            }
            Part("Speaker grille", new Vector3(-.12f, .21f, -.106f), new Vector3(.25f, .25f, .018f), new Color(.07f, .065f, .055f), false);
            var power = Part("Power", new Vector3(.17f, .14f, -.125f), new Vector3(.075f, .075f, .045f), new Color(.65f, .23f, .10f), true);
            power.AddComponent<RadioPowerButton>().Radio = this;
            var volume = Part("Volume", new Vector3(.17f, .26f, -.125f), new Vector3(.09f, .09f, .045f), new Color(.65f, .55f, .33f), true);
            volumeKnob = volume.AddComponent<RadioVolumeKnob>();
            volumeKnob.Radio = this;
            var label = new GameObject("Radio label");
            label.transform.SetParent(transform, false);
            label.transform.localPosition = new Vector3(0, .365f, -.112f);
            label.transform.localRotation = Quaternion.identity;
            display = label.AddComponent<TextMesh>();
            display.font = Resources.GetBuiltinResource<Font>("Arial.ttf");
            label.GetComponent<Renderer>().sharedMaterial = display.font.material;
            display.anchor = TextAnchor.UpperCenter;
            display.alignment = TextAlignment.Center;
            display.characterSize = .009f;
            display.fontSize = 40;
            display.color = new Color(.95f, .86f, .65f);
            controls.Add(label.transform);
        }

        private Material MakeMaterial(Color color)
        {
            Shader shader = Shader.Find("Standard") ?? GetComponent<Renderer>().sharedMaterial.shader;
            var material = new Material(shader) { color = color };
            assets.Add(material);
            return material;
        }

        private GameObject Part(string partName, Vector3 position, Vector3 scale, Color color, bool interactive)
        {
            var part = GameObject.CreatePrimitive(PrimitiveType.Cube);
            part.name = partName;
            part.transform.SetParent(transform, false);
            part.transform.localPosition = position;
            part.transform.localScale = scale;
            part.GetComponent<Renderer>().sharedMaterial = MakeMaterial(color);
            if (!interactive) Destroy(part.GetComponent<Collider>());
            else part.GetComponent<Collider>().isTrigger = true;
            controls.Add(part.transform);
            return part;
        }

        private void OnDestroy()
        {
            try { ReleaseControls(); CapturePosition?.Invoke(); }
            catch (Exception ex) { owner?.Report("Could not capture final radio position: " + ex.Message); }
            finally
            {
                try { owner?.Forget(this); }
                finally { foreach (var asset in assets) if (asset) Destroy(asset); }
            }
        }
    }

    public sealed class RadioPowerButton : GoPointerButton
    {
        public RadioItemController Radio;
        public override void OnActivate() { if (Radio) Radio.TogglePower(); }
        public override void ExtraLateUpdate() { if (Radio) lookText = Radio.State.Powered ? "Turn radio off" : "Turn radio on"; }
    }

}
