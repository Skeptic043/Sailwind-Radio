using System;
using System.Collections.Generic;

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
        // The device stacked on top of this one. It has no native item of its own while stacked. Stacking and
        // unstacking move this same state object, so a Radio keeps its music, folders and power.
        public RadioState Stacked;
        public DeviceRole Role => DeviceKind.Role(Kind);
        // The devices stacked above this one, lowest first.
        public List<RadioState> Above()
        {
            var above = new List<RadioState>();
            for (var stacked = Stacked; stacked != null; stacked = stacked.Stacked)
                above.Add(stacked);
            return above;
        }
        public static RadioState NewDevice(int kind) =>
            new RadioState { Kind = kind, Volume = DeviceKind.Role(kind) == DeviceRole.Radio ? .5f : .75f, TrackPath = "" };
    }
}
