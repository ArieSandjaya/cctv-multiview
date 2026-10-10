using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;

namespace NvrBridge
{
    sealed class ClipJob
    {
        public string Id;
        public int Cam;
        public DateTime StartUtc;
        public int Minutes;
        public string State = "queued";   // queued | running | done | error
        public string Message = "";
        public long Bytes;
        public List<string> Files = new List<string>();
        public DateTime Created = DateTime.UtcNow;
    }

    /// <summary>Antrean unduhan klip; jumlah unduhan bersamaan dibatasi agar NVR tidak kewalahan.</summary>
    sealed class JobManager : IDisposable
    {
        readonly AppConfig cfg;
        readonly NvrClient nvr;
        readonly SemaphoreSlim gate;
        readonly Dictionary<string, ClipJob> jobs = new Dictionary<string, ClipJob>();
        readonly object jobsLock = new object();
        readonly Timer cleaner;

        public JobManager(AppConfig cfg, NvrClient nvr)
        {
            this.cfg = cfg;
            this.nvr = nvr;
            gate = new SemaphoreSlim(cfg.MaxConcurrentDownloads, cfg.MaxConcurrentDownloads);
            cleaner = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public ClipJob Start(int cam, DateTime startUtc, int minutes)
        {
            if (!cfg.Cameras.Any(c => c.Id == cam)) throw new ArgumentException("Kamera tidak dikenal.");
            if (minutes < 1 || minutes > cfg.MaxClipMinutes)
                throw new ArgumentException("Durasi harus 1-" + cfg.MaxClipMinutes + " menit.");
            if (startUtc > DateTime.UtcNow.AddMinutes(5)) throw new ArgumentException("Waktu mulai berada di masa depan.");

            var job = new ClipJob { Id = Guid.NewGuid().ToString("N"), Cam = cam, StartUtc = startUtc, Minutes = minutes };
            lock (jobsLock)
            {
                int pending = jobs.Values.Count(j => j.State == "queued" || j.State == "running");
                if (pending >= 8) throw new InvalidOperationException("Terlalu banyak unduhan sedang berjalan. Coba lagi sebentar.");
                jobs[job.Id] = job;
            }
            Task.Run(() => RunAsync(job));
            return job;
        }

        public ClipJob Get(string id)
        {
            lock (jobsLock)
            {
                ClipJob j;
                return jobs.TryGetValue(id, out j) ? j : null;
            }
        }

        async Task RunAsync(ClipJob job)
        {
            using (var cts = new CancellationTokenSource(TimeSpan.FromMinutes(cfg.ClipTimeoutMinutes)))
            {
                await gate.WaitAsync().ConfigureAwait(false);
                try
                {
                    job.State = "running";

                    // Ke zona waktu NVR; menit dibulatkan ke bawah karena detik dikunci 00.
                    DateTime s = job.StartUtc;
                    if (!cfg.NvrTimeUtc) s = TimeZoneInfo.ConvertTimeFromUtc(DateTime.SpecifyKind(s, DateTimeKind.Utc), TimeZoneInfo.Local);
                    s = new DateTime(s.Year, s.Month, s.Day, s.Hour, s.Minute, 0);
                    DateTime e = s.AddMinutes(job.Minutes);

                    Log.Info("Unduh klip kamera " + job.Cam + ", " + s.ToString("yyyy-MM-dd HH:mm") + " (" +
                             (cfg.NvrTimeUtc ? "UTC" : "lokal") + "), " + job.Minutes + " menit");

                    var result = await nvr.DownloadAsync(job.Cam, s, e, cfg.ClipKind, cfg.ClipDir, job.Id,
                        n => Interlocked.Add(ref job.Bytes, n), cts.Token).ConfigureAwait(false);

                    job.Files = result.Files.Select(Path.GetFileName).ToList();
                    job.State = "done";
                    Log.Info("Klip selesai: " + job.Files.Count + " file, " + (Interlocked.Read(ref job.Bytes) / 1024) + " KB");
                }
                catch (OperationCanceledException)
                {
                    job.Message = "Unduhan melewati batas waktu (" + cfg.ClipTimeoutMinutes + " menit) dan dibatalkan.";
                    job.State = "error";
                    Log.Warn(job.Message);
                }
                catch (NvrException ex)
                {
                    job.Message = ex.Message;
                    job.State = "error";
                    Log.Warn("Klip gagal: " + ex.Message);
                }
                catch (Exception ex)
                {
                    job.Message = "Kesalahan tak terduga: " + ex.Message;
                    job.State = "error";
                    Log.Warn("Klip gagal: " + ex);
                }
                finally { gate.Release(); }
            }
        }

        public void Cleanup()
        {
            try
            {
                var limit = DateTime.UtcNow.AddMinutes(-cfg.ClipRetentionMinutes);
                List<ClipJob> old;
                lock (jobsLock)
                {
                    old = jobs.Values.Where(j => j.Created < limit && (j.State == "done" || j.State == "error")).ToList();
                    foreach (var j in old) jobs.Remove(j.Id);
                }
                foreach (var j in old)
                    foreach (var f in j.Files)
                        try { File.Delete(Path.Combine(cfg.ClipDir, f)); } catch { }
            }
            catch (Exception e) { Log.Debug("cleanup: " + e.Message); }
        }

        public void Dispose() { cleaner.Dispose(); }
    }
}
