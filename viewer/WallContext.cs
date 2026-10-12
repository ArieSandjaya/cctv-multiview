using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.IO;
using System.Linq;
using System.Threading.Tasks;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CctvViewer
{
    struct Area
    {
        public int X, Y, W, H;
        public Area(int x, int y, int w, int h) { X = x; Y = y; W = w; H = h; }
        public Rectangle Rect { get { return new Rectangle(X, Y, W, H); } }
        public override string ToString() { return X + "," + Y + " " + W + "x" + H; }
    }

    /// <summary>Mengatur ikon tray, satu jendela layar penuh per monitor, dan panel kontrol.</summary>
    sealed class WallContext : ApplicationContext
    {
        readonly Core core;
        readonly Settings st;
        readonly string cfgPath;
        readonly NotifyIcon tray;
        readonly Timer timer;
        readonly Control sync;
        readonly List<WallForm> walls = new List<WallForm>();
        PanelForm panel;
        bool quitting, warnedMonitors;

        public WallContext(Core core, string cfgPath)
        {
            this.core = core; this.st = core.St; this.cfgPath = cfgPath;
            sync = new Control();
            var h = sync.Handle;                                   // memastikan handle dibuat di thread UI

            var menu = new ContextMenuStrip();
            menu.Items.Add("Panel kontrol", null, (s, e) => ShowPanel());
            menu.Items.Add("Registrasi NVR", null, (s, e) => ShowSetup());
            menu.Items.Add("Muat ulang semua layar", null, (s, e) => ReloadAll());
            menu.Items.Add(new ToolStripSeparator());
            menu.Items.Add("Keluar", null, (s, e) => Quit());
            tray = new NotifyIcon { Icon = SystemIcons.Application, Text = "CCTV Viewer", Visible = true, ContextMenuStrip = menu };
            tray.DoubleClick += (s, e) => ShowPanel();

            core.QuitRequested += () => Post(Quit);
            core.PanelRequested += () => Post(ShowPanel);
            Microsoft.Win32.SystemEvents.DisplaySettingsChanged += (s, e) => Post(SyncScreens);

            timer = new Timer { Interval = 3000 };
            timer.Tick += (s, e) => SyncScreens();
            timer.Start();
            SyncScreens();
        }

        public Settings Settings { get { return st; } }
        public string ConfigPath { get { return cfgPath; } }
        public Core Core { get { return core; } }

        void Post(Action a)
        {
            try { if (sync.IsHandleCreated && !sync.IsDisposed) sync.BeginInvoke(a); } catch (InvalidOperationException) { }
        }

        /// <summary>Semua monitor (kiri ke kanan, lalu atas ke bawah), atau daftar manual dari konfigurasi.</summary>
        public static List<Area> Monitors(Settings st)
        {
            if (st.ManualMonitors != null) return st.ManualMonitors;
            var list = Screen.AllScreens.Select(s => new Area(s.Bounds.X, s.Bounds.Y, s.Bounds.Width, s.Bounds.Height)).ToList();
            list.Sort((a, b) => a.X != b.X ? a.X.CompareTo(b.X) : a.Y.CompareTo(b.Y));
            return list;
        }

        void SyncScreens()
        {
            if (quitting) return;
            var mons = Monitors(st);
            int want = Math.Min(st.Screens, mons.Count);
            if (mons.Count < st.Screens)
            {
                if (!warnedMonitors) { Log.Warn("Hanya " + mons.Count + " dari " + st.Screens + " monitor terdeteksi. Layar lainnya dibuka otomatis begitu monitornya muncul."); warnedMonitors = true; }
            }
            else warnedMonitors = false;

            for (int i = 0; i < st.Screens; i++)
            {
                var w = walls.Find(x => x.Index == i);
                if (i >= want) { if (w != null && w.Visible) { w.Hide(); Log.Info("Layar " + (i + 1) + " disembunyikan (monitor tidak ada)."); } continue; }
                if (w == null)
                {
                    string profile = Path.Combine(st.DataDir, "profile-" + (i + 1));
                    Directory.CreateDirectory(profile);
                    w = new WallForm(core, i, mons[i], profile);
                    walls.Add(w);
                    w.Show();
                    Log.Info("Layar " + (i + 1) + " dibuka di monitor " + mons[i] + ".");
                }
                else
                {
                    w.SetArea(mons[i]);
                    if (!w.Visible) { w.Show(); Log.Info("Layar " + (i + 1) + " dibuka kembali."); }
                }
            }
        }

        public string ScreensText()
        {
            var sb = new System.Text.StringBuilder();
            var mons = Monitors(st);
            sb.AppendLine("Monitor terdeteksi: " + mons.Count + " | layar dipakai: " + st.Screens + " x " + st.CamsPerScreen + " kamera");
            foreach (var w in walls.OrderBy(x => x.Index)) sb.AppendLine("Layar " + (w.Index + 1) + ": " + (w.Visible ? w.StateText : "disembunyikan") + "  [" + w.Area + "]");
            return sb.ToString();
        }

        public void ReloadAll() { foreach (var w in walls) w.ReloadPage(); }

        public void ShowPanel()
        {
            if (panel == null || panel.IsDisposed) panel = new PanelForm(this);
            panel.Show();
            if (panel.WindowState == FormWindowState.Minimized) panel.WindowState = FormWindowState.Normal;
            panel.Activate(); panel.BringToFront();
        }

        public void ShowSetup()
        {
            var f = new SetupForm(core);
            f.Show();
            f.Activate(); f.BringToFront();
        }

        public void Restart()
        {
            try
            {
                string exe = System.Reflection.Assembly.GetExecutingAssembly().Location;
                Process.Start(new ProcessStartInfo(exe, "--delay 4 --config \"" + cfgPath + "\"") { UseShellExecute = false });
            }
            catch (Exception e) { Log.Error("Mulai ulang gagal: " + e.Message); MessageBox.Show("Tidak bisa memulai ulang: " + e.Message, Program.AppName); return; }
            Quit();
        }

        public void Quit()
        {
            if (quitting) return;
            quitting = true;
            Log.Info("Keluar diminta.");
            timer.Stop();
            tray.Visible = false;
            foreach (var w in walls) w.ForceClose();
            if (panel != null && !panel.IsDisposed) panel.ForceClose();
            tray.Dispose();
            ExitThread();
        }
    }

    /// <summary>Jendela tanpa bingkai yang menutupi satu monitor dan menampilkan halaman multiview lewat WebView2.</summary>
    sealed class WallForm : Form
    {
        readonly Core core;
        readonly string profile;
        Area area;
        WebView2 web;
        readonly Timer fit = new Timer { Interval = 10000 };
        readonly Timer retry = new Timer { Interval = 5000 };
        bool forceClose, initializing;
        string state = "memuat";

        public int Index { get; private set; }
        public Area Area { get { return area; } }
        public string StateText { get { return state; } }

        public WallForm(Core core, int index, Area area, string profile)
        {
            this.core = core; this.Index = index; this.area = area; this.profile = profile;
            AutoScaleMode = AutoScaleMode.None;
            FormBorderStyle = FormBorderStyle.None;
            StartPosition = FormStartPosition.Manual;
            ShowInTaskbar = false;
            BackColor = Color.Black;
            Text = "CCTV Layar " + (index + 1);
            Bounds = area.Rect;
            fit.Tick += (s, e) => Fit();
            retry.Tick += (s, e) => { retry.Stop(); Navigate(); };
        }

        public void SetArea(Area a) { area = a; Fit(); }

        void Fit()
        {
            var r = area.Rect;
            if (Bounds != r) { Bounds = r; Log.Info("Layar " + (Index + 1) + ": posisi dikembalikan ke [" + area + "]"); }
        }

        protected override void OnShown(EventArgs e)
        {
            base.OnShown(e);
            Fit();
            fit.Start();
            var ignored = InitWebAsync();
        }

        protected override void OnDpiChanged(DpiChangedEventArgs e) { base.OnDpiChanged(e); Fit(); }

        async Task InitWebAsync()
        {
            if (initializing) return;
            initializing = true;
            try
            {
                var opts = new CoreWebView2EnvironmentOptions();
                opts.AdditionalBrowserArguments =
                    "--autoplay-policy=no-user-gesture-required --disable-features=CalculateNativeWinOcclusion " +
                    "--disable-background-timer-throttling --disable-renderer-backgrounding --disable-backgrounding-occluded-windows " +
                    core.St.WebViewArgs;
                var env = await CoreWebView2Environment.CreateAsync(null, profile, opts);
                if (web != null) { Controls.Remove(web); web.Dispose(); }
                web = new WebView2 { Dock = DockStyle.Fill };
                Controls.Add(web);
                await web.EnsureCoreWebView2Async(env);
                var s = web.CoreWebView2.Settings;
                s.AreDevToolsEnabled = false;
                s.AreDefaultContextMenusEnabled = false;
                s.IsZoomControlEnabled = false;
                s.IsStatusBarEnabled = false;
                web.CoreWebView2.NavigationCompleted += OnNavigationCompleted;
                web.CoreWebView2.ProcessFailed += OnProcessFailed;
                web.CoreWebView2.NewWindowRequested += (o, a) => { a.Handled = true; };
                Navigate();
            }
            catch (Exception ex)
            {
                state = "WebView2 gagal: " + ex.Message;
                Log.Error("Layar " + (Index + 1) + ": WebView2 gagal dimulai: " + ex.Message);
                retry.Interval = 10000;
                retry.Stop(); retry.Tick -= RetryInit; retry.Tick += RetryInit; retry.Start();
            }
            finally { initializing = false; }
        }

        void RetryInit(object s, EventArgs e) { retry.Stop(); retry.Tick -= RetryInit; var ignored = InitWebAsync(); }

        void Navigate()
        {
            try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.Navigate(core.PageUrl(Index)); }
            catch (Exception e) { Log.Warn("Layar " + (Index + 1) + ": navigasi gagal: " + e.Message); }
        }

        void OnNavigationCompleted(object sender, CoreWebView2NavigationCompletedEventArgs e)
        {
            if (e.IsSuccess) { state = "tampil"; return; }
            state = "menunggu go2rtc (" + e.WebErrorStatus + ")";
            retry.Interval = 5000;
            retry.Stop(); retry.Start();             // go2rtc belum siap: coba lagi tiap 5 detik
        }

        void OnProcessFailed(object sender, CoreWebView2ProcessFailedEventArgs e)
        {
            Log.Warn("Layar " + (Index + 1) + ": proses WebView2 gagal (" + e.ProcessFailedKind + ").");
            state = "dipulihkan setelah " + e.ProcessFailedKind;
            try
            {
                if (e.ProcessFailedKind == CoreWebView2ProcessFailedKind.BrowserProcessExited) { var ignored = InitWebAsync(); }
                else web.CoreWebView2.Reload();
            }
            catch (Exception ex) { Log.Warn("Pemulihan layar " + (Index + 1) + " gagal: " + ex.Message); }
        }

        public void ReloadPage()
        {
            try { if (web != null && web.CoreWebView2 != null) web.CoreWebView2.Reload(); else Navigate(); } catch { }
        }

        public void ForceClose() { forceClose = true; fit.Stop(); retry.Stop(); Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!forceClose && !core.St.AllowCloseScreen && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; return; }
            base.OnFormClosing(e);
        }
    }
}
