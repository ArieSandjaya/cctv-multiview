using System;
using System.IO;
using System.Net;
using System.Threading;
using System.Threading.Tasks;

namespace NvrBridge
{
    static class Program
    {
        static async Task<int> Main(string[] args)
        {
            string cfgPath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory, "nvrbridge.config");
            bool selfTest = false;
            for (int i = 0; i < args.Length; i++)
            {
                if (args[i] == "--config" && i + 1 < args.Length) cfgPath = args[++i];
                else if (args[i] == "--selftest") selfTest = true;
                else if (args[i] == "--help" || args[i] == "-h")
                {
                    Console.WriteLine("NvrBridge [--config <file>] [--selftest]");
                    Console.WriteLine("  --selftest  login ke NVR, baca rentang rekaman, lalu keluar (untuk cek koneksi)");
                    return 0;
                }
            }

            // TLS 1.2 (nilai 3072) untuk HTTPS ke NVR pada .NET Framework.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            ServicePointManager.DefaultConnectionLimit = 32;

            var quit = new ManualResetEventSlim(false);
            Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };

            // Muat ulang: halaman Registrasi NVR menyimpan data baru lalu meminta NvrBridge memuat konfigurasinya lagi tanpa keluar dari proses.
            while (true)
            {
                AppConfig cfg;
                try { cfg = AppConfig.Load(cfgPath); }
                catch (ConfigException e)
                {
                    Console.Error.WriteLine("Konfigurasi bermasalah: " + e.Message);
                    return 2;
                }
                Log.Verbose = cfg.Verbose;

                Directory.CreateDirectory(cfg.ClipDir);
                foreach (var f in Directory.GetFiles(cfg.ClipDir, "*.mp4")) { try { File.Delete(f); } catch { } }

                var reload = new ManualResetEventSlim(false);
                using (var nvr = new NvrClient(cfg))
                {
                    if (selfTest)
                    {
                        if (!cfg.NvrConfigured) { Console.Error.WriteLine("NVR belum diatur."); return 1; }
                        return await SelfTestAsync(cfg, nvr).ConfigureAwait(false);
                    }

                    Directory.CreateDirectory(cfg.DataDir);
                    using (var jobs = new JobManager(cfg, nvr))
                    using (var ptz = new PtzClient(cfg))
                    using (var scanner = new Scanner(cfg))
                    using (var server = new WebServer(cfg, nvr, jobs, ptz, scanner, () => reload.Set()))
                    {
                        try { server.Start(); }
                        catch (HttpListenerException e)
                        {
                            Console.Error.WriteLine("Tidak bisa membuka " + cfg.ListenPrefix + " : " + e.Message);
                            Console.Error.WriteLine("Jika alamatnya http://+:PORT/ atau http://*:PORT/, jalankan sekali sebagai Administrator:");
                            Console.Error.WriteLine("  netsh http add urlacl url=" + cfg.ListenPrefix + " user=Everyone");
                            return 3;
                        }

                        Log.Info("NvrBridge berjalan di " + cfg.ListenPrefix);
                        if (cfg.NvrConfigured)
                            Log.Info("NVR: " + cfg.NvrBase + "  | kamera: " + cfg.Cameras.Count +
                                     "  | zona waktu NVR: " + (cfg.NvrTimeUtc ? "UTC" : "lokal (" + TimeZoneInfo.Local.DisplayName + ")"));
                        else
                            Log.Info("NVR belum diatur. Buka " + cfg.ListenPrefix + "setup.html untuk registrasi NVR.");
                        if (ptz.Enabled) Log.Info("PTZ aktif untuk: " + string.Join(", ", ptz.CameraIds));
                        if (cfg.AllowOrigin.Length > 0 && cfg.BridgeUser.Length > 0)
                            Log.Warn("AllowOrigin diisi bersama BridgeUser: browser tidak akan meminta login lintas-origin, jadi halaman multiview tidak bisa memakai API ini. Kosongkan BridgeUser atau akses lewat jaringan tepercaya saja.");
                        if (cfg.BridgeUser.Length == 0 && !cfg.ListenPrefix.Contains("localhost") && !cfg.ListenPrefix.Contains("127.0.0.1"))
                            Log.Warn("Halaman dapat dibuka tanpa login. Isi BridgeUser/BridgePassword di nvrbridge.config.");
                        Console.WriteLine("Tekan Ctrl+C untuk berhenti.");

                        await Task.Run(() => WaitHandle.WaitAny(new[] { quit.WaitHandle, reload.WaitHandle })).ConfigureAwait(false);
                        Log.Info(quit.IsSet ? "Menghentikan..." : "Memuat ulang konfigurasi...");
                    }
                }
                if (quit.IsSet) return 0;
            }
        }

        static async Task<int> SelfTestAsync(AppConfig cfg, NvrClient nvr)
        {
            Console.WriteLine("Uji koneksi ke " + cfg.NvrBase + " ...");
            try
            {
                await nvr.GetUidAsync(CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine("Login: OK");
                try
                {
                    var r = await nvr.GetRecordedRangeAsync(CancellationToken.None).ConfigureAwait(false);
                    Console.WriteLine("Rentang rekaman (waktu NVR): " +
                        (r.Oldest.HasValue ? r.Oldest.Value.ToString("yyyy-MM-dd HH:mm") : "?") + " s/d " +
                        (r.Latest.HasValue ? r.Latest.Value.ToString("yyyy-MM-dd HH:mm") : "?"));
                }
                catch (NvrException e)
                {
                    Console.WriteLine("Rentang rekaman: tidak tersedia (" + e.Message + "). Ini opsional; unduh klip tetap bisa dicoba.");
                }
                return 0;
            }
            catch (NvrException e)
            {
                Console.Error.WriteLine("GAGAL: " + e.Message);
                return 1;
            }
        }
    }
}
