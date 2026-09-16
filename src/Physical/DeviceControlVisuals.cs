namespace SailwindRadio.Physical
{
    internal static class DeviceControlVisuals
    {
        internal static float KnobAngle(float level)
        {
            if (float.IsNaN(level) || float.IsInfinity(level))
                level = 0;
            return 135f - 270f * System.Math.Max(0f, System.Math.Min(1f, level));
        }
        internal static bool IsLit(RadioState state, string control, bool momentary)
        {
            bool enabled = state.Kind == 0 ? state.Powered : state.SpeakerEnabled;
            return enabled && (control == "power" || control == "playpause" && !state.Paused || control == "shuffle" && state.Shuffle || momentary);
        }
    }
}
