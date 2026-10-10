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
    /// Kontrol PTZ kamera Dahua (OEM) lewat /cgi-bin/ptz.cgi dengan login Digest.
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

        public PtzClient(AppConfig cfg)
        {
            this.cfg = cfg;
            var handler = new HttpClientHandler { AllowAutoRedirect = false, UseCookies = false, PreAuthenticate = false };
            var cache = new CredentialCache();
            foreach (var kv in cfg.PtzCameras)
                cache.Add(new Uri("http://" + kv.Value + "/"), "Digest", new NetworkCredential(cfg.PtzUser, cfg.PtzPassword));
            handler.Credentials = cache;
            http = new HttpClient(handler) { Timeout = TimeSpan.FromSeconds(5) };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NvrBridge/1.1");
        }

        public bool Enabled { get { return cfg.PtzCameras.Count > 0; } }

        public IEnumerable<string> CameraIds { get { return cfg.PtzCameras.Keys; } }

        public bool Has(string cam) { return cam != null && cfg.PtzCameras.ContainsKey(cam); }

        static bool IsMove(string dir) { return dir != null && Codes.ContainsKey(dir); }

        public async Task StartAsync(string cam, string dir, int speed)
        {
            if (!Has(cam)) throw new ArgumentException("Kamera PTZ tidak dikenal.");
            if (!IsMove(dir)) throw new ArgumentException("Arah PTZ tidak dikenal.");
            if (speed < 1 || speed > 8) throw new ArgumentException("speed harus 1-8.");

            string code = Codes[dir];
            bool zf = dir.StartsWith("zoom") || dir.StartsWith("focus");
            bool diag = dir == "upleft" || dir == "upright" || dir == "downleft" || dir == "downright";
            string a1 = diag ? speed.ToString(CI) : "0";
            string a2 = zf ? "0" : speed.ToString(CI);

            // Hentikan gerakan sebelumnya pada kamera ini bila arahnya berbeda
            string prev;
            lock (gate) { active.TryGetValue(cam, out prev); }
            if (prev != null && prev != code) await SendAsync(cam, "stop", prev, "0", "0", "0").ConfigureAwait(false);

            await SendAsync(cam, "start", code, a1, a2, "0").ConfigureAwait(false);
            lock (gate)
            {
                active[cam] = code;
                Timer t;
                if (autoStop.TryGetValue(cam, out t)) t.Dispose();
                autoStop[cam] = new Timer(_ => { var ignored = AutoStopAsync(cam, code); }, null,
                    TimeSpan.FromSeconds(cfg.PtzMaxMoveSeconds), Timeout.InfiniteTimeSpan);
            }
        }

        public async Task StopAsync(string cam, string dir)
        {
            if (!Has(cam)) throw new ArgumentException("Kamera PTZ tidak dikenal.");
            string code = null;
            if (dir != null && Codes.ContainsKey(dir)) code = Codes[dir];
            lock (gate)
            {
                if (code == null) active.TryGetValue(cam, out code);
                active.Remove(cam);
                Timer t;
                if (autoStop.TryGetValue(cam, out t)) { t.Dispose(); autoStop.Remove(cam); }
            }
            if (code == null) return;      // tidak ada gerakan yang berjalan
            await SendAsync(cam, "stop", code, "0", "0", "0").ConfigureAwait(false);
        }

        public Task PresetAsync(string cam, int n)
        {
            if (!Has(cam)) throw new ArgumentException("Kamera PTZ tidak dikenal.");
            if (n < 1 || n > 255) throw new ArgumentException("Nomor preset harus 1-255.");
            return SendAsync(cam, "start", "GotoPreset", "0", n.ToString(CI), "0");
        }

        async Task AutoStopAsync(string cam, string code)
        {
            lock (gate)
            {
                string cur;
                if (!active.TryGetValue(cam, out cur) || cur != code) return;   // sudah diganti/dihentikan
                active.Remove(cam);
                Timer t;
                if (autoStop.TryGetValue(cam, out t)) { t.Dispose(); autoStop.Remove(cam); }
            }
            Log.Warn("PTZ " + cam + ": tombol stop tidak diterima, berhenti otomatis.");
            try { await SendAsync(cam, "stop", code, "0", "0", "0").ConfigureAwait(false); } catch (PtzException) { }
        }

        async Task SendAsync(string cam, string action, string code, string a1, string a2, string a3)
        {
            string pq = "/cgi-bin/ptz.cgi?action=" + action + "&channel=" + cfg.PtzChannel.ToString(CI) +
                        "&code=" + code + "&arg1=" + a1 + "&arg2=" + a2 + "&arg3=" + a3;
            var uri = new Uri("http://" + cfg.PtzCameras[cam] + pq);
            Log.Debug("PTZ " + cam + " " + action + " " + code + " " + a1 + "/" + a2);
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
