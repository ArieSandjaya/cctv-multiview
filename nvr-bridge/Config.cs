using System;
using System.Collections.Generic;
using System.Globalization;
using System.IO;

namespace NvrBridge
{
    sealed class ConfigException : Exception
    {
        public ConfigException(string m) : base(m) { }
    }

    sealed class Camera
    {
        public int Id;
        public string Name;
    }

    /// <summary>Konfigurasi dari file teks Kunci=Nilai (baris # adalah komentar).</summary>
    sealed class AppConfig
    {
        public Uri NvrBase;
        public string NvrUser = "";
        public string NvrPassword = "";
        public string NvrAuthScheme = "Digest";
        public bool NvrAllowSelfSigned = true;
        public bool NvrTimeUtc = true;
        public List<Camera> Cameras = new List<Camera>();

        public string ListenPrefix = "http://localhost:8090/";
        public string BridgeUser = "";
        public string BridgePassword = "";
        public string Title = "Playback NVR";

        public string ClipKind = "MP4VO";
        public int MaxClipMinutes = 10;
        public int ClipTimeoutMinutes = 10;
        public int ClipRetentionMinutes = 60;
        public int MaxConcurrentDownloads = 2;

        // PTZ kamera Dahua (opsional) dan penyimpanan peta
        public string AllowOrigin = "";
        public string PtzUser = "";
        public string PtzPassword = "";
        public int PtzChannel = 0;
        public int PtzMaxMoveSeconds = 8;
        public Dictionary<string, string> PtzCameras = new Dictionary<string, string>(StringComparer.Ordinal);
        public string DataDir;

        public string ClipDir;
        public string WebDir;
        public bool Verbose;

