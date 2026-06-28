using System;
using System.IO;
using System.Text;

namespace LoreVS.Worker
{
    /// <summary>
    /// Minimal append-only diagnostic log for the worker process. Native Lore operations are opaque
    /// from the Visual Studio side, so each one records a start / ok / failed / timeout line here to
    /// make hangs and errors observable. The log lives at
    /// <c>%LOCALAPPDATA%\LoreVS\worker.log</c> and is best-effort: logging never throws.
    /// </summary>
    internal static class WorkerLog
    {
        private static readonly object _gate = new object();
        private static readonly int _pid = Environment.ProcessId;
        private static readonly string _path = ResolvePath();

        /// <summary>Absolute path of the log file, for surfacing to the user.</summary>
        public static string Path => _path;

        private static string ResolvePath()
        {
            try
            {
                string dir = System.IO.Path.Combine(
                    Environment.GetFolderPath(Environment.SpecialFolder.LocalApplicationData),
                    "LoreVS");
                Directory.CreateDirectory(dir);
                return System.IO.Path.Combine(dir, "worker.log");
            }
            catch
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LoreVS.Worker.log");
            }
        }

        /// <summary>Appends a single timestamped, process-tagged line. Never throws.</summary>
        public static void Write(string message)
        {
            try
            {
                string line = string.Concat(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    " [pid ", _pid.ToString(), "] ",
                    message,
                    Environment.NewLine);

                lock (_gate)
                {
                    File.AppendAllText(_path, line, Encoding.UTF8);
                }
            }
            catch
            {
                // Diagnostics must never affect worker behavior.
            }
        }
    }
}
