using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Threading;
using StreamJsonRpc;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// <see cref="ILoreClient"/> implementation backed by the native <c>LoreVcs</c> .NET SDK.
    /// Because that SDK targets .NET 9 and loads a native library, it cannot run inside the
    /// .NET Framework Visual Studio process; instead this client launches the out-of-process
    /// <c>LoreVS.Worker</c> (.NET 9/10) and talks to it over a named pipe using JSON-RPC.
    /// </summary>
    /// <remarks>
    /// The synchronous <see cref="ILoreClient"/> surface is adapted to the asynchronous
    /// <see cref="ILoreWorkerContract"/> by blocking on a worker-thread task (callers already
    /// invoke the client off the UI thread). Repository discovery is pure filesystem work and is
    /// served locally without involving the worker. When the worker cannot be launched (for
    /// example the SDK payload was not deployed) read operations report empty status and write
    /// operations return a failed result so the IDE surfaces a clear error.
    /// </remarks>
    public sealed class LoreBrokeredClient : ILoreClient, IDisposable
    {
        /// <summary>How long a cached repository status snapshot stays fresh.</summary>
        private static readonly TimeSpan CacheLifetime = TimeSpan.FromSeconds(3);

        /// <summary>How long to wait for the worker's named pipe to come up.</summary>
        private const int ConnectTimeoutMs = 15000;

        private readonly object _gate = new object();
        private readonly string _workerPath;
        private readonly ConcurrentDictionary<string, CacheEntry> _cache =
            new ConcurrentDictionary<string, CacheEntry>(StringComparer.OrdinalIgnoreCase);

        private Process _workerProcess;
        private NamedPipeClientStream _pipe;
        private JsonRpc _rpc;
        private ILoreWorkerContract _proxy;
        private bool _workerUnavailable;
        private bool _disposed;

        /// <summary>
        /// Creates a brokered client. <paramref name="workerExecutablePath"/> overrides the default
        /// worker location (next to this assembly under <c>Worker\LoreVS.Worker.exe</c>); it is used
        /// by tests to point at a freshly built worker.
        /// </summary>
        public LoreBrokeredClient(string workerExecutablePath = null)
        {
            _workerPath = string.IsNullOrWhiteSpace(workerExecutablePath)
                ? DefaultWorkerPath()
                : workerExecutablePath;
        }

        /// <inheritdoc/>
        public bool IsAvailable
        {
            get
            {
                ILoreWorkerContract proxy = GetProxy();
                if (proxy == null)
                {
                    return false;
                }

                try
                {
                    return Run(ct => proxy.IsAvailableAsync(ct));
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    MarkWorkerFailed();
                    return false;
                }
            }
        }

        /// <inheritdoc/>
        public string FindRepositoryRoot(string path) => LoreRepositoryLocator.FindRoot(path);

        /// <inheritdoc/>
        public LoreFileStatus GetStatus(string filePath)
        {
            if (string.IsNullOrEmpty(filePath))
            {
                return LoreFileStatus.NotControlled;
            }

            string root = FindRepositoryRoot(filePath);
            if (root == null)
            {
                return LoreFileStatus.NotControlled;
            }

            IReadOnlyDictionary<string, LoreFileStatus> statuses = GetRepositoryStatus(root);
            string full = NormalizePath(filePath);
            return statuses.TryGetValue(full, out LoreFileStatus status)
                ? status
                : LoreFileStatus.Unchanged;
        }

        /// <inheritdoc/>
        public IReadOnlyDictionary<string, LoreFileStatus> GetRepositoryStatus(string repositoryRoot)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return EmptyStatus();
            }

            string key = NormalizePath(repositoryRoot);
            if (_cache.TryGetValue(key, out CacheEntry entry) &&
                DateTime.UtcNow - entry.TimestampUtc < CacheLifetime)
            {
                return entry.Statuses;
            }

            IReadOnlyDictionary<string, LoreFileStatus> fresh = QueryStatus(repositoryRoot);
            _cache[key] = new CacheEntry(fresh, DateTime.UtcNow);
            return fresh;
        }

        private IReadOnlyDictionary<string, LoreFileStatus> QueryStatus(string repositoryRoot)
        {
            ILoreWorkerContract proxy = GetProxy();
            if (proxy == null)
            {
                return EmptyStatus();
            }

            try
            {
                LoreStatusEntry[] entries = Run(ct => proxy.GetRepositoryStatusAsync(repositoryRoot, ct));
                var result = new Dictionary<string, LoreFileStatus>(StringComparer.OrdinalIgnoreCase);
                foreach (LoreStatusEntry e in entries)
                {
                    if (!string.IsNullOrEmpty(e.Path))
                    {
                        result[NormalizePath(e.Path)] = e.Status;
                    }
                }

                return result;
            }
            catch (Exception ex)
            {
                LogError(ex);
                MarkWorkerFailed();
                return EmptyStatus();
            }
        }

        /// <inheritdoc/>
        public LoreCommandResult CreateRepository(string workingDirectory, string repositoryUrl, string identity)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.CreateRepositoryAsync(workingDirectory, repositoryUrl, identity, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult StageAll(string workingDirectory)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.StageAllAsync(workingDirectory, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult Commit(string workingDirectory, string message, string identity)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.CommitAsync(workingDirectory, message, identity, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult Push(string workingDirectory)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.PushAsync(workingDirectory, ct));
        }

        /// <inheritdoc/>
        public LoreCommandResult Sync(string workingDirectory)
        {
            InvalidateCache();
            return Invoke((proxy, ct) => proxy.SyncAsync(workingDirectory, ct));
        }

        /// <summary>Clears any cached status so the next query re-runs against the worker.</summary>
        public void InvalidateCache() => _cache.Clear();

        /// <summary>
        /// Runs a write operation against the worker, returning a failed result when the worker is
        /// unavailable or the call fails.
        /// </summary>
        private LoreCommandResult Invoke(
            Func<ILoreWorkerContract, CancellationToken, Task<LoreCommandResult>> operation)
        {
            ILoreWorkerContract proxy = GetProxy();
            if (proxy == null)
            {
                return WorkerUnavailable();
            }

            try
            {
                return Run(ct => operation(proxy, ct));
            }
            catch (Exception ex)
            {
                LogError(ex);
                MarkWorkerFailed();
                return WorkerUnavailable();
            }
        }

        private static LoreCommandResult WorkerUnavailable() =>
            LoreCommandResult.Failed(
                "The Lore worker could not be started. Reinstall the extension so the worker payload " +
                "is deployed, then try again.");

        /// <summary>Lazily launches the worker and returns its JSON-RPC proxy, or null on failure.</summary>
        private ILoreWorkerContract GetProxy()
        {
            if (_workerUnavailable || _disposed)
            {
                return null;
            }

            lock (_gate)
            {
                if (_workerUnavailable || _disposed)
                {
                    return null;
                }

                if (_proxy != null && _workerProcess != null && !_workerProcess.HasExited)
                {
                    return _proxy;
                }

                // A previously connected worker died; tear the stale connection down and relaunch.
                if (_proxy != null)
                {
                    TeardownConnection();
                }

                try
                {
                    StartWorker();
                    return _proxy;
                }
                catch (Exception ex)
                {
                    LogError(ex);
                    TeardownConnection();
                    _workerUnavailable = true;
                    return null;
                }
            }
        }

        private void StartWorker()
        {
            if (!File.Exists(_workerPath))
            {
                throw new FileNotFoundException(
                    $"The Lore worker was not found at '{_workerPath}'. Falling back to the Lore CLI.",
                    _workerPath);
            }

            string pipeName = "lorevs-" + Guid.NewGuid().ToString("N");

            var psi = new ProcessStartInfo
            {
                FileName = _workerPath,
                Arguments = "\"" + pipeName + "\"",
                UseShellExecute = false,
                CreateNoWindow = true,
                RedirectStandardError = true,
                WorkingDirectory = Path.GetDirectoryName(_workerPath) ?? Environment.CurrentDirectory,
            };

            _workerProcess = Process.Start(psi);

            _pipe = new NamedPipeClientStream(".", pipeName, PipeDirection.InOut, PipeOptions.Asynchronous);
            _pipe.Connect(ConnectTimeoutMs);

            var formatter = new SystemTextJsonFormatter();
            var handler = new HeaderDelimitedMessageHandler(_pipe, _pipe, formatter);
            _rpc = new JsonRpc(handler);
            _proxy = _rpc.Attach<ILoreWorkerContract>();
            _rpc.StartListening();
        }

        private void MarkWorkerFailed()
        {
            lock (_gate)
            {
                TeardownConnection();
                _workerUnavailable = true;
            }
        }

        private void TeardownConnection()
        {
            try { _rpc?.Dispose(); } catch { /* best effort */ }
            try { _pipe?.Dispose(); } catch { /* best effort */ }

            try
            {
                if (_workerProcess != null && !_workerProcess.HasExited)
                {
                    _workerProcess.Kill();
                }
            }
            catch { /* best effort */ }
            finally
            {
                try { _workerProcess?.Dispose(); } catch { /* best effort */ }
            }

            _rpc = null;
            _pipe = null;
            _proxy = null;
            _workerProcess = null;
        }

        /// <summary>
        /// Runs an async worker call to completion on a worker thread. <see cref="Task.Run{T}(Func{Task{T}})"/>
        /// detaches from any captured synchronization context so blocking here cannot deadlock the UI thread.
        /// </summary>
        private static T Run<T>(Func<CancellationToken, Task<T>> operation)
        {
            return Task.Run(() => operation(CancellationToken.None)).GetAwaiter().GetResult();
        }

        private static string DefaultWorkerPath()
        {
            try
            {
                string baseDir = Path.GetDirectoryName(typeof(LoreBrokeredClient).Assembly.Location);
                return Path.Combine(baseDir ?? string.Empty, "Worker", "LoreVS.Worker.exe");
            }
            catch
            {
                return "LoreVS.Worker.exe";
            }
        }

        private static string NormalizePath(string path)
        {
            try
            {
                return Path.GetFullPath(path).TrimEnd('\\', '/');
            }
            catch
            {
                return path;
            }
        }

        private static IReadOnlyDictionary<string, LoreFileStatus> EmptyStatus() =>
            new Dictionary<string, LoreFileStatus>(0);

        private static void LogError(Exception ex)
        {
            try
            {
                // Prefer the toolkit's logger; fall back to Debug when running outside Visual Studio.
                ex.LogAsync().FireAndForget();
            }
            catch
            {
                Debug.WriteLine($"[LoreVS] brokered client error: {ex.Message}");
            }
        }

        public void Dispose()
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            lock (_gate)
            {
                TeardownConnection();
            }

            _cache.Clear();
        }

        private readonly struct CacheEntry
        {
            public CacheEntry(IReadOnlyDictionary<string, LoreFileStatus> statuses, DateTime timestampUtc)
            {
                Statuses = statuses;
                TimestampUtc = timestampUtc;
            }

            public IReadOnlyDictionary<string, LoreFileStatus> Statuses { get; }

            public DateTime TimestampUtc { get; }
        }
    }
}
