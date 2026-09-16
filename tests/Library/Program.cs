using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using SailwindRadio;
using SailwindRadio.Library;

internal static class Program
{
    private static int checks;
    private static string scratch;
    private static void Check(bool condition, string name)
    {
        if (!condition) throw new Exception("FAILED: " + name);
        checks++;
    }
    private static string FileAt(string relative, byte[] bytes = null)
    {
        string path = Path.GetFullPath(Path.Combine(scratch, relative));
        Directory.CreateDirectory(Path.GetDirectoryName(path));
        File.WriteAllBytes(path, bytes ?? new byte[] { 1, 2, 3 });
        return path;
    }
    private static LibrarySnapshot Scan(string roots) => LibraryScanner.Scan(roots, CancellationToken.None);
    private static void Drain(LibraryScanner scanner)
    {
        var deadline = DateTime.UtcNow.AddSeconds(15);
        while (scanner.IsScanning)
        {
            scanner.Poll();
            if (DateTime.UtcNow > deadline) throw new Exception("Scanner did not finish");
            Thread.Sleep(1);
        }
    }
    private static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
    private static void Run()
    {
        string parent = Path.GetFullPath(Path.Combine(AppContext.BaseDirectory, "scan-fixtures"));
        Directory.CreateDirectory(parent);
        scratch = Path.Combine(parent, Guid.NewGuid().ToString("N"));
        Directory.CreateDirectory(scratch);
        try
        {
            string a = FileAt("music/a.mp3"), b = FileAt("music/sub/b.OGG"), c = FileAt("music/sub/deep/c.wav");
            FileAt("music/ignore.flac"); FileAt("music/ignore.txt");
            string root = Path.GetDirectoryName(a), sub = Path.GetDirectoryName(b);
            var first = Scan(root);
            Check(first.Collections.Count == 1 && first.Collections[0].Tracks.Count == 3, "one recursive root is one collection with required formats");
            Check(first.Collections[0].Tracks.Select(track => track.Path).SequenceEqual(new[] { a, b, c }), "tracks sorted consistently");
            Check(first.GetLabel(a) == "a", "malformed audio still gets filename without scanner decode");
            Check(first.GetLabel("missing_song.ogg") == "missing song", "missing labels use clean filename");
            Check(first.GetLabel(null) == "" && first.GetLabel("bad\0path.mp3") != null, "invalid saved display path cannot interrupt the host update");
            var overlap = Scan(root + "|" + sub + "\n" + root.ToUpperInvariant() + Path.DirectorySeparatorChar);
            Check(overlap.Collections.Count == 2, "root identities ignore Windows case and trailing separators");
            Check(overlap.Collections[0].Tracks.Count == 3 && overlap.Collections[1].Tracks.Count == 2, "overlapping roots retain membership");
            string duplicate = Path.Combine(scratch, "other", "music");
            Directory.CreateDirectory(duplicate);
            var named = Scan(root + "|" + duplicate);
            Check(named.Collections.Select(item => item.Label).Distinct().Count() == 2 && named.Collections.All(item => item.Label.Contains("(")), "duplicate folder labels use parents");
            string semicolon = FileAt("Music;Sea/track.mp3");
            var literalSemicolon = Scan(Path.GetDirectoryName(semicolon));
            Check(literalSemicolon.Collections.Count == 1 && literalSemicolon.Collections[0].Tracks[0].Path == semicolon,
                "literal semicolon in a Windows folder name remains one collection");
            var missing = Scan(Path.Combine(scratch, "missing"));
            Check(missing.Collections.Count == 1 && missing.Collections[0].Tracks.Count == 0 && missing.Warnings.Count == 1, "missing root retained as empty with warning");
            var rejectedRoots = Scan("relative/music|C:relative|\\rooted|\\\\server\\music|https://example.com/music|file:///C:/Music");
            Check(rejectedRoots.Collections.Count == 0 && rejectedRoots.Warnings.Count == 6, "relative UNC and URI collection roots rejected before scan");
            var legacyInvalid = new RadioState { TrackPath = a, PositionSeconds = 7 };
            Check(!new RadioQueue(legacyInvalid).ApplySnapshot(rejectedRoots) && legacyInvalid.TrackPath == a && legacyInvalid.PositionSeconds == 7,
                "invalid folder configuration retains usable legacy song");
            string[] manyRoots = Enumerable.Range(0, 70).Select(i => Path.Combine(scratch, "absent" + i)).ToArray();
            var capped = Scan(string.Join("|", manyRoots));
            Check(capped.Collections.Count == 64 && capped.Warnings.Count <= 16, "roots and warnings bounded");
            using (var canceled = new CancellationTokenSource())
            {
                canceled.Cancel();
                bool canceledObserved = false;
                try { LibraryScanner.Scan(root, canceled.Token); }
                catch (OperationCanceledException) { canceledObserved = true; }
                Check(canceledObserved, "scan cancellation observed before filesystem work");
            }
            string deep = Path.Combine(scratch, "depth"); Directory.CreateDirectory(deep);
            string current = deep;
            for (int i = 0; i < 66; i++) { current = Path.Combine(current, "d"); Directory.CreateDirectory(current); }
            File.WriteAllBytes(Path.Combine(current, "too-deep.mp3"), new byte[0]);
            var depth = Scan(deep);
            Check(depth.Collections[0].Tracks.Count == 0 && depth.Warnings.Count > 0, "depth bound stops pathological trees");
            MetadataChecks();
            QueueChecks(first, overlap, root, sub, a, b, c);
            PeekChecks(first, root);
            using (var scanner = new LibraryScanner())
            {
                Check(ReferenceEquals(scanner.Snapshot, LibrarySnapshot.Empty), "initial sentinel distinct from completed empty library");
                scanner.RequestScan(root); Drain(scanner);
                var cached = scanner.Snapshot;
                scanner.RequestScan(sub);
                Check(ReferenceEquals(scanner.Snapshot, cached), "cached library retained during rescan");
                for (int i = 0; i < 50; i++) scanner.RequestScan(i % 2 == 0 ? root : sub);
                scanner.RequestScan(duplicate); Drain(scanner);
                Check(scanner.Snapshot.Collections.Count == 1 && scanner.Snapshot.Collections[0].Id == duplicate, "latest request wins across rapid canceled scans");
                Check(!ReferenceEquals(scanner.Snapshot, cached), "completed scan publishes fresh snapshot");
                scanner.RequestScan(root); scanner.Dispose();
                Check(!scanner.IsScanning && !scanner.Poll(), "dispose cancels without publishing delayed result");
                scanner.RequestScan(root);
                Check(!scanner.IsScanning, "disposed scanner cannot restart");
            }
            var deletedState = new RadioState { TrackPath = b, PositionSeconds = 42 };
            var deletedQueue = new RadioQueue(deletedState); deletedQueue.ApplySnapshot(first);
            File.Delete(b);
            Check(deletedQueue.ApplySnapshot(Scan(root)) && deletedState.TrackPath == a && deletedState.PositionSeconds == 0, "removed current chooses valid song on refresh");
#if NET10_0
            string link = Path.Combine(root, "loop");
            try
            {
                Directory.CreateSymbolicLink(link, root);
                Check(Scan(root).Collections[0].Tracks.Count == 2, "directory link cycle ignored");
                Check(Scan(link).Collections[0].Tracks.Count == 0, "configured reparse root ignored");
                Directory.Delete(link);
            }
            catch (UnauthorizedAccessException) { Console.WriteLine("Directory symbolic-link creation unavailable on this host. Reparse logic remains source-reviewed."); }
            catch (PlatformNotSupportedException) { Console.WriteLine("Directory symbolic-link creation unavailable on this host."); }
#endif
            Console.WriteLine(checks + " library checks passed with real temporary files and production scanner/queue/metadata. No Unity audio or game acceptance is implied.");
        }
        finally
        {
            // Only delete this harness's unique directory after validating its resolved parent.
            if (Directory.Exists(scratch) && string.Equals(Path.GetDirectoryName(Path.GetFullPath(scratch)), parent, StringComparison.OrdinalIgnoreCase)) Directory.Delete(scratch, true);
        }
    }

