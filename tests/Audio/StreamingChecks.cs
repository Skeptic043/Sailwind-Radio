using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
using SailwindRadio;
using UnityEngine;

// Streaming engine edge cases: tracks shorter than the window, a few frames long or started near
// their end, repeats, backward seeks, several readers, early decoder ends and disposal while the
// worker is busy. A scripted decoder registered under .fake controls timing and length exactly.
static partial class AudioChecks
{
    // Deterministic nonzero PCM, so silence and misplaced samples are both detectable.
    static float FakeSample(long frame, int channel) { return ((frame * 7 + channel * 3) % 1000) / 1000f - .5f; }

    sealed class FakeDecoder : IPcmDecoder
    {
        // Gate holds upcoming tracks in the preload checks. Hold holds the lifecycle track of the playback checks.
        // Layout holds an open between decoder construction and the stream's window allocation.
        internal static readonly ManualResetEventSlim Gate = new ManualResetEventSlim(true), Hold = new ManualResetEventSlim(true),
            Layout = new ManualResetEventSlim(true), InLayout = new ManualResetEventSlim(false);
        private static int live, seeks, opens, reads;
        internal static int Live { get { return Volatile.Read(ref live); } }
        internal static int Seeks { get { return Volatile.Read(ref seeks); } }
        internal static int Reads { get { return Volatile.Read(ref reads); } }
        private static readonly Dictionary<string, int> opensByPath = new Dictionary<string, int>(StringComparer.OrdinalIgnoreCase);
        // Every open attempt, counted before any gate, so a second loader for one track shows up at once.
        internal static int Opens { get { return Volatile.Read(ref opens); } }
        internal static int OpensOf(string path)
        {
            int count;
            lock (opensByPath) return opensByPath.TryGetValue(path, out count) ? count : 0;
        }
        private readonly long available, damageAt, damageEnd;
        private readonly int readDelay, rate;
        private readonly bool layoutGate, foreignCancel, seekFails;
        private bool disposed, broken;
        public int Channels { get; private set; }
        public int SampleRate
        {
            get
            {
                // PcmDecoders.Open never reads the rate, so the first read is the stream's own layout step.
                if (layoutGate) { InLayout.Set(); Layout.Wait(); }
                return rate;
            }
        }
        public long Frames { get; private set; }
        public long Position { get; private set; }

        // The file holds "channels rate reportedFrames availableFrames readDelayMs gate damageFrame cancel damageEnd
        // seekFails", gate 1 for Gate, 2 for Hold and 3 for Layout. Reads from damageFrame up to damageEnd, or on
        // to the end when it is negative, throw like a decoder meeting damaged data. Cancel 1 throws a
        // cancellation that is not the stream's own instead of an IOException. SeekFails 1 makes every seek
        // after the first damage throw, like a decoder that cannot recover.
        internal static IPcmDecoder Open(string path, CancellationToken token)
        {
            Interlocked.Increment(ref opens);
            lock (opensByPath) { int count; opensByPath.TryGetValue(path, out count); opensByPath[path] = count + 1; }
            string[] fields = File.ReadAllText(path).Split(' ');
            if (fields[5] == "1") Gate.Wait(token);
            else if (fields[5] == "2") Hold.Wait(token);
            token.ThrowIfCancellationRequested();
            return new FakeDecoder(Int32.Parse(fields[0]), Int32.Parse(fields[1]), Int64.Parse(fields[2]),
                Int64.Parse(fields[3]), Int32.Parse(fields[4]), fields[5] == "3", Int64.Parse(fields[6]), fields[7] == "1",
                Int64.Parse(fields[8]), fields[9] == "1");
        }

        private FakeDecoder(int channels, int rate, long frames, long available, int readDelay, bool layoutGate, long damageAt, bool foreignCancel,
            long damageEnd, bool seekFails)
        {
            Channels = channels; this.rate = rate; Frames = frames;
            this.available = available; this.readDelay = readDelay; this.layoutGate = layoutGate; this.damageAt = damageAt;
            this.foreignCancel = foreignCancel; this.damageEnd = damageEnd; this.seekFails = seekFails;
            Interlocked.Increment(ref live);
        }

        public void Seek(long frame)
        {
            Interlocked.Increment(ref seeks);
            if (seekFails && broken) throw new IOException("Scripted seek failure after damage");
            Position = Math.Max(0, Math.Min(Frames, frame));
        }

        public int Read(float[] buffer, int offset, int count)
        {
            if (disposed) throw new ObjectDisposedException("FakeDecoder");
            Interlocked.Increment(ref reads);
            // An uninterruptible read, like a blocking native decoder call.
            if (readDelay > 0) Thread.Sleep(readDelay);
            if (damageAt >= 0 && Position >= damageAt && (damageEnd < 0 || Position < damageEnd))
            {
                broken = true;
                if (foreignCancel) throw new OperationCanceledException("Scripted foreign cancellation at frame " + damageAt);
                throw new IOException("Scripted damage at frame " + damageAt);
            }
            long end = damageAt >= 0 && Position < damageAt ? Math.Min(available, damageAt) : available;
            int frames = (int)Math.Max(0, Math.Min(count / Channels, end - Position));
            for (int f = 0; f < frames; f++)
                for (int c = 0; c < Channels; c++) buffer[offset + f * Channels + c] = FakeSample(Position + f, c);
            Position += frames;
            return frames * Channels;
        }

        public void Dispose() { if (!disposed) { disposed = true; Interlocked.Decrement(ref live); } }
    }

    static string WriteFake(string name, int channels, int rate, long frames, long available = -1, int readDelay = 0, bool gate = false,
        bool hold = false, long damageAt = -1, bool layout = false, bool foreignCancel = false, long damageEnd = -1, bool seekFails = false)
    {
        // Registration replaces the decoder table whole, so running stream workers never see a partial write.
        PcmDecoders.Register(".fake", FakeDecoder.Open);
        string path = Path.Combine(AppContext.BaseDirectory, name);
        File.WriteAllText(path, channels + " " + rate + " " + frames + " " + (available < 0 ? frames : available) + " " + readDelay + " " +
            (gate ? "1" : hold ? "2" : layout ? "3" : "0") + " " + damageAt + " " + (foreignCancel ? "1" : "0") + " " + damageEnd + " " +
            (seekFails ? "1" : "0"));
        return path;
    }

    // The playback lifecycle track: ten seconds of 48 kHz mono, the dimensions the lifecycle arithmetic
    // uses, held by the Hold gate so each check decides when its load completes.
    static string WriteHeldTrack(string name, int seconds = 10, int readDelay = 0)
    {
        return WriteFake(name, 1, 48000, 48000L * seconds, readDelay: readDelay, hold: true);
    }

