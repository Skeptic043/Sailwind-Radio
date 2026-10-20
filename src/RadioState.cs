using System;

namespace SailwindRadio
{
    [Serializable]
    public sealed class RadioState
    {
        public int Kind;
        public float Bass = .5f;
        public float LocalVolume = 1f;
        public bool SpeakerEnabled = true;
        public bool Shuffle;
        public bool CollectionsInitialized;
        public string[] SelectedCollections = Array.Empty<string>();
        public string TrackPath = "";
        public double PositionSeconds;
        public bool Powered;
        public bool Paused;
        public float Volume = 0.5f;
        public DeviceRole Role => DeviceKind.Role(Kind);
        public static RadioState NewDevice(int kind) =>
            new RadioState { Kind = kind, Volume = DeviceKind.Role(kind) == DeviceRole.Radio ? .5f : .75f, TrackPath = "" };
    }
}
