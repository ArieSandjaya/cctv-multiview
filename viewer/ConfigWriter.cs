using System;
using System.Collections.Generic;
using System.IO;
using System.Text;

namespace CctvViewer
{
    /// <summary>Mengubah nilai Kunci=Nilai di berkas konfigurasi tanpa menghilangkan komentar.</summary>
    static class ConfigWriter
    {
        /// <summary>Membaca Kunci=Nilai (tanpa komentar). Berkas tidak ada = kosong.</summary>
        public static Dictionary<string, string> Read(string path)
        {
            var d = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
            if (path == null || !File.Exists(path)) return d;
            foreach (var raw in File.ReadAllLines(path, Encoding.UTF8))
            {
                string t = raw.Trim();
                if (t.Length == 0 || t[0] == '#') continue;
                int eq = t.IndexOf('=');
                if (eq > 0) d[t.Substring(0, eq).Trim()] = t.Substring(eq + 1).Trim();
            }
            return d;
        }

        public static void Set(string path, IDictionary<string, string> values)
        {
            var lines = new List<string>(File.Exists(path) ? File.ReadAllLines(path, Encoding.UTF8) : new string[0]);
            foreach (var kv in values)
            {
                if (kv.Value.IndexOfAny(new[] { '\r', '\n' }) >= 0) throw new ArgumentException("Nilai " + kv.Key + " tidak boleh berisi baris baru.");
                bool done = false;
                for (int i = 0; i < lines.Count; i++)
                {
                    string t = lines[i].TrimStart();
                    if (t.Length == 0 || t[0] == '#') continue;
                    int eq = t.IndexOf('=');
                    if (eq > 0 && string.Equals(t.Substring(0, eq).Trim(), kv.Key, StringComparison.OrdinalIgnoreCase))
                    { lines[i] = kv.Key + "=" + kv.Value; done = true; break; }
                }
                if (!done) lines.Add(kv.Key + "=" + kv.Value);
            }
            string tmp = path + ".tmp";
            File.WriteAllLines(tmp, lines, new UTF8Encoding(false));
            if (File.Exists(path)) File.Delete(path);
            File.Move(tmp, path);
        }
    }
}
