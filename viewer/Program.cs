using System;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;
using System.Windows.Forms;

namespace CctvViewer
{
    static class Program
    {
        public const string AppName = "CCTV Viewer";

        [STAThread]
        static int Main(string[] args)
        {
            string baseDir = AppDomain.CurrentDomain.BaseDirectory;
            string cfgPath = Path.Combine(baseDir, "cctvviewer.config");
            bool stop = false, check = false; string autostart = null; int delay = 0;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) cfgPath = Path.GetFullPath(args[++i]);
                else if (args[i] == "--stop") stop = true;
                else if (args[i] == "--check") check = true;
                else if (args[i] == "--autostart" && i + 1 < args.Length) autostart = args[++i];
                else if (args[i] == "--delay" && i + 1 < args.Length) int.TryParse(args[++i], out delay);
                else if (args[i] == "--help" || args[i] == "-h" || args[i] == "/?")
                {
                    Native.Message("CctvViewer.exe [--config berkas] [--check] [--stop] [--autostart on|off]\n\n" +
                        "Tanpa opsi: menjalankan go2rtc dan NvrBridge, lalu membuka layar kamera di tiap monitor.\n" +
                        "--check      periksa monitor, WebView2, dan berkas tanpa menjalankan apa pun\n" +
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
                Application.EnableVisualStyles();
                Application.SetCompatibleTextRenderingDefault(false);
                if (check) return Check(st);
                if (delay > 0) Thread.Sleep(Math.Min(delay, 30) * 1000);     // dipakai saat "mulai ulang": menunggu proses lama selesai

                bool createdNew;
                using (var mutex = new Mutex(true, "Global\\CctvViewer.Wall", out createdNew))
                {
                    if (!createdNew) { Native.Message("CctvViewer sudah berjalan.\n\nBuka panel lewat ikon di area notifikasi, atau gerakkan mouse ke atas layar kamera lalu klik Panel/Keluar. Untuk menutup dari perintah: CctvViewer.exe --stop.", AppName, false); return 0; }
                    return Run(st, cfgPath);
                }
            }
            catch (Exception e)
            {
                Log.Error("Kesalahan fatal: " + e);
                Native.Message("CctvViewer berhenti karena kesalahan:\n\n" + e.Message + "\n\nRincian ada di " + Path.Combine(st.DataDir, "cctvviewer.log"), AppName, true);
                return 1;
            }
        }

        static int Run(Settings st, string cfgPath)
        {
            string wv = WebViewVersion();
            if (wv == null)
            {
                Native.Message("WebView2 Runtime belum terpasang di PC ini.\n\nUnduh \"Evergreen Bootstrapper\" dari https://developer.microsoft.com/microsoft-edge/webview2/ , pasang, lalu buka CctvViewer lagi.\n(Windows 11 dan Windows 10 yang rutin diperbarui biasanya sudah memilikinya.)", AppName, true);
                return 1;
            }
            Log.Info("=== CctvViewer mulai. Layar=" + st.Screens + ", kamera/layar=" + st.CamsPerScreen + ", WebView2=" + wv + " ===");
            using (var core = new Core(st))
            {
                string problem = core.Preflight();
                if (problem != null) { Native.Message(problem, AppName, true); return 1; }
                core.Start();
                Application.Run(new WallContext(core, cfgPath));
            }
            Log.Info("=== CctvViewer selesai ===");
            return 0;
        }

        public static string WebViewVersion()
        {
            try { return Microsoft.Web.WebView2.Core.CoreWebView2Environment.GetAvailableBrowserVersionString(); }
            catch { return null; }
        }

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

        public static bool AutostartEnabled()
        {
            try
            {
                using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run"))
                    return k != null && k.GetValue("CctvViewer") != null;
            }
            catch { return false; }
        }

        public static void SetAutostartQuiet(bool on)
        {
            using (var k = Microsoft.Win32.Registry.CurrentUser.OpenSubKey(@"Software\Microsoft\Windows\CurrentVersion\Run", true))
            {
                if (k == null) return;
                if (on) k.SetValue("CctvViewer", "\"" + System.Reflection.Assembly.GetExecutingAssembly().Location + "\"");
                else k.DeleteValue("CctvViewer", false);
            }
        }

        static int RequestStop(Settings st)
        {
            if (st.ControlPort == 0) { Native.Message("ControlPort=0, tidak ada yang bisa dihentikan lewat perintah ini.", AppName, true); return 1; }
            try { Core.Http(new Uri("http://127.0.0.1:" + st.ControlPort + "/quit"), "POST", 3000, "X-CctvViewer"); return 0; }
            catch { Native.Message("CctvViewer tidak sedang berjalan (atau port kontrol tidak menjawab).", AppName, true); return 1; }
        }

        static int Check(Settings st)
        {
            var sb = new StringBuilder();
            var mons = WallContext.Monitors(st);
            sb.AppendLine("Monitor terdeteksi: " + mons.Count + (st.ManualMonitors != null ? " (manual)" : ""));
            for (int i = 0; i < mons.Count; i++) sb.AppendLine("  Layar " + (i + 1) + ": " + mons[i] + (i >= st.Screens ? "   (tidak dipakai)" : ""));
            if (mons.Count < st.Screens) sb.AppendLine("  PERHATIAN: dibutuhkan " + st.Screens + " monitor. Pilih mode 'Extend' dan cek kabel HDMI.");
            string wv = WebViewVersion();
            sb.AppendLine("WebView2 Runtime: " + (wv ?? "TIDAK TERPASANG (unduh Evergreen Bootstrapper dari Microsoft)"));
            sb.AppendLine("go2rtc.exe: " + (File.Exists(st.Go2rtcExe) ? "ada" : "TIDAK ADA di " + st.Go2rtcExe));
            sb.AppendLine("go2rtc.yaml: " + (File.Exists(st.Go2rtcConfig) ? "ada" : "TIDAK ADA di " + st.Go2rtcConfig));
            sb.AppendLine("NvrBridge: " + (!st.StartNvrBridge ? "dimatikan di konfigurasi" : File.Exists(st.NvrBridgeExe) ? "ada" : "tidak ada (playback/PTZ/registrasi tidak aktif)"));
            if (Core.Go2rtcUp(st)) sb.AppendLine("go2rtc sedang berjalan. Kamera terdaftar: " + Core.CameraCount(st) + " dari kapasitas " + st.Screens * st.CamsPerScreen);
            else sb.AppendLine("go2rtc belum berjalan (normal jika CctvViewer belum dibuka).");
            sb.AppendLine("Folder data/log: " + st.DataDir);
            Native.Message(sb.ToString(), AppName + " - pemeriksaan", false);
            return 0;
        }
    }
}
