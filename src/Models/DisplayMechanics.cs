using System;
using System.Collections.Generic;

namespace SailwindRadio.Models
{
    // Schedules for the regional display mechanisms in scaled game seconds. Pure so the sequencing can be
    // checked without Unity. Characters are TrackDisplayText.Charset indices and only move forward.
    // Nothing here allocates while it runs.

    // One split-flap cell. A flip drops the top leaf over FlipSeconds. Flips follow the cell's rhythm,
    // and a leaf always lands before the next one falls.
    internal sealed class FlapCell
    {
        internal const double FlipSeconds = .09;
        internal int Shown, Target;
        private double interval = 1 / 12.0, nextFlip, flipStart = double.NaN;
        internal bool Flipping => !double.IsNaN(flipStart);
        internal bool Settled => !Flipping && Shown == Target;
        internal int Next => (Shown + 1) % TrackDisplayText.Charset.Length;

        // An idle cell starts at startAt. A running cell keeps its rhythm toward the new target.
        internal void Retarget(int target, double startAt, double flipInterval)
        {
            if (Settled && target != Shown)
            {
                nextFlip = startAt;
                interval = flipInterval;
            }
            Target = target;
        }

        // Leaf progress from upright (0) to landed (1).
        internal float Progress(double now) => Flipping ? (float)Math.Min(1, Math.Max(0, (now - flipStart) / FlipSeconds)) : 0f;

        // Landings since the last call. arrived is true when the last of them reached the target.
        internal int Update(double now, out bool arrived)
        {
            int landings = 0;
            arrived = false;
            while (true)
            {
                if (Flipping)
                {
                    double landed = flipStart + FlipSeconds;
                    if (now < landed)
                        break;
                    Shown = Next;
                    flipStart = double.NaN;
                    nextFlip = Math.Max(nextFlip, landed);
                    landings++;
                    arrived = Shown == Target;
                }
                if (Shown == Target || now < nextFlip)
                    break;
                flipStart = nextFlip;
                nextFlip += interval;
            }
            return landings;
        }
    }

    internal struct DrumEvent
    {
        internal bool Stop;
        // Share of all wheels moving on this step.
        internal float Moving;
    }

    // One indexing shaft for every wheel of a drum display. Each step turns every wheel not yet at its target
    // one character the shorter way round, so wheels may roll either way. The turn takes RotateShare of the
    // step with ease in and out, then rests.
    internal sealed class DrumShaft
    {
        internal const float RotateShare = .7f;
        internal readonly int[] Shown, Target;
        // Each wheel's direction for the current step: +1 forward, -1 backward, 0 at rest.
        private readonly int[] moving;
        private double stepStart, interval = 1 / 12.0;
        private bool arrived = true;
        internal bool Running { get; private set; }

        internal DrumShaft(int wheels)
        {
            Shown = new int[wheels];
            Target = new int[wheels];
            moving = new int[wheels];
        }

        // Targets may change at any time. An idle shaft starts its first step on the next update.
        internal void Retarget(int wheel, int target, double now, double stepInterval)
        {
            Target[wheel] = target;
            if (Running || Shown[wheel] == target)
                return;
            Running = true;
            interval = stepInterval;
            stepStart = now - interval;
            arrived = true;
        }

        // Eased turn of a wheel in the current step, 0 when it rests.
        internal float Turn(int wheel, double now)
        {
            if (!Running || arrived || moving[wheel] == 0)
                return 0f;
            float p = (float)Math.Min(1, Math.Max(0, (now - stepStart) / (interval * RotateShare)));
            return p * p * (3 - 2 * p);
        }

        // The direction a wheel turns in the current step, 0 when it rests.
        internal int Direction(int wheel) => Running && !arrived ? moving[wheel] : 0;

        internal void Update(double now, List<DrumEvent> events)
        {
            while (Running)
            {
                if (!arrived)
                {
                    if (now < stepStart + interval * RotateShare)
                        return;
                    bool done = true;
                    for (int i = 0; i < Shown.Length; i++)
                    {
                        if (moving[i] != 0)
                            Shown[i] = (Shown[i] + moving[i] + TrackDisplayText.Charset.Length) % TrackDisplayText.Charset.Length;
                        done &= Shown[i] == Target[i];
                    }
                    arrived = true;
                    if (done)
                    {
                        Running = false;
                        events.Add(new DrumEvent { Stop = true });
                        return;
                    }
                }
                if (now < stepStart + interval)
                    return;
                stepStart += interval;
                int count = 0;
                for (int i = 0; i < Shown.Length; i++)
                {
                    moving[i] = TrackDisplayText.ShortestStep(Shown[i], Target[i]);
                    if (moving[i] != 0)
                        count++;
                }
                if (count == 0)
                {
                    Running = false;
                    return;
                }
                arrived = false;
                events.Add(new DrumEvent { Moving = count / (float)Shown.Length });
            }
        }
    }

    internal enum ScrollPhase { Idle, Approach, Lower, Smoothing, Lift, Jog, Carry, Title, Return, Artist, Home }

