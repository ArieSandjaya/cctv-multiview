using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Drawing;
using System.Globalization;
using System.Linq;
using System.Text;
using System.Threading.Tasks;
using System.Windows.Forms;
using NvrBridge;

namespace CctvViewer
{
    /// <summary>Satu baris kamera di daftar (sama dengan baris registry.json di NvrBridge).</summary>
    sealed class CamRow
    {
        public bool Enabled { get; set; }
        public int Num { get; set; }          // nomor tampil (nama stream camNN, nomor playback)
        public string Nvr { get; set; }       // id NVR ("" = tanpa NVR)
        public int Cam { get; set; }          // nomor CAM di NVR
        public string Probe { get; set; }
        public string Name { get; set; }
        public string Ip { get; set; }
        public string Brand { get; set; }
        public string Rtsp { get; set; }
        public string RtspHd { get; set; }
        public bool Ptz { get; set; }
        public bool Fisheye { get; set; }
        public string Note { get; set; }
        public string FeMount { get; set; }
        public int FeFov { get; set; }
        public string FeTile { get; set; }
        public string FeMode { get; set; }
        public bool FeMirror { get; set; }
        public string FeCx { get; set; }
        public string FeCy { get; set; }
        public string FeR { get; set; }

        public CamRow()
        {
            Enabled = true; Nvr = ""; Probe = "manual"; Name = ""; Ip = ""; Brand = "dahua"; Rtsp = ""; RtspHd = ""; Note = "";
            FeMount = "ceiling"; FeFov = 180; FeTile = "view"; FeMode = "view"; FeCx = ""; FeCy = ""; FeR = "";
        }

        public static CamRow FromJson(Dictionary<string, object> c)
        {
            var r = new CamRow
            {
                Enabled = MiniJson.Bool(c, "enabled", true), Cam = MiniJson.Int(c, "cam", 0), Num = MiniJson.Int(c, "num", MiniJson.Int(c, "cam", 0)), Nvr = MiniJson.Str(c, "nvr"), Probe = MiniJson.Str(c, "probe", "manual"),
                Name = MiniJson.Str(c, "name"), Ip = MiniJson.Str(c, "ip"), Brand = MiniJson.Str(c, "brand", "dahua"),
                Rtsp = MiniJson.Str(c, "rtsp"), RtspHd = MiniJson.Str(c, "rtspHd"), Ptz = MiniJson.Bool(c, "ptz", false), Note = MiniJson.Str(c, "note"),
            };
            var fe = MiniJson.Obj(c.ContainsKey("fe") ? c["fe"] : null);
            if (fe != null)
            {
                r.Fisheye = true;
                r.FeMount = MiniJson.Str(fe, "mount", "ceiling"); r.FeFov = MiniJson.Int(fe, "fov", 180);
                r.FeTile = MiniJson.Str(fe, "tile", "view"); r.FeMode = MiniJson.Str(fe, "mode", "view"); r.FeMirror = MiniJson.Bool(fe, "mirror", false);
                r.FeCx = FeNum(fe, "cx"); r.FeCy = FeNum(fe, "cy"); r.FeR = FeNum(fe, "r");
            }
            return r;
        }

        static string FeNum(Dictionary<string, object> d, string k)
        {
            object v; return d.TryGetValue(k, out v) && v is double ? ((double)v).ToString("0.####", CultureInfo.InvariantCulture) : "";
        }

        static string NumOrNull(string s)
        {
            s = (s ?? "").Trim().Replace(',', '.');
            double x;
            if (s.Length == 0) return null;
            if (!double.TryParse(s, NumberStyles.Float, CultureInfo.InvariantCulture, out x)) throw new ArgumentException("Nilai '" + s + "' bukan angka (0-1).");
            return x.ToString("0.####", CultureInfo.InvariantCulture);
        }

