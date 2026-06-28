using System;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Runtime.CompilerServices;
using System.Threading;
using System.Threading.Tasks;
using LoreVcs;
using LoreVcs.Types;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;
using LoreVS.SourceControl;

namespace LoreVS.Worker
{
    /// <summary>
    /// Worker-side implementation of <see cref="ILoreWorkerContract"/>. Each method wraps the
    /// native <c>LoreVcs</c> fluent SDK and is invoked over JSON-RPC by the in-process
    /// <c>LoreBrokeredClient</c>. The SDK throws <see cref="LoreError"/> on a non-zero result; that
    /// is translated into a non-successful <see cref="LoreCommandResult"/> rather than propagated,
    /// so the package surface stays identical to the old CLI client.
    /// </summary>
    internal sealed class LoreServiceImpl : ILoreWorkerContract
    {
        // Upper bound on any single native SDK operation. The native calls drive their own runtime
        // and block synchronously; if one stalls (for example a revision lookup that blocks on a
        // remote) we must surface a timeout to the caller instead of hanging the JSON-RPC reply
        // forever, which would freeze Solution Explorer glyphs and the Lore Changes window.
        private const int SdkTimeoutMs = 30000;

        // Each native SDK call drives its own tokio runtime synchronously, so it must run on a
        // dedicated worker thread and block there with Wait(). Calls are NOT funneled through a
        // shared gate: a single slow/stuck native operation must never be able to stall unrelated
        // operations such as the file status scans that drive Solution Explorer glyphs.
        /// <summary>
        /// Runs a blocking native SDK action on a dedicated worker thread, logging its lifecycle and
        /// enforcing <see cref="SdkTimeoutMs"/> so a stuck native call surfaces as a
        /// <see cref="TimeoutException"/> the caller can recover from rather than an infinite hang.
        /// </summary>
        private static async Task RunExclusiveAsync(Action action, [CallerMemberName] string operation = "")
        {
            WorkerLog.Write(operation + ": start");
            var sw = Stopwatch.StartNew();

            Task work = Task.Run(action);
            Task finished = await Task.WhenAny(work, Task.Delay(SdkTimeoutMs)).ConfigureAwait(false);
            if (finished != work)
            {
                WorkerLog.Write($"{operation}: TIMEOUT after {sw.ElapsedMilliseconds} ms");
                throw new TimeoutException(
                    $"The Lore SDK operation '{operation}' did not complete within {SdkTimeoutMs} ms.");
            }

            try
            {
                await work.ConfigureAwait(false);
                WorkerLog.Write($"{operation}: ok in {sw.ElapsedMilliseconds} ms");
            }
            catch (Exception ex)
            {
                WorkerLog.Write($"{operation}: FAILED in {sw.ElapsedMilliseconds} ms: {ex.GetType().Name}: {ex.Message}");
                throw;
            }
        }

        /// <inheritdoc/>
        public Task<bool> IsAvailableAsync(CancellationToken cancellationToken)
        {
            // Reaching this method means the worker started and the SDK assembly loaded; the only
            // way to be here is for the native lorelib to have resolved, so report availability.
            return Task.FromResult(true);
        }

        /// <inheritdoc/>
        public async Task<LoreStatusEntry[]> GetRepositoryStatusAsync(string repositoryRoot, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return Array.Empty<LoreStatusEntry>();
            }

            var entries = new List<LoreStatusEntry>();

            // The native SDK drives its own runtime synchronously; run it on a worker thread under
            // the SDK gate and block there with Wait() so we never start a tokio runtime from within
            // another one. The disposable value-type args are built inside the worker thread so they
            // are not captured (and disposed) across the await boundary.
            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, Offline = true };
                using var statusArgs = new LoreRepositoryStatusArgs
                {
                    Scan = true,
                    CheckDirty = true,
                    Paths = new[] { repositoryRoot },
                };

                Lore.RepositoryStatus(globalArgs, statusArgs).Callback((loreEvent, userContext) =>
                {
                    // Never let an exception escape into the native callback: a throw across the
                    // managed/native boundary can leave the SDK's event loop waiting forever, which
                    // would hang the Wait() below and freeze the caller.
                    try
                    {
                        if (loreEvent.Tag != LoreEventTag.REPOSITORY_STATUS_FILE)
                        {
                            return;
                        }

                        LoreRepositoryStatusFileEventDataFFI file = loreEvent.GetData<LoreRepositoryStatusFileEventDataFFI>();
                        entries.Add(new LoreStatusEntry
                        {
                            Path = NormalizePath(repositoryRoot, file.Path),
                            Status = LoreStatusMapper.Map(file),
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[LoreVS.Worker] status callback failed: " + ex);
                    }
                }).Wait();
            }).ConfigureAwait(false);

