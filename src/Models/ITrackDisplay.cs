using System;

namespace SailwindRadio.Models
{
    // The walnut dot matrix and the regional rows, driven alike by the radio controller.
    internal interface ITrackDisplay : IDisposable
    {
        void SetLayer(int layer);
        void SetMetadata(bool powered, string title, string artist, string album);
        void Tick();
    }
}
