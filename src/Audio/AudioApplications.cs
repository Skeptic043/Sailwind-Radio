using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;

namespace SailwindRadio.Audio
{
    // One start-up decision for application playback. Under Proton or Wine it is never offered.
    internal static class ApplicationAudioSupport
    {
        internal static bool Enabled { get; private set; }
        internal static string Summary { get; private set; } = "Application playback is unavailable";

        // Reads the held-volume record and starts restoring volumes a crashed run left held.
        internal static void Detect(string recordPath, Action<string> warn)
        {
            try
            {
                if (MediaFoundation.WineDetected()) { Summary = "Application playback is disabled. " + MediaFoundationSupport.WineReason; return; }
                if (!CompletionHandler.Supported) { Summary = "Application playback is unavailable. It needs 64-bit Windows"; return; }
                HeldVolumes.Configure(recordPath);
                Enabled = true;
                Summary = "Application playback is available";
                HeldVolumes.Watch();
            }
            catch (Exception error) { Summary = "Application playback is unavailable. " + error.Message; }
        }
    }

    internal static class AudioApplications
    {
        // Applications with an audio session now, one entry per program, except Sailwind itself. Worker thread.
        internal static List<AudioApplication> List()
        {
            var tree = new ProcessTree();
            int self = Process.GetCurrentProcess().Id;
            var found = new Dictionary<string, AudioApplication>(StringComparer.OrdinalIgnoreCase);
            AudioSessions.Visit(session =>
            {
                int root = tree.Root(session.ProcessId);
                // Sailwind itself, or a launcher whose tree includes it.
                if (tree.Within(self, root)) return;
                string program = tree.Program(root);
                if (program != null && !found.ContainsKey(program)) found.Add(program, new AudioApplication(Label(program), program));
            });
            var list = new List<AudioApplication>(found.Values);
            list.Sort((a, b) => String.Compare(a.Label, b.Label, StringComparison.CurrentCultureIgnoreCase));
            return list;
        }

        // The program's own description, such as Spotify or Firefox, or its file name.
        private static string Label(string program)
        {
            try
            {
                string description = FileVersionInfo.GetVersionInfo(program).FileDescription;
                if (!String.IsNullOrWhiteSpace(description)) return description.Trim();
            }
            catch (Exception) { }
            return Path.GetFileNameWithoutExtension(program);
        }
    }
}
