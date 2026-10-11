using System;
using System.Collections.Generic;
using System.Runtime.InteropServices;
using System.Text;

namespace CctvViewer
{
    struct Area
    {
        public int X, Y, W, H;
        public Area(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public override string ToString() { return X + "," + Y + " " + W + "x" + H; }
    }

    static class Native
    {
        // ---------- pesan ----------
        [DllImport("user32.dll", CharSet = CharSet.Unicode)]
        static extern int MessageBoxW(IntPtr hWnd, string text, string caption, uint type);
        public static void Message(string text, string caption, bool error)
        {
            try { MessageBoxW(IntPtr.Zero, text, caption, error ? 0x10u : 0x40u); } catch { }
        }

        // ---------- monitor ----------
        delegate bool MonitorEnumProc(IntPtr hMonitor, IntPtr hdc, IntPtr rect, IntPtr data);
        [StructLayout(LayoutKind.Sequential)] struct RECT { public int Left, Top, Right, Bottom; }
        [StructLayout(LayoutKind.Sequential)]
        struct MONITORINFO { public int cbSize; public RECT rcMonitor; public RECT rcWork; public uint dwFlags; }
        [DllImport("user32.dll")] static extern bool EnumDisplayMonitors(IntPtr hdc, IntPtr clip, MonitorEnumProc proc, IntPtr data);
        [DllImport("user32.dll")] static extern bool GetMonitorInfo(IntPtr hMonitor, ref MONITORINFO info);

        /// <summary>Semua monitor aktif, diurut kiri ke kanan lalu atas ke bawah. Ukuran dalam piksel fisik (aplikasi sadar-DPI).</summary>
        public static List<Area> Monitors()
        {
            var list = new List<Area>();
            MonitorEnumProc cb = (h, dc, r, d) =>
            {
                var mi = new MONITORINFO { cbSize = Marshal.SizeOf(typeof(MONITORINFO)) };
                if (GetMonitorInfo(h, ref mi))
                    list.Add(new Area(mi.rcMonitor.Left, mi.rcMonitor.Top, mi.rcMonitor.Right - mi.rcMonitor.Left, mi.rcMonitor.Bottom - mi.rcMonitor.Top));
                return true;
            };
            EnumDisplayMonitors(IntPtr.Zero, IntPtr.Zero, cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            list.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            return list;
        }

        // ---------- jendela ----------
        delegate bool WndEnumProc(IntPtr hWnd, IntPtr data);
        [DllImport("user32.dll")] static extern bool EnumWindows(WndEnumProc proc, IntPtr data);
        [DllImport("user32.dll")] static extern uint GetWindowThreadProcessId(IntPtr hWnd, out uint pid);
        [DllImport("user32.dll")] static extern bool IsWindowVisible(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool IsIconic(IntPtr hWnd);
        [DllImport("user32.dll")] static extern bool GetWindowRect(IntPtr hWnd, out RECT r);
        [DllImport("user32.dll")] static extern bool ShowWindow(IntPtr hWnd, int cmd);
        [DllImport("user32.dll")] static extern bool SetWindowPos(IntPtr hWnd, IntPtr after, int x, int y, int cx, int cy, uint flags);
        [DllImport("user32.dll", CharSet = CharSet.Unicode)] static extern int GetClassNameW(IntPtr hWnd, StringBuilder sb, int max);

        /// <summary>Jendela utama browser milik proses pid (Chrome/Edge: kelas Chrome_WidgetWin_1), atau IntPtr.Zero.</summary>
        public static IntPtr FindBrowserWindow(int pid)
        {
            IntPtr found = IntPtr.Zero; int bestArea = 0;
            WndEnumProc cb = (h, d) =>
            {
                uint p; GetWindowThreadProcessId(h, out p);
                if (p != (uint)pid || !IsWindowVisible(h)) return true;
                var sb = new StringBuilder(64); GetClassNameW(h, sb, 64);
                if (sb.ToString() != "Chrome_WidgetWin_1") return true;
                RECT r; if (!GetWindowRect(h, out r)) return true;
                int a = (r.Right - r.Left) * (r.Bottom - r.Top);
                if (a > bestArea) { bestArea = a; found = h; }
                return true;
            };
            EnumWindows(cb, IntPtr.Zero);
            GC.KeepAlive(cb);
            return bestArea > 200 * 200 ? found : IntPtr.Zero;
        }

        public static bool GetRect(IntPtr h, out Area a)
        {
            RECT r; a = new Area();
            if (!GetWindowRect(h, out r)) return false;
            a = new Area(r.Left, r.Top, r.Right - r.Left, r.Bottom - r.Top);
            return true;
        }

        /// <summary>Menempatkan jendela menutupi monitor. Mengembalikan true jika perlu dipindah.</summary>
        public static bool Place(IntPtr h, Area target, int tolerance, out Area before)
        {
            before = new Area();
            if (IsIconic(h)) ShowWindow(h, 9);                               // SW_RESTORE
            if (!GetRect(h, out before)) return false;
            if (Math.Abs(before.X - target.X) <= tolerance && Math.Abs(before.Y - target.Y) <= tolerance &&
                Math.Abs(before.W - target.W) <= tolerance && Math.Abs(before.H - target.H) <= tolerance) return false;
            // SWP_NOACTIVATE 0x10 | SWP_SHOWWINDOW 0x40 | SWP_NOZORDER 0x04
            SetWindowPos(h, IntPtr.Zero, target.X, target.Y, target.W, target.H, 0x10 | 0x40 | 0x04);
            return true;
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