        public string ToJson()
        {
            var sb = new StringBuilder();
            sb.Append("{\"cam\":").Append(Cam).Append(",\"num\":").Append(Num).Append(",\"nvr\":").Append(Json.Q(Nvr ?? "")).Append(",\"name\":").Append(Json.Q(Name ?? "")).Append(",\"ip\":").Append(Json.Q((Ip ?? "").Trim()))
              .Append(",\"brand\":").Append(Json.Q(Brand ?? "dahua")).Append(",\"rtsp\":").Append(Json.Q((Rtsp ?? "").Trim()))
              .Append(",\"rtspHd\":").Append(Json.Q((RtspHd ?? "").Trim())).Append(",\"ptz\":").Append(Ptz ? "true" : "false")
              .Append(",\"enabled\":").Append(Enabled ? "true" : "false").Append(",\"probe\":").Append(Json.Q(Probe ?? "manual"))
              .Append(",\"note\":").Append(Json.Q(Note ?? ""));
            if (!Fisheye) sb.Append(",\"fe\":null");
            else
            {
                sb.Append(",\"fe\":{\"mount\":").Append(Json.Q(FeMount)).Append(",\"fov\":").Append(FeFov).Append(",\"tile\":").Append(Json.Q(FeTile))
                  .Append(",\"mode\":").Append(Json.Q(FeMode)).Append(",\"mirror\":").Append(FeMirror ? "true" : "false");
                string cx = NumOrNull(FeCx), cy = NumOrNull(FeCy), rr = NumOrNull(FeR);
                if (cx != null) sb.Append(",\"cx\":").Append(cx);
                if (cy != null) sb.Append(",\"cy\":").Append(cy);
                if (rr != null) sb.Append(",\"r\":").Append(rr);
                sb.Append("}");
            }
            return sb.Append("}").ToString();
        }
    }

    /// <summary>Satu NVR di daftar. Password hanya terisi jika diketik; kosong = pertahankan yang tersimpan.</summary>
    sealed class NvrRow
    {
        public string Id { get; set; }
        public string Name { get; set; }
        public string Url = "", User = "", Pass = "", Scheme = "Digest", CamUser = "", CamPass = "";
        public bool SelfSigned = true, HasPass, HasCamPass;
        public NvrRow() { Id = "nvr1"; Name = "NVR 1"; }

        public string ToJson()
        {
            return "{\"id\":" + Json.Q(Id) + ",\"name\":" + Json.Q(Name ?? "") + ",\"url\":" + Json.Q((Url ?? "").Trim()) + ",\"user\":" + Json.Q((User ?? "").Trim()) +
                   ",\"pass\":" + Json.Q(Pass ?? "") + ",\"scheme\":" + Json.Q(Scheme ?? "Digest") + ",\"selfSigned\":" + (SelfSigned ? "true" : "false") +
                   ",\"camUser\":" + Json.Q((CamUser ?? "").Trim()) + ",\"camPass\":" + Json.Q(CamPass ?? "") + "}";
        }
    }

    /// <summary>Tab "Kamera NVR": registrasi NVR, pemindaian CAM 1..N, dan editor daftar kamera (termasuk fisheye dan urutan tampil). Berbicara dengan NvrBridge.</summary>
    sealed class CameraTab : UserControl
    {
        readonly WallContext ctx;
        readonly Settings st;
        readonly BridgeClient api;
        readonly BindingList<CamRow> rows = new BindingList<CamRow>();
        readonly DataGridView grid = new DataGridView();

