using System;
using System.Runtime.InteropServices;

namespace SailwindRadio.Audio
{
    // Windows Media Foundation through P/Invoke and ComCalls vtable calls. Every pointer is released
    // explicitly by its owner.
    internal static class MediaFoundation
    {
        private const int Version = 0x20070;
        private const int StartupNoSockets = 1;
        private const int EnumSync = 0x1, EnumLocal = 0x10, EnumSortAndFilter = 0x40;
        internal const int FirstAudioStream = unchecked((int)0xFFFFFFFD);
        internal const int AllStreams = unchecked((int)0xFFFFFFFE);
        internal const int MediaSource = unchecked((int)0xFFFFFFFF);
        internal const int ReaderError = 0x1, ReaderEndOfStream = 0x2, ReaderTypeChanged = 0x20;

        internal static readonly Guid MajorType = new Guid("48eba18e-f8c9-4687-bf11-0a74c9f96a8f");
        internal static readonly Guid Subtype = new Guid("f7e34c9a-42e8-4714-b74b-cb29d72c35e5");
        internal static readonly Guid Audio = new Guid("73647561-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Float = new Guid("00000003-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Aac = new Guid("00001610-0000-0010-8000-00aa00389b71");
        // The MP4 source reports Apple Lossless with the second identifier. Both are accepted.
        internal static readonly Guid AlacFourCc = new Guid("63616c61-0000-0010-8000-00aa00389b71");
        internal static readonly Guid Alac = new Guid("616c6163-767a-494d-b478-f29d25dc9037");
        internal static readonly Guid Channels = new Guid("37e48bf5-645e-4c5b-89de-ada9e29b696a");
        internal static readonly Guid SamplesPerSecond = new Guid("5faeeae7-0290-4c31-9e8a-c534f68d9dba");
        internal static readonly Guid ChannelMask = new Guid("55fb5765-644a-4caf-8479-938983bb1588");
        internal static readonly Guid Duration = new Guid("6c990d33-bb8e-477a-8598-0d5d96fcd88a");
        internal static readonly Guid Protected = new Guid("00af2181-bdc2-423c-abca-f503593bc121");
        internal static readonly Guid AudioDecoders = new Guid("9ea73fb4-ef7a-4559-8d5d-719d8f0426c7");
        internal static readonly Guid AacPayloadType = new Guid("bfbabe79-7434-4d1c-94f0-72a3b9e17188");
        internal static readonly Guid UserData = new Guid("b6bc765f-4c3b-40a4-bd51-2535b66fe09d");
        internal static readonly Guid TransformInterface = new Guid("bf94c121-5b05-4e6f-8000-ba598961414d");

        internal const int MftMessageBeginStreaming = 0x10000000;
        internal const int MftMessageStartOfStream = 0x10000003;
        internal const int MftOutputProvidesSamples = 0x100;
        internal const int MftOutputCanProvideSamples = 0x200;
        internal const int HrNeedMoreInput = unchecked((int)0xC00D6D72);
        internal const int HrStreamChange = unchecked((int)0xC00D6D61);

        [StructLayout(LayoutKind.Sequential)]
        internal struct MftOutputStreamInfo
        {
            internal int Flags;
            internal int Size;
            internal int Alignment;
        }

        [StructLayout(LayoutKind.Sequential)]
        internal struct MftOutputDataBuffer
        {
            internal int StreamId;
            internal IntPtr Sample;
            internal int Status;
            internal IntPtr Events;
        }

        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFStartup(int version, int flags);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFShutdown();
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMediaType(out IntPtr type);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateSample(out IntPtr sample);
        [DllImport("mfplat.dll", ExactSpelling = true)] internal static extern int MFCreateMemoryBuffer(int maxLength, out IntPtr buffer);
        [DllImport("mfplat.dll", ExactSpelling = true)]
        private static extern int MFTEnumEx(Guid category, int flags, IntPtr input, IntPtr output, out IntPtr activates, out int count);
        [DllImport("mfreadwrite.dll", ExactSpelling = true, CharSet = CharSet.Unicode)]
        internal static extern int MFCreateSourceReaderFromURL(string url, IntPtr attributes, out IntPtr reader);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoTaskMemFree(IntPtr memory);
        [DllImport("ole32.dll", ExactSpelling = true)] internal static extern int PropVariantClear(IntPtr variant);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern IntPtr GetModuleHandle(string name);
        [DllImport("kernel32.dll", CharSet = CharSet.Ansi, ExactSpelling = true)] private static extern IntPtr GetProcAddress(IntPtr module, string name);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetStreamSelectionMethod(IntPtr self, int stream, int selected);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetNativeMediaTypeMethod(IntPtr self, int stream, int index, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetCurrentMediaTypeMethod(IntPtr self, int stream, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetCurrentMediaTypeMethod(IntPtr self, int stream, IntPtr reserved, IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetCurrentPositionMethod(IntPtr self, ref Guid format, IntPtr position);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)]
        internal delegate int ReadSampleMethod(IntPtr self, int stream, int control, out int actual, out int flags, out long time, out IntPtr sample);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetPresentationAttributeMethod(IntPtr self, int stream, ref Guid key, IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetUInt32Method(IntPtr self, ref Guid key, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetUInt32Method(IntPtr self, ref Guid key, int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetGuidMethod(IntPtr self, ref Guid key, out Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetGuidMethod(IntPtr self, ref Guid key, ref Guid value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetBlobMethod(IntPtr self, ref Guid key, byte[] data, int length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int GetPointerMethod(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int LockMethod(IntPtr self, out IntPtr data, out int maximum, out int current);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int UnlockMethod(IntPtr self);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int SetLengthMethod(IntPtr self, int length);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int AddBufferMethod(IntPtr self, IntPtr buffer);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int ActivateObjectMethod(IntPtr self, ref Guid iid, out IntPtr result);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MftGetOutputStreamInfoMethod(IntPtr self, int streamId, out MftOutputStreamInfo info);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MftGetOutputAvailableTypeMethod(IntPtr self, int streamId, int index, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MftSetTypeMethod(IntPtr self, int streamId, IntPtr type, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MftGetOutputCurrentTypeMethod(IntPtr self, int streamId, out IntPtr type);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MftProcessMessageMethod(IntPtr self, int message, IntPtr param);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MftProcessInputMethod(IntPtr self, int streamId, IntPtr sample, int flags);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] internal delegate int MftProcessOutputMethod(IntPtr self, int flags, int count, ref MftOutputDataBuffer output, out int status);

        // Vtable slots. IUnknown owns 0 to 2 and IMFAttributes 3 to 32.
        internal const int SlotGetUInt32 = 7, SlotGetGuid = 10, SlotSetUInt32 = 21, SlotSetGuid = 24, SlotSetBlob = 26;
        internal const int SlotActivateObject = 33;
        internal const int SlotSetStreamSelection = 4, SlotGetNativeMediaType = 5, SlotGetCurrentMediaType = 6,
            SlotSetCurrentMediaType = 7, SlotSetCurrentPosition = 8, SlotReadSample = 9, SlotGetPresentationAttribute = 12;
        internal const int SlotConvertToContiguousBuffer = 41, SlotAddBuffer = 42, SlotLock = 3, SlotUnlock = 4, SlotSetCurrentLength = 6;
        internal const int SlotMftGetOutputStreamInfo = 7, SlotMftGetOutputAvailableType = 14, SlotMftSetInputType = 15,
            SlotMftSetOutputType = 16, SlotMftGetOutputCurrentType = 18, SlotMftProcessMessage = 23,
            SlotMftProcessInput = 24, SlotMftProcessOutput = 25;

        internal static int GetUInt32(IntPtr attributes, Guid key, int fallback)
        {
            int value;
            return ComCalls.Method<GetUInt32Method>(attributes, SlotGetUInt32)(attributes, ref key, out value) >= 0 ? value : fallback;
        }

        internal static bool GetGuid(IntPtr attributes, Guid key, out Guid value)
        {
            return ComCalls.Method<GetGuidMethod>(attributes, SlotGetGuid)(attributes, ref key, out value) >= 0;
        }

        internal static bool WineDetected()
        {
            IntPtr ntdll = GetModuleHandle("ntdll.dll");
            return ntdll != IntPtr.Zero && GetProcAddress(ntdll, "wine_get_version") != IntPtr.Zero;
        }

        // True when Media Foundation starts and lists at least one local AAC decoder.
        internal static bool AacDecoderAvailable()
        {
            using (new ComCalls.Apartment())
            {
                if (MFStartup(Version, StartupNoSockets) < 0) return false;
                try { return DecoderCount(Aac) > 0; }
                finally { MFShutdown(); }
            }
        }

        internal static int DecoderCount(Guid subtype)
        {
            IntPtr info = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.StructureToPtr(Audio, info, false);
                Marshal.StructureToPtr(subtype, new IntPtr(info.ToInt64() + 16), false);
                IntPtr list;
                int count;
                if (MFTEnumEx(AudioDecoders, EnumSync | EnumLocal | EnumSortAndFilter, info, IntPtr.Zero, out list, out count) < 0) return 0;
                for (int i = 0; i < count; i++)
                {
                    IntPtr activate = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                    ComCalls.Release(ref activate);
                }
                if (list != IntPtr.Zero) CoTaskMemFree(list);
                return count;
            }
            finally { Marshal.FreeHGlobal(info); }
        }

        internal static void Startup()
        {
            int result = MFStartup(Version, StartupNoSockets);
            if (result < 0) throw new System.IO.IOException("Media Foundation could not start (error 0x" + result.ToString("X8") + ")");
        }

        // Activates the first local Windows AAC decoder MFT (IMFTransform). Caller owns the returned COM pointer.
        internal static IntPtr ActivateAacDecoder()
        {
            IntPtr info = Marshal.AllocHGlobal(32);
            try
            {
                Marshal.StructureToPtr(Audio, info, false);
                Marshal.StructureToPtr(Aac, new IntPtr(info.ToInt64() + 16), false);
                IntPtr list;
                int count;
                if (MFTEnumEx(AudioDecoders, EnumSync | EnumLocal | EnumSortAndFilter, info, IntPtr.Zero, out list, out count) < 0 || count <= 0)
                    throw new System.IO.IOException("AAC decoding is unavailable on this system.");
                IntPtr transform = IntPtr.Zero;
                try
                {
                    Guid iid = TransformInterface;
                    for (int i = 0; i < count; i++)
                    {
                        IntPtr activate = Marshal.ReadIntPtr(list, i * IntPtr.Size);
                        if (transform == IntPtr.Zero && activate != IntPtr.Zero)
                        {
                            int hr = ComCalls.Method<ActivateObjectMethod>(activate, SlotActivateObject)(activate, ref iid, out transform);
                            if (hr < 0) transform = IntPtr.Zero;
                        }
                        ComCalls.Release(ref activate);
                    }
                }
                finally
                {
                    if (list != IntPtr.Zero) CoTaskMemFree(list);
                }
                if (transform == IntPtr.Zero)
                    throw new System.IO.IOException("Could not activate the Windows AAC decoder.");
                return transform;
            }
            finally { Marshal.FreeHGlobal(info); }
        }
    }

    // One start-up decision for M4A and AAC. Under Wine or Proton Media Foundation is never
    // called, and no setting overrides that.
    internal static class MediaFoundationSupport
    {
        internal const string WineReason = "Proton or Wine detected";
        internal const string UnavailableReason = "Media Foundation is unavailable on this system";
        private static volatile bool enabled;
        private static volatile string reason = UnavailableReason;

        internal static bool Enabled { get { return enabled; } }
        internal static string Reason { get { return enabled ? "" : reason; } }
        internal static string Summary
        {
            get { return enabled ? "M4A and AAC playback is enabled through Media Foundation" : "M4A and AAC playback is disabled. " + reason; }
        }

        // Probes run in order. Any probe exception disables the formats.
        internal static bool Decide(Func<bool> wine, Func<bool> aacDecoder, out string why)
        {
            why = UnavailableReason;
            try
            {
                if (wine()) { why = WineReason; return false; }
                if (!aacDecoder()) return false;
                why = "";
                return true;
            }
            catch (Exception) { return false; }
        }

        internal static void Detect() { Detect(MediaFoundation.WineDetected, MediaFoundation.AacDecoderAvailable); }

        internal static void Detect(Func<bool> wine, Func<bool> aacDecoder)
        {
            string why;
            bool decided = Decide(wine, aacDecoder, out why);
            reason = why;
            enabled = decided;
        }
    }
}
