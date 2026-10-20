using System;
using System.Diagnostics;
using UnityEngine;

namespace SailwindRadio.Models
{
    // Optional development timing for the regional displays. Every display tick of a frame is measured,
    // and a summary is logged every ten seconds while LogDisplayTiming is on.
    // The heap figures are process wide and move in whole heap blocks, so audio and library threads that
    // allocate during a tick are counted too. Heap growth between ticks is logged beside it for comparison.
    internal static class DisplayTiming
    {
        // Indexed by DisplayMechanism.
        private static readonly string[] TypeNames = { "split-flap", "drum", "scroll" };
        private const float WindowSeconds = 10f;
        private static Func<bool> enabled = () => false;
        private static Action<string> log;
        private static readonly Stopwatch Watch = new Stopwatch();
        private static readonly double[] typeMilliseconds = new double[3];
        private static readonly int[] typeTicks = new int[3];
        private static int frame = -1, displays, animating, renderers, vertices;
        private static int lastDisplays, lastAnimating, lastRenderers, frames, collections = -1;
        private static double frameMilliseconds, totalMilliseconds, maxMilliseconds;
        private static long memory, afterTicks = -1, allocated, elsewhere, windowVertices;
        private static float windowStart = -1f;

        internal static void Configure(Func<bool> on, Action<string> output)
        {
            enabled = on;
            log = output;
        }

        internal static bool On => enabled();

        internal static void Begin()
        {
            if (frame != Time.frameCount)
                CloseFrame();
            memory = GC.GetTotalMemory(false);
            if (afterTicks >= 0 && memory > afterTicks)
                elsewhere += memory - afterTicks;
            Watch.Restart();
        }

        internal static void End(DisplayMechanism mechanism, bool moving, int verticesUpdated, int rendererCount)
        {
            int type = (int)mechanism;
            Watch.Stop();
            afterTicks = GC.GetTotalMemory(false);
            if (afterTicks > memory)
                allocated += afterTicks - memory;
            double milliseconds = Watch.Elapsed.TotalMilliseconds;
            frameMilliseconds += milliseconds;
            typeMilliseconds[type] += milliseconds;
            typeTicks[type]++;
            displays++;
            if (moving)
                animating++;
            renderers += rendererCount;
            vertices += verticesUpdated;
        }

        // Folds the finished frame into the window and logs a window that has run its ten seconds.
        private static void CloseFrame()
        {
            if (frame >= 0 && displays > 0)
            {
                frames++;
                totalMilliseconds += frameMilliseconds;
                maxMilliseconds = Math.Max(maxMilliseconds, frameMilliseconds);
                windowVertices += vertices;
                lastDisplays = displays;
                lastAnimating = animating;
                lastRenderers = renderers;
            }
            frame = Time.frameCount;
            frameMilliseconds = 0;
            displays = animating = renderers = vertices = 0;
            float now = Time.unscaledTime;
            if (windowStart < 0f)
            {
                windowStart = now;
                collections = GC.CollectionCount(0);
            }
            if (now - windowStart < WindowSeconds || frames == 0)
                return;
            string types = "";
            for (int i = 0; i < typeTicks.Length; i++)
                if (typeTicks[i] > 0)
                    types += ", " + TypeNames[i] + " " + (typeMilliseconds[i] / typeTicks[i]).ToString("F4") + " ms";
            log?.Invoke("Regional displays: " + lastDisplays + " active, " + lastAnimating + " animating, " +
                (totalMilliseconds / frames).ToString("F3") + " ms mean and " + maxMilliseconds.ToString("F3") + " ms max per frame, " +
                "mean per display tick" + (types.Length > 0 ? types.Substring(1) : " none") + ", " +
                (windowVertices / frames) + " mesh vertices updated per frame, " + lastRenderers + " display renderers, " +
                allocated + " heap bytes grew during display ticks and " + elsewhere + " between them, " +
                (GC.CollectionCount(0) - collections) + " collections, over " + (now - windowStart).ToString("F0") + " s.");
            windowStart = now;
            collections = GC.CollectionCount(0);
            frames = 0;
            totalMilliseconds = maxMilliseconds = 0;
            windowVertices = allocated = elsewhere = 0;
            Array.Clear(typeMilliseconds, 0, typeMilliseconds.Length);
            Array.Clear(typeTicks, 0, typeTicks.Length);
        }
    }
}
