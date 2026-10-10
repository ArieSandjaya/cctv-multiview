using System;
using System.Collections.Generic;
using System.Globalization;
using System.Net.Http;
using System.Security.Cryptography;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using System.Xml;

namespace NvrBridge
{
    /// <summary>
    /// PTZ lewat ONVIF (Profile S/T): ContinuousMove, Stop, GotoPreset. Dipakai untuk kamera i-PRO/Panasonic
    /// dan kamera lain yang mendukung ONVIF. Login memakai WS-UsernameToken (PasswordDigest), dengan selisih
    /// jam kamera dibaca dari GetSystemDateAndTime agar token tidak ditolak karena jam beda.
    /// Alamat layanan dari kamera hanya dipakai pathnya; host/port selalu dari konfigurasi.
    /// </summary>
    sealed class OnvifPtz
    {
        const string NsSoap = "http://www.w3.org/2003/05/soap-envelope";
        const string NsDev = "http://www.onvif.org/ver10/device/wsdl";
        const string NsMedia = "http://www.onvif.org/ver10/media/wsdl";
        const string NsPtz = "http://www.onvif.org/ver20/ptz/wsdl";
        const string NsSch = "http://www.onvif.org/ver10/schema";
        const string NsWsse = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-secext-1.0.xsd";
        const string NsWsu = "http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-wssecurity-utility-1.0.xsd";

        static readonly CultureInfo CI = CultureInfo.InvariantCulture;

        readonly HttpClient http;
        readonly string user, pass;
        readonly Dictionary<string, Session> sessions = new Dictionary<string, Session>();
        readonly SemaphoreSlim gate = new SemaphoreSlim(1, 1);

        sealed class Session
        {
            public string Base;          // http://host:port
            public string PtzPath;
            public string Token;         // profile token
            public TimeSpan Offset;      // jam kamera - jam PC
        }

        public OnvifPtz(HttpClient http, string user, string pass)
        {
            this.http = http;
            this.user = user;
            this.pass = pass;
        }

        public static readonly Dictionary<string, double[]> Vectors = new Dictionary<string, double[]>(StringComparer.Ordinal)
        {
            // pan, tilt, zoom
            { "up", new[] { 0.0, 1, 0 } }, { "down", new[] { 0.0, -1, 0 } },
            { "left", new[] { -1.0, 0, 0 } }, { "right", new[] { 1.0, 0, 0 } },
            { "upleft", new[] { -1.0, 1, 0 } }, { "upright", new[] { 1.0, 1, 0 } },
            { "downleft", new[] { -1.0, -1, 0 } }, { "downright", new[] { 1.0, -1, 0 } },
            { "zoomin", new[] { 0.0, 0, 1 } }, { "zoomout", new[] { 0.0, 0, -1 } }
        };

        public async Task MoveAsync(string cam, string hostPort, string dir, int speed)
        {
            double[] v;
            if (!Vectors.TryGetValue(dir, out v)) throw new ArgumentException("Fokus tidak didukung untuk kamera ONVIF.");
            double s = speed / 8.0;
            var se = await GetSessionAsync(cam, hostPort).ConfigureAwait(false);
            string body = "<ContinuousMove xmlns=\"" + NsPtz + "\"><ProfileToken>" + Esc(se.Token) + "</ProfileToken><Velocity>" +
                          "<PanTilt x=\"" + (v[0] * s).ToString("0.###", CI) + "\" y=\"" + (v[1] * s).ToString("0.###", CI) + "\" xmlns=\"" + NsSch + "\"/>" +
                          "<Zoom x=\"" + (v[2] * s).ToString("0.###", CI) + "\" xmlns=\"" + NsSch + "\"/></Velocity></ContinuousMove>";
            await CallAsync(cam, se, se.PtzPath, body).ConfigureAwait(false);
        }

        public async Task StopAsync(string cam, string hostPort)
        {
            var se = await GetSessionAsync(cam, hostPort).ConfigureAwait(false);
            string body = "<Stop xmlns=\"" + NsPtz + "\"><ProfileToken>" + Esc(se.Token) + "</ProfileToken><PanTilt>true</PanTilt><Zoom>true</Zoom></Stop>";
            await CallAsync(cam, se, se.PtzPath, body).ConfigureAwait(false);
        }

