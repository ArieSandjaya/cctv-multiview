using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;
using System.Text;

namespace CctvViewer
{
    sealed class ConfigException : Exception { public ConfigException(string m) : base(m) { } }

    sealed class Settings
    {
        public string BaseDir;
        public string DataDir;
        public int Screens = 3;
        public int CamsPerScreen = 16;
        public List<Area> ManualMonitors;          // null = auto
        public int MonitorWaitSeconds = 60;
        public string WebViewArgs = "";
        public bool AllowCloseScreen = false;
        public Uri BridgeUrl = new Uri("http://127.0.0.1:8090/");
        public int ReloadMinutes = 360;
        public string Go2rtcExe, Go2rtcConfig;
        public Uri Go2rtcApi = new Uri("http://127.0.0.1:1984/");
        public bool StartNvrBridge = true;
        public string NvrBridgeExe, NvrBridgeConfig;
        public int ControlPort = 1985;
        public string PageTitle = "CCTV Multiview";
        public string PageMode = "mse,webrtc";
        public int DewarpMax = 6;
        public string WwwDir;
        public bool AllowLan = false;              // true = go2rtc dan NvrBridge menerima koneksi dari PC lain
        public int RtspPort = 0;                   // 0 = stream ulang RTSP dimatikan
        public int WebrtcPort = 8555;
        public string Go2rtcLog = "warn";
        public string Go2rtcOverride;              // berkas yang dibuat otomatis dari pengaturan di atas
        public string ConfigPath;

        public static Settings Load(string path, string baseDir)
        {
            if (!File.Exists(path)) throw new ConfigException("File konfigurasi tidak ditemukan: " + path);
            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                n++;
                string line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0) throw new ConfigException("Baris " + n + " bukan format Kunci=Nilai: " + line);
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }
            var s = new Settings { BaseDir = baseDir };
            s.Screens = Int(kv, "Screens", 3, 1, 8);
            s.CamsPerScreen = Int(kv, "CamsPerScreen", 16, 1, 36);
            s.MonitorWaitSeconds = Int(kv, "MonitorWaitSeconds", 60, 0, 600);
            s.ReloadMinutes = Int(kv, "ReloadMinutes", 360, 0, 10080);
            s.ControlPort = Int(kv, "ControlPort", 1985, 0, 65535);
            s.WebViewArgs = Str(kv, "WebViewArgs", "");
            s.AllowCloseScreen = Bool(kv, "AllowCloseScreen", false);
            s.StartNvrBridge = Bool(kv, "StartNvrBridge", true);
            s.PageTitle = Str(kv, "PageTitle", "CCTV Multiview");
            s.PageMode = Str(kv, "PageMode", "mse,webrtc");
            if (s.PageMode != "mse" && s.PageMode != "webrtc" && s.PageMode != "mse,webrtc" && s.PageMode != "webrtc,mse")
                throw new ConfigException("PageMode harus mse, webrtc, mse,webrtc atau webrtc,mse.");
            s.DewarpMax = Int(kv, "DewarpMax", 6, 0, 48);
            s.AllowLan = Bool(kv, "AllowLan", false);
            s.RtspPort = Int(kv, "Go2rtcRtspPort", 0, 0, 65535);
            s.WebrtcPort = Int(kv, "Go2rtcWebrtcPort", 8555, 1, 65535);
            s.Go2rtcLog = Str(kv, "Go2rtcLog", "warn");
            if (Array.IndexOf(new[] { "trace", "debug", "info", "warn", "error" }, s.Go2rtcLog) < 0)
                throw new ConfigException("Go2rtcLog harus trace, debug, info, warn atau error.");
            s.WwwDir = Rel(baseDir, Str(kv, "WwwDir", "www"));
            s.ConfigPath = path;
            s.Go2rtcExe = Rel(baseDir, Str(kv, "Go2rtcExe", "go2rtc.exe"));
            s.Go2rtcConfig = Rel(baseDir, Str(kv, "Go2rtcConfig", "go2rtc.yaml"));
            s.NvrBridgeExe = Rel(baseDir, Str(kv, "NvrBridgeExe", @"NvrBridge\NvrBridge.exe"));
            s.NvrBridgeConfig = Rel(baseDir, Str(kv, "NvrBridgeConfig", @"NvrBridge\nvrbridge.config"));

