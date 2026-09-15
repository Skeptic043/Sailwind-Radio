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
        Reject("{\"Schema\":2,\"Radios\":[]}", "future schema is preserved");
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
        var nativeDictionary = new Dictionary<string, string> { ["OtherMod"] = "untouched", [RadioSaveStore.Key] = json };
        nativeDictionary.Remove(RadioSaveStore.Key);
        Check(nativeDictionary.Count == 1 && nativeDictionary["OtherMod"] == "untouched", "scoped reset preserves other mod data");
        Check(!json.Contains("SailwindRadio.Persistence") && !json.Contains("Assembly"), "native payload has no custom CLR type dependency");
        store.Remove(42);
        Check(!store.TryGet(42, RadioSaveStore.DonorIndex, out _) && store.TryGet(91, RadioSaveStore.DonorIndex, out _),
            "spawn rollback removes only the newly created instance record");
        Console.WriteLine($"{checks} persistence checks passed. These do not execute Sailwind, Unity rendering, or native save/load.");
        PlacementChecks.Run();
        return 0;
    }
}
