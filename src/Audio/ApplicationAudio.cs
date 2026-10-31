using System;
using System.Diagnostics;
using System.Threading;

namespace SailwindRadio.Audio
{
    // Plays one desktop application through a radio. Its worker holds the application's Windows volume down,
    // captures the application's process tree into a ring and follows the application across restarts. The
    // main thread only starts and stops it, and the outputs' audio threads read the ring without waiting.
    internal sealed class ApplicationAudio
    {
        private const int PollMilliseconds = 5, PacketMilliseconds = 20, HoldMilliseconds = 1000, JoinMilliseconds = 1000;
        internal readonly AudioApplication Application;
        internal readonly ApplicationRing Ring;
        internal readonly MediaControls Media;
        private readonly Thread worker;
        private volatile bool stopping, holdNow, found;
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
        // Unity may end the worker before it restores.
        internal void Stop(bool wait)
        {
            stopping = true;
            Media.Stop();
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
                        var packet = new float[Ring.Capacity * ApplicationRing.Channels];
                        var clock = Stopwatch.StartNew();
                        long nextHold = 0;
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
                                    if (capture != null) { capture.Dispose(); capture = null; }
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
                                    if (capture == null) capture = ProcessLoopback.Open(root, Ring.SampleRate);
                                    HeldVolumes.Hold(this, id => id != self && tree.Within(id, top));
                                }
                            }
                            if (capture != null)
                            {
                                int samples = capture.Read(packet, 0, packet.Length);
                                // A packet too loud to be held means a session escaped the hold, so hold again now.
                                if (samples > 0 && !Ring.Write(packet, samples / ApplicationRing.Channels)) holdNow = true;
                            }
                            // Windows wakes the worker for each packet. Without capture it only checks for the application.
                            if (capture != null) capture.Wait(PacketMilliseconds);
                            else Thread.Sleep(PollMilliseconds);
                        }
                    }
                    finally
                    {
                        if (capture != null) capture.Dispose();
                        HeldVolumes.Release(this);
                    }
                }
            }
            catch (Exception error) { Volatile.Write(ref fault, error); }
        }
    }
}