            Uri api;
            if (!Uri.TryCreate(Str(kv, "Go2rtcApi", "http://127.0.0.1:1984"), UriKind.Absolute, out api) || api.Scheme != "http")
                throw new ConfigException("Go2rtcApi harus berupa alamat http, contoh http://127.0.0.1:1984");
            s.Go2rtcApi = new Uri(api.GetLeftPart(UriPartial.Authority) + "/");

            Uri br;
            if (!Uri.TryCreate(Str(kv, "BridgeUrl", "http://127.0.0.1:8090"), UriKind.Absolute, out br) || br.Scheme != "http")
                throw new ConfigException("BridgeUrl harus berupa alamat http, contoh http://127.0.0.1:8090");
            s.BridgeUrl = new Uri(br.GetLeftPart(UriPartial.Authority) + "/");

            string mon = Str(kv, "Monitors", "auto");
            if (!mon.Equals("auto", StringComparison.OrdinalIgnoreCase))
            {
                s.ManualMonitors = new List<Area>();
                foreach (var part in mon.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
                {
                    var f = part.Split(',');
                    int x, y, w, h;
                    if (f.Length != 4 || !int.TryParse(f[0], NumberStyles.Integer, CultureInfo.InvariantCulture, out x) ||
                        !int.TryParse(f[1], NumberStyles.Integer, CultureInfo.InvariantCulture, out y) ||
                        !int.TryParse(f[2], NumberStyles.Integer, CultureInfo.InvariantCulture, out w) ||
                        !int.TryParse(f[3], NumberStyles.Integer, CultureInfo.InvariantCulture, out h) || w < 200 || h < 200)
                        throw new ConfigException("Monitors: format harus x,y,lebar,tinggi dipisah titik koma (salah di: " + part + ")");
                    s.ManualMonitors.Add(new Area(x, y, w, h));
                }
                if (s.ManualMonitors.Count == 0) throw new ConfigException("Monitors kosong. Isi 'auto' atau daftar x,y,lebar,tinggi.");
            }

            string data = Str(kv, "DataDir", "");
            s.DataDir = ResolveDataDir(baseDir, data);
            s.Go2rtcOverride = Path.Combine(s.DataDir, "go2rtc.settings.yaml");
            return s;
        }

        static string ResolveDataDir(string baseDir, string configured)
        {
            var candidates = new List<string>();
            if (configured.Length > 0) candidates.Add(Rel(baseDir, configured));
            else
            {
                candidates.Add(Path.Combine(baseDir, "data"));
                candidates.Add(Path.Combine(Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "CctvViewer"));
            }
            foreach (var c in candidates)
            {
                try
                {
                    Directory.CreateDirectory(c);
                    string probe = Path.Combine(c, ".w" + Guid.NewGuid().ToString("N"));
                    File.WriteAllText(probe, "x"); File.Delete(probe);
                    return c;
                }
                catch { }
            }
            throw new ConfigException("Tidak ada folder data yang bisa ditulis. Isi DataDir di cctvviewer.config.");
        }

        public string Raw(string key, string def)
        {
            var kv = ConfigWriter.Read(ConfigPath);
            string v; return kv.TryGetValue(key, out v) ? v : def;
        }

        static string Rel(string baseDir, string p) { return Path.GetFullPath(Path.IsPathRooted(p) ? p : Path.Combine(baseDir, p)); }
        static string Str(Dictionary<string, string> kv, string k, string d) { string v; return kv.TryGetValue(k, out v) ? v : d; }
        static bool Bool(Dictionary<string, string> kv, string k, bool d)
        {
            string v; if (!kv.TryGetValue(k, out v)) return d;
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1") return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0") return false;
            throw new ConfigException(k + " harus true atau false.");
        }
        static int Int(Dictionary<string, string> kv, string k, int d, int lo, int hi)
        {
            string v; if (!kv.TryGetValue(k, out v) || v.Length == 0) return d;
            int r;
            if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out r) || r < lo || r > hi)
                throw new ConfigException(k + " harus angka " + lo + "-" + hi + ".");
            return r;
        }
    }
}
