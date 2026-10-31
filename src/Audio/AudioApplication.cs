using System;

namespace SailwindRadio.Audio
{
    // A desktop application a radio can play, known by its program file because process IDs change
    // between launches. Browsers and Spotify play from child processes, so audio is taken from the
    // whole tree under the application's top process.
    public sealed class AudioApplication
    {
        public readonly string Label;
        internal readonly string Program;
        internal AudioApplication(string label, string program) { Label = label; Program = program; }
        internal bool Same(AudioApplication other) => other != null && String.Equals(Program, other.Program, StringComparison.OrdinalIgnoreCase);
    }
}
