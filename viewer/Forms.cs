using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.Drawing;
using System.Globalization;
using System.IO;
using System.Linq;
using System.Windows.Forms;
using Microsoft.Web.WebView2.Core;
using Microsoft.Web.WebView2.WinForms;

namespace CctvViewer
{
    /// <summary>Panel kontrol: status, dan jalan pintas ke registrasi NVR, pengaturan, dan keluar.</summary>
    sealed class PanelForm : Form
    {
        readonly WallContext ctx;
        readonly Label status = new Label { AutoSize = false, Dock = DockStyle.Fill, Font = new Font("Consolas", 10f), Padding = new Padding(8) };
        readonly Timer timer = new Timer { Interval = 2000 };
        bool force;

        public PanelForm(WallContext ctx)
        {
            this.ctx = ctx;
            Text = Program.AppName;
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(640, 330);
            MinimumSize = new Size(560, 300);
            ShowInTaskbar = true;

            var buttons = new FlowLayoutPanel { Dock = DockStyle.Bottom, Height = 48, Padding = new Padding(6), FlowDirection = FlowDirection.LeftToRight, WrapContents = true };
            Button B(string text, Action a) { var b = new Button { Text = text, AutoSize = true, Padding = new Padding(6, 2, 6, 2) }; b.Click += (s, e) => a(); buttons.Controls.Add(b); return b; }
            B("Registrasi NVR", ctx.ShowSetup);
            B("Pengaturan", () => { using (var f = new SettingsForm(ctx)) f.ShowDialog(this); });
            B("Kenali layar", () => IdentifyForm.Flash(ctx.Settings));
            B("Muat ulang layar", ctx.ReloadAll);
            B("Folder log", () => { try { Process.Start("explorer.exe", "\"" + ctx.Settings.DataDir + "\""); } catch { } });
            B("Keluar", () => { if (MessageBox.Show(this, "Tutup CCTV Viewer? Semua layar kamera akan berhenti.", Program.AppName, MessageBoxButtons.YesNo, MessageBoxIcon.Question) == DialogResult.Yes) ctx.Quit(); });

            Controls.Add(status);
            Controls.Add(buttons);
            timer.Tick += (s, e) => Refresh2();
            timer.Start();
            Refresh2();
        }

        void Refresh2() { status.Text = ctx.Core.StatusText() + Environment.NewLine + ctx.ScreensText(); }

        public void ForceClose() { force = true; timer.Stop(); Close(); }

        protected override void OnFormClosing(FormClosingEventArgs e)
        {
            if (!force && e.CloseReason == CloseReason.UserClosing) { e.Cancel = true; Hide(); return; }   // ditutup = disembunyikan; program tetap berjalan
            base.OnFormClosing(e);
        }
    }

    /// <summary>Registrasi NVR: memuat halaman setup.html dari NvrBridge di dalam jendela ini.</summary>
    sealed class SetupForm : Form
    {
        readonly Core core;
        readonly WebView2 web = new WebView2 { Dock = DockStyle.Fill };

        public SetupForm(Core core)
        {
            this.core = core;
            Text = "Registrasi NVR";
            StartPosition = FormStartPosition.CenterScreen;
            ClientSize = new Size(1200, 820);
            Controls.Add(web);
            Shown += async (s, e) => await InitAsync();
        }

        async System.Threading.Tasks.Task InitAsync()
        {
            try
            {
                string profile = Path.Combine(core.St.DataDir, "profile-setup");
                Directory.CreateDirectory(profile);
                var env = await CoreWebView2Environment.CreateAsync(null, profile, new CoreWebView2EnvironmentOptions());
                await web.EnsureCoreWebView2Async(env);
                web.CoreWebView2.Settings.AreDevToolsEnabled = false;
                web.CoreWebView2.NavigationCompleted += (o, a) =>
                {
                    if (!a.IsSuccess)
                        web.NavigateToString("<html><body style='font:14px sans-serif;background:#0b0e12;color:#e6eaf0;padding:24px'>NvrBridge belum berjalan di " + core.SetupUrl +
                            ".<br>Pastikan <b>StartNvrBridge=true</b> dan NvrBridge\\nvrbridge.config ada, lalu buka jendela ini lagi.</body></html>");
                };
                web.CoreWebView2.Navigate(core.SetupUrl.ToString());
            }
            catch (Exception ex) { MessageBox.Show(this, "Tidak bisa membuka halaman registrasi: " + ex.Message, Program.AppName); }
        }
    }

    /// <summary>Menampilkan nomor besar di tiap monitor selama 3 detik, untuk mengenali urutan layar.</summary>
    static class IdentifyForm
    {
        public static void Flash(Settings st)
        {
            var mons = WallContext.Monitors(st);
            var forms = new List<Form>();
            for (int i = 0; i < mons.Count; i++)
            {
                var f = new Form
                {
                    FormBorderStyle = FormBorderStyle.None, StartPosition = FormStartPosition.Manual, ShowInTaskbar = false,
                    TopMost = true, BackColor = Color.FromArgb(20, 60, 110), Bounds = new Rectangle(mons[i].X + mons[i].W / 2 - 120, mons[i].Y + mons[i].H / 2 - 120, 240, 240),
                };
                f.Controls.Add(new Label
                {
                    Text = (i + 1).ToString(CultureInfo.InvariantCulture) + (i >= st.Screens ? "\n(tidak dipakai)" : ""), Dock = DockStyle.Fill, TextAlign = ContentAlignment.MiddleCenter,
                    Font = new Font("Segoe UI", 64f, FontStyle.Bold), ForeColor = Color.White,
                });
                f.Show();
                forms.Add(f);
            }
            var timer = new Timer { Interval = 3000 };
            timer.Tick += (s, e) => { timer.Stop(); timer.Dispose(); foreach (var f in forms) f.Close(); };
            timer.Start();
        }
    }
}
