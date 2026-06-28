using System;
using System.Collections.Generic;
using System.IO;
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

            // The native SDK drives its own runtime synchronously; run it on a worker thread and
            // block there with Wait() so we never start a tokio runtime from within another one.
            // The disposable value-type args are built inside the worker thread so they are not
            // captured (and disposed) across the await boundary.
            await Task.Run(() =>
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
                }).Wait();
            }).ConfigureAwait(false);

            return entries.ToArray();
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
                // The native SDK blocks its own runtime; run on a worker thread so we never start a
                // tokio runtime from within another one.
                await Task.Run(() => operation(globalArgs)).ConfigureAwait(false);
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
