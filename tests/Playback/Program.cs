using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using SailwindRadio;
using SailwindRadio.Audio;
using SailwindRadio.Persistence;
using SailwindRadio.Playback;
using UnityEngine;

internal static partial class Program
{
    private static int checks;
    private static string track;
    private static void Check(bool value, string description) { checks++; if (!value) throw new Exception(description); }
    private static RadioRecord Record(int id, bool power = true, double position = 2, float volume = .5f) =>
        RadioRecord.Capture(id, RadioSaveStore.DonorIndex, new RadioState { TrackPath = track, Powered = power, PositionSeconds = position, Volume = volume });

    private sealed class Endpoint : IDisposable
    {
        internal readonly int Id;
        internal readonly RadioState State;
        internal readonly RadioPlayback Playback;
        internal readonly AudioSource Source;
        internal Endpoint(int id, RadioState state)
        {
            Id = id; State = state;
            Playback = new RadioPlayback(new MonoBehaviour(), state, message => throw new Exception(message));
            Playback.SetAcoustics(new Vector3(), true, 0);
            Source = AudioSource.Last;
        }
        internal void Stop() { Playback.CapturePosition(); Playback.Tick(new Vector3(), true); }
        internal void Tick(bool suspended) { Playback.Tick(new Vector3(), suspended); }
        public void Dispose() { Playback.Dispose(); }
    }

    private static void Dispatch(IEnumerable<Endpoint> endpoints, GlobalRadioArbiter arbiter, bool suspended = false) =>
        PlaybackOrder.Tick(endpoints, arbiter.ActiveId, e => e.Id, (e, blocked) => e.Tick(blocked), suspended);
    private static void CompleteRequests(params Endpoint[] endpoints) { CompleteLoads(endpoints.Select(e => e.Playback)); }

