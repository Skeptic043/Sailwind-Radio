using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.Serialization;
using System.Runtime.Serialization.Json;
using System.Text;

namespace SailwindRadio.Persistence
{
    [DataContract]
    public sealed class RadioRecord
    {
        [DataMember] public int InstanceId;
        [DataMember] public int PrefabIndex;
        [DataMember] public string TrackPath;
        [DataMember] public double PositionSeconds;
        [DataMember] public bool Powered;
        [DataMember] public bool Paused;
        [DataMember] public float Volume;
        [DataMember] public int Kind;
        [DataMember] public float Bass = .5f;
        [DataMember] public float LocalVolume = 1f;
        [DataMember] public bool SpeakerEnabled = true;
        [DataMember] public bool Shuffle;
        [DataMember] public bool CollectionsInitialized;
        [DataMember] public string[] SelectedCollections;

        [OnDeserializing]
        private void Defaults(StreamingContext context) { Bass = .5f; LocalVolume = 1f; SpeakerEnabled = true; }

        public RadioState ToState() => new RadioState { TrackPath = TrackPath ?? "", PositionSeconds = PositionSeconds,
            Powered = Powered, Paused = Paused, Volume = Volume, Kind = Kind, Bass = Bass, LocalVolume = LocalVolume, SpeakerEnabled = SpeakerEnabled,
            Shuffle = Shuffle, CollectionsInitialized = CollectionsInitialized,
            SelectedCollections = SelectedCollections == null ? Array.Empty<string>() : (string[])SelectedCollections.Clone() };

        public static RadioRecord Capture(int id, int prefab, RadioState state) => new RadioRecord {
            InstanceId = id, PrefabIndex = prefab, TrackPath = state.TrackPath ?? "", PositionSeconds = state.PositionSeconds,
            Powered = state.Powered, Paused = state.Paused, Volume = state.Volume, Kind = state.Kind, Bass = state.Bass,
            LocalVolume = state.LocalVolume, SpeakerEnabled = state.SpeakerEnabled,
            Shuffle = state.Shuffle, CollectionsInitialized = state.CollectionsInitialized,
            SelectedCollections = state.SelectedCollections == null ? Array.Empty<string>() : (string[])state.SelectedCollections.Clone() };
    }

    [DataContract]
    public sealed class RadioSaveDocument
    {
        [DataMember(IsRequired = true)] public int Schema = 3;
        [DataMember(IsRequired = true)] public List<RadioRecord> Radios = new List<RadioRecord>();
    }

    // Uses only a string inside the native dictionary. Native deserialization never needs this assembly.
    public sealed class RadioSaveStore
    {
        public const string Key = "Skeptic043.SailwindRadio.v1";
        public const int DonorIndex = 138;
        // Fixed slots are part of the native save identity. Never derive them from directory length.
        public const int FirstItemIndex = 43040;
        public static int ItemIndex(int kind) => kind >= 0 && kind < 4 ? FirstItemIndex + kind :
            throw new ArgumentOutOfRangeException(nameof(kind));
        public static bool IsItemIndex(int index) => index >= FirstItemIndex && index < FirstItemIndex + 4;
        public static bool CanClaimItemSlots(Func<int, bool> occupied, out int collision)
        {
            if (occupied == null) throw new ArgumentNullException(nameof(occupied));
            for (int kind = 0; kind < 4; kind++)
                if (occupied(ItemIndex(kind))) { collision = ItemIndex(kind); return false; }
            collision = -1;
            return true;
        }
        public static bool HasUniqueNativeOwner(int id, IEnumerable<int> nativeIds, out int count)
        {
            if (nativeIds == null) throw new ArgumentNullException(nameof(nativeIds));
            count = 0;
            if (id <= 0) return false;
            foreach (int candidate in nativeIds)
                if (candidate == id) count++;
            return count == 1;
        }
        public const int MaximumRecords = 2048;
        public const int MaximumJsonCharacters = 4 * 1024 * 1024;
        private readonly Dictionary<int, RadioRecord> records = new Dictionary<int, RadioRecord>();
        public IEnumerable<RadioRecord> Records => records.Values;
        public bool Writable { get; private set; } = true;
        public string Problem { get; private set; } = "";

