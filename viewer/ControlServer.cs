using System;
using System.Net;
using System.Text;
using System.Threading;

namespace CctvViewer
{
    /// <summary>Server kontrol lokal (127.0.0.1) agar tombol "Keluar" di halaman bisa menutup aplikasi.</summary>
    sealed class ControlServer : IDisposable
    {
        readonly HttpListener l = new HttpListener();
        readonly string allowOrigin;
        readonly Action quit;
        readonly Func<string> status;
        volatile bool disposed;

        public ControlServer(int port, string allowOrigin, Action quit, Func<string> status)
        {
            this.allowOrigin = allowOrigin; this.quit = quit; this.status = status;
            l.Prefixes.Add("http://127.0.0.1:" + port + "/");
        }

        public void Start()
        {
            l.Start();
            var t = new Thread(Loop) { IsBackground = true, Name = "control" };
            t.Start();
        }

        void Loop()
        {
            while (!disposed)
            {
                HttpListenerContext c;
                try { c = l.GetContext(); } catch { return; }
                try { Handle(c); } catch (Exception e) { Log.Warn("Kontrol: " + e.Message); try { c.Response.Abort(); } catch { } }
            }
        }

        void Handle(HttpListenerContext c)
        {
            var req = c.Request; var res = c.Response;
            string origin = req.Headers["Origin"];
            bool originOk = string.IsNullOrEmpty(origin) || origin == allowOrigin;
            if (!string.IsNullOrEmpty(origin) && origin == allowOrigin)
            {
                res.Headers["Access-Control-Allow-Origin"] = origin;
                res.Headers["Vary"] = "Origin";
            }
            string path = req.Url.AbsolutePath;
            if (req.HttpMethod == "OPTIONS")
            {
                res.Headers["Access-Control-Allow-Methods"] = "GET, POST, OPTIONS";
                res.Headers["Access-Control-Allow-Headers"] = "X-CctvViewer";
                res.StatusCode = 204; res.Close(); return;
            }
            if (!originOk) { Send(res, 403, "Origin tidak diizinkan."); return; }
            if (path == "/status" && req.HttpMethod == "GET") { Send(res, 200, status()); return; }
            if (path == "/quit" && req.HttpMethod == "POST")
            {
                if (req.Headers["X-CctvViewer"] != "1") { Send(res, 403, "Header X-CctvViewer: 1 diperlukan."); return; }
                Send(res, 200, "ok");
                Log.Info("Permintaan keluar diterima dari " + (origin ?? "lokal") + ".");
                quit();
                return;
            }
            Send(res, 404, "Tidak ditemukan.");
        }

        static void Send(HttpListenerResponse res, int code, string text)
        {
            byte[] b = Encoding.UTF8.GetBytes(text);
            res.StatusCode = code; res.ContentType = "text/plain; charset=utf-8";
            res.Headers["Cache-Control"] = "no-store";
            res.ContentLength64 = b.Length;
            res.OutputStream.Write(b, 0, b.Length);
            res.Close();
        }

        public void Dispose() { disposed = true; try { l.Close(); } catch { } }
    }
}
