using System;
using System.Collections.Generic;
using SailwindRadio.Library;

namespace SailwindRadio.Playback
{
    // Every radio's playback engine and queue. A failure inside one radio's work is contained
    // there: its engine is rebuilt and its track skipped, and after repeated failures that radio
    // alone is switched off through the normal power path. Main thread only.
    internal sealed class RadioPlaybackSet<TKey> where TKey : class
    {
        internal const int PowerOffFaults = 3;
        internal const double FaultWindowSeconds = 60;
        internal const int WarningsPerWindow = 5;

        private sealed class Channel
        {
            internal RadioState State;
            internal RadioPlayback Engine;
            internal RadioQueue Queue;
            internal readonly List<double> Faults = new List<double>();
            // Switched off after repeated faults. A fresh engine starts once the radio is off then on again.
            internal bool Quarantined, SeenOff;
            // Held until the power-off takes effect, which the game refuses while loading or saving is blocked.
            internal string OffNotice;
        }

        private readonly Dictionary<TKey, Channel> channels = new Dictionary<TKey, Channel>();
        private readonly List<KeyValuePair<TKey, Exception>> faults = new List<KeyValuePair<TKey, Exception>>();
        private readonly List<KeyValuePair<TKey, Channel>> ticking = new List<KeyValuePair<TKey, Channel>>();
        private readonly Func<TKey, RadioState, RadioPlayback> createEngine;
        private readonly Action<TKey, RadioPlayback> attach;
        private readonly Action<TKey> powerOff;
        private readonly Action<string> warn;
        private readonly List<double> warnings = new List<double>();
        private int suppressed;
        private LibrarySnapshot snapshot;

        // attach receives each new engine, or null while a radio has none.
        internal RadioPlaybackSet(Func<TKey, RadioState, RadioPlayback> createEngine, Action<TKey, RadioPlayback> attach,
            Action<TKey> powerOff, Action<string> warn)
        {
            this.createEngine = createEngine;
            this.attach = attach;
            this.powerOff = powerOff;
            this.warn = warn ?? delegate { };
        }

        internal ICollection<TKey> Keys { get { return channels.Keys; } }

        // A radio whose player cannot be built is handled like any other playback fault at the next Tick.
        internal void Add(TKey key, RadioState state)
        {
            if (channels.ContainsKey(key)) return;
            var channel = new Channel { State = state };
            channels.Add(key, channel);
            try
            {
                channel.Queue = NewQueue(state);
                channel.Engine = createEngine(key, state);
                attach(key, channel.Engine);
            }
            catch (Exception error) { Record(key, error); }
        }

        internal void Remove(TKey key)
        {
            Channel channel;
            if (!channels.TryGetValue(key, out channel)) return;
            channels.Remove(key);
            RadioPlayback engine = channel.Engine;
            channel.Engine = null;
            try { attach(key, null); }
            catch (Exception error) { warn("Could not release a radio player: " + error.Message); }
            try { if (engine != null) engine.Dispose(); }
            catch (Exception error) { warn("Could not release a radio player: " + error.Message); }
        }

        // Applies a completed library scan to every queue. Each radio's work is contained.
        internal void SetSnapshot(LibrarySnapshot latest)
        {
            snapshot = latest;
            foreach (var pair in channels)
            {
                Channel channel = pair.Value;
                if (channel.Quarantined) continue;
                Run(pair.Key, (engine, queue) => { if (queue.ApplySnapshot(latest)) engine.SetTrack(channel.State.TrackPath); });
            }
        }

        // Save hook. A capture failure is contained so the remaining radios are still saved.
        internal void Capture(TKey key) { Run(key, (engine, queue) => engine.CapturePosition()); }

        // Runs one radio's playback or queue work. A failure is recorded and recovered at the next
        // Tick, never inside a caller's enumeration. Returns false after a failure or when idle.
        // The suspend hook rethrows so the arbiter's stop-before-start rule still applies.
        internal bool Run(TKey key, Action<RadioPlayback, RadioQueue> work, bool rethrow = false)
        {
            Channel channel;
            if (!channels.TryGetValue(key, out channel) || channel.Quarantined || channel.Engine == null) return false;
            try
            {
                work(channel.Engine, channel.Queue);
                return true;
            }
            catch (Exception error)
            {
                Record(key, error);
                if (rethrow) throw;
                return false;
            }
        }

        // Ticks every radio in arbitration order, containing each radio's failure.
        internal void Tick(double now, int activeId, Func<TKey, int> identity, Action<TKey, RadioPlayback, RadioQueue, bool> tick, bool suspended)
        {
            Recover(now);
            ticking.Clear();
            foreach (var pair in channels) if (!pair.Value.Quarantined && pair.Value.Engine != null) ticking.Add(pair);
            PlaybackOrder.Tick(ticking, activeId, pair => identity(pair.Key), (pair, blocked) =>
            {
                try { tick(pair.Key, pair.Value.Engine, pair.Value.Queue, blocked); }
                catch (Exception error) { Record(pair.Key, error); }
            }, suspended);
            ticking.Clear();
            Recover(now);
        }

