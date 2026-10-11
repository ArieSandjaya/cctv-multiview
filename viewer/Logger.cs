using System;
using System.IO;
using System.Text;

namespace CctvViewer
{
    /// <summary>Log berkas sederhana dengan rotasi (maks 5 MB, simpan 1 cadangan). Tidak pernah melempar exception.</summary>
    sealed class FileLog
    {
        readonly string path;
        readonly object gate = new object();
        const long Max = 5 * 1024 * 1024;
        public FileLog(string path) { this.path = path; }

        public void Write(string line)
        {
            lock (gate)
            {
                try
                {
                    var fi = new FileInfo(path);
                    if (fi.Exists && fi.Length > Max)
                    {
                        string old = path + ".1";
                        if (File.Exists(old)) File.Delete(old);
                        File.Move(path, old);
                    }
                    File.AppendAllText(path, line + Environment.NewLine, new UTF8Encoding(false));
                }
                catch { }
            }
        }
    }

    static class Log
    {
        static FileLog file;
        public static void Init(string path) { file = new FileLog(path); }
        static void Put(string level, string msg)
        {
            var f = file;
            if (f != null) f.Write(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " [" + level + "] " + msg);
        }
        public static void Info(string m) { Put("INFO", m); }
        public static void Warn(string m) { Put("WARN", m); }
        public static void Error(string m) { Put("ERROR", m); }
    }
}
