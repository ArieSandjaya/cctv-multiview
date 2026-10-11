using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;

namespace CctvViewer
{
    static class Program
    {
        const string AppName = "CCTV Viewer";
        static readonly ManualResetEvent quitEvent = new ManualResetEvent(false);

        [STAThread]
        static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = Path.Combine(baseDir, "cctvviewer.config");
            bool stop = false, check = false; string autostart = null;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) cfgPath = Path.GetFullPath(args[++i]);
                else if (args[i] == "--stop") stop = true;
                else if (args[i] == "--check") check = true;
                else if (args[i] == "--autostart" && i + 1 < args.Length) autostart = args[++i];
                else if (args[i] == "--help" || args[i] == "-h" || args[i] == "/?")
                {
                    Native.Message("CctvViewer.exe [--config berkas] [--check] [--stop] [--autostart on|off]\n\n" +
                        "Tanpa opsi: menjalankan go2rtc dan NvrBridge, lalu membuka layar kamera di tiap monitor.\n" +
                        "--check      periksa monitor, browser, go2rtc, dan konfigurasi tanpa menjalankan apa pun\n" +
                        "--stop       menutup CctvViewer yang sedang berjalan\n" +
                        "--autostart  on/off: jalankan otomatis saat Windows login", AppName, false);
                    return 0;
                }
            }

            Settings st;
            try { st = Settings.Load(cfgPath, baseDir); }
            catch (ConfigException e) { Native.Message("Konfigurasi bermasalah:\n\n" + e.Message + "\n\nBerkas: " + cfgPath, AppName, true); return 2; }
            catch (Exception e) { Native.Message("Tidak bisa membaca konfigurasi:\n\n" + e.Message, AppName, true); return 2; }
            Log.Init(Path.Combine(st.DataDir, "cctvviewer.log"));

            try
            {
                if (autostart != null) return SetAutostart(autostart);
                if (stop) return RequestStop(st);
                if (check) return Check(st);
                return Run(st);
            }
            catch (Exception e)
            {
                Log.Error("Kesalahan fatal: " + e);
                Native.Message("CctvViewer berhenti karena kesalahan:\n\n" + e.Message + "\n\nRincian ada di " + Path.Combine(st.DataDir, "cctvviewer.log"), AppName, true);
                return 1;
            }
        }

        // ---------- utilitas ----------
        static string FindBrowser(string configured)
        {
            if (!configured.Equals("auto", StringComparison.OrdinalIgnoreCase))
                return File.Exists(configured) ? Path.GetFullPath(configured) : null;
            string pf = Environment.GetEnvironmentVariable("ProgramFiles") ?? @"C:\Program Files";
            string pf86 = Environment.GetEnvironmentVariable("ProgramFiles(x86)") ?? @"C:\Program Files (x86)";
            string lad = Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData);
            foreach (var c in new[]
            {
                Path.Combine(pf, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(lad, @"Google\Chrome\Application\chrome.exe"),
                Path.Combine(pf86, @"Microsoft\Edge\Application\msedge.exe"),
                Path.Combine(pf, @"Microsoft\Edge\Application\msedge.exe"),
            }) if (File.Exists(c)) return c;
            return null;
        }

        static List<Area> GetMonitors(Settings st) { return st.ManualMonitors ?? Native.Monitors(); }

        static string Http(Uri url, string method, int timeoutMs, string header = null)
        {
            var rq = (HttpWebRequest)WebRequest.Create(url);
            rq.Method = method; rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs; rq.Proxy = null;
            if (header != null) rq.Headers[header] = "1";
            if (method == "POST") rq.ContentLength = 0;
            using (var rs = (HttpWebResponse)rq.GetResponse())
            using (var sr = new StreamReader(rs.GetResponseStream(), Encoding.UTF8))
                return sr.ReadToEnd();
        }

        static bool Go2rtcUp(Settings st)
        {
            try { Http(new Uri(st.Go2rtcApi, "api"), "GET", 1500); return true; } catch { return false; }
        }

        /// <summary>Nama stream tingkat atas dari JSON /api/streams (tanpa pustaka JSON).</summary>
        static List<string> StreamNames(string json)
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

        static int CameraCount(Settings st, out string err)
        {
            err = null;
            try
            {
                var names = StreamNames(Http(new Uri(st.Go2rtcApi, "api/streams"), "GET", 3000));
                int n = 0; foreach (var k in names) if (!k.EndsWith("_hd", StringComparison.Ordinal)) n++;
                return n;
            }
            catch (Exception e) { err = e.Message; return -1; }
        }

        // ---------- perintah tambahan ----------
        static int SetAutostart(string mode)
        {
            const string key = @"Software\Microsoft\Windows\CurrentVersion\Run";
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(key, true))
            {
                if (k == null) { Native.Message("Tidak bisa membuka registry Run.", AppName, true); return 1; }
                if (mode.Equals("on", StringComparison.OrdinalIgnoreCase))
                {
                    k.SetValue("CctvViewer", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                    Native.Message("CctvViewer akan berjalan otomatis saat Windows login (akun ini).", AppName, false);
                }
                else if (mode.Equals("off", StringComparison.OrdinalIgnoreCase))
                {
                    k.DeleteValue("CctvViewer", false);
                    Native.Message("Jalan otomatis dimatikan.", AppName, false);
                }
                else { Native.Message("Gunakan --autostart on atau --autostart off.", AppName, true); return 2; }
            }
            return 0;
        }

        static int RequestStop(Settings st)
        {
            if (st.ControlPort == 0) { Native.Message("ControlPort=0, tidak ada yang bisa dihentikan lewat perintah ini.", AppName, true); return 1; }
            try { Http(new Uri("http://127.0.0.1:" + st.ControlPort + "/quit"), "POST", 3000, "X-CctvViewer"); return 0; }
            catch { Native.Message("CctvViewer tidak sedang berjalan (atau port kontrol tidak menjawab).", AppName, true); return 1; }
        }

        static int Check(Settings st)
        {
            var sb = new StringBuilder();
            var mons = GetMonitors(st);
            sb.AppendLine("Monitor terdeteksi: " + mons.Count + (st.ManualMonitors != null ? " (manual)" : ""));
            for (int i = 0; i < mons.Count; i++) sb.AppendLine("  Layar " + (i + 1) + ": " + mons[i] + (i >= st.Screens ? "   (tidak dipakai)" : ""));
            if (mons.Count < st.Screens) sb.AppendLine("  PERHATIAN: dibutuhkan " + st.Screens + " monitor. Pilih mode 'Extend' dan cek kabel HDMI.");
            string br = FindBrowser(st.Browser);
            sb.AppendLine("Browser: " + (br ?? "TIDAK DITEMUKAN (pasang Google Chrome atau isi Browser= di cctvviewer.config)"));
            sb.AppendLine("go2rtc.exe: " + (File.Exists(st.Go2rtcExe) ? "ada" : "TIDAK ADA di " + st.Go2rtcExe));
            sb.AppendLine("go2rtc.yaml: " + (File.Exists(st.Go2rtcConfig) ? "ada" : "TIDAK ADA di " + st.Go2rtcConfig));
            sb.AppendLine("NvrBridge: " + (!st.StartNvrBridge ? "dimatikan di konfigurasi" : File.Exists(st.NvrBridgeExe) ? "ada" : "tidak ada (playback/PTZ/peta tidak aktif)"));
            if (Go2rtcUp(st))
            {
                string err; int n = CameraCount(st, out err);
                sb.AppendLine("go2rtc sedang berjalan. Kamera terdaftar: " + (n >= 0 ? n.ToString() : "?") + " dari kapasitas " + st.Screens * st.CamsPerScreen);
            }
            else sb.AppendLine("go2rtc belum berjalan (normal jika CctvViewer belum dibuka).");
            sb.AppendLine("Folder data/log: " + st.DataDir);
            Native.Message(sb.ToString(), AppName + " - pemeriksaan", false);
            return 0;
        }

        // ---------- layar ----------
        sealed class Screen
        {
            public int Index;                     // 0-based
            public ManagedProcess Proc;
            public DateTime LastPlace = DateTime.MinValue;
            public bool Placed;
        }

        static string PageUrl(Settings st, int index)
        {
            return new Uri(st.Go2rtcApi, "").ToString() + "?screen=" + (index + 1) + "&screens=" + st.Screens + "&per=" + st.CamsPerScreen +
                   "&ctl=" + st.ControlPort + "&reload=" + st.ReloadMinutes;
        }

        static string BrowserArgs(Settings st, string browser, int index, Area m, string profile)
        {
            var a = new StringBuilder();
            a.Append("--user-data-dir=\"" + profile + "\" ");
            a.Append("--kiosk --new-window ");
            a.Append("--window-position=" + m.X + "," + m.Y + " --window-size=" + m.W + "," + m.H + " ");
            a.Append("--no-first-run --no-default-browser-check --disable-infobars --noerrdialogs ");
            a.Append("--disable-session-crashed-bubble --hide-crash-restore-bubble --disable-sync --disable-translate ");
            a.Append("--disable-features=CalculateNativeWinOcclusion,Translate ");
            a.Append("--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows ");
            a.Append("--autoplay-policy=no-user-gesture-required --overscroll-history-navigation=0 --disable-pinch ");
            if (browser.EndsWith("msedge.exe", StringComparison.OrdinalIgnoreCase)) a.Append("--edge-kiosk-type=fullscreen ");
            if (st.BrowserArgs.Length > 0) a.Append(st.BrowserArgs + " ");
            a.Append("\"" + PageUrl(st, index) + "\"");
            return a.ToString();
        }

        // ---------- jalankan ----------
        static int Run(Settings st)
        {
            bool createdNew;
            using (var mutex = new Mutex(true, "Global\\CctvViewer.Wall", out createdNew))
            {
                if (!createdNew) { Native.Message("CctvViewer sudah berjalan.\n\nUntuk menutupnya gunakan tombol \"Keluar\" di header layar (gerakkan mouse ke atas layar), atau jalankan CctvViewer.exe --stop.", AppName, false); return 0; }

                string browser = FindBrowser(st.Browser);
                if (browser == null) { Native.Message("Google Chrome atau Microsoft Edge tidak ditemukan.\n\nPasang Google Chrome, atau isi Browser= di cctvviewer.config dengan jalur lengkap.", AppName, true); return 1; }
                bool go2rtcOwn = !Go2rtcUp(st);
                if (go2rtcOwn && !File.Exists(st.Go2rtcExe)) { Native.Message("go2rtc.exe tidak ditemukan:\n" + st.Go2rtcExe + "\n\nUnduh go2rtc_win64.zip dari github.com/AlexxIT/go2rtc/releases dan taruh go2rtc.exe di folder yang sama dengan CctvViewer.exe.", AppName, true); return 1; }
                if (go2rtcOwn && !File.Exists(st.Go2rtcConfig)) { Native.Message("go2rtc.yaml tidak ditemukan:\n" + st.Go2rtcConfig, AppName, true); return 1; }

                Log.Info("=== CctvViewer mulai. Layar=" + st.Screens + ", kamera/layar=" + st.CamsPerScreen + ", browser=" + browser + " ===");
                IntPtr job = Native.CreateKillOnCloseJob();
                if (job == IntPtr.Zero) Log.Warn("Job object gagal dibuat; proses anak bisa tertinggal jika CctvViewer dihentikan paksa.");

                var procs = new List<ManagedProcess>();
                ManagedProcess go2rtc = null, bridge = null;
                if (go2rtcOwn)
                {
                    go2rtc = new ManagedProcess("go2rtc", st.Go2rtcExe, "-config \"" + st.Go2rtcConfig + "\"", Path.GetDirectoryName(st.Go2rtcConfig),
                        new FileLog(Path.Combine(st.DataDir, "go2rtc.log")), job);
                    procs.Add(go2rtc);
                }
                else Log.Info("go2rtc sudah berjalan di " + st.Go2rtcApi + "; dipakai apa adanya.");
                if (st.StartNvrBridge && File.Exists(st.NvrBridgeExe) && File.Exists(st.NvrBridgeConfig))
                {
                    bridge = new ManagedProcess("NvrBridge", st.NvrBridgeExe, "--config \"" + st.NvrBridgeConfig + "\"", Path.GetDirectoryName(st.NvrBridgeExe),
                        new FileLog(Path.Combine(st.DataDir, "nvrbridge.log")), job);
                    procs.Add(bridge);
                }
                else if (st.StartNvrBridge) Log.Warn("NvrBridge tidak dijalankan (berkas exe/config tidak ditemukan). Playback, PTZ, dan peta tidak aktif.");

                ControlServer ctl = null;
                if (st.ControlPort > 0)
                {
                    try
                    {
                        string origin = st.Go2rtcApi.GetLeftPart(UriPartial.Authority);
                        ctl = new ControlServer(st.ControlPort, origin, () => quitEvent.Set(), () => "{\"screens\":" + st.Screens + "}");
                        ctl.Start();
                    }
                    catch (Exception e) { Log.Warn("Server kontrol tidak bisa dibuka di port " + st.ControlPort + ": " + e.Message + ". Tombol Keluar di halaman tidak berfungsi."); ctl = null; }
                }

                foreach (var p in procs) p.Ensure();

                // Tunggu go2rtc siap
                var t0 = DateTime.UtcNow;
                while (!Go2rtcUp(st) && (DateTime.UtcNow - t0).TotalSeconds < 30 && !quitEvent.WaitOne(500)) { foreach (var p in procs) p.Ensure(); }
                if (Go2rtcUp(st))
                {
                    string err; int n = CameraCount(st, out err);
                    Log.Info("go2rtc siap. Kamera terdaftar: " + n + " (kapasitas " + st.Screens * st.CamsPerScreen + ").");
                    if (n > st.Screens * st.CamsPerScreen) Log.Warn((n - st.Screens * st.CamsPerScreen) + " kamera tidak muat di " + st.Screens + " layar dan tidak ditampilkan.");
                    if (n == 0) Log.Warn("Belum ada kamera di go2rtc.yaml.");
                }
                else Log.Warn("go2rtc belum menjawab setelah 30 detik; layar tetap dibuka dan akan mencoba lagi sendiri.");

                // Tunggu monitor (HDMI kadang terlambat terdeteksi setelah boot)
                var tm = DateTime.UtcNow;
                while (GetMonitors(st).Count < st.Screens && (DateTime.UtcNow - tm).TotalSeconds < st.MonitorWaitSeconds && !quitEvent.WaitOne(1000)) { }
                var mons = GetMonitors(st);
                Log.Info("Monitor terdeteksi: " + mons.Count + " -> " + string.Join(" | ", mons.ConvertAll(m => m.ToString()).ToArray()));
                if (mons.Count < st.Screens) Log.Warn("Hanya " + mons.Count + " dari " + st.Screens + " monitor. Layar yang lain dibuka otomatis begitu monitornya terdeteksi.");

                var screens = new List<Screen>();
                int tick = 0;
                while (!quitEvent.WaitOne(2000))
                {
                    foreach (var p in procs) p.Ensure();
                    mons = GetMonitors(st);
                    int want = Math.Min(st.Screens, mons.Count);

                    for (int i = 0; i < want; i++)
                    {
                        Screen s = screens.Find(x => x.Index == i);
                        if (s == null)
                        {
                            string profile = Path.Combine(st.DataDir, "profile-" + (i + 1));
                            Directory.CreateDirectory(profile);
                            s = new Screen { Index = i, Proc = new ManagedProcess("Layar " + (i + 1), browser, BrowserArgs(st, browser, i, mons[i], profile), Path.GetDirectoryName(browser), null, job) };
                            screens.Add(s);
                        }
                        if (!st.ReopenClosedScreens && s.Placed && !s.Proc.Running)
                            continue;                                           // ditutup sengaja; tidak dibuka lagi
                        s.Proc.Ensure();
                    }

                    // Keluar sendiri jika opsi buka-ulang mati dan semua layar sudah ditutup
                    if (!st.ReopenClosedScreens && screens.Count > 0 && screens.TrueForAll(x => x.Placed && !x.Proc.Running)) { Log.Info("Semua layar ditutup. Keluar."); break; }

                    // Pastikan tiap jendela menutupi monitornya (juga setelah monitor dicabut/dipasang lagi)
                    if (tick % 3 == 0)
                    {
                        foreach (var s in screens)
                        {
                            if (!s.Proc.Running || s.Index >= mons.Count) continue;
                            IntPtr h = Native.FindBrowserWindow(s.Proc.Pid);
                            if (h == IntPtr.Zero) continue;
                            s.Placed = true;
                            if ((DateTime.UtcNow - s.LastPlace).TotalSeconds < 20) continue;
                            Area before;
                            if (Native.Place(h, mons[s.Index], 8, out before))
                            {
                                s.LastPlace = DateTime.UtcNow;
                                Log.Info("Layar " + (s.Index + 1) + ": jendela dipindah dari [" + before + "] ke [" + mons[s.Index] + "]");
                            }
                        }
                    }
                    tick++;
                }

                Log.Info("Menutup semua proses...");
                foreach (var s in screens) s.Proc.Stop();
                foreach (var p in procs) p.Stop();
                if (ctl != null) ctl.Dispose();
                Native.CloseJob(job);
                Log.Info("=== CctvViewer selesai ===");
                return 0;
            }
        }
    }
}