        public bool Load(string json)
        {
            records.Clear();
            Writable = true;
            Problem = "";
            if (String.IsNullOrEmpty(json)) return true;
            try
            {
                if (json.Length > MaximumJsonCharacters) throw new SerializationException("radio data is too large");
                RadioSaveDocument document;
                using (var stream = new MemoryStream(Encoding.UTF8.GetBytes(json)))
                    document = (RadioSaveDocument)new DataContractJsonSerializer(typeof(RadioSaveDocument)).ReadObject(stream);
                if (document == null || document.Schema < 1 || document.Schema > 3 || document.Radios == null)
                    throw new SerializationException("unsupported radio save schema");
                if (document.Radios.Count > MaximumRecords) throw new SerializationException("too many radio records");
                foreach (RadioRecord record in document.Radios)
                {
                    Validate(record);
                    if (records.ContainsKey(record.InstanceId)) throw new SerializationException("duplicate radio identity");
                    records.Add(record.InstanceId, record);
                }
                return true;
            }
            catch (Exception ex) when (ex is SerializationException || ex is ArgumentException || ex is System.Xml.XmlException || ex is FormatException)
            {
                records.Clear();
                Writable = false;
                Problem = ex.Message;
                return false;
            }
        }

        public bool TryGet(int id, int prefab, out RadioRecord record)
        {
            if (records.TryGetValue(id, out record) && record.PrefabIndex == prefab) return true;
            record = null;
            return false;
        }

        public bool TryGetAny(int id, out RadioRecord record) => records.TryGetValue(id, out record);

        public bool MigrateLegacy(int id, int kind)
        {
            if (!Writable || !records.TryGetValue(id, out var record) || record.PrefabIndex != DonorIndex || record.Kind != kind)
                return false;
            record.PrefabIndex = ItemIndex(kind);
            try { Save(); return true; }
            catch (SerializationException) { record.PrefabIndex = DonorIndex; return false; }
        }

        public void RevertLegacyMigration(int id, int kind)
        {
            if (records.TryGetValue(id, out var record) && record.Kind == kind && record.PrefabIndex == ItemIndex(kind))
                record.PrefabIndex = DonorIndex;
        }

        public bool TryAdoptMissing(int id, int index, RadioState state, bool dataWasAbsent)
        {
            if (!dataWasAbsent || !Writable || records.ContainsKey(id)) return false;
            Put(RadioRecord.Capture(id, index, state));
            try { Save(); return true; }
            catch (SerializationException) { records.Remove(id); return false; }
        }

        public void Put(RadioRecord record)
        {
            if (!Writable) throw new InvalidOperationException("Existing radio data is preserved because it could not be read");
            Validate(record);
            if (!records.ContainsKey(record.InstanceId) && records.Count >= MaximumRecords)
                throw new InvalidOperationException("Radio save capacity reached");
            records[record.InstanceId] = record;
        }

        public void Remove(int id)
        {
            if (Writable) records.Remove(id);
        }

        public string Save()
        {
            if (!Writable) throw new InvalidOperationException("Unreadable radio data must not be overwritten");
            var document = new RadioSaveDocument { Radios = new List<RadioRecord>(records.Values) };
            document.Radios.Sort((a, b) => a.InstanceId.CompareTo(b.InstanceId));
            using (var stream = new MemoryStream())
            {
                new DataContractJsonSerializer(typeof(RadioSaveDocument)).WriteObject(stream, document);
                string json = Encoding.UTF8.GetString(stream.ToArray());
                // The caller assigns the native modData value only after this returns. Refuse an
                // unreadable replacement while preserving the previous native extension value.
                if (json.Length > MaximumJsonCharacters) throw new SerializationException("radio data is too large");
                return json;
            }
        }

        private static void Validate(RadioRecord record)
        {
            if (record == null || record.InstanceId <= 0 || record.Kind < 0 || record.Kind > 3 ||
                (record.PrefabIndex != DonorIndex && record.PrefabIndex != ItemIndex(record.Kind)))
                throw new SerializationException("invalid radio identity or donor");
            if (Double.IsNaN(record.PositionSeconds) || Double.IsInfinity(record.PositionSeconds) || record.PositionSeconds < 0 ||
                Single.IsNaN(record.Volume) || Single.IsInfinity(record.Volume) || record.Volume < 0 || record.Volume > 1 ||
                record.Kind < 0 || record.Kind > 3 || Single.IsNaN(record.Bass) || Single.IsInfinity(record.Bass) || record.Bass < 0 || record.Bass > 1 ||
                Single.IsNaN(record.LocalVolume) || Single.IsInfinity(record.LocalVolume) || record.LocalVolume < 0 || record.LocalVolume > 1 ||
                (record.TrackPath != null && record.TrackPath.Length > 32768))
                throw new SerializationException("invalid radio playback state");
            if (record.SelectedCollections != null)
            {
                if (record.SelectedCollections.Length > 256) throw new SerializationException("too many selected collections");
                foreach (var root in record.SelectedCollections)
                    if (string.IsNullOrWhiteSpace(root) || root.Length > 32768) throw new SerializationException("invalid collection root");
            }
        }
    }
}
