using System;
using System.Text.RegularExpressions;

namespace NvrBridge
{
    /// <summary>Log sederhana ke konsol. Nilai UID sesi NVR disamarkan.</summary>
    static class Log
    {
        public static bool Verbose;
        static readonly object Gate = new object();
        static readonly Regex UidRx = new Regex(@"UID=\d+", RegexOptions.Compiled);

        public static void Info(string m) { Write("INFO", m); }
        public static void Warn(string m) { Write("WARN", m); }
        public static void Debug(string m) { if (Verbose) Write("DBG ", m); }

        static void Write(string level, string m)
        {
            m = UidRx.Replace(m ?? "", "UID=***");
            lock (Gate)
                Console.WriteLine(DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss") + " " + level + " " + m);
        }
    }
}
