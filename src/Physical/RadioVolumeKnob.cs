using UnityEngine;

namespace SailwindRadio.Physical
{
    public sealed class RadioVolumeKnob : GoPointerButton
    {
        public RadioItemController Radio;
        private UnityEngine.Object capturedCharacter;
        private UnityEngine.Object capturedController;
        private UnityEngine.Object capturedCrosshair;
        private bool initialCharacterEnabled;
        private bool initialControllerEnabled;
        private bool initialCrosshairActive;

        private bool CanInteract => Radio && Radio.IsPlacedForControls && Application.isFocused && GameState.playing &&
            !GameState.currentlyLoading && GameState.loadingScenes == 0 && !GameState.sleeping && !GameState.inBed && !GameState.inCursorMenu &&
            Time.timeScale > 0 && !BoatCamera.on;

        private bool WithinNativeReach(GoPointer pointer)
        {
            var collider = GetComponent<Collider>();
            return pointer && collider && Vector3.Distance(pointer.transform.position,
                collider.ClosestPoint(pointer.transform.position)) <= 1.8f;
        }

        public override void OnActivate(GoPointer activatingPointer)
        {
            if (!CanInteract || !WithinNativeReach(activatingPointer) || activatingPointer.GetHeldItem() ||
                !Refs.ovrController || !Refs.charController || !Refs.mouseCrosshair ||
                !Refs.ovrController.enabled || !Refs.charController.enabled) return;
            capturedCharacter = Refs.charController;
            capturedController = Refs.ovrController;
            capturedCrosshair = Refs.mouseCrosshair;
            initialCharacterEnabled = Refs.charController.enabled;
            initialControllerEnabled = Refs.ovrController.enabled;
            initialCrosshairActive = Refs.mouseCrosshair.activeSelf;
            // Native GoPointer consumes the next main click to release its sticky target, just like a rope winch.
            StickyClick(activatingPointer);
        }

        public override void ExtraLateUpdate()
        {
            if (stickyClickedBy && (!CanInteract || !WithinNativeReach(stickyClickedBy))) ReleaseInteraction();
            if (!Radio) return;
            if (stickyClickedBy && CanInteract)
                Radio.State.Volume = Mathf.Clamp01(Radio.State.Volume + GameInput.GetScrollAxis() * .25f);
            lookText = "Radio volume " + Mathf.RoundToInt(Radio.State.Volume * 100) + "%  " +
                (stickyClickedBy ? "Scroll to adjust, click to release" : "Click to adjust");
        }

        public void ReleaseInteraction()
        {
            if (!stickyClickedBy) return;
            // Only native states that actually own the controller flags retain them. Additive island
            // loading, a timescale-only pause and cursor menus do not own these flags. Keeping our own
            // disabled values across those transitions would permanently strand movement after release.
            bool preserveControllers = GameState.sleeping || GameState.inBed || GameState.currentlyLoading || !GameState.playing;
            // BoatCamera owns crosshair visibility, but only changes MouseLook rather than these controllers.
            bool preserveCrosshair = preserveControllers || BoatCamera.on;
            var pointer = stickyClickedBy;
            try
            {
                // Forced exit also works after the native sound UI has been destroyed. A normal second
                // click still uses GoPointerButton.UnStickyClick and its normal release sound.
                pointer.UnStickyClick();
            }
            finally
            {
                stickyClickedBy = null;
                if (!preserveControllers)
                {
                    if (Refs.charController && Refs.charController == capturedCharacter)
                        Refs.charController.enabled = initialCharacterEnabled;
                    if (Refs.ovrController && Refs.ovrController == capturedController)
                        Refs.ovrController.enabled = initialControllerEnabled;
                }
                if (!preserveCrosshair && Refs.mouseCrosshair && Refs.mouseCrosshair == capturedCrosshair)
                    Refs.mouseCrosshair.SetActive(initialCrosshairActive);
            }
        }

        private void OnDisable() { ReleaseInteraction(); }
        private void OnDestroy() { ReleaseInteraction(); }
        private void OnApplicationFocus(bool focused) { if (!focused) ReleaseInteraction(); }
        private void OnApplicationPause(bool paused) { if (paused) ReleaseInteraction(); }
    }
}