    private static void QueueChecks(LibrarySnapshot first, LibrarySnapshot overlap, string root, string sub, string a, string b, string c)
    {
        var state = new RadioState { TrackPath = b, PositionSeconds = 37.25, Volume = .7f, Powered = true };
        var queue = new RadioQueue(state, new Random(12));
        Check(!queue.ApplySnapshot(LibrarySnapshot.Empty) && state.TrackPath == b && state.PositionSeconds == 37.25, "pending first scan preserves saved position");
        Check(!queue.ApplySnapshot(overlap) && state.CollectionsInitialized && state.SelectedCollections.Length == 2, "new radio chooses all without restarting valid current");
        Check(queue.TrackCount == 3 && state.PositionSeconds == 37.25, "overlap queue deduplicated and saved position retained");
        Check(queue.Next() && state.TrackPath == c && state.PositionSeconds == 0, "next advances sorted path and resets position");
        Check(queue.Next() && state.TrackPath == a, "next wraps");
        Check(queue.Previous() && state.TrackPath == c, "previous wraps");
        Check(queue.SetCollections(overlap, new[] { sub }) == false && queue.TrackCount == 2, "changing selection retains current if still valid");
        Check(queue.SetCollections(overlap, Array.Empty<string>()) && state.TrackPath == "" && queue.TrackCount == 0, "zero selection clears playing path");
        Check(!queue.Next() && !queue.Previous() && !queue.TrackEnded(), "empty queue remains silent");
        Check(!queue.ApplySnapshot(Scan(root)) && state.SelectedCollections.Length == 0, "explicit empty selection survives rescan");
        Check(queue.SetCollections(first, new[] { root.ToUpperInvariant(), root }) && queue.TrackCount == 3 && state.SelectedCollections.Length == 1, "canonical collection selection deduplicates");
        Check(state.Volume == .7f && state.Powered, "queue never changes power or volume");
        state.PositionSeconds = 8;
        Check(!queue.SetShuffle(true) && state.PositionSeconds == 8, "shuffle toggle preserves current position");
        string initial = state.TrackPath;
        var visited = new HashSet<string> { initial };
        queue.Next(); string second = state.TrackPath; visited.Add(second);
        queue.Next(); string third = state.TrackPath; visited.Add(third);
        Check(visited.Count == 3, "shuffle bag visits all before repeat");
        Check(queue.Previous() && state.TrackPath == second && queue.Next() && state.TrackPath == third, "shuffle previous and forward traverse actual history");
        for (int i = 0; i < 300; i++)
        {
            string prior = state.TrackPath;
            queue.Next();
            Check(state.TrackPath != prior, "rapid shuffle avoids immediate repeat " + i);
        }
        queue.SetCollections(first, new[] { root });
        Check(!queue.TrackFailed("stale decoder.mp3"), "stale decoder failure does not skip current");
        for (int i = 0; i < 3; i++) Check(queue.TrackFailed(state.TrackPath), "decode failure advances bounded pool " + i);
        Check(state.TrackPath == "" && queue.TrackCount == 0 && !queue.TrackFailed(a) && !queue.Next(), "exhausted failures cannot spin or retry");
        Check(!queue.ApplySnapshot(first), "same snapshot does not retry known failures");
        Check(queue.ApplySnapshot(Scan(root)) && queue.TrackCount == 3, "new rescan retries corrected files even same content");
        queue.SetShuffle(false);
        queue.SetCollections(first, new[] { root });
        state.TrackPath = c; state.PositionSeconds = 22;
        Check(queue.TrackEnded() && state.TrackPath == a && state.PositionSeconds == 0, "natural completion advances and wraps");
        var solo = new LibrarySnapshot(new[] { new MusicCollection(root, "solo", new[] { new MusicTrack(a) }) }, Array.Empty<string>());
        queue.ApplySnapshot(solo); queue.SetShuffle(true);
        Check(queue.Next() && state.TrackPath == a && queue.Previous() && queue.TrackEnded(), "single song restart supports every navigation action");
        var two = new LibrarySnapshot(new[] { new MusicCollection(root, "two", new[] { new MusicTrack(a), new MusicTrack(b) }) }, Array.Empty<string>());
        queue.ApplySnapshot(two);
        for (int i = 0; i < 12; i++) { string prior = state.TrackPath; queue.Next(); Check(prior != state.TrackPath, "two track shuffle alternates " + i); }
        var legacy = new RadioState { TrackPath = a, PositionSeconds = 19 };
        var legacyQueue = new RadioQueue(legacy);
        Check(!legacyQueue.ApplySnapshot(Scan("")) && legacy.TrackPath == a && !legacy.CollectionsInitialized, "legacy single file preserved without configured roots");
        Check(legacyQueue.TrackFailed(a) && legacy.TrackPath == "", "legacy failed file becomes silent");
        Check(legacyQueue.ApplySnapshot(Scan("")) && legacy.TrackPath == a, "legacy failed file can retry after explicit rescan");
        legacyQueue.SetCollections(Scan(""), Array.Empty<string>());
        Check(legacy.TrackPath == "" && !legacyQueue.ApplySnapshot(Scan("")), "explicit deselect ends legacy fallback");
        var persisted = new RadioState { TrackPath = b, PositionSeconds = 99, CollectionsInitialized = true, SelectedCollections = new[] { sub } };
        var restored = new RadioQueue(persisted);
        Check(!restored.ApplySnapshot(overlap) && restored.TrackCount == 2 && persisted.PositionSeconds == 99, "saved subset and exact position restore");
        restored.ApplySnapshot(first);
        Check(restored.TrackCount == 0 && persisted.SelectedCollections[0] == sub, "unconfigured saved root remains selected identity without other fallback");
    }

