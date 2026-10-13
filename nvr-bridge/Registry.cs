using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Text.RegularExpressions;

namespace NvrBridge
{
    /// <summary>Satu NVR terdaftar. Kamera di bawahnya memakai akun kamera (CamUser/CamPass) milik NVR ini untuk URL RTSP otomatis.</summary>
    sealed class RegNvr
    {
        public string Id = "nvr1";
        public string Name = "";
        public string Url = "", User = "", Pass = "", Scheme = "Digest";
        public bool SelfSigned = true;
        public string CamUser = "", CamPass = "";
    }

    sealed class RegCam
    {
        public int Num;                    // nomor tampil global (unik): menjadi nama stream camNN dan nomor playback
        public string Nvr = "";            // id NVR ("" = tidak terhubung ke NVR, tanpa playback)
        public int Cam;                    // nomor kamera (channel) di NVR tersebut
        public string Name = "";
        public string Ip = "";             // host[:port] kamera
        public string Brand = "dahua";     // dahua | ipro | custom
        public string Rtsp = "";           // jika diisi, menggantikan pola otomatis
        public string RtspHd = "";
        public bool Ptz;
        public bool Enabled = true;
        public string Probe = "manual";    // rec | none | error | manual
        public string Note = "";

        // Kamera fisheye yang mengirim lingkaran mentah: halaman multiview men-dewarp-nya (WebGL).
        public bool Fisheye;
        public string FeMount = "ceiling";   // ceiling | wall
        public int FeFov = 180;              // derajat
        public string FeTile = "view";       // view | raw
        public string FeMode = "view";       // view | pano | raw (tampilan awal saat diperbesar)
        public bool FeMirror;
        public string FeCx = "", FeCy = "", FeR = "";    // pecahan frame 0-1; kosong = otomatis (tengah)

        public string StreamId { get { return "cam" + Num.ToString("00"); } }
    }

    /// <summary>Daftar NVR dan kamera hasil registrasi. Disimpan di data\registry.json.</summary>
    sealed class Registry
    {
        public List<RegNvr> Nvrs = new List<RegNvr>();
        public List<RegCam> Cams = new List<RegCam>();

        static readonly Regex HostRx = new Regex(@"^[A-Za-z0-9.\-]{1,100}(:\d{1,5})?$", RegexOptions.Compiled);
        static readonly Regex RtspRx = new Regex(@"^rtsp://[^\s""'\\]{3,400}$", RegexOptions.Compiled);
        static readonly Regex IdRx = new Regex(@"^[a-z0-9_-]{1,20}$", RegexOptions.Compiled);

        public static string PathFor(AppConfig cfg) { return Path.Combine(cfg.DataDir, "registry.json"); }

        public RegNvr NvrOf(RegCam c) { return Nvrs.FirstOrDefault(n => n.Id == c.Nvr); }

        public static Registry Load(AppConfig cfg)
        {
            string p = PathFor(cfg);
            if (!File.Exists(p)) return null;
            try { return FromJson(MiniJson.Obj(MiniJson.Parse(File.ReadAllText(p, Encoding.UTF8))), null); }
            catch (Exception e) { Log.Warn("registry.json tidak bisa dibaca: " + e.Message); return null; }
        }

        /// <summary>Registry dari nvrbridge.config lama (NvrBaseUrl/Cameras), dipakai sebagai nilai awal jika registry.json belum ada.</summary>
        public static Registry FromConfig(AppConfig cfg)
        {
            var r = new Registry();
            foreach (var n in cfg.Nvrs)
                r.Nvrs.Add(new RegNvr { Id = n.Id, Name = n.Name, Url = n.Base.GetLeftPart(UriPartial.Authority), User = n.User, Pass = n.Pass, Scheme = n.Scheme, SelfSigned = n.SelfSigned });
            foreach (var c in cfg.Cameras) r.Cams.Add(new RegCam { Num = c.Id, Nvr = c.NvrId, Cam = c.Channel, Name = c.Name });
            return r;
        }

