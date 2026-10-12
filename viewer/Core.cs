using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace CctvViewer
{
    /// <summary>
    /// Bagian tanpa tampilan: menjaga go2rtc dan NvrBridge (jalan ulang jika berhenti), server kontrol lokal, dan alamat halaman tiap layar.
    /// Berjalan di thread sendiri sehingga tidak pernah membekukan jendela.
    /// </summary>
    sealed class Core : IDisposable
    {
        public readonly Settings St;
        readonly IntPtr job;
        ManagedProcess go2rtc, bridge;
        ControlServer ctl;
        Thread loop;
        readonly ManualResetEvent stop = new ManualResetEvent(false);
        volatile bool ready;
        public bool Go2rtcOwn { get; private set; }

        public event Action QuitRequested;
        public event Action PanelRequested;

        public Core(Settings st) { St = st; job = Native.CreateKillOnCloseJob(); if (job == IntPtr.Zero) Log.Warn("Job object gagal dibuat; proses anak bisa tertinggal jika CctvViewer dihentikan paksa."); }

        public IntPtr Job { get { return job; } }

        public static string Http(Uri url, string method, int timeoutMs, string header = null)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = method; rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs; rq.Proxy = null;
            if (header != null) rq.Headers[header] = "1";
            if (method == "POST") rq.ContentLength = 0;
            using (var rs = (HttpWebResponse)rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        public static bool Go2rtcUp(Settings st)
        {
            try { Http(new Uri(st.Go2rtcApi, "api"), "GET", 1500); return true; } catch { return false; }
        }

        /// <summary>Nama stream tingkat atas dari JSON /api/streams (tanpa pustaka JSON).</summary>
        public static List<string> StreamNames(string json)
        {
            var keys = new List<string>(); int depth = 0;
            for (int i = 0; i < json.Length; i++)
            {
                char ch = json[i];
                if (ch == '{' || ch == '[') depth++;
                else if (ch == '}' || ch == ']') depth--;
                else if (ch == '"')
                {
                    var sb = new StringBuilder(); i++;
                    while (i < json.Length && json[i] != '"') { if (json[i] == '\\' && i + 1 < json.Length) i++; sb.Append(json[i]); i++; }
                    int j = i + 1; while (j < json.Length && char.IsWhiteSpace(json[j])) j++;
                    if (depth == 1 && j < json.Length && json[j] == ':') keys.Add(sb.ToString());
                }
            }
            return keys;
        }

        public static int CameraCount(Settings st)
        {
            try
            {
                int n = 0;
                foreach (var k in StreamNames(Http(new Uri(st.Go2rtcApi, "api/streams"), "GET", 3000)))
                    if (!k.EndsWith("_hd", StringComparison.Ordinal)) n++;
                return n;
            }
            catch { return -1; }
        }

        public string PageUrl(int index)
        {
            return St.Go2rtcApi.ToString() + "?screen=" + (index + 1) + "&screens=" + St.Screens + "&per=" + St.CamsPerScreen +
                   "&ctl=" + St.ControlPort + "&reload=" + St.ReloadMinutes;
        }

        public Uri SetupUrl { get { return new Uri(St.BridgeUrl, "setup.html"); } }

        /// <summary>Memeriksa berkas yang dibutuhkan. Mengembalikan pesan kesalahan, atau null jika siap.</summary>
        public string Preflight()
        {
            Go2rtcOwn = !Go2rtcUp(St);
            if (Go2rtcOwn && !File.Exists(St.Go2rtcExe)) return "go2rtc.exe tidak ditemukan:\n" + St.Go2rtcExe + "\n\nUnduh go2rtc_win64.zip dari github.com/AlexxIT/go2rtc/releases dan taruh go2rtc.exe di folder yang sama dengan CctvViewer.exe.";
            if (Go2rtcOwn && !File.Exists(St.Go2rtcConfig)) return "go2rtc.yaml tidak ditemukan:\n" + St.Go2rtcConfig;
            return null;
        }

        public void Start()
        {
            if (Go2rtcOwn)
                go2rtc = new ManagedProcess("go2rtc", St.Go2rtcExe, "-config \"" + St.Go2rtcConfig + "\"", Path.GetDirectoryName(St.Go2rtcConfig),
                    new FileLog(Path.Combine(St.DataDir, "go2rtc.log")), job);
            else Log.Info("go2rtc sudah berjalan di " + St.Go2rtcApi + "; dipakai apa adanya.");
            if (St.StartNvrBridge && File.Exists(St.NvrBridgeExe) && File.Exists(St.NvrBridgeConfig))
                bridge = new ManagedProcess("NvrBridge", St.NvrBridgeExe, "--config \"" + St.NvrBridgeConfig + "\"", Path.GetDirectoryName(St.NvrBridgeExe),
                    new FileLog(Path.Combine(St.DataDir, "nvrbridge.log")), job);
            else if (St.StartNvrBridge) Log.Warn("NvrBridge tidak dijalankan (berkas exe/config tidak ditemukan). Playback, PTZ, peta, dan registrasi NVR tidak aktif.");

            if (St.ControlPort > 0)
            {
                try
                {
                    ctl = new ControlServer(St.ControlPort, St.Go2rtcApi.GetLeftPart(UriPartial.Authority),
                        () => { var h = QuitRequested; if (h != null) h(); },
                        () => { var h = PanelRequested; if (h != null) h(); },
                        () => "{\"screens\":" + St.Screens + "}");
                    ctl.Start();
                }
                catch (Exception e) { Log.Warn("Server kontrol tidak bisa dibuka di port " + St.ControlPort + ": " + e.Message + ". Tombol Keluar/Panel di halaman tidak berfungsi."); ctl = null; }
            }

            if (go2rtc != null) go2rtc.Ensure();
            if (bridge != null) bridge.Ensure();
            loop = new Thread(Loop) { IsBackground = true, Name = "core" };
            loop.Start();
        }

        void Loop()
        {
            while (!stop.WaitOne(2000))
            {
                try
                {
                    if (go2rtc != null) go2rtc.Ensure();
                    if (bridge != null) bridge.Ensure();
                    if (!ready && Go2rtcUp(St))
                    {
                        ready = true;
                        int n = CameraCount(St);
                        Log.Info("go2rtc siap. Kamera terdaftar: " + n + " (kapasitas " + St.Screens * St.CamsPerScreen + ").");
                        if (n > St.Screens * St.CamsPerScreen) Log.Warn((n - St.Screens * St.CamsPerScreen) + " kamera tidak muat di " + St.Screens + " layar dan tidak ditampilkan.");
                        if (n == 0) Log.Warn("Belum ada kamera di go2rtc.yaml. Pakai Registrasi NVR atau isi go2rtc.yaml.");
                    }
                }
                catch (Exception e) { Log.Warn("Loop utama: " + e.Message); }
            }
        }

        public string StatusText()
        {
            var sb = new StringBuilder();
            sb.AppendLine("go2rtc: " + (go2rtc == null ? (Go2rtcUp(St) ? "berjalan (di luar aplikasi)" : "tidak berjalan") : go2rtc.Running ? "berjalan (pid " + go2rtc.Pid + ")" : "berhenti, akan dijalankan ulang"));
            sb.AppendLine("NvrBridge: " + (bridge == null ? "tidak dijalankan" : bridge.Running ? "berjalan (pid " + bridge.Pid + ")" : "berhenti, akan dijalankan ulang"));
            return sb.ToString();
        }

        public void Dispose()
        {
            stop.Set();
            if (ctl != null) ctl.Dispose();
            if (go2rtc != null) go2rtc.Stop();
            if (bridge != null) bridge.Stop();
            Native.CloseJob(job);
        }
    }
}
