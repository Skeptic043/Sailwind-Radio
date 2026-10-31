using System;
using System.Collections.Generic;
using SailwindRadio.Models;
using SailwindRadio.Playback;
using SailwindRadio.Shops;
using UnityEngine;

namespace SailwindRadio.Physical
{
    // Speakers and Woofers of one family stack in any order, and the family's Radio can crown the stack. Nothing
    // stacks on a Radio. The base stays the native item with its own identity. Each device above it is a controller
    // on its own child of the base, its state chained from the base's saved state. It owns no native item, so a
    // stacked Radio can never hang from a hook, and its collision boxes join the base's physics body.
    public sealed partial class RadioItemController
    {
        private RadioItemController stackBase;
        // On a base, the devices above it, lowest first.
        private readonly List<RadioItemController> stack = new List<RadioItemController>();
        // On a stacked device, its collision boxes as tagged children of the base, copied into the base's body.
        private readonly List<GameObject> stackBoxes = new List<GameObject>();
        // A stacked speaker's output key, apart from every native ID and the stacked Radio's arbiter key.
        private static int nextStackedSpeakerId = int.MinValue;
        private int stackedSpeakerId;
        private static int nextStackBoxId;
        private bool physicsFitted;
        private const float MaxDepenetration = 1f;
        internal IReadOnlyList<RadioItemController> Stack => stack;
        internal RadioItemController StackTop => stack.Count > 0 ? stack[stack.Count - 1] : null;
        internal bool IsStackTop => stackBase;
        // The device whose saved record holds this one's state.
        internal RadioItemController RecordOwner => stackBase ? stackBase : this;

        // A stacked Radio is tracked by the arbiter under its base's stacked ID. Speakers need only an output key.
        private int StackedInstanceId => State.Role == DeviceRole.Radio ? GlobalRadioArbiter.StackedId(stackBase.InstanceId) : stackedSpeakerId;

        private void InitializeTop(RadioWorldService service, RadioItemController baseDevice, RadioState state)
        {
            owner = service;
            stackBase = baseDevice;
            item = baseDevice.item;
            State = state;
            stackedSpeakerId = nextStackedSpeakerId++;
            RefreshOwnership();
            BuildModel();
        }

        // Builds, refreshes or removes the stacked devices to match the saved chain, then refits the boxes, mass and value.
        internal void SyncStack()
        {
            if (stackBase || item == null)
                return;
            var chain = State.Above();
            int kept = 0;
            while (kept < stack.Count && kept < chain.Count && stack[kept].State.Kind == chain[kept].Kind)
            {
                stack[kept].State = chain[kept];
                kept++;
            }
            while (stack.Count > kept)
                RemoveTop();
            for (int i = kept; i < chain.Count; i++)
            {
                // The stack stays saved, so a later load or a right click can still recover the rest.
                try { BuildTop(chain[i]); }
                catch (Exception error)
                {
                    owner?.Report("Could not build a stacked device. It stays saved with its base: " + error.Message);
                    break;
                }
            }
            ApplyBoxes();
            float mass = Mass(State.Role);
            int value = RadioShopCatalog.BasePrice(State.Kind);
            foreach (var top in stack)
            {
                mass += Mass(top.State.Role);
                value += RadioShopCatalog.BasePrice(top.State.Kind);
            }
            item.mass = mass;
            // Native ItemRigidbody reads mass at Start and in UpdateMass, as crates and kettles refresh it.
            if (item.itemRigidbodyC && item.itemRigidbodyC.GetBody())
                item.itemRigidbodyC.UpdateMass();
            // A stack sells as all its devices.
            item.value = value;
        }

        private void BuildTop(RadioState state)
        {
            var below = StackTop;
            float floor = below ? below.transform.localPosition.y : 0f;
            float seat = floor + DeviceModel.StackSeat(CollisionBoxes(below ? below.State.Kind : State.Kind), CollisionBoxes(state.Kind));
            var top = new GameObject("Stacked " + RadioDevice.Name(state.Kind)) { layer = gameObject.layer };
            top.transform.SetParent(transform, false);
            top.transform.localPosition = new Vector3(0, seat, 0);
            // DeviceModel draws a cabinet body on the object it builds into, as on the native item root.
            top.AddComponent<MeshFilter>();
            var renderer = top.AddComponent<MeshRenderer>();
            var own = GetComponent<Renderer>();
            renderer.sharedMaterial = own.sharedMaterial;
            renderer.shadowCastingMode = own.shadowCastingMode;
            renderer.receiveShadows = own.receiveShadows;
            var controller = top.AddComponent<RadioItemController>();
            try { controller.InitializeTop(owner, this, state); }
            catch
            {
                Destroy(top);
                throw;
            }
            stack.Add(controller);
            foreach (var box in CollisionBoxes(state.Kind))
                controller.stackBoxes.Add(AddStackBox(controller, box.Min + top.transform.localPosition, box.Max + top.transform.localPosition));
            owner?.AddStackTop(controller);
        }

