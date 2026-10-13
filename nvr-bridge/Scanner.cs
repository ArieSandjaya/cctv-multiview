using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NvrBridge
{
    sealed class ScanResult
    {
        public int Cam;
        public string State;       // rec | none | error
        public string Message = "";
    }

    sealed class ScanJob
    {
        public string Id;
        public volatile string State = "running";     // running | done | failed | cancelled
        public string Message = "";
        public int Total, Done;
        public string Window = "";
        public string TimeMode = "";
        public readonly List<ScanResult> Results = new List<ScanResult>();
        public readonly CancellationTokenSource Cts = new CancellationTokenSource();
    }

    /// <summary>
    /// Menguji nomor kamera 1..N di NVR satu per satu: login, lalu kirim permintaan httpdl per CAM dan hanya membaca header jawabannya (tidak ada rekaman yang diunduh).
    /// Dokumen CGI NVR tidak menyediakan daftar kamera, jadi jawaban httpdl yang normal (bukan penolakan atau "tanpa rekaman") adalah tanda terbaik bahwa nomor itu terdaftar.
    /// </summary>
    sealed class Scanner : IDisposable
    {
        static readonly System.Globalization.CultureInfo CI = System.Globalization.CultureInfo.InvariantCulture;
        const int PauseMs = 3000;                 // jeda antar perintah ke NVR (dokumen: sekitar 3 detik)
        readonly AppConfig cfg;
        readonly object gate = new object();
        readonly Dictionary<string, ScanJob> jobs = new Dictionary<string, ScanJob>();

        public Scanner(AppConfig cfg) { this.cfg = cfg; }

        public ScanJob Get(string id) { lock (gate) { ScanJob j; return jobs.TryGetValue(id, out j) ? j : null; } }

        public ScanJob Start(RegNvr nvrSettings, int maxCam, DateTime? probeLocal)
        {
            lock (gate)
            {
                if (jobs.Values.Any(j => j.State == "running")) throw new InvalidOperationException("Pemindaian lain masih berjalan.");
                var job = new ScanJob { Id = Guid.NewGuid().ToString("N"), Total = maxCam };
                jobs[job.Id] = job;
                var tc = new AppConfig
                {
                    NvrBase = new Uri(nvrSettings.Url + "/"), NvrUser = nvrSettings.User, NvrPassword = nvrSettings.Pass,
                    NvrAuthScheme = nvrSettings.Scheme, NvrAllowSelfSigned = nvrSettings.SelfSigned,
                };
                Task.Run(() => Run(job, tc, maxCam, probeLocal));
                return job;
            }
        }

        public void Cancel(string id) { var j = Get(id); if (j != null) j.Cts.Cancel(); }

        async Task Run(ScanJob job, AppConfig tc, int maxCam, DateTime? probeLocal)
        {
            string dir = null;
            var ct = job.Cts.Token;
            try
            {
                using (var nvr = new NvrClient(tc))
                {
                    await nvr.GetUidAsync(ct).ConfigureAwait(false);           // gagal login langsung terlihat
                    DateTime localStart;
                    if (probeLocal.HasValue) localStart = probeLocal.Value;
                    else
                    {
                        var r = await nvr.GetRecordedRangeAsync(ct).ConfigureAwait(false);
                        if (!r.Latest.HasValue) throw new NvrException("NVR tidak melaporkan rentang rekaman (HDD kosong?). Isi waktu uji secara manual.");
                        localStart = r.Latest.Value.AddMinutes(-10);
                    }
                    localStart = new DateTime(localStart.Year, localStart.Month, localStart.Day, localStart.Hour, localStart.Minute, 0);

                    // Kalibrasi mode waktu: coba CAM 1-4 dengan jam NVR apa adanya ("Local"), lalu bergeser ke UTC jika tidak ada hasil.
                    // Pergeseran memakai zona waktu PC ini, dengan asumsi sama dengan zona NVR.
                    DateTime utcStart = localStart - TimeZoneInfo.Local.GetUtcOffset(localStart);
                    DateTime start = localStart; job.TimeMode = "Local";
                    var done = new Dictionary<int, ScanResult>();
                    for (int cam = 1; cam <= Math.Min(4, maxCam); cam++)
                    {
                        ct.ThrowIfCancellationRequested();
                        var res = await Probe(nvr, cam, start, dir, ct).ConfigureAwait(false);
                        done[cam] = res; Report(job, res);
                        if (res.State == "rec") break;
                        await Task.Delay(PauseMs, ct).ConfigureAwait(false);
                    }
                    if (!done.Values.Any(x => x.State == "rec") && utcStart != localStart)
                    {
                        for (int cam = 1; cam <= Math.Min(4, maxCam); cam++)
                        {
                            ct.ThrowIfCancellationRequested();
                            var res = await Probe(nvr, cam, utcStart, dir, ct).ConfigureAwait(false);
                            if (res.State == "rec") { start = utcStart; job.TimeMode = "Utc"; done[cam] = res; Replace(job, res); break; }
                            await Task.Delay(PauseMs, ct).ConfigureAwait(false);
                        }
                    }
                    job.Window = start.ToString("yyyy-MM-dd HH:mm", CI) + " (jam " + job.TimeMode + ")";

                    for (int cam = 1; cam <= maxCam; cam++)
                    {
                        ct.ThrowIfCancellationRequested();
                        if (!done.ContainsKey(cam))
                        {
                            await Task.Delay(PauseMs, ct).ConfigureAwait(false);
                            var res = await Probe(nvr, cam, start, dir, ct).ConfigureAwait(false);
                            Report(job, res);
                        }
                        job.Done = cam;
                    }
                }
                int found = job.Results.Count(x => x.State == "rec");
                job.Message = found > 0 ? found + " kamera menjawab permintaan httpdl."
                                        : "Tidak ada CAM yang menjawab dengan normal. Cek pesan per CAM (jawaban asli NVR), coba waktu uji lain, atau tambah kamera manual.";
                job.State = "done";
            }
            catch (OperationCanceledException) { job.State = "cancelled"; job.Message = "Dibatalkan."; }
            catch (NvrException e) { job.State = "failed"; job.Message = e.Message; }
            catch (Exception e) { job.State = "failed"; job.Message = "Kesalahan: " + e.Message; Log.Warn("Pemindaian gagal: " + e); }
        }

        static void Report(ScanJob job, ScanResult r) { lock (job.Results) job.Results.Add(r); }
        static void Replace(ScanJob job, ScanResult r) { lock (job.Results) { job.Results.RemoveAll(x => x.Cam == r.Cam); job.Results.Add(r); } }

        async Task<ScanResult> Probe(NvrClient nvr, int cam, DateTime nvrTime, string dir, CancellationToken ct)
        {
            var r = await nvr.ProbeCamAsync(cam, nvrTime, nvrTime.AddMinutes(1), ct).ConfigureAwait(false);
            return new ScanResult { Cam = cam, State = r.Item1, Message = r.Item2 };
        }

        public string ToJson(ScanJob j)
        {
            ScanResult[] rs; lock (j.Results) rs = j.Results.OrderBy(x => x.Cam).ToArray();
            var sb = new StringBuilder();
            sb.Append("{\"id\":").Append(Json.Q(j.Id)).Append(",\"state\":").Append(Json.Q(j.State)).Append(",\"message\":").Append(Json.Q(j.Message))
              .Append(",\"total\":").Append(j.Total).Append(",\"done\":").Append(j.Done).Append(",\"window\":").Append(Json.Q(j.Window))
              .Append(",\"results\":[");
            sb.Append(string.Join(",", rs.Select(x => "{\"cam\":" + x.Cam + ",\"state\":" + Json.Q(x.State) + ",\"message\":" + Json.Q(x.Message) + "}")));
            return sb.Append("]}").ToString();
        }

        public void Dispose() { lock (gate) foreach (var j in jobs.Values) j.Cts.Cancel(); }
    }
}
