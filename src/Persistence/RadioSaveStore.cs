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
        // The device stacked on this one, which has no native item while stacked. Its own InstanceId is 0. Omitted
        // when empty, so saves without stacks stay byte for byte as before.
        [DataMember(EmitDefaultValue = false)] public RadioRecord Stacked;

        [OnDeserializing]
        private void Defaults(StreamingContext context) { Bass = .5f; LocalVolume = 1f; SpeakerEnabled = true; }

        public RadioState ToState() => new RadioState { TrackPath = TrackPath ?? "", PositionSeconds = PositionSeconds,
            Powered = Powered, Paused = Paused, Volume = Volume, Kind = Kind, Bass = Bass, LocalVolume = LocalVolume, SpeakerEnabled = SpeakerEnabled,
            Shuffle = Shuffle, CollectionsInitialized = CollectionsInitialized,
            SelectedCollections = SelectedCollections == null ? Array.Empty<string>() : (string[])SelectedCollections.Clone(),
            Stacked = Stacked?.ToState() };

        public static RadioRecord Capture(int id, int prefab, RadioState state) => new RadioRecord {
            InstanceId = id, PrefabIndex = prefab, TrackPath = state.TrackPath ?? "", PositionSeconds = state.PositionSeconds,
            Powered = state.Powered, Paused = state.Paused, Volume = state.Volume, Kind = state.Kind, Bass = state.Bass,
            LocalVolume = state.LocalVolume, SpeakerEnabled = state.SpeakerEnabled,
            Shuffle = state.Shuffle, CollectionsInitialized = state.CollectionsInitialized,
            SelectedCollections = state.SelectedCollections == null ? Array.Empty<string>() : (string[])state.SelectedCollections.Clone(),
            Stacked = state.Stacked == null ? null : Capture(0, RadioSaveStore.ItemIndex(state.Stacked.Kind), state.Stacked) };
    }

    [DataContract]
    public sealed class RadioSaveDocument
    {
        // Schema 4 marks a save holding stacks. Radio 1.3 refuses it and preserves the data rather than dropping a speaker.
        public const int Plain = 3, WithStacks = 4;
        [DataMember(IsRequired = true)] public int Schema = Plain;
        [DataMember(IsRequired = true)] public List<RadioRecord> Radios = new List<RadioRecord>();
    }

    // Uses only a string inside the native dictionary. Native deserialization never needs this assembly.
    public sealed class RadioSaveStore
    {
        public const string Key = "Skeptic043.SailwindRadio.v1";
        public const int DonorIndex = 138;
        // Fixed slots are part of the native save identity. Never derive them from directory length.
        public const int FirstItemIndex = 43040;
        public static int ItemIndex(int kind) => DeviceKind.IsValid(kind) ? FirstItemIndex + kind :
            throw new ArgumentOutOfRangeException(nameof(kind));
        public static bool IsItemIndex(int index) => index >= FirstItemIndex && index < FirstItemIndex + DeviceKind.Count;
        // The walnut slots are required exactly as before. Each regional family present in the model
        // document is claimed whole or skipped, so one occupied slot never leaves a partial set.
        // Returns the ascending kinds to register, or null when a walnut slot is occupied.
        public static int[] ClaimItemKinds(IEnumerable<int> present, Func<int, bool> occupied, Action<int, int> familySkipped, out int collision)
        {
            if (present == null) throw new ArgumentNullException(nameof(present));
            if (occupied == null) throw new ArgumentNullException(nameof(occupied));
            collision = -1;
            for (int kind = 0; kind < 4; kind++)
                if (occupied(ItemIndex(kind))) { collision = ItemIndex(kind); return null; }
            var claimed = new List<int> { 0, 1, 2, 3 };
            var available = new HashSet<int>(present);
            for (int family = 1; family < DeviceKind.Count / 4; family++)
            {
                var members = new List<int>();
                int blocked = -1;
                for (int kind = family * 4; kind < family * 4 + 4; kind++)
                    if (available.Contains(kind))
                    {
                        members.Add(kind);
                        if (blocked < 0 && occupied(ItemIndex(kind))) blocked = ItemIndex(kind);
                    }
                if (blocked >= 0) familySkipped?.Invoke(family, blocked);
                else claimed.AddRange(members);
            }
            return claimed.ToArray();
        }
        public static int DirectoryLength(int[] registered) => ItemIndex(registered[registered.Length - 1]) + 1;
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
                if (document == null || document.Schema < 1 || document.Schema > RadioSaveDocument.WithStacks || document.Radios == null)
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
            if (document.Radios.Exists(record => record.Stacked != null)) document.Schema = RadioSaveDocument.WithStacks;
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
            // Only the walnut set ever used the legacy donor item.
            if (record == null || record.InstanceId <= 0 || !DeviceKind.IsValid(record.Kind) ||
                (record.PrefabIndex != ItemIndex(record.Kind) && !(record.PrefabIndex == DonorIndex && DeviceKind.IsWalnut(record.Kind))))
                throw new SerializationException("invalid radio identity or donor");
            ValidateSettings(record);
            // Each stacked device has no native identity of its own and may stand on the device below it.
            int devices = 1;
            for (var below = record; below.Stacked != null; below = below.Stacked)
            {
                var stacked = below.Stacked;
                if (++devices > DeviceKind.MaximumStack || !DeviceKind.CanStack(below.Kind, stacked.Kind) || stacked.InstanceId != 0 ||
                    stacked.PrefabIndex != ItemIndex(stacked.Kind))
                    throw new SerializationException("invalid device stack");
                ValidateSettings(stacked);
            }
        }

        private static void ValidateSettings(RadioRecord record)
        {
            if (Double.IsNaN(record.PositionSeconds) || Double.IsInfinity(record.PositionSeconds) || record.PositionSeconds < 0 ||
                !Level(record.Volume) || !Level(record.Bass) || !Level(record.LocalVolume) ||
                (record.TrackPath != null && record.TrackPath.Length > 32768))
                throw new SerializationException("invalid radio playback state");
            if (record.SelectedCollections != null)
            {
                if (record.SelectedCollections.Length > 256) throw new SerializationException("too many selected collections");
                foreach (var root in record.SelectedCollections)
                    if (string.IsNullOrWhiteSpace(root) || root.Length > 32768) throw new SerializationException("invalid collection root");
            }
        }

        private static bool Level(float value) => !Single.IsNaN(value) && value >= 0 && value <= 1;
    }
}