        // Like the base's own body boxes: a tagged trigger child that native physics copies as a solid box.
        private GameObject AddStackBox(RadioItemController top, Vector3 min, Vector3 max)
        {
            // A unique name, so NativeSubcolliders.Remove never finds a removed box's copy still awaiting Destroy.
            var body = new GameObject("Radio stack box " + nextStackBoxId++) { tag = "ItemSubcollider", layer = 2 };
            body.transform.SetParent(transform, false);
            var box = body.AddComponent<BoxCollider>();
            box.center = (min + max) * .5f;
            box.size = max - min;
            box.isTrigger = true;
            NativeSubcolliders.Add(item.itemRigidbodyC, body.transform);
            return body;
        }

        private void RemoveTop()
        {
            var top = StackTop;
            stack.RemoveAt(stack.Count - 1);
            foreach (var box in top.stackBoxes)
            {
                NativeSubcolliders.Remove(item.itemRigidbodyC, box.transform);
                Destroy(box);
            }
            // Destroy completes at the end of the frame. Hide the top and drop its output now.
            top.gameObject.SetActive(false);
            owner?.Forget(top);
            Destroy(top.gameObject);
        }

        // A cabinet's collision boxes. Walnut kinds have no model data, so their cabinet box stands in.
        private static List<(Vector3 Min, Vector3 Max)> CollisionBoxes(int kind)
        {
            var boxes = DeviceModel.CollisionBoxes(kind);
            if (boxes.Count == 0)
            {
                WalnutCabinet(kind, out var center, out var size);
                boxes.Add((center - size * .5f, center + size * .5f));
            }
            return boxes;
        }

        // The cabinet's largest authored solid, which lies inside it while its other solids collide on their own as
        // native subcolliders. A cabinet without solids keeps its contact box, as before solids existed.
        private static (Vector3 Min, Vector3 Max) Core(int kind)
        {
            var solids = DeviceModel.SolidBoxes(kind);
            if (solids.Count == 0)
            {
                Contact(kind, out var center, out var extent);
                return (center - extent * .5f, center + extent * .5f);
            }
            var core = solids[0];
            foreach (var box in solids)
            {
                Vector3 size = box.Max - box.Min, largest = core.Max - core.Min;
                if (size.x * size.y * size.z > largest.x * largest.y * largest.z)
                    core = box;
            }
            return core;
        }

        // The pointer box covers the stack without covering any device's controls, see DeviceModel.StackPointerBox.
        // Native physics copies a separate box, which must stay inside the base so nothing rests on empty space. The
        // stacked devices' own boxes hold them up. Native ItemRigidbody adds that box some frames after a spawn or
        // load, copying the pointer box, so Tick fits it once it exists.
        private void ApplyBoxes()
        {
            var kinds = new List<int> { State.Kind };
            var contacts = new List<(Vector3 Min, Vector3 Max)> { (contactCenter - contactSize * .5f, contactCenter + contactSize * .5f) };
            foreach (var top in stack)
            {
                Contact(top.State.Kind, out var center, out var size);
                Vector3 offset = top.transform.localPosition;
                kinds.Add(top.State.Kind);
                contacts.Add((center - size * .5f + offset, center + size * .5f + offset));
            }
            var (min, max) = DeviceModel.StackPointerBox(kinds, contacts);
            var box = GetComponent<BoxCollider>();
            box.center = (min + max) * .5f;
            box.size = max - min;
            var physicsBox = item.itemRigidbodyC ? item.itemRigidbodyC.GetComponent<BoxCollider>() : null;
            if (!physicsBox)
                return;
            var core = Core(State.Kind);
            physicsBox.center = (core.Min + core.Max) * .5f;
            physicsBox.size = core.Max - core.Min;
            // Unity 2019.1 pushes overlapping bodies apart at any speed. A tall stack tipping over an edge swings its
            // far end deep into it within one step and is flung away, so every device body caps that push near walking speed.
            item.itemRigidbodyC.GetBody().maxDepenetrationVelocity = MaxDepenetration;
            physicsFitted = true;
        }
    }
}
