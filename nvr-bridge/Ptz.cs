using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net;
using System.Net.Http;
using System.Threading;
using System.Threading.Tasks;

namespace NvrBridge
{
    sealed class PtzException : Exception
    {
        public PtzException(string m) : base(m) { }
    }

    /// <summary>
    /// Kontrol PTZ: Dahua (OEM) lewat /cgi-bin/ptz.cgi, ONVIF (disarankan untuk i-PRO/Panasonic) lewat SOAP,
    /// dan "ipro" lewat /cgi-bin/aw_ptz (protokol kamera seri AW; belum terverifikasi untuk kamera pengawas i-PRO).
    /// Hanya kamera yang tercantum di PtzCameras yang bisa dituju, dan hanya perintah dalam daftar putih.
    /// Gerakan berhenti otomatis setelah PtzMaxMoveSeconds jika tombol stop tidak pernah sampai.
    /// </summary>
    sealed class PtzClient : IDisposable
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        // Nama perintah dari halaman -> kode Dahua
        static readonly Dictionary<string, string> Codes = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            { "up", "Up" }, { "down", "Down" }, { "left", "Left" }, { "right", "Right" },
            { "upleft", "LeftUp" }, { "upright", "RightUp" }, { "downleft", "LeftDown" }, { "downright", "RightDown" },
            { "zoomin", "ZoomTele" }, { "zoomout", "ZoomWide" },
            { "focusnear", "FocusNear" }, { "focusfar", "FocusFar" }
        };

        readonly AppConfig cfg;
        readonly HttpClient http;
        readonly object gate = new object();
        readonly Dictionary<string, Timer> autoStop = new Dictionary<string, Timer>();
        readonly Dictionary<string, string> active = new Dictionary<string, string>();

        readonly OnvifPtz onvif;

        public PtzClient(AppConfig cfg)
        {
            this.cfg = cfg;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, PreAuthenticate = false };
            var cache = new CredentialCache();
            var added = new HashSet<string>(StringComparer.OrdinalIgnoreCase);   // satu host bisa dipakai beberapa nama stream
            foreach (var kv in cfg.PtzCameras)
            {
                var cred = new NetworkCredential(cfg.PtzUser, cfg.PtzPassword);
                var u = new Uri("http://" + kv.Value.Host + "/");
                if (added.Add("Digest|" + u)) cache.Add(u, "Digest", cred);
                // Kamera i-PRO/Panasonic ada yang hanya menawarkan Basic; Dahua hanya Digest.
                if (kv.Value.Type == "ipro" && added.Add("Basic|" + u)) cache.Add(u, "Basic", cred);
            }
            handler.Credentials = cache;
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NvrBridge/1.1");
            onvif = new OnvifPtz(http, cfg.PtzUser, cfg.PtzPassword);
        }

        public bool Enabled { get { return cfg.PtzCameras.Count > 0; } }

        public IEnumerable<string> CameraIds { get { return cfg.PtzCameras.Keys; } }

        public string TypeOf(string cam) { return cfg.PtzCameras[cam].Type; }

        public bool Has(string cam) { return cam != null && cfg.PtzCameras.ContainsKey(cam); }

        static bool IsMove(string dir) { return dir != null && Codes.ContainsKey(dir); }

        // ---------- penyusun perintah per jenis kamera ----------

        string StartQuery(PtzTarget t, string dir, int speed)
        {
            if (t.Type == "ipro")
            {
                // Protokol AW: 01-99 dengan 50 = berhenti. Kiri/bawah/zoom-out/fokus-dekat di bawah 50.
                int k = speed * 6, hi = 50 + k, lo = 50 - k;
                int pan = 50, tilt = 50;
                if (dir.EndsWith("left")) pan = lo; else if (dir.EndsWith("right")) pan = hi;
                if (dir.StartsWith("up")) tilt = hi; else if (dir.StartsWith("down")) tilt = lo;
                if (dir == "zoomin") return "/cgi-bin/aw_ptz?cmd=%23Z" + hi.ToString(CI) + "&res=1";
                if (dir == "zoomout") return "/cgi-bin/aw_ptz?cmd=%23Z" + lo.ToString(CI) + "&res=1";
                if (dir == "focusfar") return "/cgi-bin/aw_ptz?cmd=%23F" + hi.ToString(CI) + "&res=1";
                if (dir == "focusnear") return "/cgi-bin/aw_ptz?cmd=%23F" + lo.ToString(CI) + "&res=1";
                return "/cgi-bin/aw_ptz?cmd=%23PTS" + pan.ToString("00", CI) + tilt.ToString("00", CI) + "&res=1";
            }
            // Dahua
            bool zf = dir.StartsWith("zoom") || dir.StartsWith("focus");
            bool diag = dir == "upleft" || dir == "upright" || dir == "downleft" || dir == "downright";
            string a1 = diag ? speed.ToString(CI) : "0";
            string a2 = zf ? "0" : speed.ToString(CI);
            return DahuaQuery("start", Codes[dir], a1, a2, "0");
        }

        string StopQuery(PtzTarget t, string dir)
        {
            if (t.Type == "ipro")
            {
                if (dir.StartsWith("zoom")) return "/cgi-bin/aw_ptz?cmd=%23Z50&res=1";
                if (dir.StartsWith("focus")) return "/cgi-bin/aw_ptz?cmd=%23F50&res=1";
                return "/cgi-bin/aw_ptz?cmd=%23PTS5050&res=1";
            }
            return DahuaQuery("stop", Codes[dir], "0", "0", "0");
        }

        string PresetQuery(PtzTarget t, int n)
        {
            if (t.Type == "ipro") return "/cgi-bin/aw_ptz?cmd=%23R" + (n - 1).ToString("00", CI) + "&res=1";
            return DahuaQuery("start", "GotoPreset", "0", n.ToString(CI), "0");
        }

        string DahuaQuery(string action, string code, string a1, string a2, string a3)
        {
            return "/cgi-bin/ptz.cgi?action=" + action + "&channel=" + cfg.PtzChannel.ToString(CI) +
                   "&code=" + code + "&arg1=" + a1 + "&arg2=" + a2 + "&arg3=" + a3;
        }

        // ---------- perintah ----------

        public async Task StartAsync(string cam, string dir, int speed)
        {
            if (!Has(cam)) throw new ArgumentException("Kamera PTZ tidak dikenal.");
            if (!IsMove(dir)) throw new ArgumentException("Arah PTZ tidak dikenal.");
            if (speed < 1 || speed > 8) throw new ArgumentException("speed harus 1-8.");
            var target = cfg.PtzCameras[cam];
            bool viaOnvif = target.Type == "onvif";
            if (viaOnvif && dir.StartsWith("focus")) throw new ArgumentException("Fokus tidak didukung untuk kamera ONVIF.");

            // Hentikan gerakan sebelumnya pada kamera ini bila arahnya berbeda
            string prev;
            lock (gate) { active.TryGetValue(cam, out prev); }
            if (prev != null && prev != dir) await StopCoreAsync(cam, target, prev).ConfigureAwait(false);

            if (viaOnvif) await onvif.MoveAsync(cam, target.Host, dir, speed).ConfigureAwait(false);
            else await SendAsync(cam, StartQuery(target, dir, speed)).ConfigureAwait(false);
            lock (gate)
            {
                active[cam] = dir;
                Timer t;
                if (autoStop.TryGetValue(cam, out t)) t.Dispose();
                autoStop[cam] = new Timer(_ => { var ignored = AutoStopAsync(cam, dir); }, null,
                    TimeSpan.FromSeconds(cfg.PtzMaxMoveSeconds), Timeout.InfiniteTimeSpan);
            }
        }

        public async Task StopAsync(string cam, string dir)
        {
            if (!Has(cam)) throw new ArgumentException("Kamera PTZ tidak dikenal.");
            if (dir != null && !IsMove(dir)) dir = null;
            lock (gate)
            {
                if (dir == null) active.TryGetValue(cam, out dir);
                active.Remove(cam);
                Timer t;
                if (autoStop.TryGetValue(cam, out t)) { t.Dispose(); autoStop.Remove(cam); }
            }
            if (dir == null) return;      // tidak ada gerakan yang berjalan
            await StopCoreAsync(cam, cfg.PtzCameras[cam], dir).ConfigureAwait(false);
        }

        Task StopCoreAsync(string cam, PtzTarget t, string dir)
        {
            if (t.Type == "onvif") return onvif.StopAsync(cam, t.Host);
            return SendAsync(cam, StopQuery(t, dir));
        }

        public Task PresetAsync(string cam, int n)
        {
            if (!Has(cam)) throw new ArgumentException("Kamera PTZ tidak dikenal.");
            var target = cfg.PtzCameras[cam];
            int max = target.Type == "ipro" ? 100 : 255;
            if (n < 1 || n > max) throw new ArgumentException("Nomor preset harus 1-" + max.ToString(CI) + " untuk kamera ini.");
            if (target.Type == "onvif") return onvif.PresetAsync(cam, target.Host, n);
            return SendAsync(cam, PresetQuery(target, n));
        }

        async Task AutoStopAsync(string cam, string dir)
        {
            lock (gate)
            {
                string cur;
                if (!active.TryGetValue(cam, out cur) || cur != dir) return;   // sudah diganti/dihentikan
                active.Remove(cam);
                Timer t;
                if (autoStop.TryGetValue(cam, out t)) { t.Dispose(); autoStop.Remove(cam); }
            }
            Log.Warn("PTZ " + cam + ": tombol stop tidak diterima, berhenti otomatis.");
            try { await StopCoreAsync(cam, cfg.PtzCameras[cam], dir).ConfigureAwait(false); } catch (PtzException) { }
        }

        async Task SendAsync(string cam, string pq)
        {
            var uri = new Uri("http://" + cfg.PtzCameras[cam].Host + pq);
            Log.Debug("PTZ " + cam + " " + pq);
            try
            {
                using (var resp = await http.GetAsync(uri).ConfigureAwait(false))
                {
                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new PtzException("Kamera menolak login PTZ (periksa PtzUser/PtzPassword).");
                    if (resp.StatusCode != HttpStatusCode.OK)
                        throw new PtzException("Kamera menjawab HTTP " + (int)resp.StatusCode + " untuk perintah PTZ.");
                }
            }
            catch (HttpRequestException e) { throw new PtzException("Tidak bisa menghubungi kamera: " + e.Message); }
            catch (TaskCanceledException) { throw new PtzException("Kamera tidak menjawab (timeout)."); }
        }

        public void Dispose()
        {
            lock (gate) { foreach (var t in autoStop.Values) t.Dispose(); autoStop.Clear(); }
            http.Dispose();
        }
    }
}
