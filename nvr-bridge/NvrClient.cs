using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.Http;
using System.Text.RegularExpressions;
using System.Threading;
using System.Threading.Tasks;

namespace NvrBridge
{
    class NvrException : Exception
    {
        public NvrException(string m) : base(m) { }
    }

    /// <summary>Sesi (UID) ditolak atau kedaluwarsa; login ulang lalu coba lagi.</summary>
    sealed class NvrUidException : NvrException
    {
        public NvrUidException(string m) : base(m) { }
    }

    sealed class RecordRange
    {
        public DateTime? Oldest;
        public DateTime? Latest;
    }

    sealed class ProbeInfo
    {
        public int Http;
        public string ContentType = "";
        public string RecStatus = "";      // header X-RecData-Satus (jika ada)
        public string Snippet = "";        // awal isi jika berupa teks
    }

    sealed class DownloadResult
    {
        public List<string> Files = new List<string>();
    }

    /// <summary>
    /// Klien CGI NVR i-PRO (dokumen "i-PRO NVR CGI Command Interface" v1.3R1):
    /// login Digest -> UID, keep-alive, rentang rekaman, dan unduh MP4 (httpdl.cgi).
    /// </summary>
    sealed class NvrClient : IDisposable
    {
        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        readonly AppConfig cfg;
        readonly HttpClient http;
        readonly object uidLock = new object();
        readonly SemaphoreSlim loginGate = new SemaphoreSlim(1, 1);
        readonly Timer timer;
        string uid;
        DateTime lastUse = DateTime.UtcNow;
        int active;
        int ticking;

        public NvrClient(AppConfig cfg)
        {
            this.cfg = cfg;
            var handler = new HttpClientHandler
            {
                AllowAutoRedirect = false,
                UseCookies = false,
                PreAuthenticate = false
            };

            // Hanya skema yang dipilih (default Digest) yang boleh dipakai; tidak turun ke Basic.
            var cache = new CredentialCache();
            cache.Add(new Uri(cfg.NvrBase.GetLeftPart(UriPartial.Authority) + "/"),
                      cfg.NvrAuthScheme, new NetworkCredential(cfg.NvrUser, cfg.NvrPassword));
            handler.Credentials = cache;

            if (cfg.NvrAllowSelfSigned)
            {
                string host = cfg.NvrBase.Host;
                // Sertifikat yang tidak tepercaya hanya diterima untuk host NVR ini.
                handler.ServerCertificateCustomValidationCallback = (req, cert, chain, errors) =>
                    errors == System.Net.Security.SslPolicyErrors.None ||
                    string.Equals(req.RequestUri.Host, host, StringComparison.OrdinalIgnoreCase);
            }

            http = new HttpClient(handler) { Timeout = Timeout.InfiniteTimeSpan };
            http.DefaultRequestHeaders.UserAgent.ParseAdd("NvrBridge/1.0");
            timer = new Timer(OnTick, null, TimeSpan.FromSeconds(30), TimeSpan.FromSeconds(30));
        }

        Uri Url(string pathAndQuery) { return new Uri(cfg.NvrBase, pathAndQuery); }

        // ---------- permintaan kecil ----------

