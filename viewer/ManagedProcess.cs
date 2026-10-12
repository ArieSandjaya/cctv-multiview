using System;
using System.Diagnostics;
using System.IO;

namespace CctvViewer
{
    /// <summary>Satu proses yang dijaga: dijalankan, dipantau, dan dijalankan ulang dengan jeda yang makin lama jika terus gagal.</summary>
    sealed class ManagedProcess
    {
        readonly string name, exe, args, workDir;
        readonly FileLog output;           // null = buang keluaran
        readonly IntPtr job;
        Process proc;
        DateTime started = DateTime.MinValue, nextTry = DateTime.MinValue;
        int failures;

        public ManagedProcess(string name, string exe, string args, string workDir, FileLog output, IntPtr job)
        { this.name = name; this.exe = exe; this.args = args; this.workDir = workDir; this.output = output; this.job = job; }

        public string Name { get { return name; } }
        public bool Running { get { try { return proc != null && !proc.HasExited; } catch { return false; } } }
        public int Pid { get { try { return proc != null ? proc.Id : 0; } catch { return 0; } } }

        /// <summary>Dipanggil berkala. Menjalankan proses jika belum/ tidak lagi berjalan dan sudah waktunya.</summary>
        public void Ensure()
        {
            if (Running) return;
            if (proc != null)
            {
                int code = 0; try { code = proc.ExitCode; } catch { }
                double alive = (DateTime.UtcNow - started).TotalSeconds;
                failures = alive > 120 ? 1 : failures + 1;
                int wait = Math.Min(60, 2 * failures * failures);
                nextTry = DateTime.UtcNow.AddSeconds(wait);
                Log.Warn(name + " berhenti (kode " + code + ", hidup " + (int)alive + " dtk). Dijalankan ulang dalam " + wait + " dtk.");
                try { proc.Dispose(); } catch { }
                proc = null;
            }
            if (DateTime.UtcNow < nextTry) return;
            Start();
        }

        void Start()
        {
            try
            {
                var psi = new ProcessStartInfo(exe, args)
                {
                    WorkingDirectory = workDir,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = output != null,
                    RedirectStandardError = output != null,
                };
                var p = new Process { StartInfo = psi };
                if (output != null)
                {
                    p.OutputDataReceived += (s, e) => { if (e.Data != null) output.Write(e.Data); };
                    p.ErrorDataReceived += (s, e) => { if (e.Data != null) output.Write(e.Data); };
                }
                p.Start();
                if (output != null) { p.BeginOutputReadLine(); p.BeginErrorReadLine(); }
                if (!Native.AddToJob(job, p)) Log.Warn(name + ": tidak bisa dimasukkan ke job object (proses mungkin tertinggal jika aplikasi dihentikan paksa).");
                proc = p;
                started = DateTime.UtcNow;
                Log.Info(name + " dimulai (pid " + p.Id + ").");
            }
            catch (Exception e)
            {
                failures++;
                nextTry = DateTime.UtcNow.AddSeconds(Math.Min(60, 5 * failures));
                Log.Error(name + " gagal dimulai: " + e.Message);
            }
        }

        /// <summary>Menghentikan proses; Ensure() menjalankannya lagi pada putaran berikutnya.</summary>
        public void Restart()
        {
            Stop();
            failures = 0; nextTry = DateTime.MinValue;
        }

        public void Stop()
        {
            try { if (Running) { proc.Kill(); proc.WaitForExit(3000); } } catch { }
        }
    }
}
