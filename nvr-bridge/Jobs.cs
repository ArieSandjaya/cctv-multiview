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

    /// <summary>Satu NvrClient per NVR terdaftar; kamera dicari lewat nomor tampil.</summary>
    sealed class NvrPool : IDisposable
    {
        readonly AppConfig cfg;
        readonly Dictionary<string, NvrClient> clients = new Dictionary<string, NvrClient>();

        public NvrPool(AppConfig cfg)
        {
            this.cfg = cfg;
            foreach (var n in cfg.Nvrs) clients[n.Id] = new NvrClient(cfg.ForNvr(n));
        }

        public bool Any { get { return clients.Count > 0; } }
        public NvrClient Get(string id) { NvrClient c; return id != null && clients.TryGetValue(id, out c) ? c : null; }
        public Camera CamByNum(int num) { return cfg.Cameras.FirstOrDefault(c => c.Id == num); }
        public NvrClient ForCam(int num) { var c = CamByNum(num); return c == null ? null : Get(c.NvrId); }
        public string FirstId { get { return cfg.Nvrs.Count > 0 ? cfg.Nvrs[0].Id : ""; } }
        public string NameOf(string id) { var n = cfg.Nvrs.FirstOrDefault(x => x.Id == id); return n != null ? n.Name : ""; }

        public void Dispose() { foreach (var c in clients.Values) c.Dispose(); }
    }

    /// <summary>Antrean unduhan klip; jumlah unduhan bersamaan dibatasi agar NVR tidak kewalahan.</summary>
    sealed class JobManager : IDisposable
    {
        readonly AppConfig cfg;
        readonly NvrPool pool;
        readonly SemaphoreSlim gate;
        readonly Dictionary<string, ClipJob> jobs = new Dictionary<string, ClipJob>();
        readonly object jobsLock = new object();
        readonly Timer cleaner;

        public JobManager(AppConfig cfg, NvrPool pool)
        {
            this.cfg = cfg;
            this.pool = pool;
            gate = new SemaphoreSlim(cfg.MaxConcurrentDownloads, cfg.MaxConcurrentDownloads);
            cleaner = new Timer(_ => Cleanup(), null, TimeSpan.FromMinutes(1), TimeSpan.FromMinutes(1));
        }

        public ClipJob Start(int cam, DateTime startUtc, int minutes)
        {
            var camera = pool.CamByNum(cam);
            if (camera == null) throw new ArgumentException("Kamera tidak dikenal.");
            if (pool.Get(camera.NvrId) == null) throw new ArgumentException("Kamera ini tidak terhubung ke NVR.");
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

                    var camera = pool.CamByNum(job.Cam);
                    var result = await pool.Get(camera.NvrId).DownloadAsync(camera.Channel, s, e, cfg.ClipKind, cfg.ClipDir, job.Id,
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
