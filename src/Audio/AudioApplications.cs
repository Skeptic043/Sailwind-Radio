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

    // Parses and matches the pipe-separated ExcludedApps list. Plain file names such as Discord.exe match any
    // install path so auto-updating apps stay hidden. Entries containing a slash match that specific full path.
    internal static class ExcludedApplications
    {
        internal static List<string> Parse(string setting)
        {
            var result = new List<string>();
            if (String.IsNullOrWhiteSpace(setting)) return result;
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            foreach (string raw in setting.Split('|'))
            {
                string entry = raw.Trim();
                if (entry.Length > 0 && seen.Add(entry)) result.Add(entry);
            }
            return result;
        }

        internal static string Format(IEnumerable<string> entries)
        {
            if (entries == null) return "";
            var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            var clean = new List<string>();
            foreach (string raw in entries)
            {
                if (raw == null) continue;
                string entry = raw.Trim();
                if (entry.Length > 0 && seen.Add(entry)) clean.Add(entry);
            }
            return String.Join(" | ", clean.ToArray());
        }

        internal static string Key(string program)
        {
            if (String.IsNullOrWhiteSpace(program)) return "";
            string trimmed = program.Trim();
            string name = Path.GetFileName(trimmed);
            return String.IsNullOrEmpty(name) ? trimmed : name;
        }

        internal static bool Matches(string entry, string program)
        {
            if (String.IsNullOrWhiteSpace(entry) || String.IsNullOrWhiteSpace(program)) return false;
            string rule = entry.Trim();
            string target = program.Trim();
            if (rule.IndexOf('\\') >= 0 || rule.IndexOf('/') >= 0)
                return String.Equals(NormalizePath(rule), NormalizePath(target), StringComparison.OrdinalIgnoreCase);
            string fileName = Path.GetFileName(target);
            if (String.Equals(rule, fileName, StringComparison.OrdinalIgnoreCase)) return true;
            string stem = Path.GetFileNameWithoutExtension(target);
            return !String.IsNullOrEmpty(stem) && String.Equals(rule, stem, StringComparison.OrdinalIgnoreCase);
        }

        internal static bool IsExcluded(string setting, string program)
        {
            if (String.IsNullOrWhiteSpace(setting) || String.IsNullOrWhiteSpace(program)) return false;
            foreach (string entry in Parse(setting))
                if (Matches(entry, program)) return true;
            return false;
        }

        internal static string Add(string setting, string program)
        {
            var entries = Parse(setting);
            string key = Key(program);
            if (key.Length == 0) return Format(entries);
            foreach (string existing in entries)
                if (Matches(existing, program)) return Format(entries);
            entries.Add(key);
            return Format(entries);
        }

        internal static string Remove(string setting, string entry)
        {
            var entries = Parse(setting);
            if (String.IsNullOrWhiteSpace(entry)) return Format(entries);
            string target = entry.Trim();
            entries.RemoveAll(existing => String.Equals(existing, target, StringComparison.OrdinalIgnoreCase));
            return Format(entries);
        }

        private static string NormalizePath(string path)
        {
            try { return Path.GetFullPath(path).TrimEnd('\\', '/'); }
            catch (Exception) { return path.Replace('/', '\\').TrimEnd('\\'); }
        }
    }
}
