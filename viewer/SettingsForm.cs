using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Text;
using System.Windows.Forms;
using NvrBridge;

namespace CctvViewer
{
    /// <summary>Semua pengaturan dalam satu jendela: aplikasi dan layar, NvrBridge, dan tampilan kamera.
    /// Tiap tab menulis berkasnya sendiri (cctvviewer.config, nvrbridge.config, www\viewer.json); tidak perlu mengedit teks.</summary>
    sealed class SettingsForm : Form
    {
        readonly WallContext ctx;
        readonly Settings st;
        readonly Dictionary<string, string> v0, b0;     // isi awal, untuk mendeteksi perubahan
        readonly string viewerJsonPath;

        // ----- tab 1: layar & aplikasi -----
        readonly NumericUpDown screens = Num(1, 8), per = Num(1, 36), reload = Num(0, 10080), wait = Num(0, 600), ctlPort = Num(0, 65535);
        readonly CheckBox bridge = new CheckBox { Text = "Jalankan NvrBridge (playback, PTZ, peta, registrasi)", AutoSize = true };
        readonly CheckBox allowClose = new CheckBox { Text = "Izinkan Alt+F4 menutup layar kamera", AutoSize = true };
        readonly CheckBox auto = new CheckBox { Text = "Jalan otomatis saat Windows login", AutoSize = true };
        readonly CheckBox manualMon = new CheckBox { Text = "Atur posisi monitor secara manual (jika urutan otomatis kiri-ke-kanan salah)", AutoSize = true };
        readonly TextBox monitors = new TextBox { Multiline = true, Width = 300, Height = 70 };
        readonly TextBox wvArgs = new TextBox { Width = 420 }, go2rtcApi = new TextBox { Width = 420 }, bridgeUrl = new TextBox { Width = 420 };

        // ----- tab 2: NvrBridge -----
        readonly TextBox bTitle = new TextBox { Width = 300 }, bListen = new TextBox { Width = 300 }, bUser = new TextBox { Width = 200 },
            bPass = new TextBox { Width = 200, UseSystemPasswordChar = true }, bOrigin = new TextBox { Width = 300 };
        readonly ComboBox bClipKind = Combo("MP4VO", "MP4"), bTime = Combo("Utc", "Local");
        readonly NumericUpDown bMaxClip = Num(1, 60), bConc = Num(1, 8), bRetain = Num(1, 10080), bPtzMax = Num(1, 60), bPtzCh = Num(0, 64);

        // ----- tab 3: tampilan kamera -----
        readonly TextBox vTitle = new TextBox { Width = 300 }, vHd = new TextBox { Width = 120 };
        readonly ComboBox vMode = Combo("mse,webrtc", "webrtc,mse", "mse", "webrtc"), vLayout = Combo("auto", "1", "2", "3", "4");
        readonly NumericUpDown vRotate = Num(3, 600), vDewarp = Num(0, 36);
        readonly DataGridView grid = new DataGridView { Dock = DockStyle.Fill };
        readonly Label gridNote = new Label { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 4, 0, 4) };
        Dictionary<string, object> vj = new Dictionary<string, object>();   // viewer.json yang sedang dipakai (untuk menjaga kunci yang tidak ditampilkan)
        readonly List<CamRow> rows = new List<CamRow>();

        sealed class CamRow { public string Id, Name, Mount = "", Tile = "view"; public bool Show = true, Mirror; public string Fov = ""; }

