using System;
using System.Collections.Generic;
using System.Reflection;
using HarmonyLib;
using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed partial class RadioWorldService
    {
        private readonly List<KeyValuePair<MethodInfo, MethodInfo>> patches = new List<KeyValuePair<MethodInfo, MethodInfo>>();

        private void InstallPatches()
        {
            Patch(typeof(SaveLoadManager), "SaveModData", nameof(SavePrefix), true);
            Patch(typeof(SaveLoadManager), "LoadGame", nameof(LoadPrefix), true);
            Patch(typeof(SaveLoadManager), "LoadModData", nameof(LoadPostfix), false);
            Patch(typeof(SaveablePrefab), "Load", nameof(PrefabLoadPrefix), true);
            Patch(typeof(SaveablePrefab), "Load", nameof(PrefabPostfix), false);
            Patch(typeof(SaveablePrefab), "PrepareSaveData", nameof(CapturePrefix), true);
            PatchDirectory();
            TryPatchControlHover();
            TryPatchHeldItemControlTarget();
            TryPatchHammerNail();
            TryPatchNativeHooks();
            TryPatchHouseTriggers();
            TryPatchInventoryControls();
        }

        private void Patch(Type type, string name, string callback, bool prefix)
        {
            MethodInfo target = AccessTools.Method(type, name);
            MethodInfo patch = AccessTools.Method(typeof(RadioWorldService), callback);
            if (target == null || patch == null || BusyField == null)
                throw new MissingMethodException("Native radio save compatibility check failed");
            harmony.Patch(target, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
        }

        private void PatchDirectory()
        {
            var start = AccessTools.DeclaredMethod(typeof(PrefabsDirectory), "Start");
            if (start == null) throw new MissingMethodException("Native item directory lifecycle changed");
            var directoryPatch = AccessTools.Method(typeof(RadioWorldService), nameof(DirectoryPrefix));
            harmony.Patch(start, prefix: new HarmonyMethod(directoryPatch) { priority = Priority.First });
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(start, directoryPatch));
        }

        private void TryPatchControlHover()
        {
            try
            {
                var target = typeof(LookUI).GetMethod("ShowLookText", BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                    null, new[] { typeof(GoPointerButton) }, null);
                if (target == null || target.ReturnType != typeof(void) || !RadioControlHover.Compatible)
                    throw new MissingMethodException("Native control hint layout changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(ControlHoverPostfix));
                harmony.Patch(target, postfix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
            }
            catch (Exception error) { warn("Radio control hint cleanup unavailable. Native hints remain visible. " + error.Message); }
        }

        private void TryPatchHeldItemControlTarget()
        {
            var pointedAt = typeof(GoPointer).GetField("pointedAtButton", BindingFlags.Instance | BindingFlags.NonPublic);
            var lookDistance = typeof(GoPointer).GetField("currentLookDistance", BindingFlags.Instance | BindingFlags.NonPublic);
            bool pointerFields = pointedAt?.FieldType == typeof(GoPointerButton) && lookDistance?.FieldType == typeof(float);
            try
            {
                var lateUpdate = typeof(GoPointer).GetMethod("LateUpdate", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                if (lateUpdate == null || lateUpdate.ReturnType != typeof(void) || !pointerFields)
                    throw new MissingMethodException("Native pointer target layout changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(HeldItemControlClearPrefix));
                harmony.Patch(lateUpdate, prefix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(lateUpdate, patch));
            }
            catch (Exception error) { warn("Radio held-item pointer cleanup unavailable. " + error.Message); }

            try
            {
                var raycast = typeof(GoPointer).GetMethod("DoRaycast", BindingFlags.Instance | BindingFlags.NonPublic,
                    null, Type.EmptyTypes, null);
                var hit = typeof(GoPointer).GetField("hit", BindingFlags.Instance | BindingFlags.NonPublic);
                if (raycast == null || raycast.ReturnType != typeof(void) || !pointerFields || hit?.FieldType != typeof(RaycastHit))
                    throw new MissingMethodException("Native pointer raycast layout changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(ControlRaycastPostfix));
                var orderedPatch = new HarmonyMethod(patch) { after = new[] { "com.dizzy.sailwind.fixes" } };
                harmony.Patch(raycast, postfix: orderedPatch);
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(raycast, patch));
            }
            catch (Exception error) { warn("Radio pointer target correction unavailable. " + error.Message); }
        }

        private void TryPatchHammerNail()
        {
            try
            {
                var target = typeof(ShipItemHammer).GetMethod("CanNail", BindingFlags.Public | BindingFlags.Static,
                    null, new[] { typeof(ShipItem) }, null);
                if (target == null || target.ReturnType != typeof(bool))
                    throw new MissingMethodException("Native hammer item check changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(HammerCanNailPostfix));
                harmony.Patch(target, postfix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
            }
            catch (Exception error) { warn("Radio hammer locking unavailable. " + error.Message); }
        }

        private void TryPatchNativeHooks()
        {
            HookCompatible = InstallGroup("Radio hook placement unavailable on this game version.", () =>
            {
                if (!RadioNativeHooks.Compatible)
                    throw new MissingFieldException("Native HangableItem.currentHook contract changed");
                var target = typeof(ShipItem).GetMethod("AllowOnItemClick", BindingFlags.Instance | BindingFlags.Public,
                    null, new[] { typeof(GoPointerButton) }, null);
                if (target == null || target.ReturnType != typeof(bool))
                    throw new MissingMethodException("Native hook target check changed");
                var patch = AccessTools.Method(typeof(RadioWorldService), nameof(HookTargetPostfix));
                harmony.Patch(target, postfix: new HarmonyMethod(patch));
                patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
                PatchNativeHook(typeof(ShipItem), "OnPickup", Type.EmptyTypes, nameof(HookReleasePostfix), false);
                PatchNativeHook(typeof(ShipItem), "OnEnterInventory", Type.EmptyTypes, nameof(HookInventoryPostfix), false);
                PatchNativeHook(typeof(HangableItem), "LateUpdate", Type.EmptyTypes, nameof(HookPositionPrefix), true);
                PatchNativeHook(typeof(HangableItem), "LateUpdate", Type.EmptyTypes, nameof(HookPositionPostfix), false);
            });
        }

        private void TryPatchHouseTriggers()
        {
            InstallGroup("Radio house-trigger cleanup unavailable.", () =>
            {
                PatchNativeHook(typeof(ShipItem), "OnTriggerEnter", new[] { typeof(Collider) }, nameof(HouseTriggerPrefix), true);
                PatchNativeHook(typeof(ShipItem), "OnTriggerExit", new[] { typeof(Collider) }, nameof(HouseTriggerPrefix), true);
            });
        }

        private void TryPatchInventoryControls()
        {
            InstallGroup("Radio inventory control cleanup unavailable. Slotted devices may join boats.", () =>
            {
                PatchNativeHook(typeof(ItemRigidbody), "EnterInventorySlot", new[] { typeof(Transform) }, nameof(InventoryEnterPostfix), false);
                PatchNativeHook(typeof(ItemRigidbody), "ExitInventorySlot", Type.EmptyTypes, nameof(InventoryExitPostfix), false);
            });
        }

        // A group is all or nothing. A partial failure removes the patches it already installed.
        private bool InstallGroup(string unavailableWarning, Action install)
        {
            int first = patches.Count;
            try
            {
                install();
                return true;
            }
            catch (Exception error)
            {
                for (int i = patches.Count - 1; i >= first; i--)
                {
                    harmony.Unpatch(patches[i].Key, patches[i].Value);
                    patches.RemoveAt(i);
                }
                warn(unavailableWarning + " " + error.Message);
                return false;
            }
        }

        private void PatchNativeHook(Type type, string name, Type[] arguments, string callback, bool prefix)
        {
            var target = type.GetMethod(name, BindingFlags.Instance | BindingFlags.Public | BindingFlags.NonPublic,
                null, arguments, null);
            var patch = AccessTools.Method(typeof(RadioWorldService), callback);
            if (target == null || target.ReturnType != typeof(void) || patch == null)
                throw new MissingMethodException("Native radio hook contract changed: " + type.Name + "." + name);
            harmony.Patch(target, prefix ? new HarmonyMethod(patch) : null, prefix ? null : new HarmonyMethod(patch));
            patches.Add(new KeyValuePair<MethodInfo, MethodInfo>(target, patch));
        }

        private static void DirectoryPrefix(PrefabsDirectory __instance) => Guard(() => active.EnsureTemplates(__instance));

        private static void HammerCanNailPostfix(ShipItem item, ref bool __result)
        {
            if (__result || !item || !item.sold || active == null || active.disposed)
                return;
            var radio = item.GetComponent<RadioItemController>();
            if (radio && radio.State != null && radio.State.Role == DeviceRole.Radio && active.items.Contains(radio))
                __result = true;
        }

        private static void HookTargetPostfix(ShipItem __instance, GoPointerButton lookedAtButton, ref bool __result)
        {
            if (__result || active == null || active.disposed || !HookCompatible || !__instance || !__instance.sold)
                return;
            // A HangableItem handles attachment, but plain ShipItem does not grant the
            // pointer permission to click hooks. Match ShipItemHangable's target rule.
            // Native hook click handling still owns occupied-hook rejection and attachment.
            if (RadioNativeHooks.AllowsHookTarget(__instance, lookedAtButton)) __result = true;
        }

        private static void HookReleasePostfix(ShipItem __instance) => Guard(() => { RadioNativeHooks.Release(__instance); });
        private static void HookInventoryPostfix(ShipItem __instance) => Guard(() => RadioNativeHooks.EnterInventory(__instance));
        private static void InventoryEnterPostfix(ItemRigidbody __instance) => Guard(() => RadioNativeHooks.InventorySlotChanged(__instance, true));
        private static void InventoryExitPostfix(ItemRigidbody __instance) => Guard(() => RadioNativeHooks.InventorySlotChanged(__instance, false));
        private static void HookPositionPrefix(HangableItem __instance) => Guard(() => RadioNativeHooks.RejectPhantomHook(__instance));
        private static void HookPositionPostfix(HangableItem __instance) => Guard(() => RadioNativeHooks.PositionBelowHook(__instance));
        private static bool HouseTriggerPrefix(ShipItem __instance, Collider other)
        {
            if (active == null || active.disposed) return true;
            try
            {
                if (RadioNativeHooks.AllowHouseTrigger(__instance, other)) return true;
                if (!active.malformedHouseReported)
                {
                    active.malformedHouseReported = true;
                    active.warn("Radio ignored a House trigger without a native SaveableObject");
                }
                return false;
            }
            catch (Exception error)
            {
                active.warn("Radio house-trigger compatibility check failed: " + error.Message);
                return true;
            }
        }

        // Exceptions must never interrupt the game's save/load call or suppress its original body.
        private static void SavePrefix() => Guard(() => active.Save());
        private static void LoadPrefix(SaveLoadManager __instance) => Guard(() => active.BeginLoad(__instance));
        private static void LoadPostfix() => Guard(() => active.ReadNativeData());
        private static void PrefabLoadPrefix(SaveablePrefab __instance) => Guard(() =>
        {
            active.loadingPrefabs.Add(__instance);
            var controller = __instance.GetComponent<RadioItemController>();
            if (controller) active.pendingBindings.Remove(controller);
        });
        private static void PrefabPostfix(SaveablePrefab __instance) => Guard(() => { active.loadingPrefabs.Remove(__instance); active.Restore(__instance); });
        private static void CapturePrefix(SaveablePrefab __instance) => Guard(() => active.Capture(__instance));
        private static void ControlHoverPostfix(LookUI __instance, GoPointerButton button) => Guard(() => RadioControlHover.ClearPickupHint(__instance, button));
        private static void HeldItemControlClearPrefix(GoPointer __instance,
            ref GoPointerButton ___pointedAtButton, ref float ___currentLookDistance)
        {
            if (active == null || active.disposed) return;
            try { RadioHeldItemTarget.Clear(__instance, ref ___pointedAtButton, ref ___currentLookDistance); }
            catch (Exception ex) { active.warn("Radio held-item pointer cleanup failed: " + ex.Message); }
        }
        private static void ControlRaycastPostfix(GoPointer __instance, RaycastHit ___hit,
            ref GoPointerButton ___pointedAtButton, ref float ___currentLookDistance)
        {
            if (active == null || active.disposed) return;
            try { RadioHeldItemTarget.Restore(__instance, ___hit, ref ___pointedAtButton, ref ___currentLookDistance); }
            catch (Exception ex) { active.warn("Radio pointer target correction failed: " + ex.Message); }
        }

        private static void Guard(Action action)
        {
            if (active == null || active.disposed)
                return;
            try
            {
                action();
            }
            catch (Exception ex) { active.warn("Radio native integration failed: " + ex.Message); }
        }
    }
}