        // Save and suspend hooks. Each engine is isolated and reported with the caller's message.
        internal void ForEachEngine(Action<TKey, RadioPlayback> action, string failure)
        {
            foreach (var pair in channels)
            {
                if (pair.Value.Engine == null) continue;
                try { action(pair.Key, pair.Value.Engine); }
                catch (Exception error) { warn(failure + ": " + error.Message); }
            }
        }

        internal void Clear()
        {
            foreach (TKey key in new List<TKey>(channels.Keys)) Remove(key);
            faults.Clear();
        }

        private void Record(TKey key, Exception error)
        {
            foreach (var fault in faults) if (ReferenceEquals(fault.Key, key)) return;
            faults.Add(new KeyValuePair<TKey, Exception>(key, error));
        }

        private void Recover(double now)
        {
            foreach (var pair in channels)
            {
                Channel quarantined = pair.Value;
                if (!quarantined.Quarantined) continue;
                if (quarantined.OffNotice != null) SwitchOff(pair.Key, quarantined, now);
                else if (!quarantined.State.Powered) quarantined.SeenOff = true;
                else if (quarantined.SeenOff) faults.Add(new KeyValuePair<TKey, Exception>(pair.Key, null));
            }
            if (faults.Count == 0) return;
            var pending = faults.ToArray();
            faults.Clear();
            foreach (var fault in pending)
            {
                Channel channel;
                if (!channels.TryGetValue(fault.Key, out channel)) continue;
                if (fault.Value == null) Restore(fault.Key, channel);
                else Contain(fault.Key, channel, fault.Value, now);
            }
        }

        private void Contain(TKey key, Channel channel, Exception error, double now)
        {
            channel.Faults.RemoveAll(time => now - time >= FaultWindowSeconds || time > now);
            channel.Faults.Add(now);
            string track = channel.State.TrackPath ?? "";
            Release(key, channel);
            if (channel.Faults.Count < PowerOffFaults)
            {
                try
                {
                    SkipTrack(channel, track);
                    channel.Engine = createEngine(key, channel.State);
                    attach(key, channel.Engine);
                    if (channel.Faults.Count == 1)
                        Warn(now, "A radio stopped after a playback error and restarted with the next track: " + error);
                    return;
                }
                catch (Exception again)
                {
                    Release(key, channel);
                    error = again;
                }
            }
            channel.Quarantined = true;
            channel.SeenOff = false;
            channel.Faults.Clear();
            channel.OffNotice = "A radio kept failing and was switched off. Turn it on again to retry. Last error: " + error;
            SwitchOff(key, channel, now);
        }

        private void SwitchOff(TKey key, Channel channel, double now)
        {
            try { powerOff(key); }
            catch (Exception failure)
            {
                channel.OffNotice = null;
                Warn(now, "Could not switch off a failing radio: " + failure.Message);
                return;
            }
            if (channel.State.Powered) return;
            Warn(now, channel.OffNotice);
            channel.OffNotice = null;
        }

        private void Restore(TKey key, Channel channel)
        {
            channel.Quarantined = false;
            channel.SeenOff = false;
            try
            {
                channel.Engine = createEngine(key, channel.State);
                attach(key, channel.Engine);
            }
            catch (Exception error)
            {
                Release(key, channel);
                channel.Quarantined = true;
                warn("Could not restart a radio: " + error.Message);
            }
        }

        private void Release(TKey key, Channel channel)
        {
            RadioPlayback failed = channel.Engine;
            channel.Engine = null;
            try { attach(key, null); }
            catch (Exception) { }
            // The fault being handled is the one reported. A second failure while releasing adds nothing.
            try { if (failed != null) failed.Dispose(); }
            catch (Exception) { }
        }

        private void SkipTrack(Channel channel, string track)
        {
            try { channel.Queue.TrackFailed(track); }
            catch (Exception)
            {
                // A queue that fails is rebuilt from the library before skipping.
                channel.Queue = NewQueue(channel.State);
                channel.Queue.TrackFailed(track);
            }
        }

        private RadioQueue NewQueue(RadioState state)
        {
            var queue = new RadioQueue(state);
            if (snapshot != null) queue.ApplySnapshot(snapshot);
            return queue;
        }

        // At most a few messages a minute across all radios, with a count of what was held back.
        private void Warn(double now, string message)
        {
            warnings.RemoveAll(time => now - time >= FaultWindowSeconds || time > now);
            if (warnings.Count >= WarningsPerWindow) { suppressed++; return; }
            warnings.Add(now);
            if (suppressed > 0) message += " (" + suppressed + " similar radio errors were not logged)";
            suppressed = 0;
            warn(message);
        }
    }
}
