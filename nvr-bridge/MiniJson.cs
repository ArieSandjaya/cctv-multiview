using System;
using System.Collections.Generic;
using System.Globalization;
using System.Text;

namespace NvrBridge
{
    /// <summary>Pembaca JSON kecil tanpa pustaka luar. Hasil: Dictionary&lt;string,object&gt;, List&lt;object&gt;, string, double, bool, atau null.</summary>
    static class MiniJson
    {
        public sealed class JsonException : Exception { public JsonException(string m) : base(m) { } }

        public static object Parse(string s)
        {
            int i = 0;
            object v = Value(s, ref i, 0);
            Ws(s, ref i);
            if (i != s.Length) throw new JsonException("Karakter berlebih di akhir JSON.");
            return v;
        }

        static void Ws(string s, ref int i) { while (i < s.Length && char.IsWhiteSpace(s[i])) i++; }

        static object Value(string s, ref int i, int depth)
        {
            if (depth > 16) throw new JsonException("JSON terlalu bersarang.");
            Ws(s, ref i);
            if (i >= s.Length) throw new JsonException("JSON berakhir terlalu cepat.");
            char c = s[i];
            if (c == '{')
            {
                i++;
                var d = new Dictionary<string, object>(StringComparer.Ordinal);
                Ws(s, ref i);
                if (i < s.Length && s[i] == '}') { i++; return d; }
                while (true)
                {
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != '"') throw new JsonException("Nama properti harus string.");
                    string k = Str(s, ref i);
                    Ws(s, ref i);
                    if (i >= s.Length || s[i] != ':') throw new JsonException("Tanda ':' tidak ditemukan.");
                    i++;
                    d[k] = Value(s, ref i, depth + 1);
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == '}') { i++; return d; }
                    throw new JsonException("Objek JSON tidak valid.");
                }
            }
            if (c == '[')
            {
                i++;
                var l = new List<object>();
                Ws(s, ref i);
                if (i < s.Length && s[i] == ']') { i++; return l; }
                while (true)
                {
                    l.Add(Value(s, ref i, depth + 1));
                    Ws(s, ref i);
                    if (i < s.Length && s[i] == ',') { i++; continue; }
                    if (i < s.Length && s[i] == ']') { i++; return l; }
                    throw new JsonException("Larik JSON tidak valid.");
                }
            }
            if (c == '"') return Str(s, ref i);
            if (Lit(s, ref i, "true")) return true;
            if (Lit(s, ref i, "false")) return false;
            if (Lit(s, ref i, "null")) return null;
            int st = i;
            while (i < s.Length && "+-0123456789.eE".IndexOf(s[i]) >= 0) i++;
            double n;
            if (i == st || !double.TryParse(s.Substring(st, i - st), NumberStyles.Float, CultureInfo.InvariantCulture, out n))
                throw new JsonException("Nilai JSON tidak dikenal.");
            return n;
        }

        static bool Lit(string s, ref int i, string w)
        {
            if (string.CompareOrdinal(s, i, w, 0, w.Length) != 0) return false;
            i += w.Length; return true;
        }

        static string Str(string s, ref int i)
        {
            i++;                                     // tanda kutip pembuka
            var sb = new StringBuilder();
            while (i < s.Length)
            {
                char c = s[i++];
                if (c == '"') return sb.ToString();
                if (c != '\\') { sb.Append(c); continue; }
                if (i >= s.Length) break;
                char e = s[i++];
                switch (e)
                {
                    case '"': sb.Append('"'); break;
                    case '\\': sb.Append('\\'); break;
                    case '/': sb.Append('/'); break;
                    case 'b': sb.Append('\b'); break;
                    case 'f': sb.Append('\f'); break;
                    case 'n': sb.Append('\n'); break;
                    case 'r': sb.Append('\r'); break;
                    case 't': sb.Append('\t'); break;
                    case 'u':
                        int code;
                        if (i + 4 > s.Length || !int.TryParse(s.Substring(i, 4), NumberStyles.HexNumber, CultureInfo.InvariantCulture, out code))
                            throw new JsonException("Escape \\u tidak valid.");
                        sb.Append((char)code); i += 4; break;
                    default: throw new JsonException("Escape tidak dikenal.");
                }
            }
            throw new JsonException("String JSON tidak ditutup.");
        }

        // ---------- bantuan membaca ----------
        public static Dictionary<string, object> Obj(object o) { return o as Dictionary<string, object>; }
        public static string Str(Dictionary<string, object> d, string k, string def = "")
        {
            object v; return d != null && d.TryGetValue(k, out v) && v is string ? (string)v : def;
        }
        public static bool Bool(Dictionary<string, object> d, string k, bool def)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v is bool ? (bool)v : def;
        }
        public static int Int(Dictionary<string, object> d, string k, int def)
        {
            object v; return d != null && d.TryGetValue(k, out v) && v is double ? (int)Math.Round((double)v) : def;
        }
        public static List<object> List(Dictionary<string, object> d, string k)
        {
            object v; return d != null && d.TryGetValue(k, out v) ? v as List<object> : null;
        }
    }
}
