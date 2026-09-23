using System;
using System.Collections.Generic;
using System.Linq;
using System.Runtime.Serialization;
using SailwindRadio;
using SailwindRadio.Persistence;

internal static class Program
{
    private static int checks;
    private static void Check(bool condition, string description)
    {
        checks++;
        if (!condition) throw new Exception(description);
    }
    private static RadioRecord Record(int id = 42) => RadioRecord.Capture(id, RadioSaveStore.DonorIndex,
        new RadioState { TrackPath = @"E:\Music\Ocean\song with ☀.ogg", PositionSeconds = 87.125,
            Powered = true, Paused = false, Volume = .625f });

    private static void Reject(string json, string reason)
    {
        var store = new RadioSaveStore();
        store.Put(Record());
        Check(!store.Load(json), reason);
        Check(!store.Writable && !store.Records.Any(), "failed document must not partially restore");
        bool blocked = false;
        try { store.Save(); } catch (InvalidOperationException) { blocked = true; }
        Check(blocked, "failed document must not be overwritten");
    }

    public static int Main()
    {
        var store = new RadioSaveStore();
        Check(store.Load(null) && store.Writable, "new game starts empty");
        store.Put(Record());
        store.Put(Record(91));
        string json = store.Save();
        var restored = new RadioSaveStore();
        Check(restored.Load(json), "valid document round trips");
        Check(restored.TryGet(42, RadioSaveStore.DonorIndex, out var record), "stable native identity survives");
        Check(record.PositionSeconds == 87.125 && record.Volume == .625f && record.Powered && !record.Paused,
            "independent playback fields survive");
        Check(record.TrackPath == Record().TrackPath, "unicode and nested folder paths survive");
        Check(!restored.TryGet(42, 139, out _), "same id on another native prefab must not become radio");
        Check(!restored.TryGet(43, RadioSaveStore.DonorIndex, out _), "unrelated donor item must remain donor");
        var changed = record.ToState();
        changed.PositionSeconds = 1000;
        changed.Powered = false;
        restored.Put(RadioRecord.Capture(42, RadioSaveStore.DonorIndex, changed));
        var afterStreaming = new RadioSaveStore();
        Check(afterStreaming.Load(restored.Save()), "save after one live radio changes remains readable");
        Check(afterStreaming.TryGet(91, RadioSaveStore.DonorIndex, out var cached) && cached.PositionSeconds == 87.125,
            "temporarily absent cached radio remains in native extension");
        Check(afterStreaming.TryGet(42, RadioSaveStore.DonorIndex, out var live) && live.PositionSeconds == 1000 && !live.Powered,
            "changed live radio keeps independent state");
        Check(restored.Load(null) && !restored.Records.Any(), "old save with no radio data must clear previous slot records");
        Reject("{", "malformed JSON is isolated");
        Reject("{\"Schema\":4,\"Radios\":[]}", "future schema is preserved");
        Reject("{\"Radios\":[]}", "missing schema is rejected");
        Reject("{\"Schema\":1,\"Radios\":null}", "null collection is rejected");
        Reject(json.Replace("\"InstanceId\":91", "\"InstanceId\":42"), "duplicate identity is rejected");
        Reject(json.Replace("\"PrefabIndex\":138", "\"PrefabIndex\":999"), "foreign donor reference is rejected");
        Reject(json.Replace("\"PositionSeconds\":87.125", "\"PositionSeconds\":-1"), "negative time is rejected");
        Reject(json.Replace("\"Volume\":0.625", "\"Volume\":2"), "invalid volume is rejected");
        var invalid = Record();
        invalid.PositionSeconds = Double.NaN;
        bool nanRejected = false;
        try { new RadioSaveStore().Put(invalid); } catch (SerializationException) { nanRejected = true; }
        Check(nanRejected, "nonfinite engine state never serializes");
        var old = new RadioSaveStore();
        Check(old.Load("{\"Schema\":1,\"Radios\":[{\"InstanceId\":10,\"PrefabIndex\":138,\"Powered\":true,\"Paused\":false,\"PositionSeconds\":12.5,\"Volume\":0.8}]}"),
            "actual schema1 payload without new fields still loads");
        old.TryGet(10,138,out var oldRecord);
        var oldState=oldRecord.ToState();
        Check(oldState.Kind==0 && oldState.Bass==.5f && !oldState.Shuffle && !oldState.CollectionsInitialized && oldState.SelectedCollections.Length==0,
            "schema1 migrates neutral device and collection defaults");
        Check(oldState.LocalVolume == 1f && oldState.SpeakerEnabled, "schema1 preserves prior audible output defaults");
        Check(old.Save().Contains("\"Schema\":3"),"new schema blocks unsafe downgrade that would discard independent audio controls");
        var secondGeneration = new RadioSaveStore();
        Check(secondGeneration.Load("{\"Schema\":2,\"Radios\":[{\"InstanceId\":11,\"PrefabIndex\":138,\"Kind\":3,\"Bass\":0.4,\"Volume\":0.7,\"PositionSeconds\":99,\"CollectionsInitialized\":true,\"SelectedCollections\":[\"E:\\\\Music\"]}]}"), "schema2 records load without new controls");
        secondGeneration.TryGet(11,138,out var oldSpeaker);
        Check(oldSpeaker.LocalVolume == 1 && oldSpeaker.SpeakerEnabled && oldSpeaker.Bass == .4f && oldSpeaker.Volume == .7f && oldSpeaker.PositionSeconds == 99,
            "schema2 migration preserves existing bass and level with new controls neutral");
        for(int kind=0;kind<4;kind++)
        {
            var state=new RadioState { Kind=kind,Bass=.75f,Volume=.35f,LocalVolume=.2f,SpeakerEnabled=false,Shuffle=true,CollectionsInitialized=true,
                SelectedCollections=new[]{@"E:\Music\Crossings",@"E:\Music\Local"},TrackPath="song.ogg",PositionSeconds=133.25 };
            var captured=RadioRecord.Capture(100+kind,138,state);
            state.SelectedCollections[0]="mutated";
            Check(captured.SelectedCollections[0]==@"E:\Music\Crossings","capturing selections isolates mutable array");
            old.Put(captured); var roundtrip=new RadioSaveStore(); Check(roundtrip.Load(old.Save()),"all device kinds roundtrip");
            roundtrip.TryGet(100+kind,138,out var loaded);
            var restoredState=loaded.ToState();
            Check(restoredState.Kind==kind && restoredState.Bass==.75f && restoredState.Volume==.35f && restoredState.LocalVolume==.2f && !restoredState.SpeakerEnabled && restoredState.Shuffle &&
                restoredState.CollectionsInitialized && restoredState.SelectedCollections.SequenceEqual(captured.SelectedCollections),"expanded state survives native extension");
            restoredState.SelectedCollections[0]="changed";
            Check(loaded.SelectedCollections[0]!=restoredState.SelectedCollections[0],"restoration isolates selection array");
        }
        foreach(var value in new[]{-1,4,999}) Reject(json.Replace("\"Kind\":0","\"Kind\":"+value),"unknown device kind fails closed");
        Reject(json.Replace("\"Bass\":0.5","\"Bass\":2"),"invalid bass fails closed");
        Reject(json.Replace("\"LocalVolume\":1", "\"LocalVolume\":-1"), "negative local volume fails closed");
        Reject(json.Replace("\"LocalVolume\":1", "\"LocalVolume\":2"), "excessive local volume fails closed");
        foreach (float value in new[] { float.NaN, float.PositiveInfinity, float.NegativeInfinity })
        {
            var badLocal = Record(); badLocal.LocalVolume = value;
            bool rejected = false;
            try { new RadioSaveStore().Put(badLocal); } catch (SerializationException) { rejected = true; }
            Check(rejected, "nonfinite local volume never serializes");
        }
        Reject(json.Replace("\"SelectedCollections\":[]","\"SelectedCollections\":[null]"),"invalid collection roots fail closed");
        var largeStore=new RadioSaveStore();
        for(int i=0;i<127;i++)
            largeStore.Put(RadioRecord.Capture(10000+i,138,new RadioState{TrackPath=new string('a',32768)}));
        var boundaryRecord=RadioRecord.Capture(10127,138,new RadioState());
        largeStore.Put(boundaryRecord);
        int remaining=RadioSaveStore.MaximumJsonCharacters-largeStore.Save().Length;
        Check(remaining>=0 && remaining<32768,"boundary fixture fits valid per-record path limits");
        boundaryRecord.TrackPath=new string('b',remaining);
        largeStore.Put(boundaryRecord);
        string boundaryJson=largeStore.Save();
        Check(boundaryJson.Length==RadioSaveStore.MaximumJsonCharacters,"exact maximum JSON size may be emitted");
        var boundaryReload=new RadioSaveStore();
        Check(boundaryReload.Load(boundaryJson)&&boundaryReload.Save()==boundaryJson,"exact size boundary roundtrips without losing records");
        boundaryRecord.TrackPath+="c";
        largeStore.Put(boundaryRecord);
        var guardedNativeData=new Dictionary<string,string>{{RadioSaveStore.Key,"previous"},{"OtherMod","untouched"}};
        bool emissionRejected=false;
        try { guardedNativeData[RadioSaveStore.Key]=largeStore.Save(); }
        catch(SerializationException){emissionRejected=true;}
        Check(emissionRejected&&guardedNativeData[RadioSaveStore.Key]=="previous"&&guardedNativeData["OtherMod"]=="untouched",
            "oversized valid records cannot emit an unreadable save or replace native extension value");
        Check(largeStore.Writable,"failed size emission does not mark valid runtime state unreadable");
        var nativeDictionary = new Dictionary<string, string> { ["OtherMod"] = "untouched", [RadioSaveStore.Key] = json };
        nativeDictionary.Remove(RadioSaveStore.Key);
        Check(nativeDictionary.Count == 1 && nativeDictionary["OtherMod"] == "untouched", "scoped reset preserves other mod data");
        Check(!json.Contains("SailwindRadio.Persistence") && !json.Contains("Assembly"), "native payload has no custom CLR type dependency");
        store.Remove(42);
        Check(!store.TryGet(42, RadioSaveStore.DonorIndex, out _) && store.TryGet(91, RadioSaveStore.DonorIndex, out _),
            "spawn rollback removes only the newly created instance record");
        Console.WriteLine($"{checks} persistence checks passed. These do not execute Sailwind, Unity rendering, or native save/load.");
        return 0;
    }
}
