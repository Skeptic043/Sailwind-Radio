using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;

namespace SailwindRadio.Audio
{
    // Calls COM objects through their vtables, for Media Foundation, process loopback capture, audio sessions and
    // media controls. The Unity 2019.1 Mono spike crashed on a [ComImport] interface out-parameter, so no runtime
    // callable wrappers exist here. Every pointer is released explicitly by its owner.
    internal static class ComCalls
    {
        private const int MultiThreaded = 0, SlotRelease = 2;
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern int CoInitializeEx(IntPtr reserved, int flags);
        [DllImport("ole32.dll", ExactSpelling = true)] private static extern void CoUninitialize();
        [UnmanagedFunctionPointer(CallingConvention.StdCall)] private delegate int ReleaseMethod(IntPtr self);

        private static class Delegates<T> where T : class
        {
            internal static readonly Dictionary<IntPtr, T> ByFunction = new Dictionary<IntPtr, T>();
        }

        // The method in a vtable slot of self as a delegate, cached per native function.
        internal static T Method<T>(IntPtr self, int slot) where T : class
        {
            IntPtr function = Marshal.ReadIntPtr(Marshal.ReadIntPtr(self), slot * IntPtr.Size);
            lock (Delegates<T>.ByFunction)
            {
                T method;
                if (!Delegates<T>.ByFunction.TryGetValue(function, out method))
                {
                    method = (T)(object)Marshal.GetDelegateForFunctionPointer(function, typeof(T));
                    Delegates<T>.ByFunction.Add(function, method);
                }
                return method;
            }
        }

        // Releases the reference pointer holds, if any, and clears it.
        internal static void Release(ref IntPtr pointer)
        {
            IntPtr owned = pointer;
            pointer = IntPtr.Zero;
            if (owned != IntPtr.Zero) Method<ReleaseMethod>(owned, SlotRelease)(owned);
        }

        // PROPVARIANT is 16 bytes on x86 and 24 on x64. The value union starts at offset 8 on both.
        internal static IntPtr NewVariant()
        {
            IntPtr variant = Marshal.AllocHGlobal(32);
            for (int i = 0; i < 32; i += 8) Marshal.WriteInt64(variant, i, 0);
            return variant;
        }

        // Joins the multithreaded apartment for one call. A single-threaded caller such as the
        // Unity main thread gets RPC_E_CHANGED_MODE and keeps its apartment, which COM also accepts.
        internal sealed class Apartment : IDisposable
        {
            private bool joined;
            internal Apartment() { joined = CoInitializeEx(IntPtr.Zero, MultiThreaded) >= 0; }
            public void Dispose()
            {
                if (!joined) return;
                joined = false;
                CoUninitialize();
            }
        }
    }
}