        /// <summary>Membangun dari JSON. Password kosong = pertahankan dari "keep" (NVR dengan id dan user yang sama).
        /// Bentuk lama ({"nvr":{...},"camCreds":{...}}) dibaca sebagai satu NVR "nvr1".</summary>
        public static Registry FromJson(Dictionary<string, object> d, Registry keep)
        {
            if (d == null) throw new ArgumentException("Data registrasi kosong.");
            var r = new Registry();
            var nl = MiniJson.List(d, "nvrs");
            if (nl == null && d.ContainsKey("nvr"))
            {
                var nvr = MiniJson.Obj(d["nvr"]) ?? new Dictionary<string, object>();
                var cc = MiniJson.Obj(d.ContainsKey("camCreds") ? d["camCreds"] : null) ?? new Dictionary<string, object>();
                var one = new Dictionary<string, object>(nvr);
                one["id"] = "nvr1"; one["camUser"] = MiniJson.Str(cc, "user"); one["camPass"] = MiniJson.Str(cc, "pass");
                if (MiniJson.Str(nvr, "url").Trim().Length > 0 || MiniJson.Str(nvr, "user").Trim().Length > 0) nl = new List<object> { one };
            }
            if (nl == null) nl = new List<object>();
            if (nl.Count > 16) throw new ArgumentException("Terlalu banyak NVR (maks 16).");
            var ids = new HashSet<string>();
            foreach (var o in nl)
            {
                var n = MiniJson.Obj(o); if (n == null) continue;
                var rn = new RegNvr
                {
                    Id = MiniJson.Str(n, "id").Trim().ToLowerInvariant(), Name = Clean(MiniJson.Str(n, "name"), 40),
                    Url = MiniJson.Str(n, "url").Trim(), User = MiniJson.Str(n, "user").Trim(), Pass = MiniJson.Str(n, "pass"),
                    Scheme = MiniJson.Str(n, "scheme", "Digest"), SelfSigned = MiniJson.Bool(n, "selfSigned", true),
                    CamUser = MiniJson.Str(n, "camUser").Trim(), CamPass = MiniJson.Str(n, "camPass"),
                };
                if (rn.Id.Length == 0) { int k = 1; while (ids.Contains("nvr" + k)) k++; rn.Id = "nvr" + k; }
                if (!IdRx.IsMatch(rn.Id)) throw new ArgumentException("Id NVR tidak valid: '" + rn.Id + "'.");
                if (!ids.Add(rn.Id)) throw new ArgumentException("Id NVR ganda: " + rn.Id + ".");
                if (rn.Name.Length == 0) rn.Name = "NVR " + rn.Id.Replace("nvr", "");
                var old = keep != null ? keep.Nvrs.FirstOrDefault(x => x.Id == rn.Id) : null;
                if (old != null)
                {
                    if (rn.Pass.Length == 0 && rn.User == old.User) rn.Pass = old.Pass;
                    if (rn.CamPass.Length == 0 && rn.CamUser == old.CamUser) rn.CamPass = old.CamPass;
                }
                if (rn.Url.Length > 0)
                {
                    Uri u;
                    if (!Uri.TryCreate(rn.Url, UriKind.Absolute, out u) || (u.Scheme != "http" && u.Scheme != "https") || u.AbsolutePath != "/" || u.UserInfo.Length > 0)
                        throw new ArgumentException("Alamat NVR '" + rn.Name + "' harus seperti https://192.168.1.50 (tanpa path).");
                    rn.Url = u.GetLeftPart(UriPartial.Authority);
                }
                if (!rn.Scheme.Equals("Digest", StringComparison.OrdinalIgnoreCase) && !rn.Scheme.Equals("Basic", StringComparison.OrdinalIgnoreCase))
                    throw new ArgumentException("Skema login NVR '" + rn.Name + "' harus Digest atau Basic.");
                rn.Scheme = char.ToUpperInvariant(rn.Scheme[0]) + rn.Scheme.Substring(1).ToLowerInvariant();
                r.Nvrs.Add(rn);
            }

            var list = MiniJson.List(d, "cams") ?? new List<object>();
            if (list.Count > 256) throw new ArgumentException("Terlalu banyak kamera (maks 256).");
            var nums = new HashSet<int>();
            var pairs = new HashSet<string>();
            foreach (var o in list)
            {
                var c = MiniJson.Obj(o);
                if (c == null) continue;
                int cam = MiniJson.Int(c, "cam", 0);
                var rc = new RegCam
                {
                    Cam = cam,
                    Num = MiniJson.Int(c, "num", cam),
                    Nvr = c.ContainsKey("nvr") ? MiniJson.Str(c, "nvr").Trim().ToLowerInvariant() : (r.Nvrs.Count > 0 ? r.Nvrs[0].Id : ""),
                    Name = Clean(MiniJson.Str(c, "name"), 60),
                    Ip = MiniJson.Str(c, "ip").Trim(),
                    Brand = MiniJson.Str(c, "brand", "dahua").ToLowerInvariant(),
                    Rtsp = MiniJson.Str(c, "rtsp").Trim(),
                    RtspHd = MiniJson.Str(c, "rtspHd").Trim(),
                    Ptz = MiniJson.Bool(c, "ptz", false),
                    Enabled = MiniJson.Bool(c, "enabled", true),
                    Probe = MiniJson.Str(c, "probe", "manual"),
                    Note = Clean(MiniJson.Str(c, "note"), 200),
                };
                if (rc.Num < 1 || rc.Num > 999) throw new ArgumentException("Nomor tampil kamera harus 1-999 (kamera '" + rc.Name + "').");
                if (!nums.Add(rc.Num)) throw new ArgumentException("Nomor tampil ganda: " + rc.Num + ". Setiap kamera butuh nomor tampil (No.) yang unik.");
                if (rc.Nvr.Length > 0 && !r.Nvrs.Any(x => x.Id == rc.Nvr)) throw new ArgumentException("Kamera " + rc.Num + " merujuk NVR yang tidak ada: '" + rc.Nvr + "'.");
                if (rc.Cam < 0 || rc.Cam > 999) throw new ArgumentException("Nomor CAM di NVR harus 0-999 (kamera " + rc.Num + ").");
                if (rc.Nvr.Length > 0 && !pairs.Add(rc.Nvr + "|" + rc.Cam)) throw new ArgumentException("Nomor CAM " + rc.Cam + " dipakai dua kali pada NVR yang sama.");
                if (rc.Brand != "dahua" && rc.Brand != "ipro" && rc.Brand != "custom") throw new ArgumentException("Merek kamera " + rc.Num + " tidak dikenal.");
                if (rc.Probe != "rec" && rc.Probe != "none" && rc.Probe != "error" && rc.Probe != "manual") rc.Probe = "manual";
                if (rc.Name.Length == 0) rc.Name = "Kamera " + rc.Num;
                if (rc.Ip.Length > 0 && !HostRx.IsMatch(rc.Ip)) throw new ArgumentException("Alamat kamera " + rc.Num + " tidak valid (contoh 192.168.1.101).");
                if (rc.Rtsp.Length > 0 && !RtspRx.IsMatch(rc.Rtsp)) throw new ArgumentException("URL RTSP kamera " + rc.Num + " harus diawali rtsp:// tanpa spasi.");
                if (rc.RtspHd.Length > 0 && !RtspRx.IsMatch(rc.RtspHd)) throw new ArgumentException("URL RTSP utama kamera " + rc.Num + " harus diawali rtsp:// tanpa spasi.");
                if (rc.Ptz && rc.Ip.Length == 0) throw new ArgumentException("Kamera " + rc.Num + ": PTZ butuh alamat IP.");
                object feo;
                if (c.TryGetValue("fe", out feo)) ParseFe(rc, feo);
                else if (keep != null)
                {
                    // Klien yang tidak mengirim "fe" tidak boleh menghapus pengaturan fisheye yang sudah ada.
                    var old = keep.Cams.Find(x => x.Num == rc.Num);
                    if (old != null)
                    {
                        rc.Fisheye = old.Fisheye; rc.FeMount = old.FeMount; rc.FeFov = old.FeFov; rc.FeTile = old.FeTile; rc.FeMode = old.FeMode;
                        rc.FeMirror = old.FeMirror; rc.FeCx = old.FeCx; rc.FeCy = old.FeCy; rc.FeR = old.FeR;
                    }
                }
                r.Cams.Add(rc);
            }
            // Urutan baris dipertahankan: itu urutan tampil kamera di layar.
            return r;
        }

