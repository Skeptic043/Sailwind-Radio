using System;
using System.Collections.Generic;
using SailwindRadio.Persistence;

namespace SailwindRadio.Playback
{
    // Owns the desired power state for every known radio, including native cached/unloaded items. A stack holds at
    // most one Radio, at its top, known by StackedId of the stack's base. Its state is the same object as in the base's
    // chain of Stacked states, so it is saved through the base and never on its own. Stacked speakers need no power state.
    internal sealed class GlobalRadioArbiter
    {
        private readonly Dictionary<int, RadioState> states = new Dictionary<int, RadioState>();
        internal int ActiveId { get; private set; }

        // Native instance IDs are positive, so a stacked device never shares an ID with a native item.
        internal static int StackedId(int baseId) => -baseId;

        internal void Reset(IEnumerable<RadioRecord> records)
        {
            states.Clear();
            ActiveId = 0;
            foreach (var record in records)
                Track(record.InstanceId, record.ToState());
            foreach (var pair in states)
                if (WantsPlayback(pair.Value) && (ActiveId == 0 || pair.Key < ActiveId)) ActiveId = pair.Key;
            // Legacy saves may contain several powered radios. Resolve once over the entire document,
            // before any native prefab is restored, rather than letting streaming order choose a winner.
            foreach (var pair in states)
                if (pair.Value.Role == DeviceRole.Radio && pair.Key != ActiveId && pair.Value.Powered) TurnOff(pair.Value);
        }

        internal RadioState Register(int id, RadioState candidate)
        {
            if (states.TryGetValue(id, out var existing)) return existing;
            // A newly discovered object is not an explicit power-on request.
            if (candidate.Role == DeviceRole.Radio && candidate.Powered) TurnOff(candidate);
            var stacked = StackedRadio(candidate);
            if (stacked != null && stacked.Powered) TurnOff(stacked);
            Track(id, candidate);
            return candidate;
        }

        private void Track(int id, RadioState state)
        {
            states.Add(id, state);
            var stacked = StackedRadio(state);
            if (stacked != null) states.Add(StackedId(id), stacked);
        }

        // The arbiter ID of the Radio in a device's chain, or 0 when it holds none.
        internal static int RadioId(int id, RadioState state) =>
            state.Role == DeviceRole.Radio ? id : StackedRadio(state) != null ? StackedId(id) : 0;

        private static RadioState StackedRadio(RadioState state) => state.Above().Find(stacked => stacked.Role == DeviceRole.Radio);

        internal bool TryGet(int id, out RadioState state) => states.TryGetValue(id, out state);

        internal void Remove(int id)
        {
            states.Remove(id);
            if (ActiveId == id) ActiveId = 0;
        }

        // A Radio stacking or unstacking keeps its state object and whether it is the one playing.
        // Changes nothing and returns false when there is no state to move or its new ID is taken.
        internal bool Rename(int from, int to)
        {
            if (!states.TryGetValue(from, out var state) || states.ContainsKey(to)) return false;
            states.Remove(from);
            states.Add(to, state);
            if (ActiveId == from) ActiveId = to;
            return true;
        }

        internal void Toggle(int id, Action<int> captureAndStop)
        {
            if (!states.TryGetValue(id, out var selected) || selected.Role != DeviceRole.Radio) return;
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
                if (pair.Key < 0) continue;
                int index = store.TryGetAny(pair.Key, out var record) ? record.PrefabIndex : RadioSaveStore.ItemIndex(pair.Value.Kind);
                store.Put(RadioRecord.Capture(pair.Key, index, pair.Value));
            }
        }

        private static bool WantsPlayback(RadioState state) => state.Role == DeviceRole.Radio && state.Powered;
        private static void TurnOff(RadioState state) { state.Powered = false; state.Paused = true; }
    }
}