    // Releases held opens, then ticks until the clip is attached and the stream holds the frame the next
    // start needs. Ticks are suspended by default, so the caller's next Tick is the one that schedules.
    static void Settle(RadioPlayback player, bool suspended = true)
    {
        FakeDecoder.Hold.Set();
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (DateTime.UtcNow < deadline)
        {
            player.Tick(new Vector3(), suspended);
            var stream = (StreamedAudio)Field(player, "streamed");
            if (player.LoadFailed || stream == null) break;
            if (Field(player, "clip") != null && stream.Ready(RequestedFrame(stream)))
            {
                // The Tick above may have run before the frame arrived. One more suspended Tick refreshes Status.
                if (suspended) player.Tick(new Vector3(), true);
                return;
            }
            Thread.Sleep(1);
        }
        throw new Exception("Stream did not settle: status=" + player.Status);
    }

    // Harness Ticks run back to back, while real frames give the worker time between them. Before a Tick
    // that must start or resume, this requests and waits for the exact frame that Tick gates on, the
    // saved position, just as Tick itself requests it.
    static void WaitStream(RadioPlayback player)
    {
        var stream = (StreamedAudio)Field(player, "streamed");
        if (stream == null || !stream.IsOpen) return;
        int frame = SavedFrame(player, stream);
        stream.Request(frame);
        if (!WaitReady(stream, frame)) throw new Exception("Stream did not refill frame " + frame);
    }

    static int SavedFrame(RadioPlayback player, StreamedAudio stream)
    {
        double seconds = ((RadioState)Field(player, "state")).PositionSeconds;
        if (Double.IsNaN(seconds) || Double.IsInfinity(seconds) || seconds < 0) seconds = 0;
        return (int)Math.Min(stream.Frames - 1, Math.Floor(seconds * stream.SampleRate));
    }

    // A track that fits the ring is made wholly resident, then the saved frame is requested again, so no
    // later Tick of the lifecycle checks can find its frame still decoding.
    static void MakeResident(RadioPlayback player)
    {
        var stream = (StreamedAudio)Field(player, "streamed");
        for (int frame = 0; frame < stream.Frames; frame += stream.SampleRate / 4)
        {
            stream.Request(frame);
            if (!WaitReady(stream, frame)) throw new Exception("Stream did not hold frame " + frame);
        }
        stream.Request(SavedFrame(player, stream));
        for (int frame = 0; frame < stream.Frames; frame += stream.SampleRate / 4)
            if (!stream.Ready(frame)) throw new Exception("A resident track lost frame " + frame);
    }

