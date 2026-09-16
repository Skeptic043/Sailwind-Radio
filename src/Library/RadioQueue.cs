using System;
using System.Collections.Generic;

namespace SailwindRadio.Library
{
    // Only the host thread touches this per-radio queue and its persisted state.
    internal sealed class RadioQueue
    {
        private readonly RadioState state;
        private readonly Random random;
        private readonly string legacyPath;
        private readonly HashSet<string> failed = new HashSet<string>(LibraryPaths.Comparer);
        private readonly List<string> pool = new List<string>();
        private readonly List<string> bag = new List<string>();
        private readonly List<string> history = new List<string>();
        private int historyIndex = -1;
        private LibrarySnapshot snapshot;
        internal int TrackCount => pool.Count;

        internal RadioQueue(RadioState state, Random random = null)
        {
            this.state = state ?? throw new ArgumentNullException(nameof(state));
            this.random = random ?? new Random();
            legacyPath = state.CollectionsInitialized ? "" : state.TrackPath;
        }

        internal bool ApplySnapshot(LibrarySnapshot latest)
        {
            if (latest == null || ReferenceEquals(latest, LibrarySnapshot.Empty)) return false;
            if (!ReferenceEquals(latest, snapshot)) failed.Clear();
            snapshot = latest;
            if (!state.CollectionsInitialized && latest.Collections.Count > 0)
            {
                var selected = new List<string>();
                foreach (var collection in latest.Collections) selected.Add(collection.Id);
                state.SelectedCollections = selected.ToArray();
                state.CollectionsInitialized = true;
            }
            return Rebuild();
        }

        internal bool SetCollections(LibrarySnapshot latest, IEnumerable<string> selectedRoots)
        {
            snapshot = latest ?? LibrarySnapshot.Empty;
            var selected = new HashSet<string>(LibraryPaths.Comparer);
            if (selectedRoots != null)
                foreach (string id in selectedRoots)
                {
                    if (string.IsNullOrWhiteSpace(id)) continue;
                    try { selected.Add(LibraryPaths.Canonical(id)); }
                    catch (Exception error) when (LibraryPaths.FilesystemError(error)) { }
                    if (selected.Count >= LibraryScanner.MaximumRoots) break;
                }
            var ordered = new List<string>(selected);
            ordered.Sort(LibraryPaths.Comparer);
            state.SelectedCollections = ordered.ToArray();
            state.CollectionsInitialized = true;
            return Rebuild();
        }

        internal bool SetShuffle(bool shuffle)
        {
            if (state.Shuffle == shuffle) return false;
            state.Shuffle = shuffle;
            ResetTraversal();
            return false; // Toggling shuffle does not interrupt the current song.
        }

        private bool Rebuild()
        {
            pool.Clear();
            var unique = new HashSet<string>(LibraryPaths.Comparer);
            if (!state.CollectionsInitialized && snapshot.Collections.Count == 0)
            {
                // The original single-file configuration remains playable until collections are used.
                string legacy = string.IsNullOrEmpty(state.TrackPath) ? legacyPath : state.TrackPath;
                if (!string.IsNullOrEmpty(legacy) && !failed.Contains(legacy)) pool.Add(legacy);
            }
            else
            {
                var selected = new HashSet<string>(state.SelectedCollections ?? Array.Empty<string>(), LibraryPaths.Comparer);
                foreach (var collection in snapshot.Collections)
                {
                    if (!selected.Contains(collection.Id)) continue;
                    foreach (var track in collection.Tracks)
                        if (!failed.Contains(track.Path) && unique.Add(track.Path)) pool.Add(track.Path);
                }
                pool.Sort(LibraryPaths.Comparer);
            }
            ResetTraversal();
            if (IndexOf(state.TrackPath) >= 0) return false;
            if (pool.Count == 0) return Select("");
            return Select(state.Shuffle ? DrawShuffle() : pool[0]);
        }

        internal bool Next() => Advance();
        internal bool TrackEnded() => Advance();