    internal enum ScrollSound { Eraser, EraserEnd, Paper, Tick, Stroke, End }

    // One scroll update. The carriage travels from wherever it is to the left end. If old ink remains, its felt
    // pad lowers, sweeps the belt clear only as far as that ink reached and lifts. The belt jogs, the carriage
    // carries back to the left, the stylus writes the title and the artist and the carriage returns home.
    // Every carriage move starts where the last ended. Positions are along the title row in metres.
    internal sealed class ScrollUpdate
    {
        internal const double JogSeconds = .3, CarriageSpeed = .6, MinimumMove = .15, RetractSeconds = .3, PadSeconds = .15;
        // Seconds of smoothing per old character of the longer row, and the shortest pass.
        internal const double SmoothingPerCharacter = .09, MinimumSmoothing = .4;
        private double start, perCharacter, last;
        private double lowerAt, smoothAt, liftAt, jogAt, carryAt, titleAt, returnAt, artistAt, homeAt;
        private int[] titleStrokes = Array.Empty<int>(), artistStrokes = Array.Empty<int>();
        private float[] titleEdges = { 0f }, artistEdges = { 0f };
        private float from, left, sweepEnd, padLead, home;
        private bool swept, lifted, jogged;
        internal bool Running { get; private set; }
        internal double EndsAt { get; private set; }

        // The pass length for the longer old row, or no pass at all when the belt is already clear.
        internal static double Smoothing(int oldCharacters) =>
            oldCharacters <= 0 ? 0 : Math.Max(MinimumSmoothing, SmoothingPerCharacter * oldCharacters);

        // Edges are the left and right of each character, so a row of n characters has n + 1 edges.
        // Strokes per character are 0 for a space and 1 or 2 otherwise. Every character ticks the carriage.
        // The pad sweeps from the left end to sweepTo over smoothingSeconds, clearing ink up to its leading edge,
        // padLeading ahead of the carriage. No pass runs when smoothingSeconds is 0.
        internal void Start(double now, double smoothingSeconds, double charactersPerSecond, int[] title, int[] artist,
            float carriage, float leftEnd, float sweepTo, float padLeading, float[] titleEdgeX, float[] artistEdgeX, float homeX)
        {
            start = last = now;
            perCharacter = 1 / charactersPerSecond;
            titleStrokes = title;
            artistStrokes = artist;
            titleEdges = titleEdgeX;
            artistEdges = artistEdgeX;
            from = carriage;
            left = leftEnd;
            bool pass = smoothingSeconds > 0;
            sweepEnd = pass ? Math.Max(leftEnd, sweepTo) : leftEnd;
            padLead = padLeading;
            home = homeX;
            lowerAt = start + Move(from, left, 0);
            smoothAt = lowerAt + (pass ? PadSeconds : 0);
            liftAt = smoothAt + smoothingSeconds;
            jogAt = liftAt + (pass ? PadSeconds : 0);
            carryAt = jogAt + JogSeconds;
            titleAt = carryAt + Move(sweepEnd, titleEdges[0], MinimumMove);
            returnAt = titleAt + title.Length * perCharacter;
            artistAt = returnAt + Move(titleEdges[titleEdges.Length - 1], artistEdges[0], MinimumMove);
            homeAt = artistAt + artist.Length * perCharacter;
            // Going home also retracts the stylus, so it takes at least RetractSeconds.
            EndsAt = homeAt + Move(artistEdges[artistEdges.Length - 1], home, RetractSeconds);
            swept = lifted = !pass;
            jogged = false;
            Running = true;
        }

        private static double Move(float a, float b, double least) => Math.Max(least, Math.Abs(b - a) / CarriageSpeed);

        internal ScrollPhase Phase(double now)
        {
            if (!Running || now >= EndsAt) return ScrollPhase.Idle;
            if (now < lowerAt) return ScrollPhase.Approach;
            if (now < smoothAt) return ScrollPhase.Lower;
            if (now < liftAt) return ScrollPhase.Smoothing;
            if (now < jogAt) return ScrollPhase.Lift;
            if (now < carryAt) return ScrollPhase.Jog;
            if (now < titleAt) return ScrollPhase.Carry;
            if (now < returnAt) return ScrollPhase.Title;
            if (now < artistAt) return ScrollPhase.Return;
            if (now < homeAt) return ScrollPhase.Artist;
            return ScrollPhase.Home;
        }

        private static float Share(double now, double a, double b) => b > a ? (float)Math.Min(1, Math.Max(0, (now - a) / (b - a))) : 1f;
        private static float Lerp(float a, float b, float t) => a + (b - a) * t;
        private static float Smooth(float t) => t * t * (3 - 2 * t);

        internal static float Edge(float[] edges, float written)
        {
            int whole = Math.Max(0, Math.Min(edges.Length - 1, (int)written));
            return whole >= edges.Length - 1 ? edges[edges.Length - 1] : Lerp(edges[whole], edges[whole + 1], written - whole);
        }

