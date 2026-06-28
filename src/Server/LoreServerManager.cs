using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Text;
using System.Threading;
using System.Threading.Tasks;
using LoreVS.SourceControl;

namespace LoreVS.Server
{
    /// <summary>The outcome of an <see cref="LoreServerManager.EnsureRunningAsync"/> call.</summary>
    internal enum EnsureServerResult
    {
        /// <summary>The configured server is not local, so the extension does not manage it.</summary>
        NotManaged,

        /// <summary>A server was already responding to the health check.</summary>
        AlreadyRunning,

        /// <summary>The extension started a local server and it became healthy.</summary>
        Started,

        /// <summary>The <c>loreserver</c> executable could not be found.</summary>
        MissingBinary,

        /// <summary>The server was launched but never reported healthy.</summary>
        FailedToStart,

        /// <summary>A non-default (external) server was configured but is not reachable.</summary>
        ExternalUnreachable,
    }

    /// <summary>
    /// Manages the lifetime of a local Lore server (<c>loreserver.exe</c>) so the user never
    /// has to start one from a terminal. It detects whether a server is already responding
    /// (HTTP health check), launches a hidden one on demand, and stops the one it started when
    /// the package is disposed. This mirrors what the upstream install script's demo mode does
    /// (a zero-config foreground server) but keeps the process under the extension's control.
    /// </summary>
    internal sealed class LoreServerManager : IDisposable
    {
        private readonly object _gate = new object();
        private Process _process;
        private StreamWriter _log;
        private bool _startedByUs;

        /// <summary>The most recent log file the managed server is writing to.</summary>
        public string LogPath { get; private set; }

        /// <summary>True when this manager launched (and therefore owns) a running server.</summary>
        public bool IsManagingProcess
        {
            get
            {
                lock (_gate)
                {
                    return _startedByUs && _process != null && !_process.HasExited;
                }
            }
        }

        /// <summary>
        /// Ensures a Lore server is reachable at <paramref name="endpoint"/> before an operation
        /// that needs it. If a server is already responding it is reused (so multiple Visual Studio
        /// instances share one local server). A non-default endpoint is treated as external and is
        /// never spawned. Otherwise the zero-config demo server is launched on demand.
        /// </summary>
        /// <param name="endpoint">Host/ports to probe and (for the demo endpoint) launch.</param>
        /// <param name="serverExe">Configured loreserver path or bare name.</param>
        /// <param name="storePath">Optional persistent store root; empty = ephemeral demo server.</param>
        public async Task<EnsureServerResult> EnsureRunningAsync(
            LoreServerEndpoint endpoint, string serverExe, string storePath, CancellationToken cancellationToken = default)
        {
            if (await IsHealthyAsync(endpoint).ConfigureAwait(false))
            {
                return EnsureServerResult.AlreadyRunning;
            }

            // We only know how to launch the zero-config demo server. A custom endpoint is an
            // external server the user is expected to run themselves.
            if (!endpoint.IsDefaultDemo)
            {
                return EnsureServerResult.ExternalUnreachable;
            }

            string resolved = LoreToolLocator.Resolve(serverExe);
            if (!(Path.IsPathRooted(resolved) && File.Exists(resolved)))
            {
                return EnsureServerResult.MissingBinary;
            }

            if (!Start(resolved, storePath))
            {
                return EnsureServerResult.FailedToStart;
            }

            // Poll the health endpoint while the server boots (cert generation + store init).
            for (int i = 0; i < 40; i++)
            {
                cancellationToken.ThrowIfCancellationRequested();

                lock (_gate)
                {
                    if (_process == null || _process.HasExited)
                    {
                        return EnsureServerResult.FailedToStart;
                    }
                }

                if (await IsHealthyAsync(endpoint).ConfigureAwait(false))
                {
                    return EnsureServerResult.Started;
                }

                await Task.Delay(500, cancellationToken).ConfigureAwait(false);
            }

            return EnsureServerResult.FailedToStart;
        }

        /// <summary>Performs an HTTP GET against the endpoint's health URL; true on a 2xx response.</summary>
        public Task<bool> IsHealthyAsync(LoreServerEndpoint endpoint)
        {
            string healthUrl = endpoint.HealthUrl;
            return Task.Run(() =>
            {
                try
                {
                    var request = (HttpWebRequest)WebRequest.Create(healthUrl);
                    request.Method = "GET";
                    request.Timeout = 2000;
                    request.ReadWriteTimeout = 2000;
                    request.Proxy = null;
                    using (var response = (HttpWebResponse)request.GetResponse())
                    {
                        return (int)response.StatusCode >= 200 && (int)response.StatusCode < 300;
                    }
                }
                catch
                {
                    return false;
                }
            });
        }