    static int RequestedFrame(StreamedAudio stream)
    {
        return (int)(long)typeof(StreamedAudio).GetField("requestedFrame",
            System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(stream);
    }

    // Reads every frame through one reader in callback-sized blocks, requesting ahead as Tick does.
    static bool ReadsWholeTrack(StreamedAudio stream, StreamedAudio.Reader reader, Func<int, int, float> expected, int from, out string detail)
    {
        int channels = stream.Channels, block = 1024;
        var data = new float[block * channels];
        reader.SetPosition(from);
        detail = "";
        for (int at = from; at < stream.Frames; at += block)
        {
            stream.Request(at);
            if (!WaitReady(stream, at)) { detail = "not ready at " + at; return false; }
            reader.Read(data);
            for (int i = 0; i < data.Length; i++)
            {
                int frame = at + i / channels;
                float want = frame < stream.Frames ? expected(frame, i % channels) : 0f;
                if (Math.Abs(data[i] - want) > 1e-6f) { detail = "frame " + frame + " got " + data[i] + " want " + want; return false; }
            }
        }
        reader.Read(data);
        if (!Array.TrueForAll(data, v => v == 0)) { detail = "audio after the end"; return false; }
        return true;
    }

    static void CheckShortTracks()
    {
        string dir = AppContext.BaseDirectory;
        const int rate = 44100;
        Func<int, int, double> tone = (f, c) => .5 * Math.Sin(f * .031 + c * .7) + .2 * Math.Sin(f * .0017);
        int windowChunks = (20 * rate + 4095) / 4096;
        foreach (int frames in new[] { 3, 100, 4096, 4097, rate / 3, rate, rate * 25 })
        {
            string path = Path.Combine(dir, "short-" + frames + ".wav");
            WriteWav(path, 1, 16, 2, rate, frames, tone, false, 0);
            float[] reference;
            using (var decoder = PcmDecoders.Open(path, CancellationToken.None)) reference = ReadAll(decoder);
            Func<int, int, float> expected = (f, c) => reference[f * 2 + c];
            var stream = OpenStream(path, 0);
            string label = frames + "-frame WAV";
            int ring = Math.Min((frames + 4095) / 4096, windowChunks);
            Check(stream.Frames == frames && stream.BufferSamples == ring * 4096 * 2,
                label + " opens with a ring of " + ring + " chunks: buffer=" + stream.BufferSamples);
            Check(WaitReady(stream, 0), label + " becomes ready at its start");
            var a = stream.CreateReader();
            var b = stream.CreateReader();
            string detail;
            Check(ReadsWholeTrack(stream, a, expected, 0, out detail), label + " streams every frame exactly then silence: " + detail);
            // Repeat wraps to the start. A short track is still held, a longer one refills.
            Check(ReadsWholeTrack(stream, a, expected, 0, out detail), label + " repeats from its start exactly: " + detail);
            int nearEnd = Math.Max(0, frames - 2);
            Check(ReadsWholeTrack(stream, b, expected, nearEnd, out detail), label + " starts two frames before its end: " + detail);
            Check(ReadsWholeTrack(stream, b, expected, frames / 2, out detail) && ReadsWholeTrack(stream, a, expected, Math.Min(1, frames - 1), out detail),
                label + " seeks backward with a second reader: " + detail);
            stream.Dispose();
            Check(WaitWorker(stream) && FileClosed(path), label + " worker exits and closes its file");
            DeleteClosed(path);
        }

        // A track held whole never decodes again on repeat or backward seek.
        string held = WriteFake("held.fake", 2, rate, rate * 3);
        var heldStream = OpenStream(held, 0);
        var heldReader = heldStream.CreateReader();
        string heldDetail;
        Check(ReadsWholeTrack(heldStream, heldReader, (f, c) => FakeSample(f, c), 0, out heldDetail), "Fake track streams exactly: " + heldDetail);
        int seeks = FakeDecoder.Seeks;
        Check(ReadsWholeTrack(heldStream, heldReader, (f, c) => FakeSample(f, c), 0, out heldDetail) &&
            ReadsWholeTrack(heldStream, heldReader, (f, c) => FakeSample(f, c), rate, out heldDetail) && FakeDecoder.Seeks == seeks,
            "A track held whole repeats and seeks backward without a decoder seek: seeks=" + (FakeDecoder.Seeks - seeks));
        heldStream.Dispose();

        // A decoder that ends before its reported length pads the rest with silence and keeps playing.
        string early = WriteFake("early-end.fake", 2, rate, rate * 2, available: rate + 1234);
        var earlyStream = OpenStream(early, 0);
        Check(ReadsWholeTrack(earlyStream, earlyStream.CreateReader(), (f, c) => f < rate + 1234 ? FakeSample(f, c) : 0f, 0, out heldDetail) &&
            earlyStream.Fault == null, "A decoder ending early leaves silence instead of failing the track: " + heldDetail);
        earlyStream.Dispose();
        string empty = WriteFake("empty.fake", 2, rate, rate, available: 0);
        Check(StreamFault(empty).Contains("no decodable audio"), "A decoder with no audio at all fails clearly");

        // Missing chunks are silence and never block the audio callback, even behind a slow decoder. The
        // bounds sit well under the scripted 300 ms read, so they separate waiting from not waiting.
        string slow = WriteFake("slow.fake", 2, rate, rate * 60, readDelay: 300);
        int liveBefore = FakeDecoder.Live, readsBefore = FakeDecoder.Reads;
        var slowStream = OpenStream(slow, 0);
        var slowReader = slowStream.CreateReader();
        slowReader.SetPosition(rate * 30);
        var block = new float[2048];
        Array.Fill(block, .9f);
        var watch = System.Diagnostics.Stopwatch.StartNew();
        slowReader.Read(block);
        Check(watch.ElapsedMilliseconds < 150 && Array.TrueForAll(block, v => v == 0),
            "A missing chunk reads as silence without waiting for a slow decoder: " + watch.ElapsedMilliseconds + " ms");
        var deadline = DateTime.UtcNow.AddSeconds(5);
        while (FakeDecoder.Reads == readsBefore && DateTime.UtcNow < deadline) Thread.Sleep(1);
        watch.Restart();
        slowStream.Dispose();
        long disposeMs = watch.ElapsedMilliseconds;
        slowReader.Read(block);
        Check(disposeMs < 150 && !slowStream.WorkerCompleted && Array.TrueForAll(block, v => v == 0),
            "Disposal during a blocking decoder read returns at once and silences late callbacks: " + disposeMs + " ms");
        Check(WaitWorker(slowStream) && slowStream.Fault == null && FakeDecoder.Live == liveBefore,
            "The busy worker finishes its read, then exits and disposes its decoder");

        CheckShortPlayback();
        DeleteClosed(held); DeleteClosed(early); DeleteClosed(empty); DeleteClosed(slow);
    }

    static void CheckShortPlayback()
    {
        string dir = AppContext.BaseDirectory;
        const int rate = 48000;
        string shortWav = Path.Combine(dir, "short-playback.wav");
        WriteWav(shortWav, 1, 16, 1, rate, rate * 3 / 10, (f, c) => .4 * Math.Sin(f * .05), false, 0);
        string tiny = Path.Combine(dir, "tiny-playback.wav");
        WriteWav(tiny, 1, 16, 1, rate, 3, (f, c) => .4, false, 0);
        foreach (bool repeat in new[] { false, true })
        {
            AudioSettings.dspTime = 20000;
            var state = new RadioState { TrackPath = shortWav, Powered = true };
            var player = new RadioPlayback(new MonoBehaviour(), state, _ => { }) { RepeatTrack = repeat };
            var source = AudioSource.Last;
            player.SetAcoustics(new Vector3(), true, 0);
            PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
            Check(!player.LoadFailed && source.clip.Streaming && source.clip.samples == rate * 3 / 10 && source.Schedules == 1 &&
                source.timeSamples == 0, "A 0.3 second WAV streams and schedules from its start with repeat " + repeat);
            AudioSettings.dspTime = source.ScheduledAt + .45;
            source.timeSamples = repeat ? rate * 15 / 100 : 0;
            player.Tick(new Vector3(), false);
            if (repeat)
                Check(!player.TrackEnded && Math.Abs(state.PositionSeconds - .15) < 1e-6 && player.Status == "Playing",
                    "A repeating 0.3 second track wraps its shared timeline: position=" + state.PositionSeconds);
            else
                Check(player.TrackEnded && Math.Abs(state.PositionSeconds - .3) < 1e-9 && player.Status == "Ended",
                    "A 0.3 second track ends on the shared timeline without repeating");
            player.Dispose();
        }
        AudioSettings.dspTime = 21000;
        var nearEnd = new RadioState { TrackPath = shortWav, Powered = true, PositionSeconds = .29 };
        var endPlayer = new RadioPlayback(new MonoBehaviour(), nearEnd, _ => { }) { RepeatTrack = false };
        var endSource = AudioSource.Last;
        PumpUntil(endPlayer, false, () => endPlayer.Status == "Playing" || endPlayer.LoadFailed);
        Check(endSource.timeSamples == (int)Math.Floor(.29 * rate), "A short track saved 10 ms before its end starts there");
        AudioSettings.dspTime = endSource.ScheduledAt + .02;
        endPlayer.Tick(new Vector3(), false);
        Check(endPlayer.TrackEnded, "A start near the end reaches ordinary completion");
        endPlayer.Dispose();
        AudioSettings.dspTime = 22000;
        var tinyState = new RadioState { TrackPath = tiny, Powered = true };
        var tinyPlayer = new RadioPlayback(new MonoBehaviour(), tinyState, _ => { }) { RepeatTrack = false };
        var tinySource = AudioSource.Last;
        PumpUntil(tinyPlayer, false, () => tinyPlayer.Status == "Playing" || tinyPlayer.LoadFailed);
        Check(!tinyPlayer.LoadFailed && tinySource.clip.samples == 3 && tinySource.Schedules == 1, "A three-frame WAV streams and plays");
        AudioSettings.dspTime = tinySource.ScheduledAt + .01;
        tinyPlayer.Tick(new Vector3(), false);
        Check(tinyPlayer.TrackEnded && tinyPlayer.Status == "Ended", "A three-frame track ends normally");
        tinyPlayer.Dispose();
        DeleteClosed(shortWav); DeleteClosed(tiny);
    }

    // Waits until no fake decoder read happens for 100 ms, so an idle worker can be told from a busy one.
    static void WaitIdle()
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        int reads = FakeDecoder.Reads;
        while (DateTime.UtcNow < deadline)
        {
            Thread.Sleep(100);
            if (FakeDecoder.Reads == reads) return;
            reads = FakeDecoder.Reads;
        }
        throw new Exception("Fake decoders never went idle");
    }

