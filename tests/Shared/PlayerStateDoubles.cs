// Native player and control gate statics shared by the device menu and volume knob suites.
// Each suite supplies its own UnityEngine Component, GameObject and Transform doubles.
public static class GameState
{
    public static bool playing = true, currentlyLoading, justStarted, sleeping, inBed, inCursorMenu;
    public static int loadingScenes;
    public static UnityEngine.Transform currentBoat;
}
public static class Refs
{
    public static UnityEngine.Component charController = new(), ovrController = new(), observerMirror = new();
    public static UnityEngine.GameObject mouseCrosshair = new();
}
public static class BoatCamera { public static bool on; }
