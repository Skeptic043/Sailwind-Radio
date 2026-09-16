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
    }
}
