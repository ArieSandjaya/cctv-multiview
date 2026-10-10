using System;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Text;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NvrBridge
{
    /// <summary>Server HTTP kecil: halaman playback, API JSON, dan penyaji klip MP4 (dengan Range).</summary>
    sealed class WebServer : IDisposable
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;
        static readonly Regex ClipNameRx = new Regex(@"^[0-9a-f]{32}_\d{1,3}\.mp4$", RegexOptions.Compiled);
        static readonly Regex JobIdRx = new Regex(@"^[0-9a-f]{32}$", RegexOptions.Compiled);

        readonly AppConfig cfg;
        readonly NvrClient nvr;
        readonly JobManager jobs;
        readonly HttpListener listener = new HttpListener();
        readonly CancellationTokenSource stop = new CancellationTokenSource();
        readonly byte[] authExpected;

        DateTime rangeAt = DateTime.MinValue;
        string rangeJson;
        readonly object rangeLock = new object();

        public WebServer(AppConfig cfg, NvrClient nvr, JobManager jobs)
        {
            this.cfg = cfg;
            this.nvr = nvr;
            this.jobs = jobs;
            if (cfg.BridgeUser.Length > 0)
                authExpected = Encoding.UTF8.GetBytes(cfg.BridgeUser + ":" + cfg.BridgePassword);
            listener.Prefixes.Add(cfg.ListenPrefix);
        }

        public void Start()
        {
            listener.Start();
            Task.Run(AcceptLoopAsync);
        }

        async Task AcceptLoopAsync()
        {
            while (listener.IsListening)
            {
                HttpListenerContext ctx;
                try { ctx = await listener.GetContextAsync().ConfigureAwait(false); }
                catch (Exception) { break; }
                var ignored = Task.Run(() => HandleAsync(ctx));
            }
        }

        public void Dispose()
        {
            stop.Cancel();
            try { listener.Close(); } catch { }
        }

        // ---------- penanganan permintaan ----------

        async Task HandleAsync(HttpListenerContext ctx)
        {
            var req = ctx.Request;
            var res = ctx.Response;
            try
            {
                res.Headers["X-Content-Type-Options"] = "nosniff";
                res.Headers["Cache-Control"] = "no-store";
                res.Headers["Referrer-Policy"] = "no-referrer";

                if (!Authorized(req))
                {
                    res.AddHeader("WWW-Authenticate", "Basic realm=\"NvrBridge\"");
                    await SendTextAsync(res, 401, "text/plain", "Perlu login.").ConfigureAwait(false);
                    return;
                }

                string path = req.Url.AbsolutePath;
                string method = req.HttpMethod;

                if (path == "/" || path == "/playback.html")
                {
                    if (method != "GET") { await SendTextAsync(res, 405, "text/plain", "Method not allowed").ConfigureAwait(false); return; }
                    string file = Path.Combine(cfg.WebDir, "playback.html");
                    if (!File.Exists(file)) { await SendTextAsync(res, 404, "text/plain", "playback.html tidak ditemukan di " + cfg.WebDir).ConfigureAwait(false); return; }
                    res.Headers["Content-Security-Policy"] =
                        "default-src 'self'; media-src 'self'; style-src 'self' 'unsafe-inline'; script-src 'self' 'unsafe-inline'; frame-ancestors 'none'";
                    await SendTextAsync(res, 200, "text/html", File.ReadAllText(file, Encoding.UTF8)).ConfigureAwait(false);
                }
                else if (path == "/api/info" && method == "GET")
                {
                    var cams = string.Join(",", cfg.Cameras.Select(c => "{\"id\":" + c.Id.ToString(CI) + ",\"name\":" + Json.Q(c.Name) + "}"));
                    await SendJsonAsync(res, 200, "{\"title\":" + Json.Q(cfg.Title) + ",\"cameras\":[" + cams + "],\"maxMinutes\":" +
                        cfg.MaxClipMinutes.ToString(CI) + ",\"timeMode\":" + Json.Q(cfg.NvrTimeUtc ? "Utc" : "Local") + "}").ConfigureAwait(false);
                }
                else if (path == "/api/range" && method == "GET")
                {
                    await HandleRangeAsync(res).ConfigureAwait(false);
                }
                else if (path == "/api/clip")
                {
                    if (method != "POST") { await SendJsonAsync(res, 405, Err("Gunakan POST.")).ConfigureAwait(false); return; }
                    if (req.Headers["X-NvrBridge"] != "1") { await SendJsonAsync(res, 403, Err("Header X-NvrBridge: 1 diperlukan.")).ConfigureAwait(false); return; }
                    await HandleClipAsync(req, res).ConfigureAwait(false);
                }
                else if (path == "/api/job" && method == "GET")
                {
                    string id = req.QueryString["id"] ?? "";
                    var job = JobIdRx.IsMatch(id) ? jobs.Get(id) : null;
                    if (job == null) { await SendJsonAsync(res, 404, Err("Job tidak ditemukan atau sudah dihapus.")).ConfigureAwait(false); return; }
                    var files = string.Join(",", job.Files.Select(f => Json.Q("/clips/" + f)));
                    await SendJsonAsync(res, 200, "{\"id\":" + Json.Q(job.Id) + ",\"state\":" + Json.Q(job.State) + ",\"message\":" + Json.Q(job.Message) +
                        ",\"bytes\":" + Interlocked.Read(ref job.Bytes).ToString(CI) + ",\"files\":[" + files + "]}").ConfigureAwait(false);
                }
                else if (path.StartsWith("/clips/") && method == "GET")
                {
                    await ServeClipAsync(req, res, path.Substring("/clips/".Length)).ConfigureAwait(false);
                }
                else
                {
                    await SendTextAsync(res, 404, "text/plain", "Tidak ditemukan.").ConfigureAwait(false);
                }
            }
            catch (HttpListenerException) { /* klien terputus */ }
            catch (IOException) { /* klien terputus */ }
            catch (ObjectDisposedException) { }
            catch (Exception e)
            {
                Log.Warn("Kesalahan memproses " + req.HttpMethod + " " + req.Url.AbsolutePath + ": " + e.Message);
                try { await SendJsonAsync(res, 500, Err("Kesalahan internal server.")).ConfigureAwait(false); } catch { }
            }
            finally
            {
                try { res.Close(); } catch { }
            }
        }

        static string Err(string m) { return "{\"error\":" + Json.Q(m) + "}"; }

        bool Authorized(HttpListenerRequest req)
        {
            if (authExpected == null) return true;
            string h = req.Headers["Authorization"];
            if (string.IsNullOrEmpty(h) || !h.StartsWith("Basic ", StringComparison.OrdinalIgnoreCase)) return false;
            byte[] got;
            try { got = Convert.FromBase64String(h.Substring(6).Trim()); } catch (FormatException) { return false; }
            return FixedTimeEquals(got, authExpected);
        }

        static bool FixedTimeEquals(byte[] a, byte[] b)
        {
            int diff = a.Length ^ b.Length;
            for (int i = 0; i < b.Length; i++)
                diff |= (i < a.Length ? a[i] : 0) ^ b[i];
            return diff == 0;
        }

        // ---------- API ----------

        async Task HandleRangeAsync(HttpListenerResponse res)
        {
            string cached = null;
            lock (rangeLock) { if (rangeJson != null && DateTime.UtcNow - rangeAt < TimeSpan.FromSeconds(30)) cached = rangeJson; }
            if (cached != null) { await SendJsonAsync(res, 200, cached).ConfigureAwait(false); return; }
            try
            {
                var r = await nvr.GetRecordedRangeAsync(stop.Token).ConfigureAwait(false);
                string json = "{\"oldest\":" + Json.Q(r.Oldest.HasValue ? r.Oldest.Value.ToString("yyyy-MM-dd HH:mm", CI) : null) +
                              ",\"latest\":" + Json.Q(r.Latest.HasValue ? r.Latest.Value.ToString("yyyy-MM-dd HH:mm", CI) : null) + "}";
                lock (rangeLock) { rangeJson = json; rangeAt = DateTime.UtcNow; }
                await SendJsonAsync(res, 200, json).ConfigureAwait(false);
            }
            catch (NvrException e) { await SendJsonAsync(res, 502, Err(e.Message)).ConfigureAwait(false); }
        }

        async Task HandleClipAsync(HttpListenerRequest req, HttpListenerResponse res)
        {
            int cam, minutes;
            DateTime start;
            if (!int.TryParse(req.QueryString["cam"], NumberStyles.None, CI, out cam) ||
                !int.TryParse(req.QueryString["minutes"], NumberStyles.None, CI, out minutes) ||
                !DateTime.TryParse(req.QueryString["start"], CI, DateTimeStyles.AdjustToUniversal | DateTimeStyles.AssumeUniversal, out start))
            {
                await SendJsonAsync(res, 400, Err("Parameter cam, start (ISO UTC), dan minutes diperlukan.")).ConfigureAwait(false);
                return;
            }
            try
            {
                var job = jobs.Start(cam, DateTime.SpecifyKind(start, DateTimeKind.Utc), minutes);
                await SendJsonAsync(res, 202, "{\"id\":" + Json.Q(job.Id) + "}").ConfigureAwait(false);
            }
            catch (ArgumentException e) { await SendJsonAsync(res, 400, Err(e.Message)).ConfigureAwait(false); }
            catch (InvalidOperationException e) { await SendJsonAsync(res, 429, Err(e.Message)).ConfigureAwait(false); }
        }

        // ---------- klip MP4 dengan dukungan Range ----------

        async Task ServeClipAsync(HttpListenerRequest req, HttpListenerResponse res, string name)
        {
            if (!ClipNameRx.IsMatch(name)) { await SendTextAsync(res, 404, "text/plain", "Tidak ditemukan.").ConfigureAwait(false); return; }
            string path = Path.Combine(cfg.ClipDir, name);
            if (!File.Exists(path)) { await SendTextAsync(res, 404, "text/plain", "Klip tidak ditemukan atau sudah dihapus.").ConfigureAwait(false); return; }

            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
            {
                long total = fs.Length, start = 0, end = total - 1;
                bool partial = false;
                string rh = req.Headers["Range"];
                if (!string.IsNullOrEmpty(rh))
                {
                    var m = Regex.Match(rh, @"^bytes=(\d*)-(\d*)$");
                    if (m.Success && (m.Groups[1].Length > 0 || m.Groups[2].Length > 0))
                    {
                        long a = 0, b = 0;
                        bool ok = true;
                        if (m.Groups[1].Length > 0) ok = long.TryParse(m.Groups[1].Value, NumberStyles.None, CI, out a);
                        if (ok && m.Groups[2].Length > 0) ok = long.TryParse(m.Groups[2].Value, NumberStyles.None, CI, out b);
                        if (ok)
                        {
                            if (m.Groups[1].Length == 0) { start = Math.Max(0, total - b); end = total - 1; }
                            else { start = a; end = m.Groups[2].Length == 0 ? total - 1 : Math.Min(b, total - 1); }
                            if (start >= total || start > end)
                            {
                                res.AddHeader("Content-Range", "bytes */" + total.ToString(CI));
                                await SendTextAsync(res, 416, "text/plain", "Range tidak valid.").ConfigureAwait(false);
                                return;
                            }
                            partial = true;
                        }
                    }
                }

                res.StatusCode = partial ? 206 : 200;
                res.ContentType = "video/mp4";
                res.AddHeader("Accept-Ranges", "bytes");
                if (partial) res.AddHeader("Content-Range", "bytes " + start.ToString(CI) + "-" + end.ToString(CI) + "/" + total.ToString(CI));
                long remaining = end - start + 1;
                res.ContentLength64 = remaining;

                fs.Seek(start, SeekOrigin.Begin);
                var buf = new byte[81920];
                while (remaining > 0)
                {
                    int n = await fs.ReadAsync(buf, 0, (int)Math.Min(buf.Length, remaining), stop.Token).ConfigureAwait(false);
                    if (n <= 0) break;
                    await res.OutputStream.WriteAsync(buf, 0, n, stop.Token).ConfigureAwait(false);
                    remaining -= n;
                }
            }
        }

        // ---------- helper kirim ----------

        static Task SendJsonAsync(HttpListenerResponse res, int status, string json)
        {
            return SendTextAsync(res, status, "application/json", json);
        }

        static async Task SendTextAsync(HttpListenerResponse res, int status, string contentType, string text)
        {
            byte[] data = Encoding.UTF8.GetBytes(text);
            res.StatusCode = status;
            res.ContentType = contentType + "; charset=utf-8";
            res.ContentLength64 = data.Length;
            await res.OutputStream.WriteAsync(data, 0, data.Length).ConfigureAwait(false);
        }
    }
}