        public async Task PresetAsync(string cam, string hostPort, int n)
        {
            var se = await GetSessionAsync(cam, hostPort).ConfigureAwait(false);
            string pb = "<GetPresets xmlns=\"" + NsPtz + "\"><ProfileToken>" + Esc(se.Token) + "</ProfileToken></GetPresets>";
            var doc = await CallAsync(cam, se, se.PtzPath, pb).ConfigureAwait(false);
            string want = n.ToString(CI), token = null;
            foreach (XmlNode p in doc.SelectNodes("//*[local-name()='Preset']"))
            {
                string t = p.Attributes["token"] != null ? p.Attributes["token"].Value : null;
                var nm = p.SelectSingleNode("*[local-name()='Name']");
                if (t == want || (nm != null && nm.InnerText.Trim() == want)) { token = t; break; }
            }
            if (token == null) throw new PtzException("Preset " + n + " tidak ada di kamera.");
            string gb = "<GotoPreset xmlns=\"" + NsPtz + "\"><ProfileToken>" + Esc(se.Token) + "</ProfileToken><PresetToken>" + Esc(token) + "</PresetToken></GotoPreset>";
            await CallAsync(cam, se, se.PtzPath, gb).ConfigureAwait(false);
        }

        // ---------- sesi (alamat layanan PTZ, profil, selisih jam) ----------

        void Drop(string cam) { lock (sessions) sessions.Remove(cam); }

        async Task<Session> GetSessionAsync(string cam, string hostPort)
        {
            await gate.WaitAsync().ConfigureAwait(false);
            try
            {
                Session se;
                lock (sessions) { if (sessions.TryGetValue(cam, out se)) return se; }
                se = new Session { Base = "http://" + hostPort };

                // 1. jam kamera (tanpa login)
                try
                {
                    var t = await CallAsync(cam, se, "/onvif/device_service", "<GetSystemDateAndTime xmlns=\"" + NsDev + "\"/>", false).ConfigureAwait(false);
                    se.Offset = ParseCameraTime(t);
                }
                catch (PtzException) { se.Offset = TimeSpan.Zero; }

                // 2. alamat layanan Media dan PTZ
                var caps = await CallAsync(cam, se, "/onvif/device_service",
                    "<GetCapabilities xmlns=\"" + NsDev + "\"><Category>All</Category></GetCapabilities>").ConfigureAwait(false);
                string mediaPath = PathOf(caps, "Media", "/onvif/media_service");
                se.PtzPath = PathOf(caps, "PTZ", "/onvif/ptz_service");

                // 3. profil yang punya konfigurasi PTZ
                var prof = await CallAsync(cam, se, mediaPath, "<GetProfiles xmlns=\"" + NsMedia + "\"/>").ConfigureAwait(false);
                foreach (XmlNode p in prof.SelectNodes("//*[local-name()='Profiles']"))
                {
                    if (p.SelectSingleNode(".//*[local-name()='PTZConfiguration']") != null && p.Attributes["token"] != null)
                    { se.Token = p.Attributes["token"].Value; break; }
                }
                if (se.Token == null) throw new PtzException("Kamera tidak punya profil PTZ (bukan kamera PTZ, atau PTZ nonaktif).");
                lock (sessions) { sessions[cam] = se; }
                return se;
            }
            finally { gate.Release(); }
        }

        static string PathOf(XmlDocument d, string section, string fallback)
        {
            var n = d.SelectSingleNode("//*[local-name()='" + section + "']/*[local-name()='XAddr']");
            Uri u;
            if (n != null && Uri.TryCreate(n.InnerText.Trim(), UriKind.Absolute, out u) && u.AbsolutePath.Length > 1) return u.AbsolutePath;
            return fallback;
        }

        static TimeSpan ParseCameraTime(XmlDocument d)
        {
            try
            {
                var u = d.SelectSingleNode("//*[local-name()='UTCDateTime']");
                Func<string, int> g = (name) => int.Parse(u.SelectSingleNode(".//*[local-name()='" + name + "']").InnerText, CI);
                var cam = new DateTime(g("Year"), g("Month"), g("Day"), g("Hour"), g("Minute"), g("Second"), DateTimeKind.Utc);
                return cam - DateTime.UtcNow;
            }
            catch (Exception) { return TimeSpan.Zero; }
        }

