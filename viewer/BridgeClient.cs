using System;
using System.IO;
using System.Net;
using System.Text;
using System.Threading.Tasks;
using NvrBridge;

namespace CctvViewer
{
    sealed class BridgeException : Exception { public BridgeException(string m) : base(m) { } }

    /// <summary>Klien HTTP kecil ke NvrBridge (registrasi NVR, daftar kamera, pemindaian). Dipakai oleh form Pengaturan.</summary>
    sealed class BridgeClient
    {
        readonly Uri root;
        readonly string auth;

        public BridgeClient(Settings st)
        {
            root = st.BridgeUrl;
            var cfg = ConfigWriter.Read(st.NvrBridgeConfig);
            string u, p;
            cfg.TryGetValue("BridgeUser", out u); cfg.TryGetValue("BridgePassword", out p);
            if (!string.IsNullOrEmpty(u)) auth = "Basic " + Convert.ToBase64String(Encoding.UTF8.GetBytes(u + ":" + (p ?? "")));
        }

        /// <summary>Mengirim permintaan. Mengembalikan JSON yang sudah di-parse (objek), atau melempar BridgeException dengan pesan dari NvrBridge.</summary>
        public Task<System.Collections.Generic.Dictionary<string, object>> SendAsync(string method, string path, string jsonBody, int timeoutMs = 15000)
        {
            return Task.Run(() =>
            {
                var rq = (HttpWebRequest)WebRequest.Create(new Uri(root, path));
                rq.Method = method; rq.Timeout = timeoutMs; rq.ReadWriteTimeout = timeoutMs; rq.Proxy = null;
                if (method != "GET") rq.Headers["X-NvrBridge"] = "1";
                if (auth != null) rq.Headers["Authorization"] = auth;
                byte[] body = jsonBody != null ? Encoding.UTF8.GetBytes(jsonBody) : (method == "GET" ? null : new byte[0]);
                if (body != null)
                {
                    rq.ContentType = "application/json";
                    rq.ContentLength = body.Length;
                    using (var rs = rq.GetRequestStream()) rs.Write(body, 0, body.Length);
                }
                string text;
                try
                {
                    using (var resp = (HttpWebResponse)rq.GetResponse())
                    using (var sr = new StreamReader(resp.GetResponseStream(), Encoding.UTF8)) text = sr.ReadToEnd();
                }
                catch (WebException e)
                {
                    var er = e.Response as HttpWebResponse;
                    if (er == null) throw new BridgeException("NvrBridge tidak menjawab di " + root + ". Pastikan 'Jalankan NvrBridge' aktif dan aplikasi sudah dimulai ulang setelah menyimpan.");
                    string msg;
                    using (var sr = new StreamReader(er.GetResponseStream(), Encoding.UTF8)) msg = sr.ReadToEnd();
                    string m = null;
                    try { m = MiniJson.Str(MiniJson.Obj(MiniJson.Parse(msg)), "error", null); } catch { }
                    if ((int)er.StatusCode == 401) m = "NvrBridge menolak login (BridgeUser/BridgePassword).";
                    throw new BridgeException(m ?? ("HTTP " + (int)er.StatusCode));
                }
                var o = MiniJson.Obj(MiniJson.Parse(text));
                return o ?? new System.Collections.Generic.Dictionary<string, object>();
            });
        }
    }
}