    // Milliseconds from Request to a ready frame, polled without sleeping so timer granularity cannot hide
    // a worker poll. The frame must not be held yet. Returns infinity when the worker never serves it.
    static double RequestLatency(StreamedAudio stream, int frame)
    {
        if (stream.Ready(frame)) throw new Exception("Latency frame " + frame + " is already held");
        var watch = System.Diagnostics.Stopwatch.StartNew();
        stream.Request(frame);
        while (!stream.Ready(frame) && stream.Fault == null && watch.ElapsedMilliseconds < 10000) Thread.Yield();
        return stream.Ready(frame) ? watch.Elapsed.TotalMilliseconds : Double.PositiveInfinity;
    }

    static Exception FirstFault(StreamedAudio stream)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (stream.Fault == null && DateTime.UtcNow < deadline) Thread.Sleep(1);
        return stream.Fault;
    }

    // Damage after audio, main-thread-only worker targets, the looping look-ahead, prompt worker wakes and
    // cancelled opens, at stream level.
    static void CheckStreamRecovery()
    {
        const int rate = 44100;
        int damageAt = rate * 3 + 1234;
        string damaged = WriteFake("damaged.fake", 2, rate, rate * 10, damageAt: damageAt);
        var damagedStream = OpenStream(damaged, 0);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (damagedStream.DamageFrame != damageAt && DateTime.UtcNow < deadline) Thread.Sleep(1);
        Check(damagedStream.DamageFrame == damageAt && damagedStream.Fault == null && damagedStream.Damage != null &&
            damagedStream.Damage.Message.Contains("Scripted damage"),
            "A decode failure after audio records its frame instead of faulting the stream: damage=" + damagedStream.DamageFrame);
        string detail;
        Check(ReadsWholeTrack(damagedStream, damagedStream.CreateReader(), (f, c) => f < damageAt ? FakeSample(f, c) : 0f, 0, out detail) &&
            damagedStream.Fault == null && damagedStream.DamageFrame == damageAt,
            "Frames before the damage stream exactly and every later frame is silence: " + detail);
        damagedStream.Dispose();
        foreach (long at in new long[] { 0, 100 })
        {
            string early = WriteFake("damaged-first-" + at + ".fake", 2, rate, rate * 10, damageAt: at);
            var earlyStream = StreamedAudio.Start(early, 0);
            Exception fault = FirstFault(earlyStream);
            Check(fault != null && fault.Message.Contains("Scripted damage") && earlyStream.DamageFrame == Int32.MaxValue,
                "A decode failure inside the first fill still faults the stream at frame " + at);
            earlyStream.Dispose();
            Check(WaitWorker(earlyStream), "A faulted stream worker exits");
            DeleteClosed(early);
        }

        // A cancellation the stream did not request is a decoder failure, never a silent worker exit.
        string foreign = WriteFake("foreign-cancel.fake", 2, rate, rate * 10, damageAt: 0, foreignCancel: true);
        var foreignStream = StreamedAudio.Start(foreign, 0);
        Exception foreignFault = FirstFault(foreignStream);
        Check(foreignFault is OperationCanceledException && foreignFault.Message.Contains("foreign"),
            "A foreign cancellation before any audio faults the stream instead of leaving it loading");
        foreignStream.Dispose();
        WaitWorker(foreignStream);
        DeleteClosed(foreign);
        foreign = WriteFake("foreign-cancel-late.fake", 2, rate, rate * 10, damageAt: rate * 2, foreignCancel: true);
        foreignStream = OpenStream(foreign, 0);
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (foreignStream.DamageFrame == Int32.MaxValue && DateTime.UtcNow < deadline) Thread.Sleep(1);
        Check(foreignStream.DamageFrame == rate * 2 && foreignStream.Fault == null && foreignStream.Damage is OperationCanceledException,
            "A foreign cancellation after audio ends playback at its frame like any decode failure");
        foreignStream.Dispose();
        WaitWorker(foreignStream);
        DeleteClosed(foreign);

        // Native set-position callbacks move only their reader. The main thread alone retargets the worker.
        string plain = WriteFake("retarget.fake", 1, rate, rate * 60);
        var plainStream = OpenStream(plain, 0);
        Check(WaitReady(plainStream, 0), "A long fake track fills from its start");
        var plainReader = plainStream.CreateReader();
        var plainClip = plainStream.CreateClip(plainReader);
        int before = RequestedFrame(plainStream);
        plainReader.SetPosition(rate * 40);
        plainClip.PcmSetPosition(rate * 50);
        Thread.Sleep(50);
        Check(RequestedFrame(plainStream) == before && plainReader.Cursor == rate * 50 && !plainStream.Ready(rate * 40) && !plainStream.Ready(rate * 50),
            "A reader seek moves its own cursor but never the worker target: target=" + RequestedFrame(plainStream));

        // The idle worker waits on a signal with no timeout, so readiness after a Request proves the wake.
        WaitIdle();
        double forwardMs = RequestLatency(plainStream, rate * 45);
        Check(forwardMs < 1000, "An idle worker wakes on Request without a polling interval: ready after " + forwardMs.ToString("F1") + " ms");
        WaitIdle();
        double backwardMs = RequestLatency(plainStream, rate * 25);
        Console.WriteLine("Idle worker request to ready: " + forwardMs.ToString("F1") + " ms forward, " + backwardMs.ToString("F1") + " ms backward");
        Check(backwardMs < 1000, "A backward request to frames never decoded also wakes the idle worker");
        WaitIdle();
        int reads = FakeDecoder.Reads;
        Thread.Sleep(100);
        Check(FakeDecoder.Reads == reads, "An idle worker with its window full does no decoding while it waits");
        plainStream.Dispose();
        Check(WaitWorker(plainStream), "Disposal wakes and ends an idle worker");
        UnityEngine.Object.Destroy(plainClip);

        // Fifty chunks at 8 kHz outgrow the forty-chunk window, so the looping look-ahead holds chunks from both
        // sides of the wrap at once.
        const int loopRate = 8000;
        int loopFrames = 50 * 4096, target = loopFrames - 5 * loopRate;
        string looped = WriteFake("loop.fake", 1, loopRate, loopFrames);
        foreach (bool loop in new[] { false, true })
        {
            var stream = OpenStream(looped, target / (double)loopRate);
            stream.Loop = loop;
            Check(WaitReady(stream, target), "A track longer than the window fills at a position five seconds before its end");
            if (loop) Check(WaitReady(stream, 0) && WaitReady(stream, 4 * loopRate),
                "A looping look-ahead continues from the start of the track before the playhead wraps");
            WaitIdle();
            reads = FakeDecoder.Reads;
            Thread.Sleep(150);
            Check(FakeDecoder.Reads == reads && stream.Ready(target) && (!loop || stream.Ready(0)),
                "The " + (loop ? "looping" : "plain") + " look-ahead settles without refill churn");
            if (!loop) Check(!stream.Ready(0), "Without looping the look-ahead stops at the end of the track");
            else
            {
                var reader = stream.CreateReader();
                var block = new float[4096];
                reader.SetPosition(loopFrames - 1000);
                reader.Read(block);
                bool exact = true;
                // A tolerance, because Mono may compare the expected value at extended precision.
                for (int i = 0; i < block.Length; i++) exact &= Math.Abs(block[i] - (i < 1000 ? FakeSample(loopFrames - 1000 + i, 0) : 0f)) <= 1e-6f;
                reader.SetPosition(0);
                reader.Read(block);
                for (int i = 0; i < block.Length; i++) exact &= Math.Abs(block[i] - FakeSample(i, 0)) <= 1e-6f;
                Check(exact, "Reads across the loop point return the exact end and start of the track");
            }
            stream.Dispose();
            Check(WaitWorker(stream), "The look-ahead worker exits");
        }

        // An open cancelled once its decoder exists allocates no window and is never published as open.
        string layout = WriteFake("layout.fake", 1, rate, rate * 5, layout: true);
        FakeDecoder.Layout.Reset();
        FakeDecoder.InLayout.Reset();
        int live = FakeDecoder.Live;
        var opening = StreamedAudio.Start(layout, 0);
        Check(FakeDecoder.InLayout.Wait(5000), "A gated open reaches its layout step");
        opening.Dispose();
        FakeDecoder.Layout.Set();
        Check(WaitWorker(opening) && !opening.IsOpen && opening.Fault == null && opening.BufferSamples == 0 &&
            typeof(StreamedAudio).GetField("chunks", System.Reflection.BindingFlags.Instance | System.Reflection.BindingFlags.NonPublic).GetValue(opening) == null &&
            FakeDecoder.Live == live, "An open cancelled after its decoder is built allocates no window, never opens and disposes the decoder");
        DeleteClosed(damaged); DeleteClosed(plain); DeleteClosed(looped); DeleteClosed(layout);
        CheckPlaybackRecovery();
    }

    // The same fixes through RadioPlayback: damage ends a track like a normal end, a repeating long track
    // loops its look-ahead, adopted preloads get a fresh deadline and a restart keeps the next preload.
    static void CheckPlaybackRecovery()
    {
        int warnings = 0;
        string last = "";
        Action<string> warn = w => { warnings++; last = w; };
        const int rate = 48000;
        int damageFrame = rate * 12;
        string damagedTrack = WriteFake("damaged-track.fake", 1, rate, rate * 30, damageAt: damageFrame);
        AudioSettings.dspTime = 30000;
        var state = new RadioState { TrackPath = damagedTrack, Powered = true };
        var player = new RadioPlayback(new MonoBehaviour(), state, warn) { RepeatTrack = false };
        var source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        var stream = (StreamedAudio)Field(player, "streamed");
        double start = source.ScheduledAt;
        AudioSettings.dspTime = start + 5;
        player.Tick(new Vector3(), false);
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (stream.DamageFrame != damageFrame && DateTime.UtcNow < deadline) Thread.Sleep(1);
        player.Tick(new Vector3(), false);
        Check(stream.DamageFrame == damageFrame && stream.Fault == null && !player.TrackEnded && !player.LoadFailed && player.Status == "Playing" &&
            warnings == 0, "Damage found ahead of the playhead leaves the track playing: status=" + player.Status + " warning=" + last);
        AudioSettings.dspTime = start + 11.99;
        player.Tick(new Vector3(), false);
        Check(!player.TrackEnded && player.Status == "Playing" && warnings == 0 && Math.Abs(state.PositionSeconds - 11.99) < 1e-3,
            "Playback continues right up to the damaged frame");
        AudioSettings.dspTime = start + 12.01;
        player.Tick(new Vector3(), false);
        Check(player.TrackEnded && !player.LoadFailed && player.FailedPath == null && state.PositionSeconds == 30 && !source.isPlaying &&
            warnings == 1 && last == "Music ended early at damaged audio: Scripted damage at frame " + damageFrame + " (damaged-track.fake)",
            "Reaching the damage ends the track like a normal end with one warning: " + last);
        PumpUntil(player, false, () => player.Status == "Ended");
        for (int i = 0; i < 10; i++) player.Tick(new Vector3(), false);
        Check(player.TrackEnded && player.Status == "Ended" && warnings == 1 && source.Schedules == 1,
            "A damage end stays ended without restarting or repeating its warning");
        player.Dispose();

        // Paused before the damage, a radio ends only once it would play again.
        AudioSettings.dspTime = 31000;
        state = new RadioState { TrackPath = damagedTrack, Powered = true, PositionSeconds = 11 };
        player = new RadioPlayback(new MonoBehaviour(), state, warn) { RepeatTrack = false };
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        stream = (StreamedAudio)Field(player, "streamed");
        deadline = DateTime.UtcNow.AddSeconds(10);
        while (stream.DamageFrame != damageFrame && DateTime.UtcNow < deadline) Thread.Sleep(1);
        state.Paused = true;
        state.PositionSeconds = 12.5;
        player.Tick(new Vector3(), false);
        state.PositionSeconds = 12.5;
        player.Tick(new Vector3(), false);
        Check(!player.TrackEnded && player.Status == "Paused" && warnings == 1, "A paused radio saved past the damage waits");
        state.Paused = false;
        player.Tick(new Vector3(), false);
        Check(player.TrackEnded && warnings == 2 && !player.LoadFailed, "Resuming past the damage ends the track instead of playing silence");
        player.Dispose();

        // The saved position may lie in the damage, so a failure before any audio resets it for a later selection.
        string brokenStart = WriteFake("damaged-start-track.fake", 1, rate, rate * 30, damageAt: 0);
        state = new RadioState { TrackPath = brokenStart, Powered = true, PositionSeconds = 5 };
        player = new RadioPlayback(new MonoBehaviour(), state, warn) { RepeatTrack = false };
        PumpUntil(player, false, () => player.LoadFailed);
        Check(player.Status == "Unable to decode music" && player.FailedPath == brokenStart && !player.TrackEnded && warnings == 3 &&
            state.PositionSeconds == 0 && last == "Unable to decode music: Scripted damage at frame 0 (damaged-start-track.fake)",
            "Damage before any audio fails the track, names only the file and resets its saved position: " + last);
        player.Dispose();

        // A repeating track longer than the window loops its look-ahead through the engine.
        string loopTrack = WriteFake("loop-track.fake", 1, 8000, 50 * 4096);
        state = new RadioState { TrackPath = loopTrack, Powered = true, PositionSeconds = 22 };
        player = new RadioPlayback(new MonoBehaviour(), state, warn) { RepeatTrack = true };
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        stream = (StreamedAudio)Field(player, "streamed");
        Check(stream.Loop && WaitReady(stream, 0), "A repeating track longer than the window holds its start before the wrap");
        player.RepeatTrack = false;
        player.Tick(new Vector3(), false);
        Check(!stream.Loop, "Turning repeat off stops the looping look-ahead");
        player.Dispose();

        // An adopted preload still opening gets its own load deadline.
        string current = WriteFake("keep-current.fake", 1, rate, rate * 30);
        string next = WriteFake("keep-next.fake", 1, rate, rate * 30, gate: true);
        float time = Time.realtimeSinceStartup;
        state = new RadioState { TrackPath = current, Powered = true };
        player = new RadioPlayback(new MonoBehaviour(), state, warn) { RepeatTrack = false };
        source = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        FakeDecoder.Gate.Reset();
        player.PreloadTrack(next); player.Tick(new Vector3(), false);
        var pending = (StreamedAudio)Field(player, "preloadStream");
        Time.realtimeSinceStartup += 50;
        player.Tick(new Vector3(), false);
        Check(pending != null && Field(player, "preloadStream") == pending && !pending.IsOpen, "An upcoming stream is still opening 50 s later");
        player.SetTrack(next);
        Time.realtimeSinceStartup += 20;
        player.Tick(new Vector3(), false);
        Check(Field(player, "streamed") == pending && !player.LoadFailed && player.Status == "Loading" && warnings == 3,
            "An adopted preload still opening is not timed out by the time it spent as a preload: status=" + player.Status);
        FakeDecoder.Gate.Set();
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(!player.LoadFailed && warnings == 3 && Field(player, "streamed") == pending, "The adopted preload then plays");
        Time.realtimeSinceStartup = time;

        // Restarting the current track keeps a valid next-track preload for a later selection.
        player.PreloadTrack(current); player.Tick(new Vector3(), false);
        var upcoming = (StreamedAudio)Field(player, "preloadStream");
        Check(upcoming != null && WaitReady(upcoming, 0), "The next track preloads beside the playing one");
        int opens = FakeDecoder.OpensOf(current);
        var playing = (StreamedAudio)Field(player, "streamed");
        state.PositionSeconds = 0;
        player.SetTrack(next);
        Check(Field(player, "preloadStream") == upcoming && !upcoming.WorkerCompleted, "A restart that cannot adopt the preload keeps it");
        player.Tick(new Vector3(), false);
        player.PreloadTrack(current);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        var restarted = (StreamedAudio)Field(player, "streamed");
        Check(WaitWorker(playing) && restarted != playing && restarted != upcoming && Field(player, "preloadStream") == upcoming &&
            !upcoming.WorkerCompleted && source.timeSamples == 0, "The restarted track reloads while the kept preload stays the only other window");
        player.SetTrack(current);
        Check(Field(player, "streamed") == upcoming && Field(player, "preloadStream") == null && FakeDecoder.OpensOf(current) == opens,
            "The kept preload is adopted later without a second open");
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);

        // A preload of the selected track that has not started yet is released, never opened twice.
        FakeDecoder.Hold.Reset();
        string held = WriteHeldTrack("keep-held.fake", 30);
        player.SetTrack(held);
        player.Tick(new Vector3(), false);
        player.PreloadTrack(next);
        player.Tick(new Vector3(), false);
        Check(Field(player, "preloadStream") == null && (string)Field(player, "preloadPath") == next, "An upcoming track waits while the current one loads");
        opens = FakeDecoder.OpensOf(next);
        player.SetTrack(next);
        Check(Field(player, "preloadPath") == null && Field(player, "preloadStream") == null, "Selecting an unstarted upcoming track releases that preload");
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        Check(FakeDecoder.OpensOf(next) == opens + 1 && Field(player, "preloadStream") == null, "The selected track opens exactly once");
        FakeDecoder.Hold.Set();
        player.Dispose();
        DeleteClosed(damagedTrack); DeleteClosed(brokenStart); DeleteClosed(loopTrack); DeleteClosed(current); DeleteClosed(next); DeleteClosed(held);
        CheckLoopDamage();
        CheckOutputReaders();
    }

    static void WaitFor(Func<bool> done, string what)
    {
        var deadline = DateTime.UtcNow.AddSeconds(10);
        while (!done() && DateTime.UtcNow < deadline) Thread.Sleep(1);
        if (!done()) throw new Exception(what + " did not happen within 10 seconds");
    }

    // Damage the looping look-ahead finds lies behind the playhead. It ends a repeating track only where the
    // wrapped playhead crosses it, and a failed re-seek there never moves the end ahead of the playhead.
    static void CheckLoopDamage()
    {
        int warnings = 0;
        string last = "";
        Action<string> warn = w => { warnings++; last = w; };
        const int rate = 8000, frames = 50 * 4096;
        string looped = WriteFake("loop-damaged.fake", 1, rate, frames, damageAt: 16000, damageEnd: 16001);
        AudioSettings.dspTime = 32000;
        var state = new RadioState { TrackPath = looped, Powered = true, PositionSeconds = 22 };
        var player = new RadioPlayback(new MonoBehaviour(), state, warn) { RepeatTrack = true };
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        var stream = (StreamedAudio)Field(player, "streamed");
        WaitFor(() => stream.LoopDamageFrame == 16000, "Loop look-ahead damage");
        var source = AudioSource.Last;
        AudioSettings.dspTime = source.ScheduledAt + 1;
        player.Tick(new Vector3(), false);
        Check(stream.DamageFrame == Int32.MaxValue && !player.TrackEnded && player.Status == "Playing" && warnings == 0 &&
            Math.Abs(state.PositionSeconds - 23) < 1e-9, "Damage the looping look-ahead finds behind the playhead leaves the track playing: " + last);
        state.Paused = true;
        player.Tick(new Vector3(), false);
        state.Paused = false;
        WaitStream(player);
        player.Tick(new Vector3(), false);
        Check(!player.TrackEnded && player.Status == "Playing" && warnings == 0, "A saved position past loop damage resumes instead of ending");
        double anchor = AudioSettings.dspTime;
        foreach (double at in new[] { 2.5, 2.7, 4.59 })
        {
            AudioSettings.dspTime = anchor + at;
            player.Tick(new Vector3(), false);
        }
        Check(!player.TrackEnded && warnings == 0 && Math.Abs(state.PositionSeconds - 15920 / 8000.0) < 1e-9,
            "The wrapped playhead plays up to the loop damage: position=" + state.PositionSeconds);
        AudioSettings.dspTime = anchor + 4.61;
        player.Tick(new Vector3(), false);
        Check(player.TrackEnded && warnings == 1 && last == "Music ended early at damaged audio: Scripted damage at frame 16000 (loop-damaged.fake)",
            "Crossing the loop damage after the wrap ends the track once: " + last);
        player.Dispose();

        string reseek = WriteFake("reseek-damaged.fake", 1, rate, frames, damageAt: 192000, seekFails: true);
        state = new RadioState { TrackPath = reseek, Powered = true, PositionSeconds = 20 };
        player = new RadioPlayback(new MonoBehaviour(), state, warn) { RepeatTrack = true };
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => player.Status == "Playing" || player.LoadFailed);
        stream = (StreamedAudio)Field(player, "streamed");
        WaitFor(() => stream.LoopDamageFrame == 0, "Failed loop re-seek");
        source = AudioSource.Last;
        AudioSettings.dspTime = source.ScheduledAt + 3.99;
        player.Tick(new Vector3(), false);
        Check(stream.DamageFrame == 192000 && !player.TrackEnded && player.Status == "Playing" && warnings == 1,
            "A failed re-seek in the looping look-ahead keeps the damage ahead of the playhead as the end: damage=" + stream.DamageFrame);
        AudioSettings.dspTime = source.ScheduledAt + 4.01;
        player.Tick(new Vector3(), false);
        Check(player.TrackEnded && warnings == 2 && last.StartsWith("Music ended early at damaged audio: Scripted damage at frame 192000", StringComparison.Ordinal),
            "The track ends at the damage ahead of the playhead: " + last);
        player.Dispose();

        // A file the worker may not read is a load failure, and its saved position waits for access to return.
        PcmDecoders.Register(".denied", (p, t) => { throw new UnauthorizedAccessException("Access to the music file is denied"); });
        string denied = Path.Combine(AppContext.BaseDirectory, "locked.denied");
        File.WriteAllText(denied, "");
        state = new RadioState { TrackPath = denied, Powered = true, PositionSeconds = 5 };
        player = new RadioPlayback(new MonoBehaviour(), state, warn);
        PumpUntil(player, false, () => player.LoadFailed);
        Check(player.Status == "Unable to load music" && state.PositionSeconds == 5 &&
            last == "Unable to load music: Access to the music file is denied (locked.denied)",
            "An unreadable file fails to load and keeps its saved position: " + last);
        player.Dispose();
        PcmDecoders.Register(".denied", null);
        DeleteClosed(looped); DeleteClosed(reseek); DeleteClosed(denied);
    }

    // Unity alone positions each output's reader. It pre-reads a new clip's opening and plays it unchanged from
    // sample 0, and it seeks on any other scheduled start. The radio creates its clip only once that opening is
    // decoded. An endpoint whose clip Unity refuses stays silent instead of sharing the radio's clip.
    static void CheckOutputReaders()
    {
        // A slow decoder, so the stream opens well before its opening is decoded.
        string path = WriteFake("readers.fake", 1, 48000, 480000, readDelay: 5);
        AudioSettings.dspTime = 40000;
        var state = new RadioState { TrackPath = path, Powered = true };
        var player = new RadioPlayback(new MonoBehaviour(), state, _ => { });
        var radio = AudioSource.Last;
        player.SetAcoustics(new Vector3(), true, 0);
        PumpUntil(player, false, () => radio.clip != null || player.LoadFailed);
        var stream = (StreamedAudio)Field(player, "streamed");
        int preRead = (int)Math.Round(48000 * AudioClip.PreReadSeconds);
        int wrong = radio.clip.PreReadFrom == 0 && radio.clip.PreReadData.Length == preRead ? -1 : -2;
        for (int i = 0; wrong == -1 && i < preRead; i++) if (Math.Abs(radio.clip.PreReadData[i] - FakeSample(i, 0)) > 1e-6f) wrong = i;
        Check(wrong == -1 && stream.MissingFrames == 0,
            "The radio creates its clip only once Unity's pre-read of the opening is decoded: missing=" + stream.MissingFrames +
            " from=" + radio.clip.PreReadFrom + " length=" + radio.clip.PreReadData.Length + " first wrong frame=" + wrong);
        PumpUntil(player, false, () => radio.Schedules > 0);
        var reader = (StreamedAudio.Reader)radio.clip.PcmRead.Target;
        Check(radio.Schedules == 1 && radio.timeSamples == 0 && reader.Cursor == preRead,
            "A start at sample 0 plays the pre-read, and the reader continues after it: cursor=" + reader.Cursor);
        state.Paused = true;
        player.Tick(new Vector3(), false);
        state.Paused = false;
        WaitStream(player);
        player.Tick(new Vector3(), false);
        Check(radio.Schedules == 2 && radio.timeSamples == 0 && radio.clip.PreReadFrom == 0 && reader.Cursor == preRead,
            "A stopped output scheduled again at sample 0 is rewound by Unity alone: cursor=" + reader.Cursor);
        AudioSource speaker;
        AudioClip.RejectCreate = true;
        try
        {
            player.BeginEndpoints(); player.SetEndpoint(1, new Vector3(), false, 2, 1, 1, 0); player.EndEndpoints();
            speaker = AudioSource.Last;
            AudioSettings.dspTime += 1;
            player.Tick(new Vector3(), false);
            Check(speaker.clip == null && speaker.Schedules == 0 && radio.isPlaying && player.Status == "Playing",
                "An endpoint whose clip Unity refuses stays silent rather than sharing the radio's clip");
        }
        finally { AudioClip.RejectCreate = false; }
        player.Tick(new Vector3(), false);
        var speakerReader = (StreamedAudio.Reader)speaker.clip.PcmRead.Target;
        Check(SameStream(radio.clip, speaker) && speaker.Schedules == 1 && speaker.timeSamples > 0 &&
            speaker.clip.PreReadFrom == speaker.timeSamples && speakerReader.Cursor == speaker.timeSamples + preRead,
            "The refused endpoint joins with its own clip once Unity creates one, and Unity seeks its reader: cursor=" + speakerReader.Cursor);

        // Unity reports a stopped voice as virtual. A running output that stopped natively is scheduled again.
        int repairs = player.DriftRepairs, schedules = speaker.Schedules;
        speaker.Stop();
        speaker.isVirtual = true;
        AudioSettings.dspTime += 1;
        radio.timeSamples = (int)(((int)Field(player, "scheduledSample") +
            (long)Math.Round((AudioSettings.dspTime - (double)Field(player, "scheduledDsp")) * 48000)) % 480000);
        player.Tick(new Vector3(), false);
        Check(speaker.Schedules == schedules + 1 && speaker.isPlaying && player.DriftRepairs == repairs + 1,
            "A natively stopped output is scheduled again although Unity reports it virtual");
        player.Dispose();
        DeleteClosed(path);
        CheckPreReadReadiness();
        CheckStartContinuity();
    }

    // From frame 0, and after the seek of a saved position 8 s in, the stream hands Unity exactly the sequential
    // decode, with no reseek, gap or repeat, and Unity's pre-read right at readiness finds every frame.
    // SAILWIND_RADIO_MUSIC_FOLDER names an optional folder of real files that is only read.
    static void CheckStartContinuity()
    {
        var files = new List<string>
        {
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "stereo-noise.mp3"),
            Path.Combine(AppContext.BaseDirectory, "Fixtures", "sine-stereo.ogg"),
        };
        string music = Environment.GetEnvironmentVariable("SAILWIND_RADIO_MUSIC_FOLDER");
        if (!String.IsNullOrEmpty(music) && Directory.Exists(music))
            foreach (string file in Directory.GetFiles(music)) if (PcmDecoders.Supports(file)) files.Add(file);
        foreach (string file in files)
        {
            float[] reference;
            int channels, rate;
            using (var decoder = PcmDecoders.Open(file, CancellationToken.None))
            {
                channels = decoder.Channels;
                rate = decoder.SampleRate;
                reference = new float[rate * 12 * channels];
                int filled = 0;
                while (filled < reference.Length)
                {
                    int got = decoder.Read(reference, filled, reference.Length - filled);
                    if (got <= 0) break;
                    filled += got;
                }
                Array.Resize(ref reference, filled);
            }
            int frames = reference.Length / channels;
            foreach (int from in new[] { 0, frames >= rate * 11 ? rate * 8 : -1 })
            {
                if (from < 0) continue;
                var stream = OpenStream(file, from / (double)rate);
                string label = Path.GetFileName(file) + " from " + from / rate + " s";
                Check(WaitReady(stream, from), label + " becomes ready");
                var reader = stream.CreateReader();
                reader.SetPosition(from);
                int preRead = (int)Math.Round(rate * AudioClip.PreReadSeconds), end = Math.Min(frames, from + rate * 3), mismatch = -1;
                var data = new float[2048 * channels];
                for (int at = from; at < end && mismatch < 0; at += 2048)
                {
                    if (at >= from + preRead)
                    {
                        stream.Request(at);
                        WaitReady(stream, at);
                    }
                    reader.Read(data);
                    for (int i = 0; i < data.Length && at + i / channels < end; i++)
                    {
                        float want = Single.IsNaN(reference[at * channels + i]) ? 0f : Math.Max(-1f, Math.Min(1f, reference[at * channels + i]));
                        if (data[i] != want) { mismatch = at + i / channels; break; }
                    }
                }
                Check(mismatch < 0 && stream.MissingFrames == 0, label + " streams " + ((end - from) / (double)rate).ToString("F1") +
                    " s exactly as a sequential decode: mismatch frame=" + mismatch + " missing=" + stream.MissingFrames);
                stream.Dispose();
                WaitWorker(stream);
            }
        }
    }

    // A stream is ready only once Unity's whole pre-read is decoded, continuing from the start when looping.
    static void CheckPreReadReadiness()
    {
        int preRead = (int)Math.Round(48000 * AudioClip.PreReadSeconds);
        string path = WriteFake("preread.fake", 1, 48000, 48000 * 30, readDelay: 20);
        var stream = OpenStream(path, 0);
        Check(WaitReady(stream, 0), "A slow stream becomes ready at its opening");
        var reader = stream.CreateReader();
        var block = new float[preRead];
        reader.Read(block);
        bool exact = true;
        for (int i = 0; exact && i < preRead; i++) exact = Math.Abs(block[i] - FakeSample(i, 0)) <= 1e-6f;
        Check(exact && stream.MissingFrames == 0, "Readiness covers Unity's pre-read of the opening: missing=" + stream.MissingFrames);
        stream.Dispose();
        WaitWorker(stream);

        int frames = 48000 * 30, frame = frames - 48000 * 3 / 10;
        var looping = OpenStream(path, frame / 48000.0);
        looping.Loop = true;
        looping.Request(frame);
        Check(WaitReady(looping, frame), "A looping stream becomes ready near its end");
        var wrapped = looping.CreateReader();
        var tail = new float[frames - frame];
        wrapped.SetPosition(frame);
        wrapped.Read(tail);
        var head = new float[preRead - tail.Length];
        wrapped.SetPosition(0);
        wrapped.Read(head);
        Check(looping.MissingFrames == 0 && Math.Abs(head[0] - FakeSample(0, 0)) <= 1e-6f && Math.Abs(tail[0] - FakeSample(frame, 0)) <= 1e-6f,
            "Readiness near the end of a looping track covers the pre-read across the wrap: missing=" + looping.MissingFrames);
        looping.Dispose();
        WaitWorker(looping);
        DeleteClosed(path);
    }

    static void CheckStreamingIsolation()
    {
        // Every format streams through PcmDecoders. Unity PCM upload and web requests appear nowhere in production audio.
        string audio = Path.Combine(RepoRoot, "src", "Audio");
        foreach (string file in Directory.GetFiles(audio, "*.cs"))
        {
            string text = File.ReadAllText(file);
            string name = Path.GetFileName(file);
            Check(text.IndexOf("SetData(", StringComparison.Ordinal) < 0, name + " never uploads PCM with AudioClip.SetData");
            Check(text.IndexOf("UnityWebRequest", StringComparison.Ordinal) < 0 && text.IndexOf("UnityEngine.Networking", StringComparison.Ordinal) < 0 &&
                text.IndexOf("DownloadHandler", StringComparison.Ordinal) < 0 && text.IndexOf("AudioType", StringComparison.Ordinal) < 0,
                name + " never uses Unity web requests or its runtime audio loader");
        }
        string project = File.ReadAllText(Path.Combine(RepoRoot, "Radio.csproj"));
        Check(project.IndexOf("UnityWebRequest", StringComparison.Ordinal) < 0, "The plugin no longer references Unity web request modules");
        Check(typeof(RadioPlayback).Assembly.GetType("SailwindRadio.OggClipLoad") == null &&
            typeof(RadioPlayback).Assembly.GetType("SailwindRadio.OggVorbisInfo") == null, "The Unity Ogg loader and its length probe are gone");
        foreach (string name in new[] { "song.mp3", "song.WAV", "song.flac", "song.ogg", "SONG.OGG" })
            Check(PcmDecoders.Supports(name), name + " routes to an in-source decoder");
        Check(!PcmDecoders.Supports("song.opus") && !PcmDecoders.Supports("song.oga"), "Other Ogg extensions stay unsupported");
    }
}