        static void ParseFe(RegCam rc, object feo)
        {
            var fe = MiniJson.Obj(feo);
            if (fe == null) { rc.Fisheye = false; return; }          // null/bukan objek = bukan fisheye
            rc.Fisheye = true;
            rc.FeMount = MiniJson.Str(fe, "mount", "ceiling").ToLowerInvariant();
            if (rc.FeMount != "ceiling" && rc.FeMount != "wall") throw new ArgumentException("Fisheye kamera " + rc.Cam + ": pemasangan harus ceiling atau wall.");
            rc.FeFov = MiniJson.Int(fe, "fov", 180);
            if (rc.FeFov < 90 || rc.FeFov > 360) throw new ArgumentException("Fisheye kamera " + rc.Cam + ": sudut pandang harus 90-360 derajat.");
            rc.FeTile = MiniJson.Str(fe, "tile", "view").ToLowerInvariant();
            if (rc.FeTile != "view" && rc.FeTile != "raw") throw new ArgumentException("Fisheye kamera " + rc.Cam + ": tampilan tile harus view atau raw.");
            rc.FeMode = MiniJson.Str(fe, "mode", "view").ToLowerInvariant();
            if (rc.FeMode != "view" && rc.FeMode != "pano" && rc.FeMode != "raw") throw new ArgumentException("Fisheye kamera " + rc.Cam + ": mode harus view, pano, atau raw.");
            rc.FeMirror = MiniJson.Bool(fe, "mirror", false);
            rc.FeCx = Frac(fe, "cx", rc.Cam); rc.FeCy = Frac(fe, "cy", rc.Cam); rc.FeR = Frac(fe, "r", rc.Cam);
        }