        internal string PeekNext()
        {
            if (pool.Count == 0) return "";
            if (state.Shuffle && historyIndex + 1 < history.Count) return history[historyIndex + 1];
            if (state.Shuffle) return pool.Count == 1 ? pool[0] : bag[bag.Count - 1];
            return pool[(IndexOf(state.TrackPath) + 1) % pool.Count];
        }

        internal bool Previous()
        {
            if (pool.Count == 0) return false;
            if (state.Shuffle && historyIndex > 0)
            {
                historyIndex--;
                return Select(history[historyIndex], false);
            }
            int index = IndexOf(state.TrackPath);
            return Select(pool[(index <= 0 ? pool.Count : index) - 1]);
        }

        internal bool TrackFailed(string path)
        {
            // Late failure events from an old decoder must not skip the newer selection.
            if (string.IsNullOrEmpty(path) || !LibraryPaths.Comparer.Equals(path, state.TrackPath)) return false;
            failed.Add(path);
            int index = IndexOf(path);
            pool.RemoveAll(candidate => LibraryPaths.Comparer.Equals(candidate, path));
            bag.RemoveAll(candidate => LibraryPaths.Comparer.Equals(candidate, path));
            history.RemoveAll(candidate => LibraryPaths.Comparer.Equals(candidate, path));
            historyIndex = history.Count - 1;
            if (pool.Count == 0) return Select("");
            return Select(state.Shuffle ? DrawShuffle() : pool[index >= 0 ? index % pool.Count : 0]);
        }

        private bool Advance()
        {
            if (pool.Count == 0) return false;
            if (state.Shuffle && historyIndex + 1 < history.Count)
            {
                historyIndex++;
                return Select(history[historyIndex], false);
            }
            int index = IndexOf(state.TrackPath);
            return Select(state.Shuffle ? DrawShuffle() : pool[(index + 1) % pool.Count]);
        }

        private string DrawShuffle()
        {
            if (pool.Count == 1) return pool[0];
            PrepareShuffleBag();
            string result = bag[bag.Count - 1];
            bag.RemoveAt(bag.Count - 1);
            return result;
        }

        private void PrepareShuffleBag()
        {
            if (!state.Shuffle || pool.Count < 2) return;
            bag.RemoveAll(candidate => LibraryPaths.Comparer.Equals(candidate, state.TrackPath));
            if (bag.Count == 0)
            {
                foreach (string path in pool)
                    if (!LibraryPaths.Comparer.Equals(path, state.TrackPath)) bag.Add(path);
                for (int i = bag.Count - 1; i > 0; i--)
                {
                    int chosen = random.Next(i + 1);
                    string temporary = bag[i]; bag[i] = bag[chosen]; bag[chosen] = temporary;
                }
            }
        }

        private bool Select(string path, bool remember = true)
        {
            bool change = !LibraryPaths.Comparer.Equals(state.TrackPath, path) || state.PositionSeconds != 0;
            state.TrackPath = path;
            state.PositionSeconds = 0;
            PrepareShuffleBag();
            if (remember && path.Length > 0)
            {
                if (historyIndex + 1 < history.Count) history.RemoveRange(historyIndex + 1, history.Count - historyIndex - 1);
                if (history.Count == 0 || !LibraryPaths.Comparer.Equals(history[history.Count - 1], path)) history.Add(path);
                if (history.Count > 256) history.RemoveAt(0);
                historyIndex = history.Count - 1;
            }
            // A one-track queue still needs an explicit restart even if its saved position is zero.
            return change || path.Length > 0;
        }

        private int IndexOf(string path) => pool.FindIndex(candidate => LibraryPaths.Comparer.Equals(candidate, path));

        private void ResetTraversal()
        {
            bag.Clear();
            history.Clear();
            if (IndexOf(state.TrackPath) >= 0) history.Add(state.TrackPath);
            historyIndex = history.Count - 1;
            PrepareShuffleBag();
        }
    }
}
