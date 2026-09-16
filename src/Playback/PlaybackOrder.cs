using System;
using System.Collections.Generic;

namespace SailwindRadio.Playback
{
    internal static class PlaybackOrder
    {
        // Defer the sole winner until every other endpoint has reconciled its stopped state.
        // Even duplicate native identities cannot result in two unsuspended endpoints.
        internal static void Tick<T>(IEnumerable<T> endpoints, int activeId, Func<T, int> identity,
            Action<T, bool> tick, bool suspended)
        {
            bool found = false;
            T winner = default(T);
            foreach (T endpoint in endpoints)
            {
                if (!found && activeId > 0 && identity(endpoint) == activeId)
                {
                    winner = endpoint;
                    found = true;
                }
                else tick(endpoint, true);
            }
            if (found) tick(winner, suspended);
        }
    }
}