        // ---------- SOAP ----------

        async Task<XmlDocument> CallAsync(string cam, Session se, string path, string bodyXml, bool auth = true)
        {
            string header = "";
            if (auth)
            {
                var nonce = new byte[16];
                using (var rng = RandomNumberGenerator.Create()) rng.GetBytes(nonce);
                string created = (DateTime.UtcNow + se.Offset).ToString("yyyy-MM-dd'T'HH:mm:ss.fff'Z'", CI);
                byte[] cb = Encoding.UTF8.GetBytes(created), pb = Encoding.UTF8.GetBytes(pass);
                var all = new byte[nonce.Length + cb.Length + pb.Length];
                Buffer.BlockCopy(nonce, 0, all, 0, nonce.Length);
                Buffer.BlockCopy(cb, 0, all, nonce.Length, cb.Length);
                Buffer.BlockCopy(pb, 0, all, nonce.Length + cb.Length, pb.Length);
                string digest;
                using (var sha = SHA1.Create()) digest = Convert.ToBase64String(sha.ComputeHash(all));
                header = "<s:Header><Security s:mustUnderstand=\"1\" xmlns=\"" + NsWsse + "\"><UsernameToken><Username>" + Esc(user) +
                         "</Username><Password Type=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-username-token-profile-1.0#PasswordDigest\">" + digest +
                         "</Password><Nonce EncodingType=\"http://docs.oasis-open.org/wss/2004/01/oasis-200401-wss-soap-message-security-1.0#Base64Binary\">" +
                         Convert.ToBase64String(nonce) + "</Nonce><Created xmlns=\"" + NsWsu + "\">" + created + "</Created></UsernameToken></Security></s:Header>";
            }
            string xml = "<?xml version=\"1.0\" encoding=\"utf-8\"?><s:Envelope xmlns:s=\"" + NsSoap + "\">" + header + "<s:Body>" + bodyXml + "</s:Body></s:Envelope>";

            var req = new HttpRequestMessage(HttpMethod.Post, se.Base + path);
            req.Content = new StringContent(xml, new UTF8Encoding(false), "application/soap+xml");
            Log.Debug("ONVIF " + cam + " " + path);
            string text;
            System.Net.HttpStatusCode code;
            try
            {
                using (var resp = await http.SendAsync(req).ConfigureAwait(false))
                {
                    code = resp.StatusCode;
                    text = await resp.Content.ReadAsStringAsync().ConfigureAwait(false);
                }
            }
            catch (HttpRequestException e) { Drop(cam); throw new PtzException("Tidak bisa menghubungi kamera: " + e.Message); }
            catch (TaskCanceledException) { Drop(cam); throw new PtzException("Kamera tidak menjawab (timeout)."); }

            var doc = new XmlDocument { XmlResolver = null };
            try { doc.LoadXml(text); } catch (XmlException) { doc = null; }

            if (code == System.Net.HttpStatusCode.Unauthorized)
            { Drop(cam); throw new PtzException("Kamera menolak login ONVIF (periksa PtzUser/PtzPassword dan pastikan ONVIF aktif)."); }
            if ((int)code >= 400)
            {
                Drop(cam);
                string reason = null;
                if (doc != null)
                {
                    var r = doc.SelectSingleNode("//*[local-name()='Reason']/*[local-name()='Text']");
                    if (r != null) reason = r.InnerText.Trim();
                }
                if (reason != null && reason.Length > 120) reason = reason.Substring(0, 120);
                throw new PtzException("Kamera menolak perintah ONVIF" + (reason != null ? ": " + reason : " (HTTP " + (int)code + ")") + ".");
            }
            if (doc == null) { Drop(cam); throw new PtzException("Jawaban ONVIF kamera tidak bisa dibaca."); }
            return doc;
        }

        static string Esc(string s) { return System.Security.SecurityElement.Escape(s); }
    }
}
