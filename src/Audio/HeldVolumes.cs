using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;
using System.Threading;

namespace SailwindRadio.Audio
{
    // Holds an application's Windows sessions about 80 dB down while a radio plays it, and puts them back
    // afterwards. Windows mixes in float, so the radio restores full quality from the capture. The record
    // file lists every held session with its original level, so a start after a crash can restore it. A
    // session the player has moved since is left at their level. Worker threads only, except Configure and Watch.
    internal static class HeldVolumes
    {
        internal const float Level = .0001f;
        private const int WatchMilliseconds = 5000;
        private static readonly object gate = new object();
        // Session identifier to its level before holding, and the radio players holding it now.
        private static readonly Dictionary<string, float> held = new Dictionary<string, float>(StringComparer.Ordinal);
        private static readonly Dictionary<string, HashSet<object>> owners = new Dictionary<string, HashSet<object>>(StringComparer.Ordinal);
        // Held by an earlier run, or by this one for a session that was gone when released, such as a closed
        // application. Restored whenever it is found again, because Windows keeps an application's level.
        private static readonly Dictionary<string, float> leftovers = new Dictionary<string, float>(StringComparer.Ordinal);
        private static string recordPath;
        private static Thread watcher;

        // Reads the record before anything can hold, so every later hold is written to it.
        internal static void Configure(string path)
        {
            lock (gate)
            {
                recordPath = path;
                foreach (var pair in Load()) if (!held.ContainsKey(pair.Key)) leftovers[pair.Key] = pair.Value;
            }
        }

        // Restores what is left held, as after a crash or for an application closed while held, now and then every
        // few seconds on a background thread until none is left. Windows keeps an application's level, so a closed
        // one relaunches held until its session is found and restored.
        internal static void Watch()
        {
            lock (gate)
            {
                if (watcher != null || recordPath == null) return;
                watcher = new Thread(WatchLeftovers) { IsBackground = true, Name = "Sailwind Radio volume restore" };
                watcher.Start();
            }
        }

        private static void WatchLeftovers()
        {
            try
            {
                using (new ComCalls.Apartment())
                    while (true)
                    {
                        lock (gate)
                        {
                            RestoreLeftovers();
                            // Cleared under the same lock, so a leftover added after this check starts a new watch.
                            if (leftovers.Count == 0) { watcher = null; return; }
                        }
                        Thread.Sleep(WatchMilliseconds);
                    }
            }
            // A failed check ends the watch. The next release or start begins another.
            catch (Exception) { lock (gate) watcher = null; }
        }

        // Inside gate. Restores leftovers whose sessions are back and forgets those that never return.
        private static void RestoreLeftovers()
        {
            foreach (string identifier in Restore(leftovers)) leftovers.Remove(identifier);
            // A session without a program path, such as a WinRT media player's, gets a new identifier each launch,
            // and Windows does not keep its level, so once it is gone it never needs restoring.
            foreach (string identifier in new List<string>(leftovers.Keys))
                if (identifier.IndexOf("|#", StringComparison.Ordinal) >= 0) leftovers.Remove(identifier);
            Write();
        }

        // Holds every session of the matching processes for owner.
        internal static void Hold(object owner, Func<int, bool> owned)
        {
            lock (gate)
            {
                AudioSessions.Visit(session =>
                {
                    if (!owned(session.ProcessId)) return;
                    float level = session.Level;
                    if (level < 0) return;
                    if (!held.ContainsKey(session.Identifier))
                    {
                        // Already at the held level means an earlier run left it there, so its true original is a leftover.
                        float original;
                        if (!leftovers.TryGetValue(session.Identifier, out original)) original = Held(level) ? 1f : level;
                        leftovers.Remove(session.Identifier);
                        held.Add(session.Identifier, original);
                        owners.Add(session.Identifier, new HashSet<object>());
                        // Recorded before the change, so a crash right after still finds it.
                        Write();
                    }
                    owners[session.Identifier].Add(owner);
                    if (!Held(level)) session.Level = Level;
                });
            }
        }

        // Restores the sessions no other radio still holds. A session that is gone stays recorded as a leftover.
        internal static void Release(object owner)
        {
            lock (gate)
            {
                var released = new Dictionary<string, float>(StringComparer.Ordinal);
                foreach (var pair in owners)
                    if (pair.Value.Remove(owner) && pair.Value.Count == 0) released.Add(pair.Key, held[pair.Key]);
                foreach (string identifier in released.Keys)
                {
                    held.Remove(identifier);
                    owners.Remove(identifier);
                    leftovers[identifier] = released[identifier];
                }
                RestoreLeftovers();
                if (leftovers.Count > 0) Watch();
            }
        }

        private static bool Held(float level) { return Math.Abs(level - Level) < 1e-6f; }

        // Restores sessions still at the held level and returns every listed session found.
        private static List<string> Restore(Dictionary<string, float> levels)
        {
            var found = new List<string>();
            if (levels.Count == 0) return found;
            AudioSessions.Visit(session =>
            {
                float original;
                if (!levels.TryGetValue(session.Identifier, out original)) return;
                found.Add(session.Identifier);
                if (Held(session.Level)) session.Level = original;
            });
            return found;
        }

        private static Dictionary<string, float> Load()
        {
            var levels = new Dictionary<string, float>(StringComparer.Ordinal);
            if (recordPath == null || !File.Exists(recordPath)) return levels;
            foreach (string line in File.ReadAllLines(recordPath, Encoding.UTF8))
            {
                int tab = line.IndexOf('\t');
                float level;
                if (tab > 0 && Single.TryParse(line.Substring(0, tab), NumberStyles.Float, CultureInfo.InvariantCulture, out level) &&
                    level >= 0 && level <= 1)
                    levels[line.Substring(tab + 1)] = level;
            }
            return levels;
        }

        private static void Write()
        {
            if (recordPath == null) return;
            if (held.Count + leftovers.Count == 0)
            {
                if (File.Exists(recordPath)) File.Delete(recordPath);
                return;
            }
            var text = new StringBuilder();
            foreach (var levels in new[] { held, leftovers })
                foreach (var pair in levels)
                    text.Append(pair.Value.ToString("R", CultureInfo.InvariantCulture)).Append('\t').Append(pair.Key).Append('\n');
            File.WriteAllText(recordPath, text.ToString(), Encoding.UTF8);
        }
    }
}