        static NumericUpDown Num(int lo, int hi) { return new NumericUpDown { Minimum = lo, Maximum = hi, Width = 90 }; }
        static ComboBox Combo(params string[] items)
        {
            var c = new ComboBox { Width = 140, DropDownStyle = ComboBoxStyle.DropDown };
            foreach (var i in items) c.Items.Add(i);
            return c;
        }
        static string S(decimal d) { return ((int)d).ToString(CultureInfo.InvariantCulture); }
        static string B(bool b) { return b ? "true" : "false"; }
        static bool IsTrue(string v, bool def) { if (v == null) return def; return v.Equals("true", StringComparison.OrdinalIgnoreCase) || v == "1" ? true : (v.Equals("false", StringComparison.OrdinalIgnoreCase) || v == "0" ? false : def); }
        static string G(Dictionary<string, string> d, string k, string def) { string v; return d.TryGetValue(k, out v) && v.Length > 0 ? v : def; }
        static void SetNum(NumericUpDown n, string v, int def)
        {
            int x; if (!int.TryParse(v, NumberStyles.Integer, CultureInfo.InvariantCulture, out x)) x = def;
            n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, x));
        }

        public SettingsForm(WallContext ctx)
        {
            this.ctx = ctx; st = ctx.Settings;
            v0 = ConfigWriter.ReadAll(ctx.ConfigPath);
            b0 = ConfigWriter.ReadAll(st.NvrBridgeConfig);
            viewerJsonPath = Path.Combine(st.WwwDir, "viewer.json");

            Text = "Pengaturan";
            StartPosition = FormStartPosition.CenterParent;
            FormBorderStyle = FormBorderStyle.FixedDialog;
            MaximizeBox = false; MinimizeBox = false;
            ClientSize = new Size(760, 600);

            var tabs = new TabControl { Dock = DockStyle.Fill };
            tabs.TabPages.Add(TabApp());
            tabs.TabPages.Add(TabBridge());
            tabs.TabPages.Add(TabView());

            var bar = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(6), FlowDirection = FlowDirection.RightToLeft };
            var cancel = new Button { Text = "Batal", AutoSize = true, DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "Simpan", AutoSize = true };
            save.Click += (s, e) => { if (Save()) { DialogResult = DialogResult.OK; Close(); } };
            bar.Controls.Add(cancel); bar.Controls.Add(save);

            Controls.Add(tabs);
            Controls.Add(bar);
            CancelButton = cancel;
            LoadValues();
        }

        static TableLayoutPanel Table()
        {
            return new TableLayoutPanel { ColumnCount = 2, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
        }
        static void Row(TableLayoutPanel t, string label, Control c)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 12, 0) });
            t.Controls.Add(c);
        }
        static void Wide(TableLayoutPanel t, Control c) { t.Controls.Add(c); t.SetColumnSpan(c, 2); }
        static Label Note(string text) { return new Label { Text = text, AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 6, 0, 6), MaximumSize = new Size(700, 0) }; }

        TabPage TabApp()
        {
            var p = new TabPage("Layar dan aplikasi") { AutoScroll = true };
            var t = Table();
            Row(t, "Jumlah layar (monitor)", screens);
            Row(t, "Kamera per layar", per);
            Row(t, "Muat ulang halaman (menit, 0 = tidak)", reload);
            Row(t, "Tunggu monitor setelah menyala (detik)", wait);
            Wide(t, bridge); Wide(t, allowClose); Wide(t, auto);
            Wide(t, manualMon);
            Row(t, "Satu monitor per baris:\nx,y,lebar,tinggi", monitors);
            var fill = new Button { Text = "Isi dari monitor yang terdeteksi", AutoSize = true };
            fill.Click += (s, e) =>
            {
                var list = Screen.AllScreens.Select(x => new Area(x.Bounds.X, x.Bounds.Y, x.Bounds.Width, x.Bounds.Height)).ToList();
                list.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
                monitors.Text = string.Join(Environment.NewLine, list.Select(a => a.X + "," + a.Y + "," + a.W + "," + a.H));
                manualMon.Checked = true;
            };
            Row(t, "", fill);
            Wide(t, Note("Tombol \"Kenali layar\" di panel menampilkan nomor di tiap monitor. Urutan layar 1, 2, 3 mengikuti daftar ini."));
            Row(t, "Alamat API go2rtc", go2rtcApi);
            Row(t, "Alamat NvrBridge", bridgeUrl);
            Row(t, "Port kontrol lokal (0 = mati)", ctlPort);
            Row(t, "Argumen WebView2 (lanjutan)", wvArgs);
            Wide(t, Note("Perubahan di tab ini membutuhkan CctvViewer dimulai ulang; Anda akan ditanya setelah menyimpan."));
            p.Controls.Add(t);
            return p;
        }

        TabPage TabBridge()
        {
            var p = new TabPage("NvrBridge") { AutoScroll = true };
            var t = Table();
            Wide(t, Note("NVR, daftar kamera, akun kamera, URL RTSP, dan PTZ diatur di jendela Registrasi NVR."));
            var reg = new Button { Text = "Buka Registrasi NVR...", AutoSize = true };
            reg.Click += (s, e) => ctx.ShowSetup();
            Wide(t, reg);
            Row(t, "Judul halaman playback", bTitle);
            Row(t, "Alamat yang didengarkan", bListen);
            Row(t, "Origin halaman multiview\n(kosong = otomatis)", bOrigin);
            Row(t, "Login halaman (pengguna)", bUser);
            Row(t, "Login halaman (kata sandi)", bPass);
            Row(t, "Jenis klip", bClipKind);
            Row(t, "Durasi klip maksimal (menit)", bMaxClip);
            Row(t, "Unduhan bersamaan", bConc);
            Row(t, "Hapus klip setelah (menit)", bRetain);
            Row(t, "Zona waktu perintah ke NVR", bTime);
            Row(t, "PTZ: berhenti otomatis (detik)", bPtzMax);
            Row(t, "PTZ: channel (Dahua)", bPtzCh);
            Wide(t, Note("MP4VO = tanpa suara (aman di browser). Zona waktu: Utc; ganti ke Local jika klip yang diterima bergeser jam. Pastikan alamat yang didengarkan tetap 127.0.0.1 kecuali Anda perlu akses dari PC lain; jika diisi login, isi juga kata sandi."));
            p.Controls.Add(t);
            return p;
        }

        TabPage TabView()
        {
            var p = new TabPage("Tampilan kamera");
            var top = Table();
            Row(top, "Judul halaman", vTitle);
            Row(top, "Urutan mode pemutaran", vMode);
            Row(top, "Akhiran stream resolusi tinggi", vHd);
            Row(top, "Layout awal (mode satu layar)", vLayout);
            Row(top, "Jeda ganti halaman (detik)", vRotate);
            Row(top, "Maks. tile fisheye di-dewarp sekaligus", vDewarp);

            grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.RowHeadersVisible = false;
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Stream", ReadOnly = true, Width = 90 });
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "Nama tampilan", Width = 190 });
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Tampil", Width = 55 });
            var fe = new DataGridViewComboBoxColumn { HeaderText = "Fisheye", Width = 90 };
            fe.Items.Add("tidak"); fe.Items.Add("ceiling"); fe.Items.Add("wall");
            grid.Columns.Add(fe);
            grid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = "FOV", Width = 50 });
            var tl = new DataGridViewComboBoxColumn { HeaderText = "Tile", Width = 70 };
            tl.Items.Add("view"); tl.Items.Add("raw");
            grid.Columns.Add(tl);
            grid.Columns.Add(new DataGridViewCheckBoxColumn { HeaderText = "Cermin", Width = 55 });

            var btns = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 40, FlowDirection = FlowDirection.LeftToRight };
            Button Bn(string text, Action a) { var b = new Button { Text = text, AutoSize = true }; b.Click += (s, e) => a(); btns.Controls.Add(b); return b; }
            Bn("Naik", () => Move(-1));
            Bn("Turun", () => Move(1));
            Bn("Muat ulang daftar dari go2rtc", () => { Collect(); LoadStreams(); Fill(); });

            var bottom = new Panel { Dock = DockStyle.Bottom, Height = 70 };
            bottom.Controls.Add(gridNote);
            bottom.Controls.Add(btns);

            p.Controls.Add(grid);
            p.Controls.Add(bottom);
            p.Controls.Add(top);
            return p;
        }

        // ---------- muat ----------
        void LoadValues()
        {
            SetNum(screens, G(v0, "Screens", "3"), 3); SetNum(per, G(v0, "CamsPerScreen", "16"), 16);
            SetNum(reload, G(v0, "ReloadMinutes", "360"), 360); SetNum(wait, G(v0, "MonitorWaitSeconds", "60"), 60);
            SetNum(ctlPort, G(v0, "ControlPort", "1985"), 1985);
            bridge.Checked = IsTrue(G(v0, "StartNvrBridge", "true"), true);
            allowClose.Checked = IsTrue(G(v0, "AllowCloseScreen", "false"), false);
            auto.Checked = Program.AutostartEnabled();
            string mon = G(v0, "Monitors", "auto");
            manualMon.Checked = !mon.Equals("auto", StringComparison.OrdinalIgnoreCase);
            monitors.Text = manualMon.Checked ? string.Join(Environment.NewLine, mon.Split(new[] { ';' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim())) : "";
            wvArgs.Text = G(v0, "WebViewArgs", "");
            go2rtcApi.Text = G(v0, "Go2rtcApi", "http://127.0.0.1:1984");
            bridgeUrl.Text = G(v0, "BridgeUrl", "http://127.0.0.1:8090");

            bTitle.Text = G(b0, "Title", "Playback NVR");
            bListen.Text = G(b0, "ListenPrefix", "http://127.0.0.1:8090/");
            bOrigin.Text = G(b0, "AllowOrigin", "") == OriginFromApi() ? "" : G(b0, "AllowOrigin", "");
            bUser.Text = G(b0, "BridgeUser", ""); bPass.Text = G(b0, "BridgePassword", "");
            bClipKind.Text = G(b0, "ClipKind", "MP4VO"); bTime.Text = G(b0, "NvrTimeMode", "Utc");
            SetNum(bMaxClip, G(b0, "MaxClipMinutes", "10"), 10); SetNum(bConc, G(b0, "MaxConcurrentDownloads", "2"), 2);
            SetNum(bRetain, G(b0, "ClipRetentionMinutes", "60"), 60); SetNum(bPtzMax, G(b0, "PtzMaxMoveSeconds", "8"), 8);
            SetNum(bPtzCh, G(b0, "PtzChannel", "0"), 0);

            vTitle.Text = "CCTV Multiview"; vMode.Text = "mse,webrtc"; vHd.Text = "_hd"; vLayout.Text = "auto"; vRotate.Value = 15; vDewarp.Value = 6;
            try
            {
                if (File.Exists(viewerJsonPath))
                    vj = MiniJson.Obj(MiniJson.Parse(File.ReadAllText(viewerJsonPath, Encoding.UTF8))) ?? new Dictionary<string, object>();
            }
            catch (Exception e) { vj = new Dictionary<string, object>(); gridNote.Text = "viewer.json tidak terbaca (" + e.Message + "); dimulai dari kosong."; }
            if (MiniJson.Str(vj, "title") != "") vTitle.Text = MiniJson.Str(vj, "title");
            if (MiniJson.Str(vj, "mode") != "") vMode.Text = MiniJson.Str(vj, "mode");
            if (MiniJson.Str(vj, "hdSuffix") != "") vHd.Text = MiniJson.Str(vj, "hdSuffix");
            if (MiniJson.Str(vj, "layout") != "") vLayout.Text = MiniJson.Str(vj, "layout");
            object o;
            if (vj.TryGetValue("layout", out o) && o is double) vLayout.Text = ((int)(double)o).ToString(CultureInfo.InvariantCulture);
            if (vj.ContainsKey("rotateSeconds")) SetNum(vRotate, MiniJson.Int(vj, "rotateSeconds", 15).ToString(CultureInfo.InvariantCulture), 15);
            if (vj.ContainsKey("dewarpMax")) SetNum(vDewarp, MiniJson.Int(vj, "dewarpMax", 6).ToString(CultureInfo.InvariantCulture), 6);
            LoadStreams();
            Fill();
        }

        string OriginFromApi() { return st.Go2rtcApi.GetLeftPart(UriPartial.Authority); }

        /// <summary>Gabungkan stream dari go2rtc dengan nama/urutan/fisheye yang tersimpan di viewer.json.</summary>
        void LoadStreams()
        {
            var saved = rows.ToDictionary(r => r.Id, r => r);   // perubahan yang belum disimpan dipertahankan
            var ids = new List<string>();
            string hd = vHd.Text.Trim();
            bool up = true;
            try
            {
                foreach (var k in Core.StreamNames(Core.Http(new Uri(st.Go2rtcApi, "api/streams"), "GET", 3000)))
                    if (hd.Length == 0 || !k.EndsWith(hd, StringComparison.Ordinal)) ids.Add(k);
            }
            catch { up = false; }

            var names = MiniJson.Obj(vj.ContainsKey("names") ? vj["names"] : null);
            var fish = MiniJson.Obj(vj.ContainsKey("fisheye") ? vj["fisheye"] : null);
            var hide = new HashSet<string>((MiniJson.List(vj, "hide") ?? new List<object>()).OfType<string>());
            var order = (MiniJson.List(vj, "order") ?? new List<object>()).OfType<string>().ToList();

            var all = new List<string>();
            foreach (var id in order) if (ids.Contains(id) || (!up && !all.Contains(id))) { if (!all.Contains(id)) all.Add(id); }
            foreach (var id in ids) if (!all.Contains(id)) all.Add(id);
            foreach (var id in saved.Keys) if (!all.Contains(id)) all.Add(id);

            rows.Clear();
            foreach (var id in all)
            {
                CamRow r;
                if (saved.TryGetValue(id, out r)) { rows.Add(r); continue; }
                r = new CamRow { Id = id, Name = names != null ? MiniJson.Str(names, id) : "", Show = !hide.Contains(id) };
                var f = fish != null ? MiniJson.Obj(fish.ContainsKey(id) ? fish[id] : null) : null;
                if (f != null)
                {
                    r.Mount = MiniJson.Str(f, "mount"); if (r.Mount != "ceiling" && r.Mount != "wall") r.Mount = "";
                    r.Tile = MiniJson.Str(f, "tile") == "raw" ? "raw" : "view";
                    r.Mirror = MiniJson.Bool(f, "mirror", false);
                    object fv; if (f.TryGetValue("fov", out fv) && fv is double) r.Fov = ((int)(double)fv).ToString(CultureInfo.InvariantCulture);
                }
                rows.Add(r);
            }
            gridNote.Text = up
                ? "Kamera ditambah/dihapus lewat Registrasi NVR (atau go2rtc.yaml). Fisheye: pilih ceiling atau wall hanya untuk kamera yang mengirim lingkaran mentah."
                : "go2rtc belum berjalan, jadi daftar stream hanya dari pengaturan tersimpan. Jalankan go2rtc lalu klik \"Muat ulang daftar\".";
        }

        void Fill()
        {
            grid.Rows.Clear();
            foreach (var r in rows) grid.Rows.Add(r.Id, r.Name, r.Show, string.IsNullOrEmpty(r.Mount) ? "tidak" : r.Mount, r.Fov, r.Tile, r.Mirror);
        }

        void Collect()
        {
            grid.EndEdit();
            for (int i = 0; i < rows.Count && i < grid.Rows.Count; i++)
            {
                var c = grid.Rows[i].Cells; var r = rows[i];
                r.Name = ((c[1].Value as string) ?? "").Trim();
                r.Show = c[2].Value is bool && (bool)c[2].Value;
                string m = (c[3].Value as string) ?? "tidak"; r.Mount = m == "tidak" ? "" : m;
                r.Fov = ((c[4].Value as string) ?? "").Trim();
                r.Tile = (c[5].Value as string) == "raw" ? "raw" : "view";
                r.Mirror = c[6].Value is bool && (bool)c[6].Value;
            }
        }

        void Move(int d)
        {
            Collect();
            int i = grid.CurrentRow == null ? -1 : grid.CurrentRow.Index;
            int j = i + d;
            if (i < 0 || j < 0 || j >= rows.Count) return;
            var t = rows[i]; rows[i] = rows[j]; rows[j] = t;
            Fill();
            grid.Rows[j].Selected = true;
        }

        // ---------- simpan ----------
        bool Save()
        {
            try
            {
                // 1) cctvviewer.config (divalidasi dengan pembaca konfigurasi yang sama sebelum menulis)
                var v = new Dictionary<string, string>
                {
                    {"Screens", S(screens.Value)}, {"CamsPerScreen", S(per.Value)}, {"ReloadMinutes", S(reload.Value)},
                    {"MonitorWaitSeconds", S(wait.Value)}, {"ControlPort", S(ctlPort.Value)},
                    {"StartNvrBridge", B(bridge.Checked)}, {"AllowCloseScreen", B(allowClose.Checked)},
                    {"WebViewArgs", wvArgs.Text.Trim()}, {"Go2rtcApi", go2rtcApi.Text.Trim()}, {"BridgeUrl", bridgeUrl.Text.Trim()},
                    {"Monitors", MonitorsValue()},
                };
                string chk = ctx.ConfigPath + ".check";
                File.Copy(ctx.ConfigPath, chk, true);
                try { ConfigWriter.Set(chk, v); Settings.Load(chk, st.BaseDir); }
                finally { try { File.Delete(chk); } catch { } }

                // 2) nvrbridge.config
                var api = new Uri(go2rtcApi.Text.Trim());
                string origin = bOrigin.Text.Trim().Length > 0 ? bOrigin.Text.Trim() : api.GetLeftPart(UriPartial.Authority);
                if (bPass.Text.Length > 0 && bUser.Text.Trim().Length == 0) throw new ConfigException("Kata sandi login halaman diisi, tetapi pengguna kosong.");
                if (!bListen.Text.Trim().StartsWith("http://", StringComparison.OrdinalIgnoreCase) || !bListen.Text.Trim().EndsWith("/")) throw new ConfigException("Alamat yang didengarkan harus berbentuk http://host:port/ (diakhiri garis miring).");
                var b = new Dictionary<string, string>
                {
                    {"Title", bTitle.Text.Trim()}, {"ListenPrefix", bListen.Text.Trim()}, {"AllowOrigin", origin},
                    {"BridgeUser", bUser.Text.Trim()}, {"BridgePassword", bPass.Text},
                    {"ClipKind", G2(bClipKind.Text, "MP4VO")}, {"NvrTimeMode", G2(bTime.Text, "Utc")},
                    {"MaxClipMinutes", S(bMaxClip.Value)}, {"MaxConcurrentDownloads", S(bConc.Value)}, {"ClipRetentionMinutes", S(bRetain.Value)},
                    {"PtzMaxMoveSeconds", S(bPtzMax.Value)}, {"PtzChannel", S(bPtzCh.Value)},
                    {"Go2rtcYaml", st.Go2rtcConfig}, {"Go2rtcApi", api.GetLeftPart(UriPartial.Authority)},
                };
                string oldYaml;
                if (b0.TryGetValue("Go2rtcYaml", out oldYaml) && oldYaml.Length > 0)
                {
                    try { if (string.Equals(Path.GetFullPath(Path.Combine(Path.GetDirectoryName(st.NvrBridgeConfig), oldYaml)), st.Go2rtcConfig, StringComparison.OrdinalIgnoreCase)) b.Remove("Go2rtcYaml"); } catch { }
                }
                foreach (var k in new[] { "ClipKind", "NvrTimeMode" })
                {
                    string x = b[k];
                    if (k == "ClipKind" && x != "MP4VO" && x != "MP4") throw new ConfigException("Jenis klip harus MP4VO atau MP4.");
                    if (k == "NvrTimeMode" && x != "Utc" && x != "Local") throw new ConfigException("Zona waktu harus Utc atau Local.");
                }

                // 3) viewer.json
                Collect();
                string json = BuildViewerJson();
                string mode = vMode.Text.Trim();
                if (mode.Length == 0) throw new ConfigException("Urutan mode pemutaran tidak boleh kosong.");

                // semua valid: tulis
                bool vChanged = !SameDict(v0, v);
                ConfigWriter.Set(ctx.ConfigPath, v);
                Program.SetAutostartQuiet(auto.Checked);

                bool bChanged = !SameDict(b0, b);
                if (bChanged)
                {
                    Directory.CreateDirectory(Path.GetDirectoryName(st.NvrBridgeConfig));
                    ConfigWriter.Set(st.NvrBridgeConfig, b);
                }

                Directory.CreateDirectory(st.WwwDir);
                string tmp = viewerJsonPath + ".tmp";
                File.WriteAllText(tmp, json, new UTF8Encoding(false));
                if (File.Exists(viewerJsonPath)) File.Delete(viewerJsonPath);
                File.Move(tmp, viewerJsonPath);
                Log.Info("Pengaturan disimpan (aplikasi=" + vChanged + ", NvrBridge=" + bChanged + ", tampilan kamera=ya).");

                if (vChanged)
                {
                    if (MessageBox.Show(this, "Pengaturan layar/aplikasi berubah. Mulai ulang CCTV Viewer sekarang agar berlaku?", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes)
                    { ctx.Restart(); return true; }
                }
                if (bChanged && ctx.Core.BridgeManaged) ctx.Core.RestartBridge();
                ctx.ReloadAll();
                return true;
            }
            catch (UriFormatException) { MessageBox.Show(this, "Alamat API go2rtc tidak valid. Contoh: http://127.0.0.1:1984", Program.AppName); return false; }
            catch (Exception e) { MessageBox.Show(this, "Tidak bisa menyimpan: " + e.Message, Program.AppName); return false; }
        }

        static string G2(string v, string def) { v = (v ?? "").Trim(); return v.Length == 0 ? def : v; }

        // Nilai yang tidak tertulis di berkas dianggap bernilai bawaan, agar menyimpan tanpa perubahan tidak memicu mulai ulang.
        static readonly Dictionary<string, string> Defaults = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase)
        {
            {"Screens","3"}, {"CamsPerScreen","16"}, {"ReloadMinutes","360"}, {"MonitorWaitSeconds","60"}, {"ControlPort","1985"},
            {"StartNvrBridge","true"}, {"AllowCloseScreen","false"}, {"Monitors","auto"}, {"Go2rtcApi","http://127.0.0.1:1984"}, {"BridgeUrl","http://127.0.0.1:8090"},
            {"Title","Playback NVR"}, {"ListenPrefix","http://127.0.0.1:8090/"}, {"ClipKind","MP4VO"}, {"NvrTimeMode","Utc"}, {"MaxClipMinutes","10"},
            {"MaxConcurrentDownloads","2"}, {"ClipRetentionMinutes","60"}, {"PtzMaxMoveSeconds","8"}, {"PtzChannel","0"},
        };

        static bool SameDict(Dictionary<string, string> old, Dictionary<string, string> now)
        {
            foreach (var kv in now)
            {
                string o, d;
                if (!old.TryGetValue(kv.Key, out o) || o.Length == 0) o = Defaults.TryGetValue(kv.Key, out d) ? d : "";
                if (!string.Equals(o.TrimEnd('/'), kv.Value.TrimEnd('/'), StringComparison.Ordinal)) return false;
            }
            return true;
        }

        string MonitorsValue()
        {
            if (!manualMon.Checked) return "auto";
            var parts = monitors.Text.Split(new[] { '\r', '\n' }, StringSplitOptions.RemoveEmptyEntries).Select(x => x.Trim()).Where(x => x.Length > 0).ToList();
            if (parts.Count == 0) throw new ConfigException("Monitor manual dicentang tetapi daftarnya kosong.");
            return string.Join(";", parts);
        }

        string BuildViewerJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\n");
            sb.Append("  \"title\": ").Append(Json.Q(vTitle.Text.Trim())).Append(",\n");
            sb.Append("  \"mode\": ").Append(Json.Q(vMode.Text.Trim())).Append(",\n");
            sb.Append("  \"hdSuffix\": ").Append(Json.Q(vHd.Text.Trim())).Append(",\n");
            sb.Append("  \"layout\": ").Append(Json.Q(vLayout.Text.Trim().Length == 0 ? "auto" : vLayout.Text.Trim())).Append(",\n");
            sb.Append("  \"rotateSeconds\": ").Append(S(vRotate.Value)).Append(",\n");
            sb.Append("  \"dewarpMax\": ").Append(S(vDewarp.Value)).Append(",\n");
            sb.Append("  \"order\": [").Append(string.Join(", ", rows.Select(r => Json.Q(r.Id)))).Append("],\n");
            sb.Append("  \"hide\": [").Append(string.Join(", ", rows.Where(r => !r.Show).Select(r => Json.Q(r.Id)))).Append("],\n");
            sb.Append("  \"names\": {").Append(string.Join(", ", rows.Where(r => r.Name.Length > 0).Select(r => Json.Q(r.Id) + ": " + Json.Q(r.Name)))).Append("},\n");

            var oldFish = MiniJson.Obj(vj.ContainsKey("fisheye") ? vj["fisheye"] : null);
            var fe = new List<string>();
            foreach (var r in rows)
            {
                if (r.Mount.Length == 0) continue;
                var f = new List<string> { "\"mount\": " + Json.Q(r.Mount) };
                int fov;
                if (r.Fov.Length > 0)
                {
                    if (!int.TryParse(r.Fov, NumberStyles.Integer, CultureInfo.InvariantCulture, out fov) || fov < 90 || fov > 360)
                        throw new ConfigException("FOV untuk " + r.Id + " harus angka 90-360 (derajat), atau kosongkan.");
                    f.Add("\"fov\": " + fov.ToString(CultureInfo.InvariantCulture));
                }
                if (r.Tile == "raw") f.Add("\"tile\": \"raw\"");
                if (r.Mirror) f.Add("\"mirror\": true");
                // nilai lanjutan (mode, cx, cy, r) yang pernah ditulis manual tetap dipertahankan
                var old = oldFish != null ? MiniJson.Obj(oldFish.ContainsKey(r.Id) ? oldFish[r.Id] : null) : null;
                if (old != null)
                {
                    if (MiniJson.Str(old, "mode") != "") f.Add("\"mode\": " + Json.Q(MiniJson.Str(old, "mode")));
                    foreach (var k in new[] { "cx", "cy", "r" })
                    { object n; if (old.TryGetValue(k, out n) && n is double) f.Add("\"" + k + "\": " + ((double)n).ToString("R", CultureInfo.InvariantCulture)); }
                }
                fe.Add(Json.Q(r.Id) + ": {" + string.Join(", ", f) + "}");
            }
            sb.Append("  \"fisheye\": {").Append(string.Join(", ", fe)).Append("}\n");
            sb.Append("}\n");
            return sb.ToString();
        }
    }
}
