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
            if (reader != null) reader.Mix(data, channels, Spatial);
        }
    }
}
