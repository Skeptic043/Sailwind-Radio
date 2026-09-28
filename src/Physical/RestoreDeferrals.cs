using System.Collections.Generic;

namespace SailwindRadio.Physical
{
    // Native cached-item spawning creates the live clone before it removes the cached copy, so a
    // cached device briefly has two owners of its item ID. Only an ID still shared after loading
    // has settled is reported, once.
    internal sealed class RestoreDeferrals
    {
        internal const float SettleSeconds = 5f;
        private readonly Dictionary<int, float> firstDeferred = new Dictionary<int, float>();
        private readonly HashSet<int> reported = new HashSet<int>();

        // Returns true when the deferral of this ID should now be reported.
        internal bool Defer(int id, float now, bool loading)
        {
            float first;
            if (!firstDeferred.TryGetValue(id, out first)) firstDeferred[id] = first = now;
            return !loading && now - first >= SettleSeconds && reported.Add(id);
        }

        internal void Restored(int id) { firstDeferred.Remove(id); }

        internal void Clear()
        {
            firstDeferred.Clear();
            reported.Clear();
        }
    }
}
