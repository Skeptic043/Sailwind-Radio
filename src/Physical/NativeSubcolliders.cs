using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindRadio.Physical
{
    // Native ItemRigidbody.Start copies each child of its item tagged ItemSubcollider into its physics body, as a solid
    // box on layer 2, and keeps the copy in a list its FixedUpdate turns into triggers while the item is handled.
    // Boxes added after Start, such as a stacked device's, are copied the same way through its private
    // CreateSubcollider, and leave that list when removed.
    internal static class NativeSubcolliders
    {
        private static readonly MethodInfo Create = AccessTools.Method(typeof(ItemRigidbody), "CreateSubcollider", new[] { typeof(Transform) });
        private static readonly FieldInfo Copies = AccessTools.Field(typeof(ItemRigidbody), "subcolliders");
        internal static bool Compatible => Create != null && Copies?.FieldType == typeof(List<Collider>);

        // Native Start adds the Rigidbody first, so a body without one has yet to copy the item's tagged children.
        internal static void Add(ItemRigidbody body, Transform box)
        {
            if (Compatible && body && body.GetBody())
                Create.Invoke(body, new object[] { box });
        }

        // Crate insertion stops FixedUpdate before it updates these copies, so they keep their current state. A held
        // insertion leaves triggers. A load or scripted pack leaves solid boxes that push the crate.
        internal static void MakeTriggers(ItemRigidbody body)
        {
            if (!Compatible || !body || !(Copies.GetValue(body) is List<Collider> copies)) return;
            foreach (var copy in copies)
                if (copy) copy.isTrigger = true;
        }

        // The copy is the instantiated box, a direct child of the body named after it.
        internal static void Remove(ItemRigidbody body, Transform box)
        {
            var copy = body && box ? body.transform.Find(box.name + "(Clone)") : null;
            if (!copy) return;
            if (Compatible) (Copies.GetValue(body) as List<Collider>)?.Remove(copy.GetComponent<Collider>());
            Object.Destroy(copy.gameObject);
        }
    }
}
