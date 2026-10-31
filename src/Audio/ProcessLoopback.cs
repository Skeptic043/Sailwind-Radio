using System;
using System.IO;
using System.Runtime.InteropServices;
using System.Threading;

namespace SailwindRadio.Audio
{
    // Captures one application's audio, child processes included, through the documented Windows
    // process loopback client. It needs Windows 10 build 20348 or later, and no driver, helper
    // process or administrator rights. Windows converts the audio to the requested float stereo
    // rate. COM calls go through ComCalls vtables. Activation is polled because Windows reports
    // it on a thread pool thread where no managed code may run, see CompletionHandler. 64-bit only. Call
    // from one worker thread.
    internal sealed class ProcessLoopback : IDisposable
    {
        internal const int Channels = 2;
        private const string VirtualDevice = "VAD\\Process_Loopback";
        private const int ProcessLoopbackActivation = 1, IncludeTargetProcessTree = 0;
        private const int VariantBlob = 65;
        private const int StreamLoopback = 0x20000, StreamEventCallback = 0x40000, StreamAutoConvertPcm = unchecked((int)0x80000000), StreamDefaultQuality = 0x08000000;
        private const long BufferHundredNanoseconds = 2000000;
        private const int BufferSilent = 0x2;
        private static readonly Guid AudioClientId = new Guid("1CB9AD4C-DBFA-4c32-B178-C2F568A703B2");
        private static readonly Guid CaptureClientId = new Guid("C8ADBD64-E71E-48a0-A4DE-185C395CD317");

