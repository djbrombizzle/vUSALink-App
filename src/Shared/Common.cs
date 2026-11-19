using System;
using System.IO;
using System.Reflection;
using System.Threading;

namespace VusaLinkApp
{
    // Log next to the exe, named after it (vUSALinkApp.log / vUSALinkLite.log).
    static class Log
    {
        static readonly object Sync = new object();
        static readonly string FilePath = Path.Combine(AppDomain.CurrentDomain.BaseDirectory,
            Path.GetFileNameWithoutExtension(Assembly.GetEntryAssembly() != null ? Assembly.GetEntryAssembly().Location : "vUSALink") + ".log");

        public static string File_ { get { return FilePath; } }

        public static void Info(string s)
        {
            try
            {
                lock (Sync)
                    File.AppendAllText(FilePath, DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff") + " " + s + Environment.NewLine);
            }
            catch { }
        }
    }

    // Data shared by vUSALink App and vUSALink-Lite (learned CIDs, settings): %LOCALAPPDATA%\vUSALinkApp
    static class AppPaths
    {
        public static readonly string Dir = Path.Combine(
            Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData), "vUSALinkApp");
    }

    // Only one program may watch CRC at a time: if vUSALink App and vUSALink-Lite both trimmed /U from CRC's
    // line, CRC would get twice the Backspaces. The first one to start owns CRC mirroring.
    static class CrcMirrorLock
    {
        static Mutex mutex;

        public static bool TryAcquire()
        {
            if (mutex != null) return true;
            bool created;
            var m = new Mutex(true, @"Local\vUSALink.CrcMirror", out created);
            if (!created)
            {
                try { created = m.WaitOne(0); } catch (AbandonedMutexException) { created = true; }
            }
            if (created) { mutex = m; return true; }
            m.Dispose();
            return false;
        }
    }
}
