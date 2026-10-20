using System;
using System.Collections.Generic;

namespace SailwindRadio.Models
{
    // Voice caps for display one-shots. Each radio plays on its own pool and steals its oldest voice when
    // the pool is full. Across all radios the oldest voice is stolen once total voices play. Times are seconds.
    internal sealed class VoiceLedger<T> where T : class
    {
        private sealed class Voice
        {
            internal object Owner;
            internal T Handle;
            internal double Ends;
            internal long Order;
        }
        private readonly int total;
        private readonly List<Voice> voices = new List<Voice>();
        // Retired entries are reused so claims allocate nothing once warm.
        private readonly Stack<Voice> spare = new Stack<Voice>();
        private long order;

        internal VoiceLedger(int total) { this.total = total; }

        internal int Count => voices.Count;

        // The handle to play on. stolen is another voice the caller must stop first, or null.
        internal T Claim(object owner, IList<T> pool, double now, double length, out T stolen)
        {
            stolen = null;
            for (int i = voices.Count - 1; i >= 0; i--)
                if (voices[i].Ends <= now)
                    Retire(i);
            Voice oldestMine = null, oldest = null;
            int mine = 0;
            for (int i = 0; i < voices.Count; i++)
            {
                var voice = voices[i];
                if (oldest == null || voice.Order < oldest.Order) oldest = voice;
                if (voice.Owner != owner) continue;
                mine++;
                if (oldestMine == null || voice.Order < oldestMine.Order) oldestMine = voice;
            }
            Voice claimed;
            if (mine >= pool.Count)
                claimed = oldestMine;
            else
            {
                if (voices.Count >= total)
                {
                    stolen = oldest.Handle;
                    Retire(voices.IndexOf(oldest));
                }
                claimed = spare.Count > 0 ? spare.Pop() : new Voice();
                claimed.Owner = owner;
                claimed.Handle = Free(owner, pool);
                voices.Add(claimed);
            }
            claimed.Ends = now + length;
            claimed.Order = order++;
            return claimed.Handle;
        }

        private T Free(object owner, IList<T> pool)
        {
            for (int p = 0; p < pool.Count; p++)
            {
                bool busy = false;
                for (int i = 0; i < voices.Count; i++)
                    busy |= voices[i].Owner == owner && voices[i].Handle == pool[p];
                if (!busy)
                    return pool[p];
            }
            return pool[0];
        }

        private void Retire(int index)
        {
            var voice = voices[index];
            voices.RemoveAt(index);
            voice.Owner = null;
            voice.Handle = null;
            spare.Push(voice);
        }

        internal void Release(object owner)
        {
            for (int i = voices.Count - 1; i >= 0; i--)
                if (voices[i].Owner == owner)
                    Retire(i);
        }
    }

    // Folds bursts of one sound into at most one voice per window. The voice takes the loudest event's gain
    // plus 10 log10 of the event count, capped. A full split-flap update lands hundreds of leaves a second.
    internal sealed class SoundCoalescer
    {
        internal const double Window = .025;
        internal const float MaxDecibels = 6f;
        private double opened;
        private int count;
        private float loudest;
        internal bool Pending => count > 0;

        // True when this event closed an earlier window, whose voice then starts at decibels.
        internal bool Add(double now, float gain, out float decibels)
        {
            bool started = Flush(now, out decibels);
            if (count == 0)
            {
                opened = now;
                loudest = gain;
            }
            else
                loudest = Math.Max(loudest, gain);
            count++;
            return started;
        }

        // True when the open window has ended, with the gain of its single voice.
        internal bool Flush(double now, out float decibels)
        {
            decibels = 0f;
            if (count == 0 || now < opened + Window)
                return false;
            decibels = Math.Min(MaxDecibels, loudest + 10f * (float)Math.Log10(count));
            count = 0;
            return true;
        }

        internal void Clear() => count = 0;
    }
}
