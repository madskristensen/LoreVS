using System;
using System.Collections.Concurrent;
using System.Diagnostics;
using System.IO;
using System.Text;
using System.Threading;

namespace LoreVS
{
    /// <summary>
    /// Minimal append-only diagnostic log for the Visual Studio package (in-process) side. The SCC
    /// activation and glyph entry points are driven by Visual Studio on the UI thread and are hard to
    /// observe from the outside, so the key decision points record a line here. The log lives at
    /// <c>%LOCALAPPDATA%\LoreVS\package.log</c> (a sibling of the worker's <c>worker.log</c>) and is
    /// best-effort: logging never throws.
    /// </summary>
    internal static class DiagLog
    {
        private static readonly int _pid = Process.GetCurrentProcess().Id;
        private static readonly string _path = ResolvePath();
        private static readonly BlockingCollection<string> _queue = StartWriter();

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
                return System.IO.Path.Combine(dir, "package.log");
            }
            catch
            {
                return System.IO.Path.Combine(System.IO.Path.GetTempPath(), "LoreVS.Package.log");
            }
        }

        /// <summary>
        /// Queues a single timestamped, process-tagged line for the background writer. Non-blocking
        /// so it is safe to call from UI-thread hot paths (such as glyph queries); never throws.
        /// </summary>
        public static void Write(string message)
        {
            try
            {
                string line = string.Concat(
                    DateTime.Now.ToString("yyyy-MM-dd HH:mm:ss.fff"),
                    " [pid ", _pid.ToString(), "] ",
                    message,
                    Environment.NewLine);

                _queue.Add(line);
            }
            catch
            {
                // Diagnostics must never affect package behavior (e.g. queue completed on shutdown).
            }
        }

        /// <summary>
        /// Starts the single background consumer that drains the queue and appends lines to disk, so
        /// callers never pay file I/O. The thread is a background thread and exits with the process.
        /// </summary>
        private static BlockingCollection<string> StartWriter()
        {
            var queue = new BlockingCollection<string>();
            var thread = new Thread(() =>
            {
                foreach (string line in queue.GetConsumingEnumerable())
                {
                    try
                    {
                        File.AppendAllText(_path, line, Encoding.UTF8);
                    }
                    catch
                    {
                        // Diagnostics must never affect package behavior.
                    }
                }
            })
            {
                IsBackground = true,
                Name = "LoreVS DiagLog",
            };
            thread.Start();
            return queue;
        }
    }
}
