using System;
using System.Runtime.InteropServices;

namespace CctvViewer
{
    static class Native
    {
        // ---------- pesan ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        public static void Message(string text, string caption, bool error)
        {
            try { MessageBoxW(IntPtr.Zero, text, caption, error ? 0x10u : 0x40u); } catch { }
        }

        // ---------- job object: semua proses anak ikut mati jika CctvViewer berhenti ----------
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_BASIC_LIMIT_INFORMATION
        {
            public long PerProcessUserTimeLimit, PerJobUserTimeLimit;
            public uint LimitFlags;
            public UIntPtr MinimumWorkingSetSize, MaximumWorkingSetSize;
            public uint ActiveProcessLimit;
            public UIntPtr Affinity;
            public uint PriorityClass, SchedulingClass;
        }
        [StructLayout(LayoutKind.Sequential)]
        struct IO_COUNTERS { public ulong a, b, c, d, e, f; }
        [StructLayout(LayoutKind.Sequential)]
        struct JOBOBJECT_EXTENDED_LIMIT_INFORMATION
        {
            public JOBOBJECT_BASIC_LIMIT_INFORMATION Basic;
            public IO_COUNTERS Io;
            public UIntPtr ProcessMemoryLimit, JobMemoryLimit, PeakProcessMemoryUsed, PeakJobMemoryUsed;
        }
        [DllImport("kernel32.dll", CharSet = CharSet.Unicode)] static extern IntPtr CreateJobObjectW(IntPtr attrs, string name);
        [DllImport("kernel32.dll")] static extern bool SetInformationJobObject(IntPtr job, int infoClass, IntPtr info, uint size);
        [DllImport("kernel32.dll")] static extern bool AssignProcessToJobObject(IntPtr job, IntPtr process);
        [DllImport("kernel32.dll")] static extern bool CloseHandle(IntPtr h);

        public static IntPtr CreateKillOnCloseJob()
        {
            IntPtr job = CreateJobObjectW(IntPtr.Zero, null);
            if (job == IntPtr.Zero) return IntPtr.Zero;
            var info = new JOBOBJECT_EXTENDED_LIMIT_INFORMATION();
            info.Basic.LimitFlags = 0x2000;                                  // JOB_OBJECT_LIMIT_KILL_ON_JOB_CLOSE
            int size = Marshal.SizeOf(typeof(JOBOBJECT_EXTENDED_LIMIT_INFORMATION));
            IntPtr mem = Marshal.AllocHGlobal(size);
            try
            {
                Marshal.StructureToPtr(info, mem, false);
                if (!SetInformationJobObject(job, 9, mem, (uint)size)) { CloseHandle(job); return IntPtr.Zero; }   // 9 = ExtendedLimitInformation
            }
            finally { Marshal.FreeHGlobal(mem); }
            return job;
        }
        public static bool AddToJob(IntPtr job, System.Diagnostics.Process p)
        {
            if (job == IntPtr.Zero) return false;
            try { return AssignProcessToJobObject(job, p.Handle); } catch { return false; }
        }
        public static void CloseJob(IntPtr job) { if (job != IntPtr.Zero) CloseHandle(job); }
    }
}
