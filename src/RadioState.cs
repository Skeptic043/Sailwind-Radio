using System;

namespace SailwindRadio
{
    [Serializable]
    public sealed class RadioState
    {
        public string TrackPath = "";
        public double PositionSeconds;
        public bool Powered;
        public bool Paused;
        public float Volume = 0.5f;
    }
}