        async Task<Tuple<HttpStatusCode, string>> GetTextAsync(string pathAndQuery, int timeoutSec, CancellationToken ct)
        {
            using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
            {
                cts.CancelAfter(TimeSpan.FromSeconds(timeoutSec));
                try
                {
                    using (var resp = await http.GetAsync(Url(pathAndQuery), cts.Token).ConfigureAwait(false))
                    {
                        string body = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                        return Tuple.Create(resp.StatusCode, body);
                    }
                }
                catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                {
                    throw new NvrException("NVR tidak menjawab dalam " + timeoutSec + " detik.");
                }
                catch (HttpRequestException e)
                {
                    throw new NvrException("Tidak bisa terhubung ke NVR: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                }
            }
        }

        // ---------- UID (login) ----------

        public async Task<string> GetUidAsync(CancellationToken ct)
        {
            lock (uidLock) { if (uid != null) { lastUse = DateTime.UtcNow; return uid; } }
            await loginGate.WaitAsync(ct).ConfigureAwait(false);
            try
            {
                lock (uidLock) { if (uid != null) return uid; }
                string fresh = await LoginAsync(ct).ConfigureAwait(false);
                lock (uidLock) { uid = fresh; lastUse = DateTime.UtcNow; }
                return fresh;
            }
            finally { loginGate.Release(); }
        }

        async Task<string> LoginAsync(CancellationToken ct)
        {
            var r = await GetTextAsync("/cgi-bin/dlogin.cgi?UID=-1", 15, ct).ConfigureAwait(false);
            if (r.Item1 == HttpStatusCode.Unauthorized)
                throw new NvrException("Login ke NVR ditolak (HTTP 401). Periksa NvrUser, NvrPassword, dan NvrAuthScheme.");
            if (r.Item1 != HttpStatusCode.OK)
                throw new NvrException("Login ke NVR gagal: HTTP " + (int)r.Item1 + ".");
            var m = Regex.Match(r.Item2 ?? "", @"UID\s*=\s*(\d{1,19})");
            if (!m.Success)
                throw new NvrException("Jawaban login NVR tidak berisi UID.");
            Log.Info("Login ke NVR berhasil.");
            return m.Groups[1].Value;
        }

        void Invalidate(string used)
        {
            lock (uidLock) { if (uid == used) uid = null; }
        }

        async Task LogoutAsync(string u)
        {
            Invalidate(u);
            try { await GetTextAsync("/cgi-bin/logout.cgi?UID=" + u, 5, CancellationToken.None).ConfigureAwait(false); }
            catch (Exception e) { Log.Debug("logout: " + e.Message); }
        }

        async void OnTick(object state)
        {
            if (Interlocked.Exchange(ref ticking, 1) == 1) return;
            try
            {
                string u;
                DateTime used;
                lock (uidLock) { u = uid; used = lastUse; }
                if (u == null) return;

                if (Volatile.Read(ref active) > 0) { lock (uidLock) { lastUse = DateTime.UtcNow; } }
                else if (DateTime.UtcNow - used > TimeSpan.FromMinutes(5))
                {
                    await LogoutAsync(u).ConfigureAwait(false);   // lepas UID saat tidak dipakai
                    Log.Debug("UID dilepas (tidak ada aktivitas).");
                    return;
                }

                var r = await GetTextAsync("/cgi-bin/status.cgi?UID=" + u + "&PC=AS60", 10, CancellationToken.None).ConfigureAwait(false);
                if (r.Item1 != HttpStatusCode.OK)
                {
                    Log.Warn("Keep-alive ditolak (HTTP " + (int)r.Item1 + "); akan login ulang saat dibutuhkan.");
                    Invalidate(u);
                }
            }
            catch (Exception e) { Log.Debug("keep-alive: " + e.Message); }
            finally { Interlocked.Exchange(ref ticking, 0); }
        }

        // ---------- rentang rekaman ----------

        public async Task<RecordRange> GetRecordedRangeAsync(CancellationToken ct)
        {
            var r = await GetTextAsync("/cgi-bin/recordedtime.cgi", 15, ct).ConfigureAwait(false);
            if (r.Item1 != HttpStatusCode.OK)
            {
                string u = await GetUidAsync(ct).ConfigureAwait(false);
                r = await GetTextAsync("/cgi-bin/recordedtime.cgi?UID=" + u, 15, ct).ConfigureAwait(false);
            }
            if (r.Item1 != HttpStatusCode.OK)
                throw new NvrException("NVR menjawab HTTP " + (int)r.Item1 + " untuk daftar rentang rekaman.");

            var range = new RecordRange();
            foreach (var line in (r.Item2 ?? "").Split('\n'))
            {
                var p = line.Trim().Split(',');
                if (p.Length < 9) continue;
                if (!(p[0].StartsWith("MAIN") || p[0].StartsWith("EXT"))) continue;
                DateTime a, b;
                if (DateTime.TryParseExact(p[3].Trim() + " " + p[5].Trim(), "yyyy/MM/dd HH:mm:ss", CI, DateTimeStyles.None, out a))
                    if (range.Oldest == null || a < range.Oldest) range.Oldest = a;
                if (DateTime.TryParseExact(p[6].Trim() + " " + p[8].Trim(), "yyyy/MM/dd HH:mm:ss", CI, DateTimeStyles.None, out b))
                    if (range.Latest == null || b > range.Latest) range.Latest = b;
            }
            return range;
        }

        // ---------- unduh MP4 ----------

        static string Stamp(DateTime d) { return d.ToString("yyMMddHHmm", CI) + "00"; }   // detik dikunci 00

        public async Task<DownloadResult> DownloadAsync(
            int cam, DateTime start, DateTime end, string kind,
            string dir, string prefix, Action<long> onBytes, CancellationToken ct)
        {
            Interlocked.Increment(ref active);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    string u = await GetUidAsync(ct).ConfigureAwait(false);
                    try
                    {
                        return await DownloadOnceAsync(u, cam, start, end, kind, dir, prefix, onBytes, ct).ConfigureAwait(false);
                    }
                    catch (NvrUidException e) when (attempt == 0)
                    {
                        Log.Warn("Sesi NVR ditolak (" + e.Message + "); login ulang dan coba sekali lagi.");
                        Invalidate(u);
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
                lock (uidLock) { lastUse = DateTime.UtcNow; }
            }
        }

        async Task<DownloadResult> DownloadOnceAsync(
            string u, int cam, DateTime start, DateTime end, string kind,
            string dir, string prefix, Action<long> onBytes, CancellationToken ct)
        {
            string pq = "/cgi-bin/httpdl.cgi?UID=" + u + "&STARTTIME=" + Stamp(start) + "&ENDTIME=" + Stamp(end) +
                        "&KIND=" + kind + "&CAM=" + cam.ToString(CI) + "&PC=AS60";
            Log.Debug("GET " + pq);

            var writer = new SegmentWriter(dir, prefix, onBytes);
            try
            {
                HttpResponseMessage resp;
                try
                {
                    resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(pq)),
                                                HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                }
                catch (HttpRequestException e)
                {
                    throw new NvrException("Tidak bisa terhubung ke NVR: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                }

                using (resp)
                {
                    var ctype = resp.Content.Headers.ContentType;
                    string media = ctype != null ? (ctype.MediaType ?? "") : "";
                    Log.Debug("HTTP " + (int)resp.StatusCode + " " + media + " panjang=" + resp.Content.Headers.ContentLength);

                    if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                        throw new NvrUidException("HTTP " + (int)resp.StatusCode);
                    if (!resp.IsSuccessStatusCode)
                        throw new NvrException("NVR menjawab HTTP " + (int)resp.StatusCode + " untuk permintaan unduh.");

                    using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                    {
                        if (media.StartsWith("multipart/", StringComparison.OrdinalIgnoreCase))
                        {
                            string boundary = ctype.Parameters
                                .Where(p => string.Equals(p.Name, "boundary", StringComparison.OrdinalIgnoreCase))
                                .Select(p => p.Value).FirstOrDefault();
                            if (string.IsNullOrEmpty(boundary))
                                throw new NvrException("Jawaban multipart dari NVR tidak punya boundary.");
                            var reader = new MultipartReader(body, boundary.Trim('"'), writer.CountBytes);
                            await reader.ReadAllAsync(writer.BeginPart, writer.EndPart, ct).ConfigureAwait(false);
                        }
                        else if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                        {
                            var buf = new byte[512];
                            int n = await body.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                            string snippet = System.Text.Encoding.UTF8.GetString(buf, 0, n).Replace('\r', ' ').Replace('\n', ' ').Trim();
                            throw new NvrUidException("NVR mengirim teks, bukan video: " + snippet);
                        }
                        else
                        {
                            // Satu file biasa; keterangan status bisa ada di header HTTP.
                            IEnumerable<string> vals;
                            if (resp.Headers.TryGetValues("X-RecData-Satus", out vals) || resp.Headers.TryGetValues("X-RecData-Status", out vals))
                                writer.Status = vals.FirstOrDefault();
                            writer.OpenNew();
                            await writer.CopyFromAsync(body, ct).ConfigureAwait(false);
                        }
                    }
                }

                writer.Finish();
                CheckStatus(writer.Status, writer.Files.Count);
                foreach (var f in writer.Files) VerifyMp4(f);
                var result = new DownloadResult();
                result.Files.AddRange(writer.Files);
                return result;
            }
            catch
            {
                writer.Abort();
                throw;
            }
        }

        /// <summary>
        /// Hanya menanyakan ke NVR apakah permintaan httpdl untuk CAM ini dijawab: membaca header jawaban (dan sedikit teks jika berupa teks)
        /// lalu memutus koneksi. Tidak ada rekaman yang diunduh.
        /// </summary>
        public async Task<ProbeInfo> ProbeDownloadAsync(int cam, DateTime start, DateTime end, string kind, CancellationToken ct)
        {
            Interlocked.Increment(ref active);
            try
            {
                for (int attempt = 0; ; attempt++)
                {
                    string u = await GetUidAsync(ct).ConfigureAwait(false);
                    string pq = "/cgi-bin/httpdl.cgi?UID=" + u + "&STARTTIME=" + Stamp(start) + "&ENDTIME=" + Stamp(end) +
                                "&KIND=" + kind + "&CAM=" + cam.ToString(CI) + "&PC=AS60";
                    HttpResponseMessage resp;
                    try
                    {
                        resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(pq)), HttpCompletionOption.ResponseHeadersRead, ct).ConfigureAwait(false);
                    }
                    catch (HttpRequestException e)
                    {
                        throw new NvrException("Tidak bisa terhubung ke NVR: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                    }
                    using (resp)
                    {
                        if ((resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden) && attempt == 0)
                        { Invalidate(u); continue; }

                        var info = new ProbeInfo { Http = (int)resp.StatusCode };
                        var ctype = resp.Content.Headers.ContentType;
                        info.ContentType = ctype != null ? (ctype.MediaType ?? "") : "";
                        IEnumerable<string> vals;
                        if (resp.Headers.TryGetValues("X-RecData-Satus", out vals) || resp.Headers.TryGetValues("X-RecData-Status", out vals))
                            info.RecStatus = (vals.FirstOrDefault() ?? "").Trim();
                        if (info.ContentType.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                        {
                            using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                            {
                                var buf = new byte[300];
                                int n = await body.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false);
                                info.Snippet = System.Text.Encoding.UTF8.GetString(buf, 0, n).Replace('\r', ' ').Replace('\n', ' ').Trim();
                            }
                        }
                        return info;           // using resp: koneksi diputus tanpa membaca isi video
                    }
                }
            }
            finally
            {
                Interlocked.Decrement(ref active);
                lock (uidLock) { lastUse = DateTime.UtcNow; }
            }
        }

        /// <summary>
        /// Uji ringan satu nomor kamera: kirim permintaan httpdl.cgi lalu baca status dan header saja, tanpa mengunduh isi video.
        /// Mengembalikan (state, pesan): rec = NVR mulai mengirim data, none = NVR menjawab "tidak ada rekaman", error = jawaban lain.
        /// </summary>
        public async Task<Tuple<string, string>> ProbeCamAsync(int cam, DateTime start, DateTime end, CancellationToken ct)
        {
            for (int attempt = 0; ; attempt++)
            {
                string u = await GetUidAsync(ct).ConfigureAwait(false);
                string pq = "/cgi-bin/httpdl.cgi?UID=" + u + "&STARTTIME=" + Stamp(start) + "&ENDTIME=" + Stamp(end) +
                            "&KIND=MP4VO&CAM=" + cam.ToString(CI) + "&PC=AS60";
                Log.Debug("GET " + pq);
                using (var cts = CancellationTokenSource.CreateLinkedTokenSource(ct))
                {
                    cts.CancelAfter(TimeSpan.FromSeconds(30));
                    try
                    {
                        using (var resp = await http.SendAsync(new HttpRequestMessage(HttpMethod.Get, Url(pq)),
                                                               HttpCompletionOption.ResponseHeadersRead, cts.Token).ConfigureAwait(false))
                        {
                            var ctype = resp.Content.Headers.ContentType;
                            string media = ctype != null ? (ctype.MediaType ?? "") : "";
                            IEnumerable<string> vals;
                            string recStatus = null;
                            if (resp.Headers.TryGetValues("X-RecData-Satus", out vals) || resp.Headers.TryGetValues("X-RecData-Status", out vals))
                                recStatus = (vals.FirstOrDefault() ?? "").Trim();

                            if (resp.StatusCode == HttpStatusCode.Unauthorized || resp.StatusCode == HttpStatusCode.Forbidden)
                            {
                                if (attempt == 0) { Invalidate(u); continue; }
                                return Tuple.Create("error", "HTTP " + (int)resp.StatusCode + " (sesi/izin ditolak)");
                            }
                            if (!resp.IsSuccessStatusCode) return Tuple.Create("error", "HTTP " + (int)resp.StatusCode);
                            if (recStatus == "1") return Tuple.Create("none", "Tidak ada rekaman pada rentang waktu uji.");
                            if (recStatus == "3") return Tuple.Create("error", "NVR sibuk.");
                            if (media.StartsWith("text/", StringComparison.OrdinalIgnoreCase))
                            {
                                using (var body = await resp.Content.ReadAsStreamAsync().ConfigureAwait(false))
                                {
                                    var buf = new byte[300];
                                    int n = await body.ReadAsync(buf, 0, buf.Length, cts.Token).ConfigureAwait(false);
                                    string snip = System.Text.Encoding.UTF8.GetString(buf, 0, n).Replace('\r', ' ').Replace('\n', ' ').Trim();
                                    return Tuple.Create("error", "NVR mengirim teks: " + snip);
                                }
                            }
                            return Tuple.Create("rec", "HTTP " + (int)resp.StatusCode + " " + (media.Length > 0 ? media : "data") +
                                                       (recStatus != null ? ", status " + recStatus : ""));
                        }
                    }
                    catch (OperationCanceledException) when (!ct.IsCancellationRequested)
                    {
                        return Tuple.Create("error", "NVR tidak menjawab dalam 30 detik.");
                    }
                    catch (HttpRequestException e)
                    {
                        throw new NvrException("Tidak bisa terhubung ke NVR: " + (e.InnerException != null ? e.InnerException.Message : e.Message));
                    }
                }
            }
        }

        static void CheckStatus(string status, int fileCount)
        {
            status = (status ?? "").Trim();
            if (status == "1") throw new NvrException("Tidak ada rekaman pada rentang waktu itu.");
            if (status == "2") throw new NvrException("Kodek rekaman tidak kompatibel dengan MP4 (kemungkinan JPEG).");
            if (status == "3") throw new NvrException("NVR sedang sibuk (terlalu banyak proses bersamaan). Coba lagi sebentar.");
            if (fileCount == 0) throw new NvrException("NVR tidak mengirim data rekaman.");
        }

        static void VerifyMp4(string path)
        {
            var head = new byte[12];
            int n;
            using (var fs = new FileStream(path, FileMode.Open, FileAccess.Read, FileShare.Read))
                n = fs.Read(head, 0, head.Length);
            // File MP4 yang sah memiliki "ftyp" di byte 4-7.
            if (n < 12 || head[4] != (byte)'f' || head[5] != (byte)'t' || head[6] != (byte)'y' || head[7] != (byte)'p')
                throw new NvrException("File yang diterima dari NVR bukan MP4 yang dikenal.");
        }

        public void Dispose()
        {
            timer.Dispose();
            string u;
            lock (uidLock) { u = uid; }
            if (u != null)
            {
                try { LogoutAsync(u).Wait(3000); } catch { }
            }
            http.Dispose();
        }
    }

    /// <summary>Menulis bagian multipart ke file lokal; mengikuti X-Temp-FileName / X-Prev-Filename dari NVR.</summary>
    sealed class SegmentWriter
    {
        static readonly Regex NameRx = new Regex("filename\\s*=\\s*\"([^\"]*)\"", RegexOptions.IgnoreCase | RegexOptions.Compiled);

        readonly string dir;
        readonly string prefix;
        readonly Action<long> onBytes;
        FileStream cur;
        string curPath;
        string curTemp;
        int index;

        public readonly List<string> Files = new List<string>();
        public string Status;

        public SegmentWriter(string dir, string prefix, Action<long> onBytes)
        {
            this.dir = dir;
            this.prefix = prefix;
            this.onBytes = onBytes;
        }

        public void CountBytes(long n) { if (onBytes != null) onBytes(n); }

        static string FileNameOf(string headerValue)
        {
            if (string.IsNullOrWhiteSpace(headerValue)) return "";
            var m = NameRx.Match(headerValue);
            return (m.Success ? m.Groups[1].Value : headerValue).Trim().Trim('"');
        }

        public Stream BeginPart(Dictionary<string, string> h)
        {
            string tmp, prev, st;
            h.TryGetValue("X-Temp-FileName", out tmp);
            h.TryGetValue("X-Prev-Filename", out prev);
            if (!h.TryGetValue("X-RecData-Satus", out st)) h.TryGetValue("X-RecData-Status", out st);   // ejaan dokumen: "Satus"
            if (!string.IsNullOrWhiteSpace(st)) Status = st.Trim();

            string tmpName = FileNameOf(tmp);
            string prevName = FileNameOf(prev);

            if (prevName.Length > 0 && cur != null) CloseCurrent();       // file sebelumnya selesai
            if (tmpName.Length > 0 && (cur == null || tmpName != curTemp))
            {
                if (cur != null) CloseCurrent();
                OpenNew();
                curTemp = tmpName;
            }
            return cur != null ? (Stream)cur : Stream.Null;
        }

        public void EndPart(Dictionary<string, string> h) { }

        public void OpenNew()
        {
            if (cur != null) CloseCurrent();
            curPath = Path.Combine(dir, prefix + "_" + index.ToString(CultureInfo.InvariantCulture) + ".mp4");
            index++;
            cur = new FileStream(curPath, FileMode.Create, FileAccess.Write, FileShare.Read, 81920, true);
        }

        public async Task CopyFromAsync(Stream src, CancellationToken ct)
        {
            var buf = new byte[81920];
            int n;
            while ((n = await src.ReadAsync(buf, 0, buf.Length, ct).ConfigureAwait(false)) > 0)
            {
                await cur.WriteAsync(buf, 0, n, ct).ConfigureAwait(false);
                CountBytes(n);
            }
        }

        void CloseCurrent()
        {
            if (cur == null) return;
            long size = cur.Length;
            cur.Dispose();
            cur = null;
            curTemp = null;
            if (size > 0) Files.Add(curPath);
            else { try { File.Delete(curPath); } catch { } }
        }

        /// <summary>Akhir transfer: tutup file yang masih terbuka.</summary>
        public void Finish() { CloseCurrent(); }

        /// <summary>Gagal/dibatalkan: tutup dan hapus semua file yang sudah dibuat.</summary>
        public void Abort()
        {
            try { if (cur != null) { cur.Dispose(); cur = null; if (curPath != null) File.Delete(curPath); } } catch { }
            foreach (var f in Files) { try { File.Delete(f); } catch { } }
            Files.Clear();
        }
    }
}
