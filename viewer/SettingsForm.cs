using System;
using System.Collections.Generic;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;

namespace CctvViewer
{
    /// <summary>Jendela Pengaturan bertab: semua konfigurasi CctvViewer, NvrBridge, go2rtc, dan daftar kamera ada di sini.</summary>
    sealed class SettingsForm : Form
    {
        public const int TabLayar = 0, TabKamera = 1, TabPlayback = 2, TabLayanan = 3;

        readonly WallContext ctx;
        readonly Settings st;
        readonly Dictionary<string, string> bc;       // isi nvrbridge.config saat ini
        readonly TabControl tabs = new TabControl { Dock = DockStyle.Fill };
        readonly CameraTab cams;

        // --- Layar ---
        readonly NumericUpDown screens = new NumericUpDown { Minimum = 1, Maximum = 8, Width = 70 };
        readonly NumericUpDown per = new NumericUpDown { Minimum = 1, Maximum = 36, Width = 70 };
        readonly NumericUpDown wait = new NumericUpDown { Minimum = 0, Maximum = 600, Width = 70 };
        readonly NumericUpDown reload = new NumericUpDown { Minimum = 0, Maximum = 10080, Width = 70 };
        readonly NumericUpDown dewarp = new NumericUpDown { Minimum = 0, Maximum = 48, Width = 70 };
        readonly CheckBox manualMon = new CheckBox { Text = "Atur posisi monitor secara manual (jika urutan otomatis salah)", AutoSize = true };
        readonly DataGridView monGrid = new DataGridView();
        readonly CheckBox allowClose = new CheckBox { Text = "Izinkan Alt+F4 menutup layar kamera", AutoSize = true };
        readonly CheckBox autostart = new CheckBox { Text = "Jalan otomatis saat Windows login", AutoSize = true };
        readonly TextBox pageTitle = new TextBox { Width = 260 };
        readonly ComboBox pageMode = new ComboBox { Width = 160, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly Label gridInfo = new Label { AutoSize = true, ForeColor = Color.DimGray };

        // --- Playback & PTZ (nvrbridge.config) ---
        readonly ComboBox timeMode = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox clipKind = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly NumericUpDown maxClip = new NumericUpDown { Minimum = 1, Maximum = 60, Width = 70 };
        readonly NumericUpDown clipTimeout = new NumericUpDown { Minimum = 1, Maximum = 120, Width = 70 };
        readonly NumericUpDown clipKeep = new NumericUpDown { Minimum = 5, Maximum = 1440, Width = 70 };
        readonly NumericUpDown maxDl = new NumericUpDown { Minimum = 1, Maximum = 8, Width = 70 };
        readonly TextBox ptzUser = new TextBox { Width = 140 };
        readonly TextBox ptzPass = new TextBox { Width = 140, UseSystemPasswordChar = true };
        readonly NumericUpDown ptzChannel = new NumericUpDown { Minimum = 0, Maximum = 64, Width = 70 };
        readonly NumericUpDown ptzMax = new NumericUpDown { Minimum = 1, Maximum = 60, Width = 70 };
        readonly TextBox bridgeTitle = new TextBox { Width = 260 };
        readonly CheckBox verbose = new CheckBox { Text = "Catat detail permintaan ke NVR di nvrbridge.log (untuk diagnosis)", AutoSize = true };

        // --- Layanan ---
        readonly NumericUpDown apiPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 80 };
        readonly NumericUpDown rtspPort = new NumericUpDown { Minimum = 0, Maximum = 65535, Width = 80 };
        readonly NumericUpDown webrtcPort = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 80 };
        readonly ComboBox go2rtcLog = new ComboBox { Width = 90, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckBox allowLan = new CheckBox { Text = "Izinkan akses dari PC lain di jaringan (go2rtc dan NvrBridge)", AutoSize = true };
        readonly CheckBox startBridge = new CheckBox { Text = "Jalankan NvrBridge (playback, PTZ, peta, registrasi NVR)", AutoSize = true };
        readonly NumericUpDown bridgePort = new NumericUpDown { Minimum = 1, Maximum = 65535, Width = 80 };
        readonly TextBox bridgeUser = new TextBox { Width = 140 };
        readonly TextBox bridgePass = new TextBox { Width = 140, UseSystemPasswordChar = true };
        readonly NumericUpDown controlPort = new NumericUpDown { Minimum = 0, Maximum = 65535, Width = 80 };
        readonly TextBox go2rtcExe = new TextBox { Width = 330 }, go2rtcCfg = new TextBox { Width = 330 }, wwwDir = new TextBox { Width = 330 };
        readonly TextBox bridgeExe = new TextBox { Width = 330 }, bridgeCfg = new TextBox { Width = 330 }, dataDir = new TextBox { Width = 330 };
        readonly TextBox webArgs = new TextBox { Width = 330 };

        public SettingsForm(WallContext ctx, int startTab)
        {
            this.ctx = ctx; st = ctx.Settings;
            bc = ConfigWriter.Read(st.NvrBridgeConfig);
            Text = "Pengaturan - " + Program.AppName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1120, 760);
            MinimumSize = new Size(900, 620);
            ShowInTaskbar = true;

            pageMode.Items.AddRange(new object[] { "mse,webrtc", "webrtc,mse", "mse", "webrtc" });
            timeMode.Items.AddRange(new object[] { "Utc", "Local" });
            clipKind.Items.AddRange(new object[] { "MP4VO", "MP4" });
            go2rtcLog.Items.AddRange(new object[] { "error", "warn", "info", "debug", "trace" });

            tabs.TabPages.Add(BuildLayar());
            cams = new CameraTab(ctx);
            var camPage = new TabPage("Kamera NVR");
            camPage.Controls.Add(cams);
            tabs.TabPages.Add(camPage);
            tabs.TabPages.Add(BuildPlayback());
            tabs.TabPages.Add(BuildLayanan());

            var bottom = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 46, Padding = new Padding(8, 6, 8, 6), FlowDirection = FlowDirection.RightToLeft };
            var close = new Button { Text = "Tutup", AutoSize = true, DialogResult = DialogResult.Cancel };
            var save = new Button { Text = "Simpan pengaturan && mulai ulang", AutoSize = true };
            var only = new Button { Text = "Simpan saja", AutoSize = true };
            save.Click += (s, e) => { if (Save()) { Close(); ctx.Restart(); } };
            only.Click += (s, e) => { if (Save()) MessageBox.Show(this, "Pengaturan disimpan. Berlaku setelah CCTV Viewer dimulai ulang.", Program.AppName); };
            var note = new Label { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 8, 12, 0), Text = "Tab Layar, Playback & PTZ, dan Layanan memakai tombol ini. Tab Kamera NVR punya tombol sendiri." };
            bottom.Controls.Add(close); bottom.Controls.Add(save); bottom.Controls.Add(only); bottom.Controls.Add(note);
            CancelButton = close;

            Controls.Add(tabs);
            Controls.Add(bottom);
            LoadValues();
            tabs.SelectedIndexChanged += (s, e) => { if (tabs.SelectedIndex == TabKamera) cams.EnsureLoaded(); };
            tabs.SelectedIndex = Math.Max(0, Math.Min(tabs.TabCount - 1, startTab));
            Shown += (s, e) => { if (tabs.SelectedIndex == TabKamera) cams.EnsureLoaded(); };
            FormClosing += (s, e) =>
            {
                if (cams.Dirty && MessageBox.Show(this, "Daftar kamera berisi perubahan yang belum disimpan. Tutup tanpa menyimpan?", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Warning) != DialogResult.Yes)
                    e.Cancel = true;
            };
        }

        // ---------- pembuat tata letak ----------
        static TableLayoutPanel Table()
        {
            var t = new TableLayoutPanel { ColumnCount = 3, AutoSize = true, Dock = DockStyle.Top, Padding = new Padding(10) };
            t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize)); t.ColumnStyles.Add(new ColumnStyle(SizeType.AutoSize));
            return t;
        }
        static void Head(TableLayoutPanel t, string text)
        {
            var l = new Label { Text = text, AutoSize = true, Font = new Font(SystemFonts.DefaultFont, FontStyle.Bold), Padding = new Padding(0, 12, 0, 4) };
            t.Controls.Add(l); t.SetColumnSpan(l, 3);
        }
        static void Row(TableLayoutPanel t, string label, Control c, string hint = null)
        {
            t.Controls.Add(new Label { Text = label, AutoSize = true, Anchor = AnchorStyles.Left, Padding = new Padding(0, 6, 12, 0) });
            t.Controls.Add(c);
            t.Controls.Add(new Label { Text = hint ?? "", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(10, 6, 0, 0), MaximumSize = new Size(520, 0) });
        }
        static void Wide(TableLayoutPanel t, Control c)
        {
            t.Controls.Add(c); t.SetColumnSpan(c, 3);
        }
        Control PathBox(TextBox tb, bool folder, string filter)
        {
            var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0) };
            var b = new Button { Text = "Pilih…", AutoSize = true };
            b.Click += (s, e) =>
            {
                if (folder)
                {
                    using (var d = new FolderBrowserDialog()) { if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.SelectedPath; }
                }
                else
                {
                    using (var d = new OpenFileDialog { Filter = filter, CheckFileExists = false }) { if (d.ShowDialog(this) == DialogResult.OK) tb.Text = d.FileName; }
                }
            };
            p.Controls.Add(tb); p.Controls.Add(b);
            return p;
        }
        static TabPage Page(string title, TableLayoutPanel t)
        {
            var pg = new TabPage(title) { AutoScroll = true };
            pg.Controls.Add(t);
            return pg;
        }

        TabPage BuildLayar()
        {
            var t = Table();
            Head(t, "Layar kamera");
            Row(t, "Jumlah layar (monitor)", screens, "Setiap layar membuka satu jendela penuh di satu monitor.");
            Row(t, "Kamera per layar", per, "16 = grid 4×4. Kamera ke-1..N layar 1, berikutnya layar 2, dst.");
            Row(t, "", gridInfo);
            Row(t, "Tunggu monitor menyala (detik)", wait, "PC yang baru menyala kadang terlambat mendeteksi monitor.");
            Row(t, "Muat ulang halaman tiap (menit)", reload, "0 = tidak. Menjaga tampilan tetap segar 24 jam.");
            Wide(t, manualMon);
            monGrid.AllowUserToAddRows = false; monGrid.AutoGenerateColumns = false; monGrid.RowHeadersVisible = false; monGrid.Height = 110; monGrid.Width = 440;
            monGrid.BackgroundColor = SystemColors.Window;
            foreach (var h in new[] { "X", "Y", "Lebar", "Tinggi" }) monGrid.Columns.Add(new DataGridViewTextBoxColumn { HeaderText = h, Width = 90 });
            var mp = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight };
            var fill = new Button { Text = "Isi dari monitor saat ini", AutoSize = true };
            var ident = new Button { Text = "Kenali layar (tampilkan nomor)", AutoSize = true };
            fill.Click += (s, e) => FillMonitors();
            ident.Click += (s, e) => IdentifyForm.Flash(ctx.Settings);
            mp.Controls.Add(monGrid); mp.Controls.Add(fill); mp.Controls.Add(ident);
            Wide(t, mp);
            manualMon.CheckedChanged += (s, e) => { monGrid.Enabled = manualMon.Checked; fill.Enabled = manualMon.Checked; };
            EventHandler gi = (s, e) => { int n = (int)per.Value; int c = (int)Math.Ceiling(Math.Sqrt(n)); gridInfo.Text = "Grid per layar: " + c + " kolom × " + (int)Math.Ceiling(n / (double)c) + " baris; kapasitas total " + (int)screens.Value * n + " kamera."; };
            screens.ValueChanged += gi; per.ValueChanged += gi;

            Head(t, "Tampilan halaman");
            Row(t, "Judul halaman", pageTitle);
            Row(t, "Mode pemutaran", pageMode, "mse,webrtc = stabil di mana saja (disarankan). webrtc,mse = latensi lebih rendah.");
            Row(t, "Maks. fisheye di-dewarp bersamaan", dewarp, "Dewarp memakai GPU; batasi agar 48 tile tetap lancar.");
            Head(t, "Perilaku");
            Wide(t, allowClose);
            Wide(t, autostart);
            return Page("Layar", t);
        }

        TabPage BuildPlayback()
        {
            var t = Table();
            Head(t, "Playback NVR (klip rekaman)");
            Row(t, "Zona waktu perintah ke NVR", timeMode, "Utc (default). Ganti ke Local jika klip yang diterima bergeser jam dari yang diminta.");
            Row(t, "Jenis klip", clipKind, "MP4VO = tanpa audio (aman di browser). MP4 = dengan audio.");
            Row(t, "Durasi maksimal klip (menit)", maxClip, "NVR membatasi 1 jam per permintaan.");
            Row(t, "Batas waktu unduh klip (menit)", clipTimeout);
            Row(t, "Simpan klip sementara (menit)", clipKeep, "Klip dihapus otomatis setelah waktu ini.");
            Row(t, "Unduhan bersamaan", maxDl, "Jaga beban NVR tetap rendah.");
            Row(t, "Judul halaman playback", bridgeTitle);
            Head(t, "PTZ kamera");
            Row(t, "User PTZ", ptzUser, "Kosong = memakai user kamera dari tab Kamera NVR.");
            Row(t, "Password PTZ", ptzPass);
            Row(t, "Channel (Dahua)", ptzChannel, "Kamera IP tunggal Dahua biasanya 0.");
            Row(t, "Berhenti otomatis setelah (detik)", ptzMax, "Jika tombol stop tidak sampai ke kamera.");
            Wide(t, new Label { AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 6, 0, 0), Text = "Kamera mana yang punya PTZ ditandai di tab Kamera NVR (kolom PTZ). Kamera i-PRO memakai ONVIF, kamera Dahua/seri E memakai ptz.cgi." });
            Head(t, "Diagnosis");
            Wide(t, verbose);
            return Page("Playback & PTZ", t);
        }

        TabPage BuildLayanan()
        {
            var t = Table();
            Head(t, "go2rtc (penarik stream RTSP)");
            Row(t, "Port web/API", apiPort, "Halaman multiview dibuka di port ini (default 1984).");
            Row(t, "Port RTSP ulang", rtspPort, "0 = mati (disarankan). Isi 8554 jika ingin membuka stream dari VLC.");
            Row(t, "Port WebRTC", webrtcPort);
            Row(t, "Tingkat log go2rtc", go2rtcLog);
            Wide(t, allowLan);
            Row(t, "go2rtc.exe", PathBox(go2rtcExe, false, "go2rtc.exe|go2rtc*.exe|Semua berkas|*.*"));
            Row(t, "Berkas go2rtc.yaml", PathBox(go2rtcCfg, false, "YAML|*.yaml;*.yml|Semua berkas|*.*"), "Berisi daftar kamera; dikelola dari tab Kamera NVR.");
            Row(t, "Folder halaman (www)", PathBox(wwwDir, true, null));
            Head(t, "NvrBridge");
            Wide(t, startBridge);
            Row(t, "Port NvrBridge", bridgePort, "Default 8090.");
            Row(t, "Login NvrBridge (opsional)", bridgeUser, "User. Jika diisi, PTZ/peta di layar kamera tidak bisa dipakai (browser tidak mengirim login lintas-origin).");
            Row(t, "Password NvrBridge", bridgePass);
            Row(t, "NvrBridge.exe", PathBox(bridgeExe, false, "NvrBridge.exe|NvrBridge.exe|Semua berkas|*.*"));
            Row(t, "Berkas nvrbridge.config", PathBox(bridgeCfg, false, "config|*.config|Semua berkas|*.*"));
            Head(t, "Lain-lain");
            Row(t, "Port kontrol lokal", controlPort, "Untuk tombol Keluar/Panel di halaman. 0 = mati.");
            Row(t, "Folder data dan log", PathBox(dataDir, true, null), "Kosong = folder data di samping CctvViewer.exe.");
            Row(t, "Argumen tambahan WebView2", webArgs, "Opsional (Chromium flags).");
            return Page("Layanan", t);
        }

        // ---------- isi nilai ----------
        string B(string key, string def) { string v; return bc.TryGetValue(key, out v) && v.Length > 0 ? v : def; }
        static int I(string s, int def) { int n; return int.TryParse(s, NumberStyles.Integer, CultureInfo.InvariantCulture, out n) ? n : def; }
        static void Set(NumericUpDown n, int v) { n.Value = Math.Max(n.Minimum, Math.Min(n.Maximum, v)); }
        static void Pick(ComboBox c, string v, int fallback) { int i = c.Items.IndexOf(v); c.SelectedIndex = i >= 0 ? i : fallback; }

        void LoadValues()
        {
            Set(screens, st.Screens); Set(per, st.CamsPerScreen); Set(wait, st.MonitorWaitSeconds); Set(reload, st.ReloadMinutes); Set(dewarp, st.DewarpMax);
            pageTitle.Text = st.PageTitle; Pick(pageMode, st.PageMode, 0);
            allowClose.Checked = st.AllowCloseScreen; autostart.Checked = Program.AutostartEnabled();
            manualMon.Checked = st.ManualMonitors != null;
            foreach (var a in st.ManualMonitors ?? new List<Area>()) monGrid.Rows.Add(a.X, a.Y, a.W, a.H);
            monGrid.Enabled = manualMon.Checked;
            screens.Value = screens.Value; per.Value = per.Value;
            gridInfo.Text = "Grid per layar: " + (int)Math.Ceiling(Math.Sqrt(st.CamsPerScreen)) + " kolom; kapasitas total " + st.Screens * st.CamsPerScreen + " kamera.";

            Pick(timeMode, B("NvrTimeMode", "Utc"), 0); Pick(clipKind, B("ClipKind", "MP4VO").ToUpperInvariant(), 0);
            Set(maxClip, I(B("MaxClipMinutes", "10"), 10)); Set(clipTimeout, I(B("ClipTimeoutMinutes", "10"), 10));
            Set(clipKeep, I(B("ClipRetentionMinutes", "60"), 60)); Set(maxDl, I(B("MaxConcurrentDownloads", "2"), 2));
            ptzUser.Text = B("PtzUser", ""); ptzPass.Text = B("PtzPassword", ""); Set(ptzChannel, I(B("PtzChannel", "0"), 0)); Set(ptzMax, I(B("PtzMaxMoveSeconds", "8"), 8));
            bridgeTitle.Text = B("Title", "Playback NVR");
            verbose.Checked = B("Verbose", "false").Equals("true", StringComparison.OrdinalIgnoreCase);

            Set(apiPort, st.Go2rtcApi.Port); Set(rtspPort, st.RtspPort); Set(webrtcPort, st.WebrtcPort); Pick(go2rtcLog, st.Go2rtcLog, 1);
            allowLan.Checked = st.AllowLan; startBridge.Checked = st.StartNvrBridge; Set(bridgePort, st.BridgeUrl.Port);
            bridgeUser.Text = B("BridgeUser", ""); bridgePass.Text = B("BridgePassword", "");
            Set(controlPort, st.ControlPort);
            go2rtcExe.Text = st.Go2rtcExe; go2rtcCfg.Text = st.Go2rtcConfig; wwwDir.Text = st.WwwDir;
            bridgeExe.Text = st.NvrBridgeExe; bridgeCfg.Text = st.NvrBridgeConfig;
            dataDir.Text = st.Raw("DataDir", ""); webArgs.Text = st.WebViewArgs;
        }

        void FillMonitors()
        {
            monGrid.Rows.Clear();
            var list = Screen.AllScreens.Select(s => s.Bounds).OrderBy(b => b.X).ThenBy(b => b.Y).ToList();
            foreach (var b in list) monGrid.Rows.Add(b.X, b.Y, b.Width, b.Height);
        }

        string MonitorsText()
        {
            if (!manualMon.Checked) return "auto";
            var parts = new List<string>();
            foreach (DataGridViewRow r in monGrid.Rows)
            {
                var v = new int[4];
                for (int i = 0; i < 4; i++)
                {
                    int n;
                    if (!int.TryParse(Convert.ToString(r.Cells[i].Value, CultureInfo.InvariantCulture), NumberStyles.Integer, CultureInfo.InvariantCulture, out n))
                        throw new ArgumentException("Posisi monitor harus berupa angka bulat (baris " + (r.Index + 1) + ").");
                    v[i] = n;
                }
                if (v[2] < 200 || v[3] < 200) throw new ArgumentException("Lebar dan tinggi monitor minimal 200 (baris " + (r.Index + 1) + ").");
                parts.Add(string.Join(",", v.Select(x => x.ToString(CultureInfo.InvariantCulture))));
            }
            if (parts.Count == 0) throw new ArgumentException("Daftar monitor manual kosong. Klik \"Isi dari monitor saat ini\" atau matikan pengaturan manual.");
            if (parts.Count < (int)screens.Value) throw new ArgumentException("Jumlah baris monitor manual (" + parts.Count + ") kurang dari jumlah layar (" + (int)screens.Value + ").");
            return string.Join(";", parts);
        }

        static string N(NumericUpDown n) { return ((int)n.Value).ToString(CultureInfo.InvariantCulture); }
        static string Bo(CheckBox c) { return c.Checked ? "true" : "false"; }

        bool Save()
        {
            try
            {
                string mon = MonitorsText();
                var ports = new List<int> { (int)apiPort.Value, (int)bridgePort.Value, (int)webrtcPort.Value };
                if (rtspPort.Value > 0) ports.Add((int)rtspPort.Value);
                if (controlPort.Value > 0) ports.Add((int)controlPort.Value);
                if (ports.Distinct().Count() != ports.Count) throw new ArgumentException("Port go2rtc, RTSP, WebRTC, NvrBridge, dan kontrol tidak boleh sama.");
                if (ptzUser.Text.Contains("\n") || bridgeTitle.Text.Contains("\n") || pageTitle.Text.Contains("\n")) throw new ArgumentException("Isian tidak boleh berisi baris baru.");
                if ((bridgeUser.Text.Length == 0) != (bridgePass.Text.Length == 0)) throw new ArgumentException("Login NvrBridge: isi user dan password keduanya, atau kosongkan keduanya.");
                foreach (var tb in new[] { go2rtcExe, go2rtcCfg, wwwDir, bridgeExe, bridgeCfg })
                    if (tb.Text.Trim().Length == 0) throw new ArgumentException("Jalur berkas/folder di tab Layanan tidak boleh kosong.");

                string loopback = "http://127.0.0.1:";
                var v = new Dictionary<string, string>
                {
                    {"Screens", N(screens)}, {"CamsPerScreen", N(per)}, {"Monitors", mon}, {"MonitorWaitSeconds", N(wait)}, {"ReloadMinutes", N(reload)},
                    {"AllowCloseScreen", Bo(allowClose)}, {"WebViewArgs", webArgs.Text.Trim()},
                    {"PageTitle", pageTitle.Text.Trim().Length > 0 ? pageTitle.Text.Trim() : "CCTV Multiview"}, {"PageMode", (string)pageMode.SelectedItem ?? "mse,webrtc"}, {"DewarpMax", N(dewarp)},
                    {"AllowLan", Bo(allowLan)}, {"StartNvrBridge", Bo(startBridge)},
                    {"Go2rtcApi", loopback + N(apiPort)}, {"Go2rtcRtspPort", N(rtspPort)}, {"Go2rtcWebrtcPort", N(webrtcPort)}, {"Go2rtcLog", (string)go2rtcLog.SelectedItem ?? "warn"},
                    {"Go2rtcExe", go2rtcExe.Text.Trim()}, {"Go2rtcConfig", go2rtcCfg.Text.Trim()}, {"WwwDir", wwwDir.Text.Trim()},
                    {"BridgeUrl", loopback + N(bridgePort)}, {"NvrBridgeExe", bridgeExe.Text.Trim()}, {"NvrBridgeConfig", bridgeCfg.Text.Trim()},
                    {"ControlPort", N(controlPort)}, {"DataDir", dataDir.Text.Trim()},
                };
                ConfigWriter.Set(ctx.ConfigPath, v);

                // nvrbridge.config: ListenPrefix/AllowOrigin/Go2rtc* diselaraskan otomatis oleh CctvViewer saat mulai.
                string bcPath = Path.IsPathRooted(bridgeCfg.Text.Trim()) ? bridgeCfg.Text.Trim() : Path.GetFullPath(Path.Combine(st.BaseDir, bridgeCfg.Text.Trim()));
                Directory.CreateDirectory(Path.GetDirectoryName(bcPath));
                ConfigWriter.Set(bcPath, new Dictionary<string, string>
                {
                    {"NvrTimeMode", (string)timeMode.SelectedItem ?? "Utc"}, {"ClipKind", (string)clipKind.SelectedItem ?? "MP4VO"},
                    {"MaxClipMinutes", N(maxClip)}, {"ClipTimeoutMinutes", N(clipTimeout)}, {"ClipRetentionMinutes", N(clipKeep)}, {"MaxConcurrentDownloads", N(maxDl)},
                    {"PtzUser", ptzUser.Text.Trim()}, {"PtzPassword", ptzPass.Text}, {"PtzChannel", N(ptzChannel)}, {"PtzMaxMoveSeconds", N(ptzMax)},
                    {"Title", bridgeTitle.Text.Trim().Length > 0 ? bridgeTitle.Text.Trim() : "Playback NVR"}, {"Verbose", Bo(verbose)},
                    {"BridgeUser", bridgeUser.Text.Trim()}, {"BridgePassword", bridgePass.Text},
                });
                Program.SetAutostartQuiet(autostart.Checked);
                Log.Info("Pengaturan disimpan.");
                if (allowLan.Checked && !st.AllowLan)
                    MessageBox.Show(this, "Akses dari PC lain aktif. Agar NvrBridge boleh menerima koneksi jaringan, jalankan sekali sebagai Administrator:\n\nnetsh http add urlacl url=http://+:" + N(bridgePort) + "/ user=Everyone\n\nBuka juga port " + N(apiPort) + " dan " + N(bridgePort) + " di Windows Firewall. Isi login NvrBridge di tab Layanan, dan jangan ekspos ke internet.", Program.AppName);
                return true;
            }
            catch (ArgumentException e) { MessageBox.Show(this, e.Message, Program.AppName, MessageBoxButtons.OK, MessageBoxIcon.Warning); return false; }
            catch (Exception e) { MessageBox.Show(this, "Tidak bisa menyimpan: " + e.Message, Program.AppName); return false; }
        }
    }
}
