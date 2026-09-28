using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using SailwindRadio;
using SailwindRadio.Library;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;
using UnityEngine;

// Per-radio fault containment with the production set, real engines, real queues and the real
// arbiter power path. Faults come from an injected tick failure and from a native Stop failure
// raised by the existing AudioSource double.
internal static partial class Program
{
    private sealed class Radio
    {
        internal int Id;
        internal RadioState State;
        internal int Ticks;
    }

    private sealed class Harness : IDisposable
    {
        internal readonly GlobalRadioArbiter Arbiter = new GlobalRadioArbiter();
        internal readonly List<string> Warnings = new List<string>();
        internal readonly List<string> EngineWarnings = new List<string>();
        internal readonly List<Radio> PoweredOff = new List<Radio>();
        internal readonly Dictionary<Radio, RadioPlayback> Attached = new Dictionary<Radio, RadioPlayback>();
        internal readonly Dictionary<RadioPlayback, AudioSource> Sources = new Dictionary<RadioPlayback, AudioSource>();
        internal readonly RadioPlaybackSet<Radio> Set;
        internal readonly List<Radio> Radios = new List<Radio>();
        internal Radio Faulty;
        internal int PendingFaults, CreateFailures;
        internal bool OffBlocked, ThrowOnDetach;
        internal double Now = 1000;

        internal Harness(LibrarySnapshot snapshot, int count)
        {
            var records = new List<RadioRecord>();
            for (int id = 1; id <= count; id++)
                records.Add(RadioRecord.Capture(id, RadioSaveStore.DonorIndex, new RadioState { Powered = id == 1, Volume = .5f }));
            Arbiter.Reset(records);
            Set = new RadioPlaybackSet<Radio>((radio, state) =>
            {
                if (CreateFailures > 0) { CreateFailures--; throw new InvalidOperationException("Injected player creation failure"); }
                var engine = new RadioPlayback(new MonoBehaviour(), state, EngineWarnings.Add)
                { RepeatTrack = false };
                engine.SetAcoustics(new Vector3(), true, 0);
                Sources[engine] = AudioSource.Last;
                return engine;
            }, (radio, engine) =>
            {
                if (engine == null && ThrowOnDetach) throw new InvalidOperationException("Injected detach failure");
                Attached[radio] = engine;
            }, radio =>
            {
                // Mirrors the game refusing a power change while loading or while saving is blocked.
                if (OffBlocked) return;
                PoweredOff.Add(radio);
                Arbiter.Toggle(radio.Id, _ => { });
            }, Warnings.Add);
            for (int id = 1; id <= count; id++)
            {
                RadioState state;
                Arbiter.TryGet(id, out state);
                var radio = new Radio { Id = id, State = state };
                Radios.Add(radio);
                Set.Add(radio, state);
            }
            Set.SetSnapshot(snapshot);
        }

        // Mirrors the plugin's per-radio tick: engine first, then queue advancement on failure or end.
        internal void Tick(Radio radio, RadioPlayback engine, RadioQueue queue, bool blocked)
        {
            radio.Ticks++;
            if (radio == Faulty && PendingFaults > 0)
            {
                PendingFaults--;
                throw new InvalidOperationException("Injected radio fault");
            }
            engine.Tick(new Vector3(), blocked);
            if (!blocked && radio.State.Powered && !radio.State.Paused)
            {
                bool changed = engine.LoadFailed ? queue.TrackFailed(engine.FailedPath) : engine.TrackEnded && queue.TrackEnded();
                if (changed) engine.SetTrack(radio.State.TrackPath);
            }
        }

        internal void Frame()
        {
            Set.Tick(Now, Arbiter.ActiveId, radio => radio.Id, Tick, false);
            CompleteLoads(Radios.Select(EngineOf));
            Set.Tick(Now, Arbiter.ActiveId, radio => radio.Id, Tick, false);
        }

        internal RadioPlayback EngineOf(Radio radio) { return Attached.TryGetValue(radio, out var engine) ? engine : null; }
        internal AudioSource SourceOf(Radio radio) { return Sources[EngineOf(radio)]; }

        public void Dispose() { Set.Clear(); }
    }