        // Carriage position along the title row.
        internal float Carriage(double now)
        {
            switch (Phase(now))
            {
                case ScrollPhase.Approach: return Lerp(from, left, Share(now, start, lowerAt));
                case ScrollPhase.Lower: return left;
                case ScrollPhase.Smoothing: return Lerp(left, sweepEnd, Smooth(Share(now, smoothAt, liftAt)));
                case ScrollPhase.Lift:
                case ScrollPhase.Jog: return sweepEnd;
                case ScrollPhase.Carry: return Lerp(sweepEnd, titleEdges[0], Share(now, carryAt, titleAt));
                case ScrollPhase.Title: return Edge(titleEdges, TitleWritten(now));
                case ScrollPhase.Return: return Lerp(titleEdges[titleEdges.Length - 1], artistEdges[0], Share(now, returnAt, artistAt));
                case ScrollPhase.Artist: return Edge(artistEdges, ArtistWritten(now));
                case ScrollPhase.Home: return Lerp(artistEdges[artistEdges.Length - 1], home, Share(now, homeAt, EndsAt));
                default: return Running && now < EndsAt ? from : home;
            }
        }

        // How far the felt pad is lowered onto the belt, 0 lifted to 1 down.
        internal float PadDown(double now)
        {
            switch (Phase(now))
            {
                case ScrollPhase.Lower: return Smooth(Share(now, lowerAt, smoothAt));
                case ScrollPhase.Smoothing: return 1f;
                case ScrollPhase.Lift: return 1f - Smooth(Share(now, liftAt, jogAt));
                default: return 0f;
            }
        }

        // Where the stylus tip reaches: 0 at rest under the carriage, 1 at the title row, 2 at the artist row.
        // It lowers while the carriage carries back, moves between rows on the return and retracts going home.
        // It stays up while the pad is down.
        internal float StylusReach(double now)
        {
            switch (Phase(now))
            {
                case ScrollPhase.Carry: return Smooth(Share(now, carryAt, titleAt));
                case ScrollPhase.Title: return 1f;
                case ScrollPhase.Return: return 1f + Smooth(Share(now, returnAt, artistAt));
                case ScrollPhase.Artist: return 2f;
                case ScrollPhase.Home: return 2f * (1f - Smooth(Share(now, homeAt, EndsAt)));
                default: return 0f;
            }
        }

        // The tip's scribble while a character is drawn, -1 to 1 along the row's up axis. One bump per stroke.
        internal float Scribble(double now)
        {
            var phase = Phase(now);
            if (phase != ScrollPhase.Title && phase != ScrollPhase.Artist)
                return 0f;
            float written = phase == ScrollPhase.Title ? TitleWritten(now) : ArtistWritten(now);
            var strokes = phase == ScrollPhase.Title ? titleStrokes : artistStrokes;
            int index = (int)written;
            if (index >= strokes.Length || strokes[index] == 0)
                return 0f;
            return (float)Math.Sin(Math.PI * strokes[index] * (written - index));
        }

        // Old ink left of this position is gone, cut exactly under the pad's leading edge as it sweeps.
        internal float Cleared(double now)
        {
            var phase = Phase(now);
            if (phase == ScrollPhase.Approach || phase == ScrollPhase.Lower) return float.NegativeInfinity;
            if (phase == ScrollPhase.Smoothing) return Carriage(now) + padLead;
            return float.PositiveInfinity;
        }

        // Characters written so far, fractional while one is being drawn.
        internal float TitleWritten(double now) => Written(now, titleAt, titleStrokes.Length);
        internal float ArtistWritten(double now) => Written(now, artistAt, artistStrokes.Length);
        private float Written(double now, double at, int count) =>
            !Running || now >= EndsAt ? count : (float)Math.Min(count, Math.Max(0, (now - at) / perCharacter));

        // Sounds whose moment passed since the last call, in order. End closes the update.
        internal void Update(double now, List<ScrollSound> sounds)
        {
            if (!Running)
                return;
            double before = last;
            last = now;
            if (!swept && now >= smoothAt)
            {
                swept = true;
                sounds.Add(ScrollSound.Eraser);
            }
            if (!lifted && now >= liftAt)
            {
                lifted = true;
                sounds.Add(ScrollSound.EraserEnd);
            }
            if (!jogged && now >= jogAt)
            {
                jogged = true;
                sounds.Add(ScrollSound.Paper);
            }
            Characters(before, now, titleAt, titleStrokes, sounds);
            Characters(before, now, artistAt, artistStrokes, sounds);
            if (now >= EndsAt)
            {
                Running = false;
                sounds.Add(ScrollSound.End);
            }
        }

        private void Characters(double before, double now, double at, int[] strokes, List<ScrollSound> sounds)
        {
            for (int i = 0; i < strokes.Length; i++)
            {
                double begin = at + i * perCharacter;
                if (before < begin && begin <= now)
                {
                    sounds.Add(ScrollSound.Tick);
                    if (strokes[i] > 0)
                        sounds.Add(ScrollSound.Stroke);
                }
                double middle = begin + perCharacter * .5;
                if (strokes[i] > 1 && before < middle && middle <= now)
                    sounds.Add(ScrollSound.Stroke);
            }
        }
    }
}
