using UnityEngine;

namespace SailwindRadio.UI
{
    // Captures only the native input state owned while this menu is open. Known native
    // transition hooks close us before taking control. Fallbacks preserve those owners.
    internal sealed class MenuInputLease
    {
        private UnityEngine.Object character, controller, crosshair;
        private bool crosshairActive, mouseLook, cursorVisible;
        private CursorLockMode cursorLock;
        internal bool Owned { get; private set; }
        internal static bool GameplayAvailable => GameState.playing && !GameState.currentlyLoading &&
            GameState.loadingScenes==0 && !GameState.justStarted && !GameState.sleeping && !GameState.inBed &&
            !BoatCamera.on && Time.timeScale>0;

        internal bool Acquire()
        {
            if (Owned) return true;
            if (!GameplayAvailable || !Application.isFocused || GameState.inCursorMenu ||
                !Refs.charController || !Refs.ovrController || !Refs.mouseCrosshair ||
                !Refs.charController.enabled || !Refs.ovrController.enabled) return false;
            character=Refs.charController;
            controller=Refs.ovrController;
            crosshair=Refs.mouseCrosshair;
            crosshairActive=Refs.mouseCrosshair.activeSelf;
            mouseLook=MouseLook.MouseLookIsEnabled();
            cursorVisible=Cursor.visible;
            cursorLock=Cursor.lockState;
            Owned=true;
            MouseLook.ToggleMouseLook(false);
            MouseLook.ToggleMouseLookAndCursor(false);
            Refs.charController.enabled=false;
            Refs.ovrController.enabled=false;
            Refs.mouseCrosshair.SetActive(false);
            return true;
        }

        internal void Release()
        {
            if (!Owned) return;
            Owned=false;
            bool nativeOwner=GameState.currentlyLoading || !GameState.playing || GameState.sleeping || GameState.inBed;
            if (!nativeOwner)
            {
                if (Refs.charController && Refs.charController==character) Refs.charController.enabled=true;
                if (Refs.ovrController && Refs.ovrController==controller) Refs.ovrController.enabled=true;
                if (Refs.charController==character && Refs.ovrController==controller)
                {
                    MouseLook.ToggleMouseLook(mouseLook);
                    MouseLook.ToggleMouseLookAndCursor(true);
                    Cursor.visible=cursorVisible;
                    Cursor.lockState=cursorLock;
                    if (!BoatCamera.on && Refs.mouseCrosshair && Refs.mouseCrosshair==crosshair) Refs.mouseCrosshair.SetActive(crosshairActive);
                }
            }
            character=controller=crosshair=null;
        }
    }
}