    // Streams open on their own workers. Waits until each pending stream is open and holds the frame its
    // next start needs, so the following Tick sees a completed load. A faulted stream is left for Tick.
    private static void CompleteLoads(IEnumerable<RadioPlayback> engines)
    {
        const System.Reflection.BindingFlags Private = System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic;
        foreach (var engine in engines)
        {
            var stream = engine == null ? null : (StreamedAudio)typeof(RadioPlayback).GetField("streamed", Private).GetValue(engine);
            if (stream == null) continue;
            var deadline = DateTime.UtcNow.AddSeconds(10);
            while (!stream.IsOpen && stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(1);
            if (stream.Fault != null) continue;
            if (!stream.IsOpen) throw new Exception("Stream did not open within 10 seconds");
            int frame = (int)(long)typeof(StreamedAudio).GetField("requestedFrame", Private).GetValue(stream);
            while (!stream.Ready(frame) && stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(1);
        }
    }

    public static int Main()
    {
        try { Run(); return 0; }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }

    private static void Run()
    {
        // Ten seconds of 48 kHz mono silence, the dimensions the lifecycle arithmetic uses. It streams through the real engine.
        track = Path.Combine(AppContext.BaseDirectory, "radio-rule-fixture.wav");
        WriteSilentWav(track, 480000, 48000, 1);
        AudioSettings.dspTime = 10;
        LegacyAndCachedStates();
        LiveSwitching();
        OrderedPlaybackAndPendingLoad();
        StreamedPendingLoad();
        FailureAndSessionBoundaries();
        FaultIsolation();
        Check(AudioSettings.Subscribers == 0, "all actual playback engines release native event subscriptions");
        DeleteWhenClosed(track);
        Console.WriteLine(checks + " global radio lifecycle checks passed with production arbitration, persistence, playback ordering, per-radio fault containment and audio engine. Native game/audio services use doubles.");
    }

    // Stream workers close their files shortly after disposal, never during it.
    private static void DeleteWhenClosed(string path)
    {
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (true)
        {
            try { File.Delete(path); return; }
            catch (IOException) { if (DateTime.UtcNow > deadline) throw; Thread.Sleep(2); }
        }
    }

    private static void LegacyAndCachedStates()
    {
        foreach (bool reversed in new[] { false, true })
        {
            var a = Record(10, position: 2.25, volume: .2f);
            var b = Record(20, position: 5.125, volume: .8f);
            var arbiter = new GlobalRadioArbiter();
            arbiter.Reset(reversed ? new[] { b, a } : new[] { a, b });
            Check(arbiter.ActiveId == 10, "legacy multi-on winner independent of record order");
            var stateB = arbiter.Register(20, b.ToState());
            Check(!stateB.Powered && stateB.Paused, "restoring higher-ID item first cannot steal cached winner");
            using (var liveB = new Endpoint(20, stateB))
            {
                Dispatch(new[] { liveB }, arbiter); CompleteRequests(liveB); Dispatch(new[] { liveB }, arbiter);
                Check(liveB.Source.Schedules == 0, "only restored legacy loser remains silent while winner is cached");
                var stops = new List<int>();
                arbiter.Toggle(20, stops.Add);
                Check(stops.SequenceEqual(new[] { 10 }) && arbiter.ActiveId == 20 && stateB.Powered && !stateB.Paused,
                    "explicit activation displaces even an unloaded owner");
                var stateA = arbiter.Register(10, a.ToState());
                Check(!stateA.Powered && stateA.Paused && stateA.PositionSeconds == 2.25 && stateA.Volume == .2f,
                    "stale deferred DTO cannot reactivate displaced cached radio or reset remembered settings");
                using (var restoredA = new Endpoint(10, stateA))
                {
                    Dispatch(new[] { liveB, restoredA }, arbiter); CompleteRequests(liveB, restoredA); Dispatch(new[] { liveB, restoredA }, arbiter);
                    Check(liveB.Source.Schedules == 1 && restoredA.Source.Schedules == 0, "deferred restoration cannot run alongside chosen radio");
                    arbiter.Toggle(20, id => { if (id == 20) liveB.Stop(); });
                    Check(arbiter.ActiveId == 0 && !stateB.Powered, "powering current radio off clears ownership");
                    var againA = arbiter.Register(10, a.ToState());
                    Dispatch(new[] { restoredA, liveB }, arbiter);
                    Check(!againA.Powered && restoredA.Source.Schedules == 0 && liveB.Source.Schedules == 1,
                        "power off never auto-reactivates older radio after deferred restoration");
                    arbiter.Toggle(10, _ => throw new Exception("no owner should need stopping"));
                    Check(arbiter.ActiveId == 10 && stateA.Powered && !stateA.Paused, "one explicit click resumes displaced radio");
                    var store = new RadioSaveStore();
                    // An obsolete native DTO cannot win over canonical state at final capture.
                    store.Put(b);
                    arbiter.WriteTo(store);
                    var reloadedStore = new RadioSaveStore();
                    Check(reloadedStore.Load(store.Save()), "updated single-owner document uses existing schema");
                    var reloaded = new GlobalRadioArbiter(); reloaded.Reset(reloadedStore.Records);
                    Check(reloaded.ActiveId == 10, "chosen winner remains stable over save/reload");
                    reloaded.TryGet(20, out var savedB);
                    Check(!savedB.Powered && savedB.Paused && savedB.PositionSeconds == 5.125 && savedB.Volume == .8f,
                        "captured loser retains own track cursor and volume with canonical off flags");
                }
            }
        }
    }

    private static void LiveSwitching()
    {
        var arbiter = new GlobalRadioArbiter();
        arbiter.Reset(new[] { Record(10), Record(20, false, 5.125, .7f) });
        arbiter.TryGet(10, out var a); arbiter.TryGet(20, out var b);
        using (var liveA = new Endpoint(10, a))
        using (var liveB = new Endpoint(20, b))
        {
            var reversed = new[] { liveB, liveA };
            Dispatch(reversed, arbiter); CompleteRequests(reversed); Dispatch(reversed, arbiter);
            Check(liveA.Source.Schedules == 1 && liveB.Source.Schedules == 0, "only initial winner schedules audio");
            AudioSettings.dspTime += 1;
            liveA.Source.timeSamples = 156001;
            arbiter.Toggle(20, id =>
            {
                Check(id == 10 && !b.Powered, "replacement remains off until prior endpoint stop callback");
                liveA.Stop();
            });
            Check(!liveA.Source.isPlaying && a.PositionSeconds == 2.95 && !a.Powered && a.Paused,
                "switch captures shared DSP position and pauses outgoing voice");
            Dispatch(reversed, arbiter); CompleteRequests(reversed); Dispatch(reversed, arbiter);
            Check(liveB.Source.Schedules == 1 && liveB.Source.timeSamples == 246000 && b.Volume == .7f,
                "new radio starts at its own remembered sample and volume");
            AudioSettings.dspTime += 1;
            liveB.Source.timeSamples = 300005;
            arbiter.Toggle(10, id => { Check(id == 20, "new explicit request displaces current owner"); liveB.Stop(); });
            Dispatch(new[] { liveA, liveB }, arbiter);
            Check(liveA.Source.Schedules == 1 && liveA.Source.UnPauses == 1 && liveA.Source.timeSamples == 141600 && b.PositionSeconds == 6.075,
                "switching back resumes both independently captured positions without rewinding");
            Dispatch(new[] { liveB, liveA }, arbiter, true);
            Check(!liveA.Source.isPlaying && arbiter.ActiveId == 10 && a.Powered, "game suspension stops output without changing chosen owner");
            Dispatch(new[] { liveB, liveA }, arbiter);
            Check(liveA.Source.Schedules == 1 && liveA.Source.UnPauses == 2 && liveB.Source.Schedules == 1, "game resume starts only chosen radio");
        }
    }

    private static void OrderedPlaybackAndPendingLoad()
    {
        var a = new RadioState { TrackPath = track, Powered = true, PositionSeconds = 3 };
        var b = new RadioState { TrackPath = track, Powered = true, PositionSeconds = 4 };
        using (var liveA = new Endpoint(10, a))
        using (var liveB = new Endpoint(20, b))
        {
            liveA.Tick(false); liveB.Tick(true); CompleteRequests(liveA, liveB); liveA.Tick(false); liveB.Tick(true);
            AudioSettings.dspTime += 1; liveA.Source.timeSamples = 192003;
            a.Powered = false; a.Paused = true;
            var trace = new List<int>();
            PlaybackOrder.Tick(new[] { liveB, liveA }, 20, e => e.Id, (endpoint, blocked) =>
            {
                if (endpoint == liveB) Check(!liveA.Source.isPlaying, "old native source is paused before new winner Tick despite reverse enumeration");
                trace.Add(endpoint.Id); endpoint.Tick(blocked);
            }, false);
            Check(trace.SequenceEqual(new[] { 10, 20 }) && liveB.Source.Schedules == 1,
                "production dispatcher performs nonwinner reconciliation before starting winner");
            Check(a.PositionSeconds == 3.95, "dispatcher suspension preserves shared outgoing clock position");
        }
        var arbiter = new GlobalRadioArbiter(); arbiter.Reset(new[] { Record(1), Record(2, false) });
        arbiter.TryGet(1, out a); arbiter.TryGet(2, out b);
        using (var pendingA = new Endpoint(1, a))
        using (var newB = new Endpoint(2, b))
        {
            Dispatch(new[] { pendingA, newB }, arbiter);
            arbiter.Toggle(2, _ => pendingA.Stop());
            Dispatch(new[] { newB, pendingA }, arbiter);
            CompleteRequests(newB, pendingA); Dispatch(new[] { newB, pendingA }, arbiter);
            Check(pendingA.Source.Schedules == 0 && newB.Source.Schedules == 1,
                "old decoder finishing after replacement cannot schedule alongside new radio");
        }
        var ticks = new List<string>();
        PlaybackOrder.Tick(new[] { "winner first", "duplicate", "other" }, 7, name => name == "other" ? 8 : 7,
            (name, blocked) => ticks.Add(name + ":" + blocked), false);
        Check(ticks.SequenceEqual(new[] { "duplicate:True", "other:True", "winner first:False" }),
            "duplicate native identities receive at most one unsuspended tick");
        ticks.Clear();
        PlaybackOrder.Tick(new[] { "a", "b" }, 0, _ => 0, (name, blocked) => ticks.Add(name + ":" + blocked), false);
        Check(ticks.All(t => t.EndsWith("True")), "no active owner suspends every endpoint without fallback selection");
    }

    // In-source formats stream through a worker. A late open on a displaced radio must stay silent.
    private static void StreamedPendingLoad()
    {
        string previous = track;
        track = Path.Combine(AppContext.BaseDirectory, "radio-rule-stream.wav");
        WriteSilentWav(track, 44100 * 3, 44100, 2);
        try
        {
            var arbiter = new GlobalRadioArbiter(); arbiter.Reset(new[] { Record(1), Record(2, false) });
            arbiter.TryGet(1, out var a); arbiter.TryGet(2, out var b);
            using (var pendingA = new Endpoint(1, a))
            using (var newB = new Endpoint(2, b))
            {
                Dispatch(new[] { pendingA, newB }, arbiter);
                arbiter.Toggle(2, _ => pendingA.Stop());
                var deadline = DateTime.UtcNow.AddSeconds(10);
                while (newB.Source.Schedules == 0 && DateTime.UtcNow < deadline) { Dispatch(new[] { newB, pendingA }, arbiter); Thread.Sleep(1); }
                for (int i = 0; i < 20; i++) { Dispatch(new[] { newB, pendingA }, arbiter); Thread.Sleep(1); }
                Check(pendingA.Source.Schedules == 0 && newB.Source.Schedules == 1 && newB.Source.clip != null && newB.Source.clip.Streaming &&
                    newB.Source.timeSamples == 88200 && pendingA.Playback.Status == "Off",
                    "a streamed load finishing after replacement cannot schedule alongside the new radio, which streams from its own position");
            }
        }
        finally { track = previous; }
    }

    // Original 16-bit silence.
    private static void WriteSilentWav(string path, int frames, int rate, int channels)
    {
        int block = channels * 2;
        using (var writer = new BinaryWriter(File.Create(path)))
        {
            writer.Write(System.Text.Encoding.ASCII.GetBytes("RIFF")); writer.Write(36 + frames * block);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("WAVEfmt ")); writer.Write(16);
            writer.Write((short)1); writer.Write((short)channels); writer.Write(rate); writer.Write(rate * block);
            writer.Write((short)block); writer.Write((short)16);
            writer.Write(System.Text.Encoding.ASCII.GetBytes("data")); writer.Write(frames * block);
            writer.Write(new byte[frames * block]);
        }
    }

    private static void FailureAndSessionBoundaries()
    {
        // A cached native clone can create a provisional controller before Load(data)
        // finishes. Its live controller must be rebound to the arbiter's saved state.
        var cachedArbiter = new GlobalRadioArbiter();
        cachedArbiter.Reset(new[] { Record(74, false, 12.5, .7f) });
        var provisional = new RadioState { Kind = 0, Volume = .5f };
        var restoredControllerState = cachedArbiter.Register(74, provisional);
        Check(!ReferenceEquals(restoredControllerState, provisional) && restoredControllerState.PositionSeconds == 12.5,
            "cached native clone adopts canonical saved playback state after registration");
        cachedArbiter.Toggle(74, _ => { });
        Check(restoredControllerState.Powered && !provisional.Powered,
            "power action changes the rebound controller state, not its provisional clone state");
        var pausedRecord = Record(3); pausedRecord.Paused = true;
        var pausedSave = new GlobalRadioArbiter(); pausedSave.Reset(new[] { pausedRecord });
        pausedSave.TryGet(3, out var pausedState);
        Check(pausedSave.ActiveId == 3 && pausedState.Powered && pausedState.Paused,
            "powered paused radio keeps global ownership across reload");
        pausedState.Paused = false;
        Check(pausedState.Powered && !pausedState.Paused && pausedSave.ActiveId == 3,
            "play control resumes paused owner without changing global winner");
        pausedState.Paused = true;
        pausedSave.Toggle(3, _ => { });
        Check(!pausedState.Powered && pausedSave.ActiveId == 0, "powered radio's off action remains consistent even if paused");
        var speakerRecord=Record(1); speakerRecord.Kind=3; speakerRecord.Bass=.8f; speakerRecord.Volume=.75f;
        var speakerArbiter=new GlobalRadioArbiter(); speakerArbiter.Reset(new[]{speakerRecord,Record(10)});
        Check(speakerArbiter.ActiveId==10,"powered speaker never wins arbitration even with lowest identity");
        speakerArbiter.Toggle(1,_=>throw new Exception("speaker must never stop a radio"));
        Check(speakerArbiter.ActiveId==10,"speaker controls cannot replace active radio");
        speakerArbiter.TryGet(1,out var speakerState);
        Check(speakerState.Kind==3&&speakerState.Bass==.8f&&speakerState.Volume==.75f,"canonical speaker state preserves independent controls");
        var speakerStore=new RadioSaveStore(); speakerArbiter.WriteTo(speakerStore);
        var speakerReload=new RadioSaveStore(); Check(speakerReload.Load(speakerStore.Save()),"speaker and radio canonical records persist together");
        speakerReload.TryGet(1,RadioSaveStore.ItemIndex(3),out var savedSpeaker);
        Check(savedSpeaker.Kind==3&&savedSpeaker.Bass==.8f,"cached speaker identity and bass survive capture");
        var arbiter = new GlobalRadioArbiter(); arbiter.Reset(new[] { Record(1), Record(2, false) });
        try { arbiter.Toggle(2, _ => throw new InvalidOperationException("stop failed")); } catch (InvalidOperationException) { }
        arbiter.TryGet(1, out var a); arbiter.TryGet(2, out var b);
        Check(arbiter.ActiveId == 1 && a.Powered && !b.Powered, "stop failure cannot activate replacement alongside old output");
        arbiter.Reset(Array.Empty<RadioRecord>());
        Check(arbiter.ActiveId == 0 && !arbiter.TryGet(1, out _), "new session clears previous ownership and canonical references");
        var store = new RadioSaveStore();
        const string future = "{\"Schema\":9,\"Radios\":[]}";
        var nativeData = new Dictionary<string, string> { [RadioSaveStore.Key] = future, ["OtherMod"] = "retained" };
        Check(!store.Load(nativeData[RadioSaveStore.Key]), "future data stays unreadable instead of being migrated destructively");
        Check(!store.Writable && nativeData[RadioSaveStore.Key] == future && nativeData["OtherMod"] == "retained",
            "unreadable native data and other mod entries remain unchanged");
        var unexpected = arbiter.Register(99, Record(99).ToState());
        Check(!unexpected.Powered && arbiter.ActiveId == 0, "unexpected restored object is not an implicit activation");
        arbiter.Toggle(99, _ => { }); arbiter.Remove(99);
        Check(arbiter.ActiveId == 0 && !arbiter.TryGet(99, out _), "failed creation rollback cannot retain an active identity");
    }
}