        static string Frac(Dictionary<string, object> d, string k, int cam)
        {
            object v;
            if (!d.TryGetValue(k, out v) || v == null) return "";
            double x;
            if (v is double) x = (double)v;
            else if (v is string && ((string)v).Trim().Length == 0) return "";
            else if (!(v is string) || !double.TryParse((string)v, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out x))
                throw new ArgumentException("Fisheye kamera " + cam + ": " + k + " harus angka 0-1.");
            if (x < 0 || x > 1 || double.IsNaN(x)) throw new ArgumentException("Fisheye kamera " + cam + ": " + k + " harus angka 0-1.");
            return x.ToString("0.####", System.Globalization.CultureInfo.InvariantCulture);
        }

        static string FeJson(RegCam c)
        {
            if (!c.Fisheye) return "null";
            var sb = new StringBuilder();
            sb.Append("{\"mount\":").Append(Json.Q(c.FeMount)).Append(",\"fov\":").Append(c.FeFov).Append(",\"tile\":").Append(Json.Q(c.FeTile))
              .Append(",\"mode\":").Append(Json.Q(c.FeMode)).Append(",\"mirror\":").Append(c.FeMirror ? "true" : "false");
            if (c.FeCx.Length > 0) sb.Append(",\"cx\":").Append(c.FeCx);
            if (c.FeCy.Length > 0) sb.Append(",\"cy\":").Append(c.FeCy);
            if (c.FeR.Length > 0) sb.Append(",\"r\":").Append(c.FeR);
            return sb.Append("}").ToString();
        }

        static string Clean(string s, int max)
        {
            s = new string((s ?? "").Where(ch => ch >= 0x20 && ch != 0x7f).ToArray()).Trim();
            return s.Length > max ? s.Substring(0, max) : s;
        }

        // ---------- URL RTSP ----------
        static string Cred(RegNvr n)
        {
            if (n == null || n.CamUser.Length == 0) return "";
            return Uri.EscapeDataString(n.CamUser) + (n.CamPass.Length > 0 ? ":" + Uri.EscapeDataString(n.CamPass) : "") + "@";
        }