    private static void MetadataChecks()
    {
        var tag = new byte[128]; Encoding.ASCII.GetBytes("TAG").CopyTo(tag, 0);
        Encoding.ASCII.GetBytes("Ocean crossing").CopyTo(tag, 3); Encoding.ASCII.GetBytes("Test artist").CopyTo(tag, 33);
        Encoding.ASCII.GetBytes("Sea album").CopyTo(tag, 63);
        string v1 = FileAt("tags/v1.mp3", tag);
        Check(TrackMetadata.ReadLabel(v1) == "Test artist - Ocean crossing", "ID3v1 artist and title");
        var v1Info = TrackMetadata.ReadInfo(v1);
        Check(v1Info.Title == "Ocean crossing" && v1Info.Artist == "Test artist" && v1Info.Album == "Sea album", "ID3v1 structured title artist album");
        foreach (int version in new[] { 3, 4 })
        {
            foreach (byte encoding in new byte[] { 0, 1, 2, 3 })
            {
                var body = new List<byte>();
                AddFrame(body, "TIT2", "Crossing", version, encoding);
                AddFrame(body, "TPE1", "Artist", version, encoding);
                AddFrame(body, "TALB", "Album", version, encoding);
                var header = new byte[] { 73, 68, 51, (byte)version, 0, 0, 0, 0, (byte)(body.Count >> 7), (byte)(body.Count & 127) };
                string path = FileAt("tags/v" + version + "-" + encoding + ".mp3", header.Concat(body).ToArray());
                Check(TrackMetadata.ReadLabel(path) == "Artist - Crossing", "ID3v" + version + " encoding " + encoding);
                var info = TrackMetadata.ReadInfo(path);
                Check(info.Title == "Crossing" && info.Artist == "Artist" && info.Album == "Album", "structured ID3 album encoding " + version + "/" + encoding);
                var snapshot = Scan(Path.GetDirectoryName(path));
                Check(snapshot.GetTrackInfo(path).Album == "Album", "snapshot provides cached structured metadata " + version + "/" + encoding);
            }
        }
        string huge = FileAt("tags/huge_tag.mp3", new byte[] { 73, 68, 51, 4, 0, 0, 127, 127, 127, 127 });
        Check(TrackMetadata.ReadLabel(huge) == "huge tag", "oversized tag never allocates unchecked length");
        string truncated = FileAt("tags/truncated.mp3", new byte[] { 73, 68, 51, 3, 0, 0, 0, 0, 0, 100 });
        Check(TrackMetadata.ReadLabel(truncated) == "truncated", "truncated tag falls back safely");
        string locked = FileAt("tags/locked.mp3");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) Check(TrackMetadata.ReadLabel(locked) == "locked", "unreadable metadata keeps clean filename");
        var cache = new MetadataCache();
        string original = cache.GetLabel(v1);
        using (new FileStream(v1, FileMode.Open, FileAccess.ReadWrite, FileShare.None))
            Check(cache.GetLabel(v1) == original, "unchanged metadata served from cache without reopening file");
        Encoding.ASCII.GetBytes("Updated title ").CopyTo(tag, 3);
        File.WriteAllBytes(v1, tag);
        File.SetLastWriteTimeUtc(v1, DateTime.UtcNow.AddSeconds(5));
        Check(cache.GetLabel(v1).Contains("Updated title"), "length/timestamp fingerprint invalidates changed metadata");
        using (new FileStream(locked, FileMode.Open, FileAccess.ReadWrite, FileShare.None)) cache.GetLabel(locked);
        Check(cache.Count == 1, "unreadable metadata is not cached permanently");
        Check(cache.GetLabel(locked) == "locked" && cache.Count == 2, "unreadable file retried after it becomes readable");
        cache.Prune(new[] { v1 });
        Check(cache.Count == 1, "obsolete metadata removed at completed scan");
        Check(cache.GetInfo(v1).Album == "Sea album", "metadata cache retains all structured fields");
        var fallback = LibrarySnapshot.Empty.GetTrackInfo("unknown_song.wav");
        Check(fallback.Title == "unknown song" && fallback.Artist == "" && fallback.Album == "", "missing metadata returns clean structured fallback without disk reads");
        var random = new Random(17);
        for (int i = 0; i < 100; i++)
        {
            var malformed = new byte[random.Next(10, 1024)]; random.NextBytes(malformed);
            malformed[0] = 73; malformed[1] = 68; malformed[2] = 51; malformed[3] = (byte)(i % 2 + 3);
            string fuzz = FileAt("tags/fuzz.mp3", malformed);
            Check(!string.IsNullOrEmpty(TrackMetadata.ReadLabel(fuzz)), "malformed tags remain bounded and readable " + i);
        }
    }
    private static void PeekChecks(LibrarySnapshot snapshot, string root)
    {
        var leftState = new RadioState { Shuffle = true, PositionSeconds = 12.5 };
        var rightState = new RadioState { Shuffle = true, PositionSeconds = 12.5 };
        var peeked = new RadioQueue(leftState, new Random(37));
        var baseline = new RadioQueue(rightState, new Random(37));
        Check(peeked.PeekNext() == "", "uninitialized queue has no preload target");
        peeked.ApplySnapshot(snapshot); baseline.ApplySnapshot(snapshot);
        for (int i = 0; i < 40; i++)
        {
            leftState.PositionSeconds = 23.75;
            string current = leftState.TrackPath;
            string next = peeked.PeekNext();
            for (int look = 0; look < 5; look++) Check(peeked.PeekNext() == next && leftState.TrackPath == current && leftState.PositionSeconds == 23.75,
                "repeated lookahead preserves current and reservation " + i + "/" + look);
            Check(peeked.Next() && leftState.TrackPath == next, "shuffle next consumes reserved target " + i);
            baseline.Next();
            Check(leftState.TrackPath == rightState.TrackPath, "peek does not consume extra random choices " + i);
        }
        peeked.Previous();
        string forward = peeked.PeekNext();
        peeked.Next();
        Check(leftState.TrackPath == forward, "peek follows shuffle forward history");
        peeked.SetShuffle(false);
        string sequential = peeked.PeekNext();
        peeked.TrackEnded();
        Check(leftState.TrackPath == sequential, "completion consumes sequential preload target");
        peeked.SetCollections(snapshot, Array.Empty<string>());
        Check(peeked.PeekNext() == "", "empty selection invalidates lookahead");
        peeked.SetCollections(snapshot, new[] { root });
        for (int i = 0; i < snapshot.Collections[0].Tracks.Count; i++) peeked.TrackFailed(leftState.TrackPath);
        Check(peeked.PeekNext() == "", "failure exhaustion clears preload target");
    }
    private static void AddFrame(List<byte> body, string id, string value, int version, byte encoding)
    {
        Encoding decoder = encoding == 0 ? Encoding.ASCII : encoding == 1 ? Encoding.Unicode : encoding == 2 ? Encoding.BigEndianUnicode : Encoding.UTF8;
        byte[] text = decoder.GetBytes(value);
        var payload = new List<byte> { encoding };
        if (encoding == 1) { payload.Add(255); payload.Add(254); }
        payload.AddRange(text);
        body.AddRange(Encoding.ASCII.GetBytes(id));
        body.AddRange(new byte[] { 0, 0, 0, (byte)payload.Count, 0, 0 });
        body.AddRange(payload);
    }
}
