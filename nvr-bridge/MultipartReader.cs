using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Text;
using System.Threading;
using System.Threading.Tasks;

namespace NvrBridge
{
    /// <summary>
    /// Pembaca multipart (boundary) yang bekerja sebagai stream: isi bagian ditulis langsung ke
    /// sink sehingga file besar tidak ditampung di memori. Mendukung bagian dengan atau tanpa
    /// Content-Length.
    /// </summary>
    sealed class MultipartReader
    {
        readonly Stream s;
        readonly string boundaryLine;
        readonly byte[] delim;
        readonly Action<long> onBytes;
        readonly byte[] buf = new byte[128 * 1024];
        int pos, len;
        bool eof;

        public MultipartReader(Stream s, string boundary, Action<long> onBytes)
        {
            this.s = s;
            this.onBytes = onBytes;
            boundaryLine = "--" + boundary;
            delim = Encoding.ASCII.GetBytes("\r\n--" + boundary);
        }

        public async Task ReadAllAsync(
            Func<Dictionary<string, string>, Stream> begin,
            Action<Dictionary<string, string>> end,
            CancellationToken ct)
        {
            // Lewati preamble sampai baris boundary pertama.
            while (true)
            {
                string line = await ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) return;
                if (line == boundaryLine) break;
                if (line == boundaryLine + "--") return;
            }

            while (true)
            {
                var h = new Dictionary<string, string>(StringComparer.OrdinalIgnoreCase);
                while (true)
                {
                    string line = await ReadLineAsync(ct).ConfigureAwait(false);
                    if (line == null) return;               // terputus saat header
                    if (line.Length == 0) break;
                    int c = line.IndexOf(':');
                    if (c > 0) h[line.Substring(0, c).Trim()] = line.Substring(c + 1).Trim();
                }
                Log.Debug("bagian multipart: " + string.Join("; ", h.Select(kv => kv.Key + "=" + kv.Value)));

                Stream sink = begin(h);
                bool final;
                string cl;
                long clen;
                if (h.TryGetValue("Content-Length", out cl) && long.TryParse(cl, out clen) && clen >= 0)
                {
                    await CopyExactAsync(sink, clen, ct).ConfigureAwait(false);
                    final = await ReadBoundaryLineAsync(ct).ConfigureAwait(false);
                }
                else
                {
                    bool found = await CopyUntilDelimiterAsync(sink, ct).ConfigureAwait(false);
                    if (!found) { end(h); return; }         // data berakhir tanpa boundary penutup
                    string rest = await ReadLineAsync(ct).ConfigureAwait(false);
                    final = rest == null || rest.StartsWith("--");
                }
                end(h);
                if (final) return;
            }
        }

        // ---- buffer ----

        async Task<bool> FillAsync(CancellationToken ct)
        {
            if (eof) return false;
            if (pos > 0)
            {
                Buffer.BlockCopy(buf, pos, buf, 0, len - pos);
                len -= pos;
                pos = 0;
            }
            if (len == buf.Length) return true;
            int n = await s.ReadAsync(buf, len, buf.Length - len, ct).ConfigureAwait(false);
            if (n <= 0) { eof = true; return false; }
            len += n;
            return true;
        }

        async Task<string> ReadLineAsync(CancellationToken ct)
        {
            while (true)
            {
                int nl = Array.IndexOf(buf, (byte)'\n', pos, len - pos);
                if (nl >= 0)
                {
                    int e = nl;
                    if (e > pos && buf[e - 1] == (byte)'\r') e--;
                    string line = Encoding.ASCII.GetString(buf, pos, e - pos);
                    pos = nl + 1;
                    return line;
                }
                if (len - pos > 16384) throw new FormatException("Baris header multipart terlalu panjang.");
                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    if (len > pos)
                    {
                        string rest = Encoding.ASCII.GetString(buf, pos, len - pos);
                        pos = len;
                        return rest;
                    }
                    return null;
                }
            }
        }

        /// <summary>Setelah bagian ber-Content-Length: lewati baris kosong, baca boundary. True jika boundary penutup/EOF.</summary>
        async Task<bool> ReadBoundaryLineAsync(CancellationToken ct)
        {
            while (true)
            {
                string line = await ReadLineAsync(ct).ConfigureAwait(false);
                if (line == null) return true;
                if (line.Length == 0) continue;
                if (line == boundaryLine) return false;
                if (line == boundaryLine + "--") return true;
                throw new FormatException("Boundary multipart tidak dikenali setelah isi bagian.");
            }
        }

        async Task CopyExactAsync(Stream sink, long count, CancellationToken ct)
        {
            while (count > 0)
            {
                int avail = len - pos;
                if (avail == 0)
                {
                    if (!await FillAsync(ct).ConfigureAwait(false))
                        throw new EndOfStreamException("Data dari NVR terputus sebelum bagian selesai.");
                    continue;
                }
                int take = (int)Math.Min(avail, count);
                await sink.WriteAsync(buf, pos, take, ct).ConfigureAwait(false);
                onBytes?.Invoke(take);
                pos += take;
                count -= take;
            }
        }

        /// <summary>Salin ke sink sampai "\r\n--boundary". False jika stream habis tanpa boundary.</summary>
        async Task<bool> CopyUntilDelimiterAsync(Stream sink, CancellationToken ct)
        {
            while (true)
            {
                int idx = IndexOf(buf, pos, len, delim);
                if (idx >= 0)
                {
                    int n = idx - pos;
                    if (n > 0) { await sink.WriteAsync(buf, pos, n, ct).ConfigureAwait(false); onBytes?.Invoke(n); }
                    pos = idx + delim.Length;
                    return true;
                }
                int safe = (len - pos) - (delim.Length - 1);
                if (safe > 0)
                {
                    await sink.WriteAsync(buf, pos, safe, ct).ConfigureAwait(false);
                    onBytes?.Invoke(safe);
                    pos += safe;
                }
                if (!await FillAsync(ct).ConfigureAwait(false))
                {
                    int rest = len - pos;
                    if (rest > 0) { await sink.WriteAsync(buf, pos, rest, ct).ConfigureAwait(false); onBytes?.Invoke(rest); pos = len; }
                    return false;
                }
            }
        }

        static int IndexOf(byte[] b, int start, int end, byte[] pat)
        {
            int last = end - pat.Length;
            int i = start;
            while (i <= last)
            {
                i = Array.IndexOf(b, pat[0], i, last - i + 1);
                if (i < 0) return -1;
                int j = 1;
                while (j < pat.Length && b[i + j] == pat[j]) j++;
                if (j == pat.Length) return i;
                i++;
            }
            return -1;
        }
    }
}