        public string RtspSub(RegCam c)
        {
            if (c.Rtsp.Length > 0) return c.Rtsp;
            if (c.Ip.Length == 0 || c.Brand == "custom") return "";
            string host = c.Ip.Contains(":") ? c.Ip : c.Ip + ":554";
            return c.Brand == "ipro"
                ? "rtsp://" + Cred(NvrOf(c)) + host + "/MediaInput/h264/stream_2"
                : "rtsp://" + Cred(NvrOf(c)) + host + "/cam/realmonitor?channel=1&subtype=1";
        }

        public string RtspMain(RegCam c)
        {
            if (c.RtspHd.Length > 0) return c.RtspHd;
            if (c.Rtsp.Length > 0 || c.Ip.Length == 0 || c.Brand == "custom") return "";
            string host = c.Ip.Contains(":") ? c.Ip : c.Ip + ":554";
            return c.Brand == "ipro"
                ? "rtsp://" + Cred(NvrOf(c)) + host + "/MediaInput/h264/stream_1"
                : "rtsp://" + Cred(NvrOf(c)) + host + "/cam/realmonitor?channel=1&subtype=0";
        }

        // ---------- simpan ----------
        string NvrsJson(bool secrets)
        {
            return "[" + string.Join(",", Nvrs.Select(n =>
            {
                var sb = new StringBuilder();
                sb.Append("{\"id\":").Append(Json.Q(n.Id)).Append(",\"name\":").Append(Json.Q(n.Name)).Append(",\"url\":").Append(Json.Q(n.Url))
                  .Append(",\"user\":").Append(Json.Q(n.User)).Append(",\"scheme\":").Append(Json.Q(n.Scheme))
                  .Append(",\"selfSigned\":").Append(n.SelfSigned ? "true" : "false").Append(",\"camUser\":").Append(Json.Q(n.CamUser));
                if (secrets) sb.Append(",\"pass\":").Append(Json.Q(n.Pass)).Append(",\"camPass\":").Append(Json.Q(n.CamPass));
                else sb.Append(",\"hasPass\":").Append(n.Pass.Length > 0 ? "true" : "false").Append(",\"hasCamPass\":").Append(n.CamPass.Length > 0 ? "true" : "false");
                return sb.Append("}").ToString();
            })) + "]";
        }

        public void Save(AppConfig cfg)
        {
            Directory.CreateDirectory(cfg.DataDir);
            string json = "{\"nvrs\":" + NvrsJson(true) + ",\"cams\":[" + string.Join(",", Cams.Select(c => CamJson(c, true))) + "]}";
            string p = PathFor(cfg), tmp = p + ".tmp";
            File.WriteAllText(tmp, json, new UTF8Encoding(false));
            if (File.Exists(p)) File.Delete(p);
            File.Move(tmp, p);
        }

        /// <summary>full=true: bentuk lengkap untuk editor ("cam" = nomor di NVR, "num" = nomor tampil).
        /// full=false: untuk halaman multiview ("cam" = nomor tampil, dipakai sebagai nomor playback).</summary>
        public static string CamJson(RegCam c, bool full)
        {
            var sb = new StringBuilder();
            sb.Append("{\"cam\":").Append(full ? c.Cam : c.Num).Append(",\"id\":").Append(Json.Q(c.StreamId)).Append(",\"name\":").Append(Json.Q(c.Name));
            sb.Append(",\"enabled\":").Append(c.Enabled ? "true" : "false").Append(",\"ptz\":").Append(c.Ptz ? "true" : "false");
            sb.Append(",\"fe\":").Append(FeJson(c));
            if (full)
            {
                sb.Append(",\"num\":").Append(c.Num).Append(",\"nvr\":").Append(Json.Q(c.Nvr))
                  .Append(",\"ip\":").Append(Json.Q(c.Ip)).Append(",\"brand\":").Append(Json.Q(c.Brand))
                  .Append(",\"rtsp\":").Append(Json.Q(c.Rtsp)).Append(",\"rtspHd\":").Append(Json.Q(c.RtspHd))
                  .Append(",\"probe\":").Append(Json.Q(c.Probe)).Append(",\"note\":").Append(Json.Q(c.Note));
            }
            return sb.Append("}").ToString();
        }