            return entries.ToArray();
        }

        /// <inheritdoc/>
        public async Task<LoreRepositorySnapshot> GetRepositorySnapshotAsync(string repositoryRoot, CancellationToken cancellationToken)
        {
            var snapshot = new LoreRepositorySnapshot();
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return snapshot;
            }

            var entries = new List<LoreStatusEntry>();
            var info = new LoreRepositoryInfo();
            var sawRevision = false;

            // Gather the file list AND the revision summary from a SINGLE status pass. The native SDK
            // emits both REPOSITORY_STATUS_FILE and REPOSITORY_STATUS_REVISION events during one scan,
            // and it does not reliably tolerate two back-to-back RepositoryStatus invocations (a second
            // call can hang). Folding both into the one proven status call avoids that hang entirely.
            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, Offline = true };
                using var statusArgs = new LoreRepositoryStatusArgs
                {
                    Scan = true,
                    CheckDirty = true,
                    Paths = new[] { repositoryRoot },
                };

                Lore.RepositoryStatus(globalArgs, statusArgs).Callback((loreEvent, userContext) =>
                {
                    // Never let an exception escape into the native callback: a throw across the
                    // managed/native boundary can leave the SDK's event loop waiting forever.
                    try
                    {
                        if (loreEvent.Tag == LoreEventTag.REPOSITORY_STATUS_FILE)
                        {
                            LoreRepositoryStatusFileEventDataFFI file = loreEvent.GetData<LoreRepositoryStatusFileEventDataFFI>();
                            entries.Add(new LoreStatusEntry
                            {
                                Path = NormalizePath(repositoryRoot, file.Path),
                                Status = LoreStatusMapper.Map(file),
                            });
                        }
                        else if (loreEvent.Tag == LoreEventTag.REPOSITORY_STATUS_REVISION)
                        {
                            LoreRepositoryStatusRevisionEventDataFFI rev = loreEvent.GetData<LoreRepositoryStatusRevisionEventDataFFI>();
                            info.BranchName = rev.BranchName ?? string.Empty;
                            info.HasRemote = rev.RemoteBranchExist;
                            info.IsLocalAhead = rev.IsLocalAhead;
                            info.IsRemoteAhead = rev.IsRemoteAhead;
                            info.LocalRevisionNumber = (long)rev.RevisionLocalNumber;
                            info.RemoteRevisionNumber = (long)rev.RevisionRemoteNumber;
                            sawRevision = true;
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[LoreVS.Worker] snapshot callback failed: " + ex);
                    }
                }).Wait();
            }).ConfigureAwait(false);

            snapshot.Files = entries.ToArray();
            snapshot.Info = sawRevision ? info : null;
            return snapshot;
        }

        /// <inheritdoc/>
        public async Task<LoreRepositoryInfo> GetRepositoryInfoAsync(string repositoryRoot, CancellationToken cancellationToken)
        {
            var info = new LoreRepositoryInfo();
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return info;
            }

            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, Offline = true };
                using var statusArgs = new LoreRepositoryStatusArgs
                {
                    // Mirror the (proven) file-status scan args: the revision event is emitted as
                    // part of the same status pass, so use the identical configuration that already
                    // works for GetRepositoryStatusAsync rather than a second, untested variation.
                    Scan = true,
                    CheckDirty = true,
                    Paths = new[] { repositoryRoot },
                };

                Lore.RepositoryStatus(globalArgs, statusArgs).Callback((loreEvent, userContext) =>
                {
                    // Guard the native callback so a marshalling failure can never escape into the
                    // SDK event loop and hang the Wait() below.
                    try
                    {
                        if (loreEvent.Tag != LoreEventTag.REPOSITORY_STATUS_REVISION)
                        {
                            return;
                        }

                        LoreRepositoryStatusRevisionEventDataFFI rev = loreEvent.GetData<LoreRepositoryStatusRevisionEventDataFFI>();
                        info.BranchName = rev.BranchName ?? string.Empty;
                        info.HasRemote = rev.RemoteBranchExist;
                        info.IsLocalAhead = rev.IsLocalAhead;
                        info.IsRemoteAhead = rev.IsRemoteAhead;
                        info.LocalRevisionNumber = (long)rev.RevisionLocalNumber;
                        info.RemoteRevisionNumber = (long)rev.RevisionRemoteNumber;
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[LoreVS.Worker] revision callback failed: " + ex);
                    }
                }).Wait();
            }).ConfigureAwait(false);

            return info;
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> CreateRepositoryAsync(string workingDirectory, string repositoryUrl, string identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(workingDirectory) || string.IsNullOrWhiteSpace(repositoryUrl))
            {
                return Task.FromResult(LoreCommandResult.Failed("A working directory and repository URL are required."));
            }

            return ExecuteAsync(identity, offline: false, workingDirectory, globalArgs =>
            {
                using var args = new LoreRepositoryCreateArgs { RepositoryUrl = repositoryUrl };
                Lore.RepositoryCreate(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> StageAllAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            return ExecuteAsync(identity: null, offline: true, workingDirectory, globalArgs =>
            {
                using var args = new LoreFileStageArgs { Paths = new[] { workingDirectory }, Scan = true };
                Lore.FileStage(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> CommitAsync(string workingDirectory, string message, string identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Task.FromResult(LoreCommandResult.Failed("A commit message is required."));
            }

            return ExecuteAsync(identity, offline: true, workingDirectory, globalArgs =>
            {
                using var args = new LoreRevisionCommitArgs { Message = message };
                Lore.RevisionCommit(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> AmendAsync(string workingDirectory, string message, string identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Task.FromResult(LoreCommandResult.Failed("A commit message is required."));
            }

            return ExecuteAsync(identity, offline: true, workingDirectory, globalArgs =>
            {
                // Fold any current working-tree changes into the amended revision, then rewrite it.
                using (var stageArgs = new LoreFileStageArgs { Paths = new[] { workingDirectory }, Scan = true })
                {
                    Lore.FileStage(globalArgs, stageArgs).Wait();
                }

                using var args = new LoreRevisionAmendArgs { Message = message };
                Lore.RevisionAmend(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> PushAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            return ExecuteAsync(identity: null, offline: false, workingDirectory, globalArgs =>
            {
                using var args = new LoreBranchPushArgs();
                Lore.BranchPush(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> SyncAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            return ExecuteAsync(identity: null, offline: false, workingDirectory, globalArgs =>
            {
                using var args = new LoreRevisionSyncArgs();
                Lore.RevisionSync(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> ResetFilesAsync(string workingDirectory, string[] paths, CancellationToken cancellationToken)
        {
            if (paths == null || paths.Length == 0)
            {
                return Task.FromResult(LoreCommandResult.Failed("At least one path is required to discard changes."));
            }

            return ExecuteAsync(identity: null, offline: true, workingDirectory, globalArgs =>
            {
                using var args = new LoreFileResetArgs { Paths = paths };
                Lore.FileReset(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> WriteFileAtRevisionAsync(string workingDirectory, string relativePath, string revision, string outputPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(relativePath) || string.IsNullOrWhiteSpace(outputPath))
            {
                return Task.FromResult(LoreCommandResult.Failed("A file path and an output path are required."));
            }

            return ExecuteAsync(identity: null, offline: true, workingDirectory, globalArgs =>
            {
                using var args = new LoreFileWriteArgs
                {
                    Path = relativePath,
                    Revision = revision ?? string.Empty,
                    Output = outputPath,
                };
                Lore.FileWrite(globalArgs, args).Wait();
            });
        }

        /// <summary>
        /// Runs a write operation against a configured <see cref="LoreGlobalArgs"/> and maps the
        /// outcome to a <see cref="LoreCommandResult"/>. The SDK throws on a non-zero result; any
        /// exception becomes a failed result carrying the error detail.
        /// </summary>
        private static async Task<LoreCommandResult> ExecuteAsync(string? identity, bool offline, string workingDirectory, Action<LoreGlobalArgs> operation)
        {
            // LoreGlobalArgs is a disposable value type, so it cannot be mutated through a 'using'
            // local; build it, set the optional identity, and dispose explicitly in finally.
            var globalArgs = new LoreGlobalArgs { RepositoryPath = workingDirectory, Offline = offline };
            if (!string.IsNullOrWhiteSpace(identity))
            {
                globalArgs.Identity = identity;
            }

            try
            {
                // The native SDK blocks its own runtime; run on a worker thread under the global SDK
                // gate so we never start a tokio runtime from within another one or concurrently with
                // an in-flight status scan.
                await RunExclusiveAsync(() => operation(globalArgs)).ConfigureAwait(false);
                return new LoreCommandResult(true, 0, string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                return new LoreCommandResult(false, 1, string.Empty, Unwrap(ex).Message);
            }
            finally
            {
                globalArgs.Dispose();
            }
        }

        /// <summary>Unwraps the inner exception from the SDK's blocking call, if present.</summary>
        private static Exception Unwrap(Exception ex) =>
            ex is AggregateException agg && agg.InnerException != null ? agg.InnerException : ex;

        /// <summary>Returns an absolute, normalized path for a status entry relative to the repo root.</summary>
        private static string NormalizePath(string repositoryRoot, string path)
        {
            try
            {
                string full = Path.IsPathRooted(path) ? path : Path.Combine(repositoryRoot, path);
                return Path.GetFullPath(full).TrimEnd('\\', '/');
            }
            catch
            {
                return path;
            }
        }
    }
}
