using System;
using System.Runtime.Serialization;
using SailwindRadio;
using SailwindRadio.Persistence;

internal static class MonoMigrationChecks
{
    private static int checks;
    private static void Check(bool condition, string label)
    {
        if (!condition) throw new Exception(label);
        checks++;
    }
    private static int Main()
    {
        try
        {
            foreach (int schema in new[] { 1, 2 })
            {
                var store = new RadioSaveStore();
                string old = "{\"Schema\":" + schema + ",\"Radios\":[{\"InstanceId\":7,\"PrefabIndex\":138,\"Kind\":3,\"Bass\":0.4,\"Volume\":0.7,\"PositionSeconds\":19.5}]}";
                Check(store.Load(old), "old schema loads on installed Mono");
                RadioRecord record;
                Check(store.TryGet(7, 138, out record) && record.LocalVolume == 1f && record.SpeakerEnabled,
                    "OnDeserializing supplies new neutral fields on installed Mono");
                Check(record.Bass == .4f && record.Volume == .7f && record.PositionSeconds == 19.5,
                    "prior playback controls survive installed Mono migration");
                Check(store.Save().Contains("\"Schema\":3"), "writer upgrades schema on installed Mono");
            }
            var current = new RadioSaveStore();
            current.Put(RadioRecord.Capture(9, 138, new RadioState { LocalVolume = .2f, SpeakerEnabled = false }));
            string json = current.Save();
            var restored = new RadioSaveStore();
            Check(restored.Load(json), "schema3 loads on installed Mono");
            RadioRecord saved;
            Check(restored.TryGet(9, 138, out saved) && saved.LocalVolume == .2f && !saved.SpeakerEnabled,
                "explicit false and local volume survive installed Mono roundtrip");
            Check(!restored.Load("{\"Schema\":4,\"Radios\":[]}") && !restored.Writable, "future schema remains protected on installed Mono");
            var invalid = RadioRecord.Capture(1, 138, new RadioState { LocalVolume = float.NaN });
            bool rejected = false;
            try { new RadioSaveStore().Put(invalid); } catch (SerializationException) { rejected = true; }
            Check(rejected, "nonfinite local control rejected on installed Mono");
            Console.WriteLine(checks + " persistence migration checks passed on the installed Unity Mono runtime. Native game save callbacks remain a live gate.");
            return 0;
        }
        catch (Exception error) { Console.Error.WriteLine(error); return 1; }
    }
}
