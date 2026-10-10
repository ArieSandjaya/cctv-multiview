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

            AppConfig cfg;
            try { cfg = AppConfig.Load(cfgPath); }
            catch (ConfigException e)
            {
                Console.Error.WriteLine("Konfigurasi bermasalah: " + e.Message);
                return 2;
            }
            Log.Verbose = cfg.Verbose;

            // TLS 1.2 (nilai 3072) untuk HTTPS ke NVR pada .NET Framework.
            ServicePointManager.SecurityProtocol |= (SecurityProtocolType)3072;
            ServicePointManager.DefaultConnectionLimit = 32;

            Directory.CreateDirectory(cfg.ClipDir);
            foreach (var f in Directory.GetFiles(cfg.ClipDir, "*.mp4")) { try { File.Delete(f); } catch { } }

            using (var nvr = new NvrClient(cfg))
            {
                if (selfTest) return await SelfTestAsync(cfg, nvr).ConfigureAwait(false);

                using (var jobs = new JobManager(cfg, nvr))
                using (var server = new WebServer(cfg, nvr, jobs))
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
                    Log.Info("NVR: " + cfg.NvrBase + "  | kamera: " + cfg.Cameras.Count +
                             "  | zona waktu NVR: " + (cfg.NvrTimeUtc ? "UTC" : "lokal (" + TimeZoneInfo.Local.DisplayName + ")"));
                    if (cfg.BridgeUser.Length == 0 && !cfg.ListenPrefix.Contains("localhost") && !cfg.ListenPrefix.Contains("127.0.0.1"))
                        Log.Warn("Halaman dapat dibuka tanpa login. Isi BridgeUser/BridgePassword di nvrbridge.config.");
                    Console.WriteLine("Tekan Ctrl+C untuk berhenti.");

                    var quit = new ManualResetEventSlim(false);
                    Console.CancelKeyPress += (s, e) => { e.Cancel = true; quit.Set(); };
                    await Task.Run(() => quit.Wait()).ConfigureAwait(false);
                    Log.Info("Menghentikan...");
                }
            }
            return 0;
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