        readonly BindingList<NvrRow> nvrs = new BindingList<NvrRow>();
        readonly ComboBox nvrSel = new ComboBox { Width = 150, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly TextBox nvrName = new TextBox { Width = 110 };
        readonly TextBox nvrUrl = new TextBox { Width = 170 }, nvrUser = new TextBox { Width = 90 }, nvrPass = new TextBox { Width = 90, UseSystemPasswordChar = true };
        readonly ComboBox scheme = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckBox selfSigned = new CheckBox { Text = "Sertifikat self-signed OK", AutoSize = true, Checked = true };
        readonly TextBox camUser = new TextBox { Width = 90 }, camPass = new TextBox { Width = 90, UseSystemPasswordChar = true };
        readonly ComboBox defBrand = new ComboBox { Width = 70, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly NumericUpDown maxCam = new NumericUpDown { Minimum = 1, Maximum = 128, Value = 64, Width = 60 };
        readonly TextBox probeTime = new TextBox { Width = 120 };
        readonly Button scanBtn = new Button { Text = "Pindai kamera di NVR", AutoSize = true };
        readonly Button cancelBtn = new Button { Text = "Batal", AutoSize = true, Visible = false };
        readonly ProgressBar bar = new ProgressBar { Width = 160, Minimum = 0, Maximum = 100, Visible = false };
        readonly Label scanMsg = new Label { AutoSize = true, ForeColor = Color.DimGray };
        readonly TextBox others = new TextBox { Multiline = true, ReadOnly = true, ScrollBars = ScrollBars.Vertical, Height = 54, Dock = DockStyle.Fill };

        readonly GroupBox fe = new GroupBox { Text = "Fisheye kamera terpilih (centang kolom Fisheye di daftar)", Dock = DockStyle.Bottom, Height = 62 };
        readonly ComboBox feMount = new ComboBox { Width = 80, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly NumericUpDown feFov = new NumericUpDown { Minimum = 90, Maximum = 360, Value = 180, Width = 60 };
        readonly ComboBox feTile = new ComboBox { Width = 60, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly ComboBox feMode = new ComboBox { Width = 60, DropDownStyle = ComboBoxStyle.DropDownList };
        readonly CheckBox feMirror = new CheckBox { Text = "Cermin kiri-kanan", AutoSize = true };
        readonly TextBox feCx = new TextBox { Width = 45 }, feCy = new TextBox { Width = 45 }, feR = new TextBox { Width = 45 };

        readonly Label status = new Label { AutoSize = true, Dock = DockStyle.Fill, Padding = new Padding(2, 6, 2, 0) };
        readonly Timer poll = new Timer { Interval = 2000 };
        bool loaded, loading, busy;
        bool loadingNvr;
        string scanId, scanNvrId;

        public bool Dirty { get; private set; }

        public CameraTab(WallContext ctx)
        {
            this.ctx = ctx; st = ctx.Settings; api = new BridgeClient(st);
            Dock = DockStyle.Fill;
            scheme.Items.AddRange(new object[] { "Digest", "Basic" }); scheme.SelectedIndex = 0;
            defBrand.Items.AddRange(new object[] { "dahua", "ipro" }); defBrand.SelectedIndex = 0;
            feMount.Items.AddRange(new object[] { "ceiling", "wall" }); feMount.SelectedIndex = 0;
            feTile.Items.AddRange(new object[] { "view", "raw" }); feTile.SelectedIndex = 0;
            feMode.Items.AddRange(new object[] { "view", "pano", "raw" }); feMode.SelectedIndex = 0;
            probeTime.Text = "";

            // ----- atas: NVR + pemindaian -----
            var top = new FlowLayoutPanel { Dock = DockStyle.Top, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(4) };
            Control L(string t, Control c) { var p = new FlowLayoutPanel { AutoSize = true, FlowDirection = FlowDirection.LeftToRight, Margin = new Padding(0, 0, 10, 4) }; p.Controls.Add(new Label { Text = t, AutoSize = true, Padding = new Padding(0, 5, 3, 0) }); p.Controls.Add(c); return p; }
            nvrSel.DataSource = nvrs; nvrSel.DisplayMember = "Name"; nvrSel.ValueMember = "Id";
            var addNvr = new Button { Text = "Tambah NVR", AutoSize = true };
            var delNvr = new Button { Text = "Hapus NVR", AutoSize = true };
            top.Controls.Add(L("NVR", nvrSel)); top.Controls.Add(addNvr); top.Controls.Add(delNvr);
            top.Controls.Add(L("Nama", nvrName)); top.Controls.Add(L("Alamat", nvrUrl)); top.Controls.Add(L("User", nvrUser)); top.Controls.Add(L("Password", nvrPass)); top.Controls.Add(L("Login", scheme)); top.Controls.Add(selfSigned);
            top.SetFlowBreak(selfSigned, true);
            top.Controls.Add(L("User kamera (NVR ini)", camUser)); top.Controls.Add(L("Password kamera", camPass)); top.Controls.Add(L("Merek kamera baru", defBrand));
            top.Controls.Add(L("Uji CAM 1 sampai", maxCam)); top.Controls.Add(L("Waktu uji (kosong = otomatis)", probeTime));
            top.Controls.Add(scanBtn); top.Controls.Add(cancelBtn); top.Controls.Add(bar); top.Controls.Add(scanMsg);
            addNvr.Click += (s2, e2) => AddNvr();
            delNvr.Click += (s2, e2) => DeleteNvr();
            nvrSel.SelectedIndexChanged += (s2, e2) => ShowNvr();
            EventHandler pushNvr = (s2, e2) => PushNvr();
            nvrName.TextChanged += pushNvr; nvrUrl.TextChanged += pushNvr; nvrUser.TextChanged += pushNvr; nvrPass.TextChanged += pushNvr;
            camUser.TextChanged += pushNvr; camPass.TextChanged += pushNvr; scheme.SelectedIndexChanged += pushNvr; selfSigned.CheckedChanged += pushNvr;

            // ----- grid -----
            grid.Dock = DockStyle.Fill; grid.AllowUserToAddRows = false; grid.AllowUserToDeleteRows = false; grid.AutoGenerateColumns = false;
            grid.SelectionMode = DataGridViewSelectionMode.FullRowSelect; grid.MultiSelect = false; grid.RowHeadersVisible = false;
            grid.AutoSizeColumnsMode = DataGridViewAutoSizeColumnsMode.None; grid.BackgroundColor = SystemColors.Window;
            DataGridViewColumn C(DataGridViewColumn c, string head, string prop, int w) { c.HeaderText = head; c.DataPropertyName = prop; c.Width = w; grid.Columns.Add(c); return c; }
            C(new DataGridViewCheckBoxColumn(), "Aktif", "Enabled", 42);
            C(new DataGridViewTextBoxColumn(), "No.", "Num", 40);
            C(new DataGridViewComboBoxColumn { DataSource = nvrs, ValueMember = "Id", DisplayMember = "Name" }, "NVR", "Nvr", 95);
            C(new DataGridViewTextBoxColumn(), "CAM", "Cam", 42);
            C(new DataGridViewTextBoxColumn { ReadOnly = true }, "Status NVR", "Probe", 70);
            C(new DataGridViewTextBoxColumn(), "Nama", "Name", 150);
            C(new DataGridViewTextBoxColumn(), "IP kamera", "Ip", 110);
            C(new DataGridViewComboBoxColumn { DataSource = new[] { "dahua", "ipro", "custom" } }, "Merek", "Brand", 62);
            C(new DataGridViewTextBoxColumn(), "RTSP sub (kosong = otomatis)", "Rtsp", 230);
            C(new DataGridViewTextBoxColumn(), "RTSP utama (kosong = otomatis)", "RtspHd", 230);
            C(new DataGridViewCheckBoxColumn(), "PTZ", "Ptz", 40);
            C(new DataGridViewCheckBoxColumn(), "Fisheye", "Fisheye", 55);
            grid.DataSource = rows;
            grid.DataError += (s, e) => { e.ThrowException = false; };
            grid.CurrentCellDirtyStateChanged += (s, e) => { if (grid.IsCurrentCellDirty) grid.CommitEdit(DataGridViewDataErrorContexts.Commit); };
            grid.CellValueChanged += (s, e) => { if (!loading) { Dirty = true; if (e.RowIndex >= 0) ShowFe(); } };
            grid.SelectionChanged += (s, e) => ShowFe();

            // ----- fisheye -----
            var fl = new FlowLayoutPanel { Dock = DockStyle.Fill, FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            fl.Controls.Add(L("Pemasangan", feMount)); fl.Controls.Add(L("Sudut pandang °", feFov)); fl.Controls.Add(L("Tile", feTile)); fl.Controls.Add(L("Saat diperbesar", feMode));
            fl.Controls.Add(feMirror); fl.Controls.Add(L("Pusat X", feCx)); fl.Controls.Add(L("Y", feCy)); fl.Controls.Add(L("Jari-jari", feR));
            fl.Controls.Add(new Label { Text = "(X/Y/jari-jari: pecahan 0-1, kosongkan jika lingkaran di tengah)", AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(0, 5, 0, 0) });
            fe.Controls.Add(fl);
            EventHandler fe1 = (s, e) => ApplyFe();
            feMount.SelectedIndexChanged += fe1; feFov.ValueChanged += fe1; feTile.SelectedIndexChanged += fe1; feMode.SelectedIndexChanged += fe1;
            feMirror.CheckedChanged += fe1; feCx.TextChanged += fe1; feCy.TextChanged += fe1; feR.TextChanged += fe1;

            // ----- bawah: tombol -----
            var btn = new FlowLayoutPanel { Dock = DockStyle.Bottom, AutoSize = true, FlowDirection = FlowDirection.LeftToRight, WrapContents = true, Padding = new Padding(2) };
            Button B(string t, Action a) { var b = new Button { Text = t, AutoSize = true }; b.Click += (s, e) => a(); btn.Controls.Add(b); return b; }
            B("Tambah kamera", AddRow); B("Hapus", DeleteRow); B("▲ Naik", () => Move(-1)); B("▼ Turun", () => Move(1));
            B("Muat ulang dari NvrBridge", () => { var t = LoadAsync(true); });
            var save = B("Simpan daftar", () => { var t = SaveAsync(false); });
            var apply = B("Simpan && terapkan ke go2rtc", () => { var t = SaveAsync(true); });
            var hint = new Label
            {
                AutoSize = true, ForeColor = Color.DimGray, Padding = new Padding(2, 4, 2, 4),
                Text = "Urutan baris = urutan tampil: 16 baris pertama ke layar 1, berikutnya layar 2, dst. Tombol di tab ini langsung berlaku (tidak perlu \"Simpan\" di bawah jendela). Menerapkan ke go2rtc memulai ulang go2rtc dan memuat ulang layar."
            };

            var scanBox = new Panel { Dock = DockStyle.Bottom, Height = 60 };
            scanBox.Controls.Add(others);
            var othersLbl = new Label { Text = "Jawaban NVR untuk CAM yang tidak terdaftar / tanpa rekaman:", Dock = DockStyle.Top, AutoSize = true, ForeColor = Color.DimGray };
            scanBox.Controls.Add(othersLbl);

            Controls.Add(grid);
            Controls.Add(top);
            Controls.Add(scanBox);
            Controls.Add(fe);
            Controls.Add(btn);
            Controls.Add(hint);
            Controls.Add(status);

            scanBtn.Click += async (s, e) => await StartScanAsync();
            cancelBtn.Click += async (s, e) => { try { await api.SendAsync("POST", "api/registry/scan?cancel=1&id=" + Uri.EscapeDataString(scanId ?? ""), "{}"); } catch (Exception ex) { scanMsg.Text = ex.Message; } };
            poll.Tick += async (s, e) => await PollAsync();
            Disposed += (s, e) => poll.Dispose();
            ShowFe();
        }

        void SetStatus(string t, bool err = false) { status.Text = t; status.ForeColor = err ? Color.Firebrick : Color.DimGray; }

        public void EnsureLoaded() { if (!loaded) { var t = LoadAsync(false); } }

        CamRow Current() { return grid.CurrentRow != null ? grid.CurrentRow.DataBoundItem as CamRow : null; }

        void ShowFe()
        {
            var r = Current();
            bool on = r != null && r.Fisheye;
            loading = true;
            try
            {
                fe.Enabled = on;
                if (on)
                {
                    feMount.SelectedItem = r.FeMount; feFov.Value = Math.Max(90, Math.Min(360, r.FeFov)); feTile.SelectedItem = r.FeTile; feMode.SelectedItem = r.FeMode;
                    feMirror.Checked = r.FeMirror; feCx.Text = r.FeCx; feCy.Text = r.FeCy; feR.Text = r.FeR;
                }
            }
            finally { loading = false; }
        }

        void ApplyFe()
        {
            if (loading) return;
            var r = Current();
            if (r == null || !r.Fisheye) return;
            r.FeMount = (string)feMount.SelectedItem ?? "ceiling"; r.FeFov = (int)feFov.Value; r.FeTile = (string)feTile.SelectedItem ?? "view";
            r.FeMode = (string)feMode.SelectedItem ?? "view"; r.FeMirror = feMirror.Checked; r.FeCx = feCx.Text; r.FeCy = feCy.Text; r.FeR = feR.Text;
            Dirty = true;
        }

        int FreeNum(int prefer)
        {
            var used = new HashSet<int>(rows.Select(x => x.Num));
            if (prefer >= 1 && prefer <= 999 && !used.Contains(prefer)) return prefer;
            int n = 1; while (used.Contains(n)) n++;
            return n;
        }

        NvrRow SelNvr { get { return nvrSel.SelectedItem as NvrRow; } }

        void AddRow()
        {
            var nv = SelNvr;
            int ch = 1;
            if (nv != null) { var usedCh = new HashSet<int>(rows.Where(x => x.Nvr == nv.Id).Select(x => x.Cam)); while (usedCh.Contains(ch)) ch++; }
            int num = FreeNum(ch);
            rows.Add(new CamRow { Num = num, Cam = ch, Nvr = nv != null ? nv.Id : "", Name = "Kamera " + num, Brand = (string)defBrand.SelectedItem ?? "dahua" });
            Dirty = true;
            grid.ClearSelection(); grid.CurrentCell = grid.Rows[grid.Rows.Count - 1].Cells[5];
        }

        // ---------- NVR ----------
        void ShowNvr()
        {
            var n = SelNvr;
            loadingNvr = true;
            try
            {
                foreach (Control c in new Control[] { nvrName, nvrUrl, nvrUser, nvrPass, scheme, selfSigned, camUser, camPass, scanBtn }) c.Enabled = n != null;
                if (n == null) { nvrName.Text = nvrUrl.Text = nvrUser.Text = nvrPass.Text = camUser.Text = camPass.Text = ""; return; }
                nvrName.Text = n.Name; nvrUrl.Text = n.Url; nvrUser.Text = n.User; nvrPass.Text = n.Pass;
                scheme.SelectedItem = n.Scheme; if (scheme.SelectedIndex < 0) scheme.SelectedIndex = 0;
                selfSigned.Checked = n.SelfSigned; camUser.Text = n.CamUser; camPass.Text = n.CamPass;
                nvrPass.Tag = n.HasPass ? "(tersimpan)" : null; camPass.Tag = n.HasCamPass ? "(tersimpan)" : null;
            }
            finally { loadingNvr = false; }
        }

        void PushNvr()
        {
            if (loadingNvr || loading) return;
            var n = SelNvr; if (n == null) return;
            bool renamed = n.Name != nvrName.Text;
            n.Name = nvrName.Text; n.Url = nvrUrl.Text; n.User = nvrUser.Text; n.Pass = nvrPass.Text; n.Scheme = (string)scheme.SelectedItem ?? "Digest";
            n.SelfSigned = selfSigned.Checked; n.CamUser = camUser.Text; n.CamPass = camPass.Text;
            Dirty = true;
            if (renamed) { loadingNvr = true; try { int i = nvrs.IndexOf(n); nvrs.ResetItem(i); nvrSel.SelectedIndex = i; } finally { loadingNvr = false; } }
        }

        void AddNvr()
        {
            int k = 1; var ids = new HashSet<string>(nvrs.Select(x => x.Id));
            while (ids.Contains("nvr" + k)) k++;
            var n = new NvrRow { Id = "nvr" + k, Name = "NVR " + k };
            nvrs.Add(n); nvrSel.SelectedItem = n; Dirty = true;
            nvrName.Focus(); nvrName.SelectAll();
        }

        void DeleteNvr()
        {
            var n = SelNvr; if (n == null) return;
            int cnt = rows.Count(x => x.Nvr == n.Id);
            if (MessageBox.Show(this, "Hapus NVR \"" + n.Name + "\"" + (cnt > 0 ? " beserta " + cnt + " kameranya dari daftar" : "") + "?", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            foreach (var r in rows.Where(x => x.Nvr == n.Id).ToList()) rows.Remove(r);
            nvrs.Remove(n); Dirty = true;
            ShowNvr();
        }

        void DeleteRow()
        {
            var r = Current(); if (r == null) return;
            if (MessageBox.Show(this, "Hapus kamera CAM " + r.Cam + " (" + r.Name + ") dari daftar?", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            rows.Remove(r); Dirty = true;
        }

        void Move(int d)
        {
            var r = Current(); if (r == null) return;
            int i = rows.IndexOf(r), j = i + d;
            if (j < 0 || j >= rows.Count) return;
            rows.RemoveAt(i); rows.Insert(j, r); Dirty = true;
            grid.ClearSelection(); grid.CurrentCell = grid.Rows[j].Cells[3];
        }

        // ---------- muat / simpan ----------
        async Task LoadAsync(bool userAction)
        {
            if (busy) return;
            if (userAction && Dirty && MessageBox.Show(this, "Perubahan yang belum disimpan akan hilang. Lanjutkan?", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) != DialogResult.Yes) return;
            busy = true; SetStatus("Memuat dari NvrBridge…");
            try
            {
                var d = await api.SendAsync("GET", "api/registry", null, 6000);
                loading = true;
                nvrs.Clear(); rows.Clear();
                foreach (var o in MiniJson.List(d, "nvrs") ?? new List<object>())
                {
                    var n = MiniJson.Obj(o); if (n == null) continue;
                    nvrs.Add(new NvrRow
                    {
                        Id = MiniJson.Str(n, "id", "nvr1"), Name = MiniJson.Str(n, "name"), Url = MiniJson.Str(n, "url"), User = MiniJson.Str(n, "user"),
                        Scheme = MiniJson.Str(n, "scheme", "Digest"), SelfSigned = MiniJson.Bool(n, "selfSigned", true), CamUser = MiniJson.Str(n, "camUser"),
                        HasPass = MiniJson.Bool(n, "hasPass", false), HasCamPass = MiniJson.Bool(n, "hasCamPass", false),
                    });
                }
                foreach (var o in MiniJson.List(d, "cams") ?? new List<object>()) { var c = MiniJson.Obj(o); if (c != null) rows.Add(CamRow.FromJson(c)); }
                loading = false;
                loaded = true; Dirty = false;
                SetStatus(nvrs.Count + " NVR, " + rows.Count + " kamera. Password yang sudah tersimpan tidak ditampilkan; kosongkan kolomnya jika tidak diubah.");
                loadingNvr = false; ShowNvr(); ShowFe();
            }
            catch (Exception e) { loading = false; SetStatus("Tidak bisa memuat: " + e.Message, true); }
            finally { busy = false; }
        }

        string Payload(bool forScan)
        {
            var sb = new StringBuilder();
            sb.Append("{\"nvrs\":[").Append(string.Join(",", nvrs.Select(n => n.ToJson()))).Append("],\"cams\":[");
            if (!forScan) sb.Append(string.Join(",", rows.Select(r => r.ToJson())));
            sb.Append("]");
            if (forScan)
            {
                sb.Append(",\"scanNvr\":").Append(Json.Q(SelNvr != null ? SelNvr.Id : ""));
                sb.Append(",\"max\":").Append((int)maxCam.Value);
                string pt = probeTime.Text.Trim();
                if (pt.Length > 0) sb.Append(",\"probeTime\":").Append(Json.Q(pt));
            }
            return sb.Append("}").ToString();
        }

        async Task SaveAsync(bool apply)
        {
            if (busy) return;
            if (!loaded) { SetStatus("Daftar belum dimuat dari NvrBridge.", true); return; }
            busy = true;
            try
            {
                grid.EndEdit();
                SetStatus("Menyimpan…");
                string body;
                try { body = Payload(false); }
                catch (ArgumentException ex) { SetStatus(ex.Message, true); MessageBox.Show(this, ex.Message, Program.AppName); return; }
                await api.SendAsync("PUT", "api/registry", body);
                Dirty = false;
                if (!apply) { SetStatus("Daftar kamera disimpan. Klik \"Simpan & terapkan ke go2rtc\" agar tampil di layar."); return; }

                SetStatus("Daftar disimpan. Menulis ke go2rtc dan memulai ulang…");
                // NvrBridge memuat ulang datanya sekitar 1 detik setelah menyimpan; coba beberapa kali.
                string last = "";
                bool ok = false;
                for (int i = 0; i < 5 && !ok; i++)
                {
                    await Task.Delay(1500);
                    try { await api.SendAsync("POST", "api/registry/export", "{\"restart\":true}"); ok = true; }
                    catch (BridgeException ex) { last = ex.Message; }
                }
                if (!ok) { SetStatus("Gagal menerapkan ke go2rtc: " + last, true); MessageBox.Show(this, "Daftar tersimpan, tetapi go2rtc.yaml tidak bisa ditulis:\n\n" + last, Program.AppName); return; }
                SetStatus("go2rtc dimulai ulang. Layar akan dimuat ulang…");
                await Task.Delay(7000);
                ctx.ReloadAll();
                int n = await Task.Run(() => Core.CameraCount(st));
                SetStatus("Selesai. Kamera aktif di go2rtc: " + (n < 0 ? "belum terbaca" : n.ToString()) + " (kapasitas " + st.Screens * st.CamsPerScreen + ").");
                await LoadAsync(false);
            }
            catch (Exception ex) { SetStatus("Gagal: " + ex.Message, true); MessageBox.Show(this, ex.Message, Program.AppName); }
            finally { busy = false; }
        }

        // ---------- pemindaian ----------
        async Task StartScanAsync()
        {
            if (!loaded) { scanMsg.Text = "Daftar belum dimuat."; return; }
            var sel = SelNvr;
            if (sel == null) { scanMsg.ForeColor = Color.Firebrick; scanMsg.Text = "Tambah NVR dulu."; return; }
            if (sel.Url.Trim().Length == 0 || sel.User.Trim().Length == 0) { scanMsg.ForeColor = Color.Firebrick; scanMsg.Text = "Isi alamat NVR dan user."; return; }
            if (sel.Pass.Length == 0 && !sel.HasPass) { scanMsg.ForeColor = Color.Firebrick; scanMsg.Text = "Isi password NVR."; return; }
            scanNvrId = sel.Id;
            scanMsg.ForeColor = Color.DimGray; scanMsg.Text = ""; others.Text = "";
            try
            {
                scanBtn.Enabled = false;
                var j = await api.SendAsync("POST", "api/registry/scan", Payload(true));
                scanId = MiniJson.Str(j, "id");
                cancelBtn.Visible = true; bar.Visible = true; bar.Value = 0;
                poll.Start();
            }
            catch (Exception e) { scanBtn.Enabled = true; scanMsg.ForeColor = Color.Firebrick; scanMsg.Text = e.Message; }
        }

        async Task PollAsync()
        {
            poll.Stop();
            try
            {
                var j = await api.SendAsync("GET", "api/registry/scan?id=" + Uri.EscapeDataString(scanId ?? ""), null, 6000);
                int done = MiniJson.Int(j, "done", 0), total = Math.Max(1, MiniJson.Int(j, "total", 1));
                bar.Value = Math.Min(100, 100 * done / total);
                scanMsg.Text = "CAM " + done + " / " + total;
                string state = MiniJson.Str(j, "state");
                MergeResults(MiniJson.List(j, "results"));
                if (state == "running") { poll.Start(); return; }
                scanBtn.Enabled = true; cancelBtn.Visible = false; bar.Visible = false;
                scanMsg.ForeColor = state == "failed" ? Color.Firebrick : Color.DimGray;
                scanMsg.Text = (state == "failed" ? "Gagal: " : "") + MiniJson.Str(j, "message") + (MiniJson.Str(j, "window").Length > 0 ? " Jendela uji: " + MiniJson.Str(j, "window") + "." : "");
            }
            catch (Exception e) { scanBtn.Enabled = true; cancelBtn.Visible = false; bar.Visible = false; scanMsg.ForeColor = Color.Firebrick; scanMsg.Text = e.Message; }
        }

        void MergeResults(List<object> results)
        {
            if (results == null) return;
            var sb = new StringBuilder();
            foreach (var o in results)
            {
                var r = MiniJson.Obj(o); if (r == null) continue;
                int cam = MiniJson.Int(r, "cam", 0);
                if (MiniJson.Str(r, "state") == "rec")
                {
                    var row = rows.FirstOrDefault(x => x.Cam == cam && x.Nvr == scanNvrId);
                    if (row == null)
                    {
                        int num = FreeNum(cam);
                        row = new CamRow { Num = num, Cam = cam, Nvr = scanNvrId ?? "", Name = "Kamera " + num, Brand = (string)defBrand.SelectedItem ?? "dahua" };
                        rows.Add(row);
                    }
                    if (row.Probe != "rec") { row.Probe = "rec"; rows.ResetItem(rows.IndexOf(row)); }
                    Dirty = true;
                }
                else sb.AppendLine("CAM " + cam + " [" + (MiniJson.Str(r, "state") == "none" ? "tanpa rekaman" : "error") + "]: " + MiniJson.Str(r, "message"));
            }
            others.Text = sb.ToString();
        }
    }
}
