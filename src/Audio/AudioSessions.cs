using System;
using System.Runtime.InteropServices;

namespace SailwindRadio.Audio
{
    // One application's audio session on one output device, valid only inside AudioSessions.Visit.
    internal sealed class AudioSession
    {
        internal IntPtr Volume;
        internal int ProcessId;
        // Stable across application restarts on the same device, so a crash record can find it again.
        internal string Identifier;

        internal float Level
        {
            get
            {
                float level;
                return ComCalls.Method<GetLevelMethod>(Volume, 4)(Volume, out level) >= 0 ? level : -1f;
            }
            set
            {
                Guid context = Guid.Empty;
                ComCalls.Method<SetLevelMethod>(Volume, 3)(Volume, value, ref context);
            }
        }

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int GetLevelMethod(IntPtr self, out float level);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int SetLevelMethod(IntPtr self, float level, ref Guid context);
    }

    // Lists every application audio session on every active output device through the documented Core
    // Audio session interfaces through ComCalls vtable calls. Call from an MTA worker thread.
    internal static class AudioSessions
    {
        private const int Render = 0, Active = 1, AllContexts = 23, SessionExpired = 2;
        private static readonly Guid EnumeratorClass = new Guid("BCDE0395-E52F-467C-8E3D-C4579291692E");
        private static readonly Guid EnumeratorId = new Guid("A95664D2-9614-4F35-A746-DE8DB63617E6");
        private static readonly Guid ManagerId = new Guid("77AA99A0-1BD6-484F-8BC7-2C654C9A9B6F");
        private static readonly Guid ControlId = new Guid("bfb7ff88-7239-4fc9-8fa2-07c950be9c6d");
        private static readonly Guid VolumeId = new Guid("87CE5498-68D6-44E5-9215-6DA47EF883D8");

        [DllImport("ole32.dll", ExactSpelling = true)]
        private static extern int CoCreateInstance(ref Guid type, IntPtr outer, int context, ref Guid iid, out IntPtr value);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoTaskMemFree(IntPtr memory);

        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int QueryMethod(IntPtr self, ref Guid iid, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int EnumMethod(IntPtr self, int flow, int states, out IntPtr devices);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int CountMethod(IntPtr self, out int count);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ItemMethod(IntPtr self, int index, out IntPtr item);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ActivateMethod(IntPtr self, ref Guid iid, int context, IntPtr parameters, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PointerMethod(IntPtr self, out IntPtr value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int IntMethod(IntPtr self, out int value);
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int PlainMethod(IntPtr self);

        // Vtable slots after IUnknown's 0 to 2.
        private const int SlotEnumEndpoints = 3, SlotCount = 3, SlotItem = 4, SlotActivate = 3, SlotSessionEnumerator = 5;
        private const int SlotState = 3, SlotIdentifier = 12, SlotProcessId = 14, SlotSystemSounds = 15;

        // Calls visit once for each application session. System sounds and expired sessions are skipped.
        internal static void Visit(Action<AudioSession> visit)
        {
            Guid type = EnumeratorClass, id = EnumeratorId;
            IntPtr enumerator, devices = IntPtr.Zero;
            Check(CoCreateInstance(ref type, IntPtr.Zero, AllContexts, ref id, out enumerator));
            try
            {
                Check(ComCalls.Method<EnumMethod>(enumerator, SlotEnumEndpoints)(enumerator, Render, Active, out devices));
                int deviceCount;
                Check(ComCalls.Method<CountMethod>(devices, SlotCount)(devices, out deviceCount));
                for (int index = 0; index < deviceCount; index++)
                {
                    IntPtr device;
                    if (ComCalls.Method<ItemMethod>(devices, SlotItem)(devices, index, out device) < 0) continue;
                    try { VisitDevice(device, visit); }
                    finally { ComCalls.Release(ref device); }
                }
            }
            finally
            {
                ComCalls.Release(ref devices);
                ComCalls.Release(ref enumerator);
            }
        }

        private static void VisitDevice(IntPtr device, Action<AudioSession> visit)
        {
            Guid managerId = ManagerId;
            IntPtr manager, sessions = IntPtr.Zero;
            // A device can vanish between listing and activation. It simply has no sessions then.
            if (ComCalls.Method<ActivateMethod>(device, SlotActivate)(device, ref managerId, AllContexts, IntPtr.Zero, out manager) < 0) return;
            try
            {
                if (ComCalls.Method<PointerMethod>(manager, SlotSessionEnumerator)(manager, out sessions) < 0) return;
                int count;
                if (ComCalls.Method<CountMethod>(sessions, SlotCount)(sessions, out count) < 0) return;
                for (int index = 0; index < count; index++)
                {
                    IntPtr control;
                    if (ComCalls.Method<ItemMethod>(sessions, SlotItem)(sessions, index, out control) < 0) continue;
                    try { VisitSession(control, visit); }
                    finally { ComCalls.Release(ref control); }
                }
            }
            finally
            {
                ComCalls.Release(ref sessions);
                ComCalls.Release(ref manager);
            }
        }

        private static void VisitSession(IntPtr control, Action<AudioSession> visit)
        {
            Guid controlId = ControlId, volumeId = VolumeId;
            IntPtr control2, volume = IntPtr.Zero;
            if (ComCalls.Method<QueryMethod>(control, 0)(control, ref controlId, out control2) < 0) return;
            try
            {
                int state, processId;
                if (ComCalls.Method<IntMethod>(control2, SlotState)(control2, out state) < 0 || state == SessionExpired) return;
                // S_OK means the system sounds session. Anything else is an application.
                if (ComCalls.Method<PlainMethod>(control2, SlotSystemSounds)(control2) == 0) return;
                if (ComCalls.Method<IntMethod>(control2, SlotProcessId)(control2, out processId) < 0 || processId == 0) return;
                IntPtr text;
                if (ComCalls.Method<PointerMethod>(control2, SlotIdentifier)(control2, out text) < 0) return;
                string identifier = Marshal.PtrToStringUni(text);
                CoTaskMemFree(text);
                if (ComCalls.Method<QueryMethod>(control2, 0)(control2, ref volumeId, out volume) < 0) return;
                visit(new AudioSession { Volume = volume, ProcessId = processId, Identifier = identifier ?? "" });
            }
            finally
            {
                ComCalls.Release(ref volume);
                ComCalls.Release(ref control2);
            }
        }

        private static void Check(int result)
        {
            if (result < 0) throw new System.IO.IOException("Windows audio sessions are unavailable (error 0x" + result.ToString("X8") + ")");
        }
    }
}
