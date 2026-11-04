using System;
using UnityEngine;

namespace SailwindRadio.Audio
{
    /// <summary>The first filter on every radio output. While its radio plays an application, the output plays a
    /// constant carrier clip and this replaces it with application audio. Unity pans the carrier before this
    /// filter and applies volume after it, and the output's own filters follow it, so an application gets
    /// every effect a music file gets with only Unity's mixing delay. Otherwise it leaves audio untouched.</summary>
    public sealed class RadioApplicationInput : MonoBehaviour
    {
        internal volatile ApplicationRing.Reader Reader;
        internal volatile bool Spatial;

        private void OnAudioFilterRead(float[] data, int channels)
        {
            ApplicationRing.Reader reader = Reader;
            // Unity's DSP clock names the same frame to every output's filter in one mix.
            if (reader != null) reader.Mix(data, channels, Spatial, (long)Math.Round(AudioSettings.dspTime * reader.Ring.SampleRate));
        }
    }
}