        public static AppConfig Load(string path)
        {
            if (!File.Exists(path))
                throw new ConfigException("File konfigurasi tidak ditemukan: " + path);

            var kv = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            int n = 0;
            foreach (var raw in File.ReadAllLines(path))
            {
                n++;
                var line = raw.Trim();
                if (line.Length == 0 || line[0] == '#') continue;
                int eq = line.IndexOf('=');
                if (eq <= 0)
                    throw new ConfigException("Baris " + n + " tidak valid (harus Kunci=Nilai): " + line);
                kv[line.Substring(0, eq).Trim()] = line.Substring(eq + 1).Trim();
            }

            var c = new AppConfig();
            string baseDir = Path.GetDirectoryName(Path.GetFullPath(path)) ?? ".";

            string url = Get(kv, "NvrBaseUrl", "");
            Uri u;
            if (!Uri.TryCreate(url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https"))
                throw new ConfigException("NvrBaseUrl harus berupa alamat http:// atau https:// (contoh: https://192.168.1.50)");
            c.NvrBase = u;
            c.NvrUser = Get(kv, "NvrUser", "");
            c.NvrPassword = Get(kv, "NvrPassword", "");
            if (c.NvrUser.Length == 0) throw new ConfigException("NvrUser belum diisi.");

            c.NvrAuthScheme = Get(kv, "NvrAuthScheme", "Digest");
            if (!c.NvrAuthScheme.Equals("Digest", StringComparison.OrdinalIgnoreCase) &&
                !c.NvrAuthScheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                throw new ConfigException("NvrAuthScheme harus Digest atau Basic.");
            c.NvrAllowSelfSigned = Bool(kv, "NvrAllowSelfSigned", true);

            string tm = Get(kv, "NvrTimeMode", "Utc");
            if (tm.Equals("Utc", StringComparison.OrdinalIgnoreCase)) c.NvrTimeUtc = true;
            else if (tm.Equals("Local", StringComparison.OrdinalIgnoreCase)) c.NvrTimeUtc = false;
            else throw new ConfigException("NvrTimeMode harus Utc atau Local.");

            string cams = Get(kv, "Cameras", "");
            foreach (var part in cams.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string idText = (eq > 0 ? part.Substring(0, eq) : part).Trim();
                string name = eq > 0 ? part.Substring(eq + 1).Trim() : "";
                int id;
                if (!int.TryParse(idText, NumberStyles.None, CultureInfo.InvariantCulture, out id) || id < 1 || id > 9999)
                    throw new ConfigException("Cameras: nomor kamera tidak valid: '" + idText + "'");
                if (name.Length == 0) name = "Kamera " + id;
                c.Cameras.Add(new Camera { Id = id, Name = name });
            }
            if (c.Cameras.Count == 0)
                throw new ConfigException("Cameras belum diisi. Contoh: Cameras=1=Lobby Utama;2=Parkir B1");

            c.ListenPrefix = Get(kv, "ListenPrefix", c.ListenPrefix);
            if (!c.ListenPrefix.EndsWith("/")) throw new ConfigException("ListenPrefix harus diakhiri tanda / (contoh: http://localhost:8090/)");
            c.BridgeUser = Get(kv, "BridgeUser", "");
            c.BridgePassword = Get(kv, "BridgePassword", "");
            if ((c.BridgeUser.Length == 0) != (c.BridgePassword.Length == 0))
                throw new ConfigException("BridgeUser dan BridgePassword harus diisi keduanya, atau dikosongkan keduanya.");
            c.Title = Get(kv, "Title", c.Title);

            c.ClipKind = Get(kv, "ClipKind", c.ClipKind).ToUpperInvariant();
            if (c.ClipKind != "MP4" && c.ClipKind != "MP4VO")
                throw new ConfigException("ClipKind harus MP4 (dengan audio) atau MP4VO (tanpa audio).");
            c.MaxClipMinutes = Int(kv, "MaxClipMinutes", c.MaxClipMinutes, 1, 60);
            c.ClipTimeoutMinutes = Int(kv, "ClipTimeoutMinutes", c.ClipTimeoutMinutes, 1, 120);
            c.ClipRetentionMinutes = Int(kv, "ClipRetentionMinutes", c.ClipRetentionMinutes, 5, 1440);
            c.MaxConcurrentDownloads = Int(kv, "MaxConcurrentDownloads", c.MaxConcurrentDownloads, 1, 8);

            // Halaman multiview (go2rtc, port lain) memanggil API ini lintas-origin: tulis asalnya di sini.
            c.AllowOrigin = Get(kv, "AllowOrigin", "").TrimEnd('/');
            if (c.AllowOrigin.Length > 0)
            {
                Uri ou;
                if (!Uri.TryCreate(c.AllowOrigin, UriKind.Absolute, out ou) || (ou.Scheme != "http" && ou.Scheme != "https") || ou.AbsolutePath != "/")
                    throw new ConfigException("AllowOrigin harus berupa alamat tanpa path, contoh: http://192.168.1.10:1984");
                c.AllowOrigin = ou.GetLeftPart(UriPartial.Authority);
            }
            c.PtzUser = Get(kv, "PtzUser", "");
            c.PtzPassword = Get(kv, "PtzPassword", "");
            c.PtzChannel = Int(kv, "PtzChannel", c.PtzChannel, 0, 64);
            c.PtzMaxMoveSeconds = Int(kv, "PtzMaxMoveSeconds", c.PtzMaxMoveSeconds, 1, 60);
            foreach (var part in Get(kv, "PtzCameras", "").Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries))
            {
                int eq = part.IndexOf('=');
                string id = eq > 0 ? part.Substring(0, eq).Trim() : "";
                string host = eq > 0 ? part.Substring(eq + 1).Trim() : "";
                if (!System.Text.RegularExpressions.Regex.IsMatch(id, @"^[A-Za-z0-9_-]{1,32}$"))
                    throw new ConfigException("PtzCameras: nama stream tidak valid: '" + id + "' (huruf, angka, _ atau -).");
                Uri hu;
                if (host.Length == 0 || !Uri.TryCreate("http://" + host + "/", UriKind.Absolute, out hu) || hu.Host.Length == 0 ||
                    hu.AbsolutePath != "/" || hu.UserInfo.Length > 0)
                    throw new ConfigException("PtzCameras: alamat kamera tidak valid untuk '" + id + "': '" + host + "' (contoh: cam01=192.168.1.101).");
                c.PtzCameras[id] = hu.Authority;
            }
            if (c.PtzCameras.Count > 0 && c.PtzUser.Length == 0)
                throw new ConfigException("PtzCameras diisi tetapi PtzUser kosong.");

            c.DataDir = Path.GetFullPath(Path.Combine(baseDir, Get(kv, "DataDir", "data")));
            c.ClipDir = Path.GetFullPath(Path.Combine(baseDir, Get(kv, "ClipDir", "clips")));
            c.WebDir = Path.GetFullPath(Path.Combine(baseDir, Get(kv, "WebDir", "web")));
            c.Verbose = Bool(kv, "Verbose", false);
            return c;
        }

        static string Get(Dictionary<string, string> kv, string key, string def)
        {
            string v;
            return kv.TryGetValue(key, out v) && v.Length > 0 ? v : def;
        }

        static bool Bool(Dictionary<string, string> kv, string key, bool def)
        {
            string v;
            if (!kv.TryGetValue(key, out v) || v.Length == 0) return def;
            if (v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" || v.Equals("yes", StringComparison.OrdinalIgnoreCase)) return true;
            if (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" || v.Equals("no", StringComparison.OrdinalIgnoreCase)) return false;
            throw new ConfigException(key + " harus true atau false.");
        }

        static int Int(Dictionary<string, string> kv, string key, int def, int min, int max)
        {
            string v;
            if (!kv.TryGetValue(key, out v) || v.Length == 0) return def;
            int n;
            if (!int.TryParse(v, NumberStyles.None, CultureInfo.InvariantCulture, out n) || n < min || n > max)
                throw new ConfigException(key + " harus berupa angka " + min + "-" + max + ".");
            return n;
        }
    }
}