        [DllImport("mmdevapi.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        private static extern int ActivateAudioInterfaceAsync(string path, ref Guid iid, IntPtr parameters, IntPtr handler, out IntPtr operation);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceMethod(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetActivateResultMethod(IntPtr self, out int result, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int InitializeMethod(IntPtr self, int shareMode, int flags, long duration, long periodicity, IntPtr format, IntPtr session);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetServiceMethod(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CallMethod(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int HandleMethod(IntPtr self, IntPtr handle);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        private delegate int GetBufferMethod(IntPtr self, out IntPtr data, out int frames, out int flags, IntPtr devicePosition, IntPtr counterPosition);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int FramesMethod(IntPtr self, out int frames);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseBufferMethod(IntPtr self, int frames);

        // Vtable slots after IUnknown's 0 to 2.
        private const int SlotQueryInterface = 0, SlotGetActivateResult = 3;
        private const int StillRunning = unchecked((int)0x8000000E), ActivateMilliseconds = 5000;
        private const int SlotInitialize = 3, SlotStart = 10, SlotStop = 11, SlotSetEventHandle = 13, SlotGetService = 14;
        private const int SlotGetBuffer = 3, SlotReleaseBuffer = 4, SlotGetNextPacketSize = 5;

        private IntPtr client, capture;
        // Windows signals it each time a packet is ready, so the worker wakes on time instead of on the coarse system timer.
        private readonly AutoResetEvent ready = new AutoResetEvent(false);

        internal int SampleRate { get; private set; }

        // Blocks until Windows activates the client. Throws when the system or process refuses it.
        internal static ProcessLoopback Open(int processId, int sampleRate)
        {
            var loopback = new ProcessLoopback { SampleRate = sampleRate };
            try
            {
                loopback.client = Activate(processId);
                loopback.Initialize(sampleRate);
                return loopback;
            }
            catch { loopback.Dispose(); throw; }
        }

        // Waits up to milliseconds for the next packet. True when one is ready.
        internal bool Wait(int milliseconds) { return ready.WaitOne(milliseconds); }

        // Copies whole captured packets into buffer and returns the samples written. Never waits.
        internal int Read(float[] buffer, int offset, int count)
        {
            int written = 0;
            while (true)
            {
                int frames;
                Check(ComCalls.Method<FramesMethod>(capture, SlotGetNextPacketSize)(capture, out frames), "read application audio");
                if (frames == 0 || written + frames * Channels > count) return written;
                IntPtr data;
                int flags;
                Check(ComCalls.Method<GetBufferMethod>(capture, SlotGetBuffer)(capture, out data, out frames, out flags, IntPtr.Zero, IntPtr.Zero),
                    "read application audio");
                try
                {
                    int samples = frames * Channels;
                    if ((flags & BufferSilent) != 0 || data == IntPtr.Zero) Array.Clear(buffer, offset + written, samples);
                    else Marshal.Copy(data, buffer, offset + written, samples);
                    written += samples;
                }
                finally { ComCalls.Method<ReleaseBufferMethod>(capture, SlotReleaseBuffer)(capture, frames); }
            }
        }

        private void Initialize(int sampleRate)
        {
            IntPtr format = Marshal.AllocHGlobal(18);
            try
            {
                // WAVEFORMATEX for 32-bit float stereo.
                Marshal.WriteInt16(format, 0, 3);
                Marshal.WriteInt16(format, 2, Channels);
                Marshal.WriteInt32(format, 4, sampleRate);
                Marshal.WriteInt32(format, 8, sampleRate * Channels * 4);
                Marshal.WriteInt16(format, 12, Channels * 4);
                Marshal.WriteInt16(format, 14, 32);
                Marshal.WriteInt16(format, 16, 0);
                Check(ComCalls.Method<InitializeMethod>(client, SlotInitialize)(client, 0,
                    StreamLoopback | StreamEventCallback | StreamAutoConvertPcm | StreamDefaultQuality, BufferHundredNanoseconds, 0, format, IntPtr.Zero), "start application audio");
            }
            finally { Marshal.FreeHGlobal(format); }
            Guid id = CaptureClientId;
            Check(ComCalls.Method<HandleMethod>(client, SlotSetEventHandle)(client, ready.SafeWaitHandle.DangerousGetHandle()), "start application audio");
            Check(ComCalls.Method<GetServiceMethod>(client, SlotGetService)(client, ref id, out capture), "start application audio");
            Check(ComCalls.Method<CallMethod>(client, SlotStart)(client), "start application audio");
        }

        private static IntPtr Activate(int processId)
        {
            // AUDIOCLIENT_ACTIVATION_PARAMS inside a PROPVARIANT blob. The blob pointer follows its 4-byte size.
            IntPtr parameters = Marshal.AllocHGlobal(12);
            IntPtr variant = ComCalls.NewVariant();
            try
            {
                Marshal.WriteInt32(parameters, 0, ProcessLoopbackActivation);
                Marshal.WriteInt32(parameters, 4, processId);
                Marshal.WriteInt32(parameters, 8, IncludeTargetProcessTree);
                Marshal.WriteInt16(variant, 0, VariantBlob);
                Marshal.WriteInt32(variant, 8, 12);
                Marshal.WriteIntPtr(variant, 8 + IntPtr.Size, parameters);
                Guid id = AudioClientId;
                IntPtr operation;
                Check(ActivateAudioInterfaceAsync(VirtualDevice, ref id, variant, CompletionHandler.Instance, out operation), "open application audio");
                try
                {
                    // The handler raises no signal, so the operation is asked until it stops reporting that it is still running.
                    int call, result;
                    IntPtr unknown;
                    for (int waited = 0; ; waited += 5)
                    {
                        call = ComCalls.Method<GetActivateResultMethod>(operation, SlotGetActivateResult)(operation, out result, out unknown);
                        if (call != StillRunning) break;
                        if (waited >= ActivateMilliseconds) throw new IOException("Windows did not open application audio");
                        Thread.Sleep(5);
                    }
                    Check(call, "open application audio");
                    Check(result, "open application audio");
                    try
                    {
                        IntPtr audioClient;
                        Check(ComCalls.Method<QueryInterfaceMethod>(unknown, SlotQueryInterface)(unknown, ref id, out audioClient), "open application audio");
                        return audioClient;
                    }
                    finally { ComCalls.Release(ref unknown); }
                }
                finally { ComCalls.Release(ref operation); }
            }
            finally
            {
                // The parameters belong to this call, so the variant is not cleared through PropVariantClear.
                Marshal.FreeHGlobal(variant);
                Marshal.FreeHGlobal(parameters);
            }
        }

        private static void Check(int result, string action)
        {
            if (result < 0) throw new IOException("Unable to " + action + " (error 0x" + result.ToString("X8") + ")");
        }

        public void Dispose()
        {
            if (client != IntPtr.Zero) ComCalls.Method<CallMethod>(client, SlotStop)(client);
            ComCalls.Release(ref capture);
            ComCalls.Release(ref client);
            ready.Dispose();
        }
    }

    // The completion handler ActivateAudioInterfaceAsync requires. Windows calls its AddRef, Release and
    // ActivateCompleted on a thread pool thread, and any managed code there keeps Unity 2019.1 Mono from
    // exiting for minutes. Those three slots point at kernel32's GetCurrentThreadId, which ignores its
    // arguments and changes nothing, so the handler keeps no count, raises no signal and is never freed.
    // Windows asks QueryInterface for IAgileObject on the activating thread, so that method stays managed.
    // Sharing one native function between signatures is valid only under the single x64 calling convention.
    internal static class CompletionHandler
    {
        private static readonly Guid Unknown = new Guid("00000000-0000-0000-C000-000000000046");
        private static readonly Guid Handler = new Guid("41D949AB-9862-444A-80F6-C261334DA5EB");
        private static readonly Guid Agile = new Guid("94ea2b94-e9cc-49e0-c0ff-ee64ca8f5b90");
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryInterfaceMethod(IntPtr self, ref Guid iid, out IntPtr value);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);
        private static readonly QueryInterfaceMethod query = QueryInterface;
        internal static readonly IntPtr Instance = Create();

        internal static bool Supported => IntPtr.Size == 8;

        private static IntPtr Create()
        {
            if (!Supported) return IntPtr.Zero;
            IntPtr nothing = GetProcAddress(GetModuleHandle("kernel32.dll"), "GetCurrentThreadId");
            IntPtr table = Marshal.AllocHGlobal(4 * IntPtr.Size);
            Marshal.WriteIntPtr(table, 0, Marshal.GetFunctionPointerForDelegate(query));
            for (int slot = 1; slot < 4; slot++) Marshal.WriteIntPtr(table, slot * IntPtr.Size, nothing);
            IntPtr instance = Marshal.AllocHGlobal(IntPtr.Size);
            Marshal.WriteIntPtr(instance, 0, table);
            return instance;
        }

        private static int QueryInterface(IntPtr self, ref Guid iid, out IntPtr value)
        {
            bool known = iid == Unknown || iid == Handler || iid == Agile;
            value = known ? self : IntPtr.Zero;
            return known ? 0 : unchecked((int)0x80004002);
        }
    }
}