    private static void FaultIsolation()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "fault-tracks");
        Directory.CreateDirectory(dir);
        var paths = new[] { "track1.wav", "track2.wav", "track3.wav", "track4.wav" }.Select(name => Path.Combine(dir, name)).ToArray();
        foreach (string path in paths) WriteSilentWav(path, 480000, 48000, 1);
        int clips = AudioClip.Created;
        var snapshot = new LibrarySnapshot(new[] { new MusicCollection(dir, "Faults",
            paths.Select(path => new MusicTrack(path, new TrackInfo(path))).ToArray()) }, new string[0]);
        AudioSettings.dspTime = 50;

        using (var h = new Harness(snapshot, 2))
        {
            Radio a = h.Radios[0], b = h.Radios[1];
            h.Frame();
            var activeSource = h.SourceOf(a);
            var activeEngine = h.EngineOf(a);
            Check(activeSource.isPlaying && activeSource.Schedules == 1 && a.State.TrackPath == paths[0] && b.State.TrackPath == paths[0],
                "both radios start on the first track and only the active one plays: playing=" + activeSource.isPlaying + " schedules=" + activeSource.Schedules + " a=" + a.State.TrackPath + " b=" + b.State.TrackPath + " status=" + activeEngine.Status + " active=" + h.Arbiter.ActiveId + " warnings=" + string.Join("|", h.EngineWarnings.Concat(h.Warnings)));

            // A standby radio fails. The playing radio is untouched and the failed radio skips its track.
            var failedEngine = h.EngineOf(b);
            h.Faulty = b; h.PendingFaults = 1;
            h.Frame();
            Check(h.EngineOf(a) == activeEngine && activeSource.isPlaying && activeSource.Schedules == 1 && activeSource.Pauses == 0,
                "another radio keeps playing without interruption while one radio fails");
            Check(h.EngineOf(b) != failedEngine && h.Attached[b] == h.EngineOf(b) && h.Sources[failedEngine].Destroyed,
                "the failing radio's engine is released and a fresh engine is attached");
            Check(b.State.TrackPath == paths[1] && b.State.PositionSeconds == 0, "the failing radio skips the track it was on");
            Check(h.Warnings.Count == 1 && h.Warnings[0].Contains("restarted with the next track") && h.Warnings[0].Contains("Injected radio fault"),
                "one warning names the contained failure");

            // A real engine fault from a native Stop failure inside SetTrack, raised outside Tick.
            h.Now += 1;
            h.SourceOf(a).ThrowOnNextStop = true;
            var faultedEngine = h.EngineOf(a);
            bool ran = h.Set.Run(a, (engine, queue) => { if (queue.Next()) engine.SetTrack(a.State.TrackPath); });
            Check(!ran && h.EngineOf(a) == faultedEngine, "a failing action is recorded and recovered only at the next tick");
            h.Frame();
            var replacement = h.SourceOf(a);
            Check(h.EngineOf(a) != faultedEngine && a.State.TrackPath == paths[2] && replacement.isPlaying && replacement.Schedules == 1,
                "a native engine fault restarts the active radio on the next track and it plays again");
            Check(h.Warnings.Count == 2 && b.State.TrackPath == paths[1] && !b.State.Powered, "the other radio's state is unchanged");

            // Three failures within a minute switch off only that radio, through the arbiter.
            h.Faulty = a;
            int warningsBefore = h.Warnings.Count;
            for (int i = 0; i < 2; i++) { h.Now += 1; h.PendingFaults = 1; h.Frame(); }
            Check(h.PoweredOff.Count == 1 && h.PoweredOff[0] == a && !a.State.Powered && h.Arbiter.ActiveId == 0 &&
                h.Attached[a] == null, "the third failure in a minute switches off only the failing radio");
            Check(h.Warnings.Count == warningsBefore + 1 && h.Warnings.Last().Contains("switched off"),
                "repeated failures add one switch-off message and no per-failure spam");
            int ticks = a.Ticks;
            for (int i = 0; i < 5; i++) { h.Now += 1; h.Frame(); }
            Check(a.Ticks == ticks && h.EngineOf(a) == null && b.Ticks > 0, "a switched-off failing radio is not ticked or rebuilt");
            h.Arbiter.Toggle(a.Id, _ => { });
            h.PendingFaults = 0;
            h.Frame();
            Check(h.EngineOf(a) != null && h.SourceOf(a).isPlaying,
                "turning the radio on again starts a fresh engine that plays");
        }

        using (var h = new Harness(snapshot, 1))
        {
            // Failures further apart than the window never switch a radio off.
            Radio a = h.Radios[0];
            h.Faulty = a;
            h.Frame();
            for (int i = 0; i < 3; i++) { h.Now += 61; h.PendingFaults = 1; h.Frame(); }
            Check(h.PoweredOff.Count == 0 && a.State.Powered && h.Warnings.Count == 3, "failures more than a minute apart restart without switching off");
        }

        using (var h = new Harness(snapshot, 8))
        {
            // Many radios failing together stay within the global warning budget.
            h.Frame();
            foreach (var radio in h.Radios)
            {
                h.Faulty = radio; h.PendingFaults = 1;
                h.Set.Tick(h.Now, h.Arbiter.ActiveId, r => r.Id, h.Tick, false);
            }
            Check(h.Warnings.Count == RadioPlaybackSet<Radio>.WarningsPerWindow, "simultaneous failures across radios log at most five warnings a minute");
            h.Now += 61;
            h.Faulty = h.Radios[0]; h.PendingFaults = 1;
            h.Frame();
            Check(h.Warnings.Count == RadioPlaybackSet<Radio>.WarningsPerWindow + 1 && h.Warnings.Last().Contains("3 similar radio errors were not logged"),
                "the next logged warning reports how many were held back");
            Check(h.Warnings.All(w => w.Split(new[] { ": " }, 2, StringSplitOptions.None)[0].IndexOf(';') < 0), "fault prose contains no semicolons");
            Check(h.EngineWarnings.Count == 0, "contained faults raise no engine load warnings");
            var hooked = h.Radios[2];
            var hookedEngine = h.EngineOf(hooked);
            bool rethrown = false;
            try { h.Set.Run(hooked, (engine, queue) => { throw new InvalidOperationException("stop failed"); }, true); }
            catch (InvalidOperationException) { rethrown = true; }
            h.Frame();
            Check(rethrown && h.EngineOf(hooked) != hookedEngine,
                "a native hook failure reaches its caller's safety handling and is still recovered");
            var removed = h.Radios[1];
            h.Set.Run(removed, (engine, queue) => { throw new InvalidOperationException("late fault"); });
            h.Set.Remove(removed);
            h.Frame();
            Check(!h.Set.Keys.Contains(removed) && h.Set.Keys.Count == 7 && h.Attached[removed] == null,
                "a fault pending for a removed radio is dropped safely");
        }
        Check(AudioClip.Created > clips, "fault checks exercised real engine loads");
        FaultBoundaries(snapshot);
        foreach (string path in paths) DeleteWhenClosed(path);
        OggQueueSkipping();
        DamagedTrackStaysQueued();
    }

    private static void FaultBoundaries(LibrarySnapshot snapshot)
    {
        using (var h = new Harness(snapshot, 2))
        {
            // The save path captures every radio, then writes the arbiter's states.
            Radio a = h.Radios[0], b = h.Radios[1];
            h.Frame();
            var engine = h.EngineOf(a);
            var source = h.SourceOf(a);
            b.State.Volume = .8f;
            AudioSettings.dspTime = source.ScheduledAt + 11;
            source.ThrowOnNextStop = true;
            bool escaped = false;
            try { foreach (var radio in h.Radios) h.Set.Capture(radio); }
            catch (Exception) { escaped = true; }
            var store = new RadioSaveStore();
            h.Arbiter.WriteTo(store);
            var saved = new RadioSaveStore();
            RadioRecord savedB;
            Check(!escaped && !source.ThrowOnNextStop && saved.Load(store.Save()) &&
                saved.TryGet(b.Id, RadioSaveStore.ItemIndex(0), out savedB) && savedB.Volume == .8f,
                "a capture fault during save stays inside its radio and the other radios are still saved");
            h.Frame();
            Check(h.EngineOf(a) != engine && h.EngineOf(a) != null, "the capture fault is recovered at the next tick");
        }

        using (var h = new Harness(snapshot, 1))
        {
            var late = new Radio { Id = 2, State = new RadioState { Volume = .5f } };
            h.CreateFailures = 1;
            h.Set.Add(late, late.State);
            Check(h.Set.Keys.Contains(late) && h.EngineOf(late) == null, "a player that cannot be built does not escape Add");
            h.Frame();
            var rebuilt = h.EngineOf(late);
            Check(rebuilt != null && h.Warnings.Count == 1 && h.Warnings[0].Contains("Injected player creation failure"),
                "the player is built again at the next tick with one warning");
            h.ThrowOnDetach = true;
            h.Set.Remove(late);
            h.ThrowOnDetach = false;
            Check(!h.Set.Keys.Contains(late) && h.Sources[rebuilt].Destroyed && h.Warnings.Count == 2 &&
                h.Warnings[1].StartsWith("Could not release a radio player", StringComparison.Ordinal),
                "a release failure is reported and the player is still disposed");
        }

        using (var h = new Harness(snapshot, 1))
        {
            Radio a = h.Radios[0];
            h.Faulty = a;
            h.Frame();
            h.OffBlocked = true;
            for (int i = 0; i < 3; i++) { h.Now += 1; h.PendingFaults = 1; h.Frame(); }
            Check(a.State.Powered && h.PoweredOff.Count == 0 && h.EngineOf(a) == null && !h.Warnings.Any(w => w.Contains("switched off")),
                "a refused power-off is not reported as switched off");
            h.OffBlocked = false;
            h.Now += 1;
            h.Frame();
            Check(!a.State.Powered && h.PoweredOff.Count == 1 && h.Warnings.Count(w => w.Contains("switched off")) == 1,
                "the power-off is retried and reported once it takes effect");
        }
    }

    private static string RepositoryRoot()
    {
        string root = AppContext.BaseDirectory;
        while (root != null && !File.Exists(Path.Combine(root, "Radio.csproj"))) root = Path.GetDirectoryName(root.TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar));
        if (root == null) throw new Exception("Cannot locate the Radio repository root");
        return root;
    }

    // A FLAC cut short throws mid-stream once its first audio has played. The real engine plays up to the
    // damage and ends the track normally, so the real queue advances and keeps the song in its pool.
    private static void DamagedTrackStaysQueued()
    {
        string dir = Path.Combine(AppContext.BaseDirectory, "damaged-queue");
        Directory.CreateDirectory(dir);
        string damaged = Path.Combine(dir, "1 damaged.flac"), healthy = Path.Combine(dir, "2 healthy.wav");
        byte[] flac = File.ReadAllBytes(Path.Combine(RepositoryRoot(), "tests", "Flac", "Fixtures", "stereo16-l8.flac"));
        File.WriteAllBytes(damaged, flac.Take(flac.Length * 6 / 10).ToArray());
        WriteSilentWav(healthy, 480000, 48000, 1);
        var snapshot = new LibrarySnapshot(new[] { new MusicCollection(dir, "Damaged",
            new[] { damaged, healthy }.Select(path => new MusicTrack(path, new TrackInfo(path))).ToArray()) }, new string[0]);
        AudioSettings.dspTime = 90;
        using (var h = new Harness(snapshot, 1))
        {
            Radio a = h.Radios[0];
            for (int i = 0; i < 10 && !(h.EngineOf(a) != null && h.SourceOf(a).isPlaying); i++) h.Frame();
            var engine = h.EngineOf(a);
            var source = h.SourceOf(a);
            Check(a.State.TrackPath == damaged && source.isPlaying && engine.Status == "Playing",
                "the truncated FLAC starts playing: track=" + a.State.TrackPath + " status=" + engine.Status + " warnings=" + string.Join(" | ", h.EngineWarnings));
            var stream = (StreamedAudio)typeof(RadioPlayback).GetField("streamed", System.Reflection.BindingFlags.Instance |
                System.Reflection.BindingFlags.NonPublic).GetValue(engine);
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (stream.DamageFrame == int.MaxValue && DateTime.UtcNow < deadline) System.Threading.Thread.Sleep(1);
            double damageSeconds = stream.DamageFrame / (double)stream.SampleRate;
            Check(stream.DamageFrame > 4096 && stream.DamageFrame < stream.Frames && stream.Fault == null && damageSeconds > .05,
                "the truncated FLAC decodes up to its damage without failing: damage=" + stream.DamageFrame + " of " + stream.Frames);
            AudioSettings.dspTime = source.ScheduledAt + damageSeconds - .02;
            h.Frame();
            Check(h.EngineOf(a) == engine && a.State.TrackPath == damaged && engine.Status == "Playing" && h.EngineWarnings.Count == 0,
                "the damaged track plays up to its damage");
            AudioSettings.dspTime = source.ScheduledAt + damageSeconds + .02;
            h.Frame();
            int pool = 0;
            h.Set.Run(a, (e, queue) => pool = queue.TrackCount);
            Check(a.State.TrackPath == healthy && pool == 2 && h.EngineWarnings.Count == 1 &&
                h.EngineWarnings[0].StartsWith("Music ended early at damaged audio: FLAC ", StringComparison.Ordinal) && h.Warnings.Count == 0,
                "reaching the damage ends the FLAC like a normal end, the queue advances and keeps the song: track=" + a.State.TrackPath +
                " pool=" + pool + " warnings=" + string.Join(" | ", h.EngineWarnings.Concat(h.Warnings)));
            h.Set.Run(a, (e, queue) => { if (queue.Next()) e.SetTrack(a.State.TrackPath); });
            Check(a.State.TrackPath == damaged, "the damaged song is still selectable after its early end");
        }
        DeleteWhenClosed(damaged);
        DeleteWhenClosed(healthy);
    }

    // Undecodable OGG files fail through the stream engine and the real queue skips them to the next
    // playable OGG. Fixtures are the committed Vorbis checks' Opus and synthetic floor 0 streams.
    private static void OggQueueSkipping()
    {
        string fixtures = Path.Combine(RepositoryRoot(), "tests", "Vorbis", "Fixtures");
        string dir = Path.Combine(AppContext.BaseDirectory, "ogg-queue");
        Directory.CreateDirectory(dir);
        var paths = new[] { "1 opus.ogg", "2 floor0.ogg", "3 tone.ogg" }.Select(name => Path.Combine(dir, name)).ToArray();
        File.Copy(Path.Combine(fixtures, "opus.ogg"), paths[0], true);
        File.Copy(Path.Combine(fixtures, "floor0-synthetic.ogg"), paths[1], true);
        File.Copy(Path.Combine(fixtures, "stereo44-q2.ogg"), paths[2], true);
        var snapshot = new LibrarySnapshot(new[] { new MusicCollection(dir, "Ogg",
            paths.Select(path => new MusicTrack(path, new TrackInfo(path))).ToArray()) }, new string[0]);
        AudioSettings.dspTime = 70;
        using (var h = new Harness(snapshot, 1))
        {
            Radio a = h.Radios[0];
            for (int i = 0; i < 10 && !(h.EngineOf(a) != null && h.SourceOf(a).isPlaying); i++) h.Frame();
            var source = h.SourceOf(a);
            Check(a.State.TrackPath == paths[2] && source.isPlaying && source.clip != null && source.clip.Streaming &&
                source.clip.frequency == 44100 && h.EngineOf(a).Status == "Playing",
                "the queue skips Ogg Opus and Vorbis floor 0 files and plays the next Ogg Vorbis track: track=" + a.State.TrackPath +
                " status=" + h.EngineOf(a).Status);
            Check(h.EngineWarnings.Count == 2 && h.EngineWarnings[0].StartsWith("Unable to decode music: Ogg Opus audio is not supported", StringComparison.Ordinal) &&
                h.EngineWarnings[1].StartsWith("Unable to decode music: Vorbis floor 0 is not supported", StringComparison.Ordinal) && h.Warnings.Count == 0,
                "each skipped Ogg file logs one clear decoder reason and no radio fault: " + string.Join(" | ", h.EngineWarnings));
        }
        foreach (string path in paths) DeleteWhenClosed(path);
    }
}