        /// <summary>
        /// Launches a hidden <c>loreserver</c> process. When <paramref name="storePath"/> is set,
        /// a minimal <c>local.toml</c> with persistent store paths is generated and passed via
        /// <c>--config</c>; otherwise the server runs in zero-config (ephemeral) demo mode.
        /// </summary>
        public bool Start(string serverExePath, string storePath)
        {
            lock (_gate)
            {
                if (_process != null && !_process.HasExited)
                {
                    return true;
                }

                try
                {
                    string args = string.Empty;
                    if (!string.IsNullOrWhiteSpace(storePath))
                    {
                        string configDir = PrepareConfig(storePath);
                        args = "--config \"" + configDir + "\"";
                    }

                    string logDir = Path.Combine(Path.GetTempPath(), "LoreVS");
                    Directory.CreateDirectory(logDir);
                    LogPath = Path.Combine(logDir, "loreserver.log");
                    _log = new StreamWriter(new FileStream(LogPath, FileMode.Create, FileAccess.Write, FileShare.Read))
                    {
                        AutoFlush = true,
                    };

                    var psi = new ProcessStartInfo
                    {
                        FileName = serverExePath,
                        Arguments = args,
                        UseShellExecute = false,
                        CreateNoWindow = true,
                        RedirectStandardOutput = true,
                        RedirectStandardError = true,
                        WorkingDirectory = Path.GetDirectoryName(serverExePath) ?? Environment.CurrentDirectory,
                    };
                    if (string.IsNullOrEmpty(psi.EnvironmentVariables["RUST_LOG"]))
                    {
                        psi.EnvironmentVariables["RUST_LOG"] = "info";
                    }

                    var process = new Process { StartInfo = psi, EnableRaisingEvents = true };
                    process.OutputDataReceived += (s, e) => WriteLog(e.Data);
                    process.ErrorDataReceived += (s, e) => WriteLog(e.Data);

                    if (!process.Start())
                    {
                        return false;
                    }

                    process.BeginOutputReadLine();
                    process.BeginErrorReadLine();

                    _process = process;
                    _startedByUs = true;
                    return true;
                }
                catch
                {
                    SafeCloseLog();
                    return false;
                }
            }
        }

        /// <summary>Stops the server this manager started (no-op for an external server).</summary>
        public void Stop()
        {
            lock (_gate)
            {
                try
                {
                    if (_startedByUs && _process != null && !_process.HasExited)
                    {
                        _process.Kill();
                        _process.WaitForExit(3000);
                    }
                }
                catch
                {
                    // Best effort.
                }
                finally
                {
                    _process?.Dispose();
                    _process = null;
                    _startedByUs = false;
                    SafeCloseLog();
                }
            }
        }

        public void Dispose() => Stop();

        /// <summary>
        /// Creates the persistent store directories and a minimal <c>local.toml</c>, returning the
        /// directory to pass to <c>loreserver --config</c>. The certificate section is intentionally
        /// omitted so the server still auto-generates an (ephemeral) self-signed cert — persisting
        /// data without requiring OpenSSL.
        /// </summary>
        private static string PrepareConfig(string storeRoot)
        {
            string immutable = Path.Combine(storeRoot, "immutable");
            string mutable = Path.Combine(storeRoot, "mutable");
            string configDir = Path.Combine(storeRoot, "config");
            Directory.CreateDirectory(immutable);
            Directory.CreateDirectory(mutable);
            Directory.CreateDirectory(configDir);

            var toml = new StringBuilder();
            toml.AppendLine("[immutable_store.local]");
            toml.AppendLine("path = \"" + immutable.Replace("\\", "/") + "\"");
            toml.AppendLine();
            toml.AppendLine("[mutable_store.local]");
            toml.AppendLine("path = \"" + mutable.Replace("\\", "/") + "\"");

            File.WriteAllText(Path.Combine(configDir, "local.toml"), toml.ToString());
            return configDir;
        }

        private void WriteLog(string line)
        {
            if (line == null)
            {
                return;
            }

            lock (_gate)
            {
                try
                {
                    _log?.WriteLine(line);
                }
                catch
                {
                    // Ignore logging failures.
                }
            }
        }

        private void SafeCloseLog()
        {
            try
            {
                _log?.Dispose();
            }
            catch
            {
                // Ignore.
            }
            finally
            {
                _log = null;
            }
        }
    }
}
