using System;
using System.Collections.Generic;
using System.IO;
using System.Runtime.InteropServices;

namespace SailwindRadio.Audio
{
    // One snapshot of running processes, their parents and program files.
    internal sealed class ProcessTree
    {
        private const int SnapshotProcesses = 0x2, QueryLimited = 0x1000;
        private readonly Dictionary<int, int> parents = new Dictionary<int, int>();
        private readonly Dictionary<int, string> programs = new Dictionary<int, string>();

        [StructLayout(LayoutKind.Sequential, CharSet = CharSet.Unicode)]
        private struct ProcessEntry
        {
            internal int Size, Usage, ProcessId;
            internal IntPtr HeapId;
            internal int ModuleId, Threads, ParentProcessId, BasePriority, Flags;
            [MarshalAs(UnmanagedType.ByValTStr, SizeConst = 260)] internal string ExeFile;
        }

        [DllImport("kernel32.dll", SetLastError = true)] private static extern IntPtr CreateToolhelp32Snapshot(int flags, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32FirstW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] private static extern bool Process32NextW(IntPtr snapshot, ref ProcessEntry entry);
        [DllImport("kernel32.dll")] private static extern bool CloseHandle(IntPtr handle);
        [DllImport("kernel32.dll")] private static extern IntPtr OpenProcess(int access, bool inherit, int processId);
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)]
        private static extern bool QueryFullProcessImageNameW(IntPtr process, int flags, System.Text.StringBuilder name, ref int size);

        internal ProcessTree()
        {
            IntPtr snapshot = CreateToolhelp32Snapshot(SnapshotProcesses, 0);
            if (snapshot == new IntPtr(-1)) throw new IOException("Unable to list running applications");
            try
            {
                var entry = new ProcessEntry { Size = Marshal.SizeOf(typeof(ProcessEntry)) };
                for (bool more = Process32FirstW(snapshot, ref entry); more; more = Process32NextW(snapshot, ref entry))
                    parents[entry.ProcessId] = entry.ParentProcessId;
            }
            finally { CloseHandle(snapshot); }
        }

        internal bool Contains(int processId) => parents.ContainsKey(processId);

        // The program file, or null when the process is gone or belongs to another user.
        internal string Program(int processId)
        {
            string program;
            if (programs.TryGetValue(processId, out program)) return program;
            IntPtr process = OpenProcess(QueryLimited, false, processId);
            if (process != IntPtr.Zero)
                try
                {
                    var name = new System.Text.StringBuilder(1024);
                    int size = name.Capacity;
                    if (QueryFullProcessImageNameW(process, 0, name, ref size)) program = name.ToString();
                }
                finally { CloseHandle(process); }
            programs[processId] = program;
            return program;
        }

        // The highest ancestor running the same program, such as the browser window process above its audio process.
        internal int Root(int processId)
        {
            string program = Program(processId);
            for (int steps = 0; steps < 64 && program != null; steps++)
            {
                int parent;
                if (!parents.TryGetValue(processId, out parent) || parent == processId ||
                    !String.Equals(Program(parent), program, StringComparison.OrdinalIgnoreCase)) break;
                processId = parent;
            }
            return processId;
        }

        internal bool Within(int processId, int root)
        {
            for (int steps = 0; steps < 64; steps++)
            {
                if (processId == root) return true;
                int parent;
                if (!parents.TryGetValue(processId, out parent) || parent == processId) return false;
                processId = parent;
            }
            return false;
        }

        // The top process currently running program, or 0.
        internal int Find(string program)
        {
            foreach (int processId in parents.Keys)
                if (String.Equals(Program(processId), program, StringComparison.OrdinalIgnoreCase)) return Root(processId);
            return 0;
        }
    }
}