        /// <summary>JSON untuk editor: tanpa password (hanya penanda sudah terisi).</summary>
        public string PublicJson()
        {
            return "{\"nvrs\":" + NvrsJson(false) + ",\"cams\":[" + string.Join(",", Cams.Select(c =>
            {
                string j = CamJson(c, true);
                return j.Substring(0, j.Length - 1) + ",\"effRtsp\":" + Json.Q(MaskCred(RtspSub(c))) + ",\"effRtspHd\":" + Json.Q(MaskCred(RtspMain(c))) + "}";
            })) + "]}";
        }

        static string MaskCred(string url) { return Regex.Replace(url ?? "", @"^(rtsp://)[^@/]*@", "$1***@"); }

        // ---------- go2rtc.yaml ----------
        const string Begin = "# >>> NVR-REGISTRY (dibuat otomatis oleh NvrBridge, jangan diedit di dalam blok ini) >>>";
        const string End = "# <<< NVR-REGISTRY <<<";

        static string Y(string s) { return "\"" + s.Replace("\\", "\\\\").Replace("\"", "\\\"") + "\""; }

        public string BuildStreamsBlock(out int count)
        {
            var sb = new StringBuilder();
            sb.AppendLine("  " + Begin);
            count = 0;
            foreach (var c in Cams.Where(x => x.Enabled))
            {
                string sub = RtspSub(c), main = RtspMain(c);
                if (sub.Length == 0) continue;
                sb.AppendLine("  " + c.StreamId + ": " + Y(sub));
                if (main.Length > 0) sb.AppendLine("  " + c.StreamId + "_hd: " + Y(main));
                count++;
            }
            sb.AppendLine("  " + End);
            return sb.ToString();
        }

        /// <summary>Menulis blok stream ke go2rtc.yaml (menimpa blok lama). Mengembalikan jumlah kamera. Melempar ArgumentException jika ada nama ganda di luar blok.</summary>
        public int ExportTo(string yamlPath)
        {
            int count;
            string block = BuildStreamsBlock(out count);
            string text = File.Exists(yamlPath) ? File.ReadAllText(yamlPath, Encoding.UTF8) : "";
            string nl = text.Contains("\r\n") ? "\r\n" : "\n";
            var lines = text.Replace("\r\n", "\n").Split('\n').ToList();

            int b = lines.FindIndex(l => l.Trim() == Begin);
            int e = lines.FindIndex(l => l.Trim() == End);
            if ((b >= 0) != (e >= 0) || (b >= 0 && e < b)) throw new ArgumentException("Penanda blok NVR-REGISTRY di go2rtc.yaml rusak. Perbaiki atau hapus kedua barisnya.");
            if (b >= 0) lines.RemoveRange(b, e - b + 1);

            var mine = new HashSet<string>(Cams.Where(x => x.Enabled).SelectMany(x => new[] { x.StreamId, x.StreamId + "_hd" }));
            var clash = new List<string>();
            foreach (var l in lines)
            {
                var m = Regex.Match(l, @"^\s+(cam\d+(?:_hd)?)\s*:");
                if (m.Success && mine.Contains(m.Groups[1].Value)) clash.Add(m.Groups[1].Value);
            }
            if (clash.Count > 0)
                throw new ArgumentException("go2rtc.yaml sudah punya stream bernama " + string.Join(", ", clash.Distinct().Take(6)) +
                                            " di luar blok otomatis. Hapus atau ganti nama baris itu dulu.");

            var blockLines = block.Replace("\r\n", "\n").TrimEnd('\n').Split('\n').ToList();
            int s = lines.FindIndex(l => Regex.IsMatch(l, @"^streams:\s*(#.*)?$"));
            if (s >= 0) lines.InsertRange(b >= 0 && b > s ? Math.Min(b, lines.Count) : s + 1, blockLines);
            else { if (lines.Count > 0 && lines[lines.Count - 1].Trim().Length > 0) lines.Add(""); lines.Add("streams:"); lines.AddRange(blockLines); }

            string tmp = yamlPath + ".tmp";
            File.WriteAllText(tmp, string.Join(nl, lines), new UTF8Encoding(false));
            if (File.Exists(yamlPath)) { File.Copy(yamlPath, yamlPath + ".bak", true); File.Delete(yamlPath); }
            File.Move(tmp, yamlPath);
            return count;
        }
    }
}
