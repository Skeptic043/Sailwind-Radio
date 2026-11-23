using System;
using System.Diagnostics;
using System.Threading;

namespace SailwindRadio.Audio
{
    // Plays one desktop application through a radio. Its worker holds the application's Windows volume down,
    // captures the application's process tree into a ring and follows the application across restarts. The
    // playback side reads the ring without blocking capture.
    internal sealed class ApplicationAudio
    {
        private const int PollMilliseconds = 5, PacketMilliseconds = 20, HoldMilliseconds = 1000, JoinMilliseconds = 1000, PauseMilliseconds = 250;
        private const int PacketsPerPass = 32;
        internal readonly AudioApplication Application;
        internal readonly ApplicationRing Ring;
        internal readonly MediaControls Media;
        private readonly Thread worker;
        private volatile bool stopping, holdNow, found, pausing;
        private Exception fault;

        // The application has a running process being captured.
        internal bool Found { get { return found; } }
        internal Exception Fault { get { return Volatile.Read(ref fault); } }

        internal ApplicationAudio(AudioApplication application, int sampleRate)
        {
            Application = application;
            Ring = new ApplicationRing(sampleRate);
            Media = new MediaControls(application);
            worker = new Thread(Run) { IsBackground = true, Name = "Sailwind Radio application audio" };
            worker.Start();
        }

        // Stops capture and restores the application's volume on the worker. Waiting is for shutdown, where
        // Unity may end the worker before it restores. Pausing pauses the application before its volume returns.
        internal void Stop(bool wait, bool pause = false)
        {
            pausing = pause;
            Media.Stop(pause ? MediaControls.Command.Pause : MediaControls.Command.None, Ring.Sounding);
            stopping = true;
            if (wait) worker.Join(JoinMilliseconds);
        }

        private void Run()
        {
            try
            {
                using (new ComCalls.Apartment())
                {
                    ProcessLoopback capture = null;
                    try
                    {
                        float[] packet = null;
                        var clock = Stopwatch.StartNew();
                        long nextHold = 0;
                        ulong previousQpc = 0;
                        int previousFrames = 0;
                        int root = 0, self = Process.GetCurrentProcess().Id;
                        while (!stopping)
                        {
                            if (holdNow || clock.ElapsedMilliseconds >= nextHold)
                            {
                                holdNow = false;
                                nextHold = clock.ElapsedMilliseconds + HoldMilliseconds;
                                var tree = new ProcessTree();
                                // Started, or the application closed or restarted with a new process.
                                if (root == 0 || !tree.Contains(root))
                                {
                                    if (capture != null)
                                    {
                                        capture.Dispose();
                                        capture = null;
                                        previousQpc = 0;
                                        previousFrames = 0;
                                        Ring.Reset();
                                    }
                                    root = tree.Find(Application.Program);
                                    // A launcher that started Sailwind would capture and hold the game itself.
                                    if (root != 0 && tree.Within(self, root)) root = 0;
                                }
                                found = root != 0;
                                if (found)
                                {
                                    int top = root;
                                    // Capture opens first, so a system that refuses it, or a quit during activation,
                                    // never leaves the application held. Its first packets come before the hold and
                                    // the ring's guard plays them as silence.
                                    if (capture == null)
                                    {
                                        capture = ProcessLoopback.Open(root, Ring.SampleRate);
                                        previousQpc = 0;
                                        previousFrames = 0;
                                        int samples = checked(capture.BufferFrames * ApplicationRing.Channels);
                                        if (packet == null || packet.Length < samples) packet = new float[samples];
                                    }
                                    HeldVolumes.Hold(this, id => id != self && tree.Within(id, top));
                                }
                            }
                            if (capture != null)
                            {
                                int packets = 0;
                                for (; packets < PacketsPerPass && !stopping; packets++)
                                {
                                    bool discontinuity;
                                    int frames = capture.ReadPacket(packet, 0, packet.Length, out discontinuity);
                                    if (frames == 0) break;
                                    ulong qpc = capture.LastPacketQpc;
                                    // Windows process loopback drops packets silently when GC freezes this thread.
                                    // Detect gaps from packet QPC timestamps so the ring can smooth across the join.
                                    if (previousQpc != 0 && qpc > previousQpc)
                                    {
                                        double missingMs = (qpc - previousQpc) / 10000.0 - previousFrames * 1000.0 / Ring.SampleRate;
                                        if (missingMs > 25.0 && missingMs < 1500.0) discontinuity = true;
                                        else if (missingMs >= 1500.0) discontinuity = false;
                                    }
                                    previousQpc = qpc;
                                    previousFrames = frames;
                                    // A loud packet means a session escaped the hold. Silence it and recheck the hold.
                                    if (!Ring.Write(packet, frames, discontinuity)) holdNow = true;
                                }
                                // The negotiated buffer sizes one packet. A stalled worker can have several queued.
                                if (packets == PacketsPerPass) continue;
                            }
                            // Windows wakes the worker for each packet. Without capture it only checks for the application.
                            if (capture != null) capture.Wait(PacketMilliseconds);
                            else Thread.Sleep(PollMilliseconds);
                        }
                    }
                    finally
                    {
                        if (capture != null) capture.Dispose();
                        if (pausing) Media.Join(PauseMilliseconds);
                        HeldVolumes.Release(this);
                    }
                }
            }
            catch (Exception error) { Volatile.Write(ref fault, error); }
        }
    }
}
