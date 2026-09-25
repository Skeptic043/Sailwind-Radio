using System;
using System.Collections.Generic;
using SailwindRadio.Persistence;

namespace SailwindRadio.Playback
{
    // Owns the desired power state for every known radio, including native cached/unloaded items.
    internal sealed class GlobalRadioArbiter
    {
        private readonly Dictionary<int, RadioState> states = new Dictionary<int, RadioState>();
        internal int ActiveId { get; private set; }

        internal void Reset(IEnumerable<RadioRecord> records)
        {
            states.Clear();
            ActiveId = 0;
            foreach (var record in records)
            {
                var state = record.ToState();
                states.Add(record.InstanceId, state);
                if (WantsPlayback(state) && (ActiveId == 0 || record.InstanceId < ActiveId)) ActiveId = record.InstanceId;
            }
            // Legacy saves may contain several powered radios. Resolve once over the entire document,
            // before any native prefab is restored, rather than letting streaming order choose a winner.
            foreach (var pair in states)
                if (pair.Value.Kind == 0 && pair.Key != ActiveId && pair.Value.Powered) TurnOff(pair.Value);
        }

        internal RadioState Register(int id, RadioState candidate)
        {
            if (states.TryGetValue(id, out var existing)) return existing;
            // A newly discovered object is not an explicit power-on request.
            if (candidate.Kind == 0 && candidate.Powered) TurnOff(candidate);
            states.Add(id, candidate);
            return candidate;
        }

        internal bool TryGet(int id, out RadioState state) => states.TryGetValue(id, out state);

        internal void Remove(int id)
        {
            states.Remove(id);
            if (ActiveId == id) ActiveId = 0;
        }

        internal void Toggle(int id, Action<int> captureAndStop)
        {
            if (!states.TryGetValue(id, out var selected) || selected.Kind != 0) return;
            if (selected.Powered)
            {
                captureAndStop(id);
                TurnOff(selected);
                if (ActiveId == id) ActiveId = 0;
                return;
            }
            // Stop the prior live endpoint before making the replacement eligible to start. If the
            // stop callback fails, this operation never powers on a second radio.
            foreach (var pair in states)
            {
                if (pair.Key == id || !WantsPlayback(pair.Value)) continue;
                captureAndStop(pair.Key);
                TurnOff(pair.Value);
            }
            selected.Powered = true;
            selected.Paused = false;
            ActiveId = id;
        }

        internal void WriteTo(RadioSaveStore store)
        {
            foreach (var pair in states)
            {
                int index = store.TryGetAny(pair.Key, out var record) ? record.PrefabIndex : RadioSaveStore.ItemIndex(pair.Value.Kind);
                store.Put(RadioRecord.Capture(pair.Key, index, pair.Value));
            }
        }

        private static bool WantsPlayback(RadioState state) => state.Kind == 0 && state.Powered;
        private static void TurnOff(RadioState state) { state.Powered = false; state.Paused = true; }
    }
}
