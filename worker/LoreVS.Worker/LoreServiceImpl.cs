using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Diagnostics;
using System.IO;
using System.Linq;
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

        // Interactive sign-in blocks while the user completes the browser-based login, so it gets a
        // far larger budget than ordinary operations. Five minutes is generous for a human round trip
        // while still bounding a flow the user abandoned without cancelling.
        private const int LoginTimeoutMs = 300000;

        // Lore's native runtime is process-global and cannot safely execute multiple SDK operations
        // concurrently. Serialize calls before starting their dedicated worker threads.
        private static readonly SemaphoreSlim _sdkGate = new SemaphoreSlim(1, 1);

        /// <summary>
        /// Runs a blocking native SDK action on a dedicated worker thread, logging its lifecycle and
        /// enforcing <see cref="SdkTimeoutMs"/> so a stuck native call surfaces as a
        /// <see cref="TimeoutException"/> the caller can recover from rather than an infinite hang.
        /// </summary>
        private static async Task RunExclusiveAsync(Action action, [CallerMemberName] string operation = "")
        {
            await RunExclusiveAsync(action, SdkTimeoutMs, operation).ConfigureAwait(false);
        }

        /// <summary>
        /// Runs a blocking native SDK action on a dedicated worker thread with an explicit timeout.
        /// Used by interactive sign-in, which blocks on a human completing the browser login and so
        /// needs a far larger budget than <see cref="SdkTimeoutMs"/>.
        /// </summary>
        private static async Task RunExclusiveAsync(Action action, int timeoutMs, [CallerMemberName] string operation = "")
        {
            await _sdkGate.WaitAsync().ConfigureAwait(false);
            try
            {
                WorkerLog.Write(operation + ": start");
                var sw = Stopwatch.StartNew();

                Task work = Task.Run(action);
                Task finished = await Task.WhenAny(work, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (finished != work)
                {
                    WorkerLog.Write($"{operation}: TIMEOUT after {sw.ElapsedMilliseconds} ms");
                    throw new TimeoutException(
                        $"The Lore SDK operation '{operation}' did not complete within {timeoutMs} ms.");
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
            finally
            {
                _sdkGate.Release();
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
        public async Task<LoreAuthResult> LoginAsync(string workingDirectory, string remoteUrl, CancellationToken cancellationToken)
        {
            var result = new LoreAuthResult();
            try
            {
                // no_browser=true makes the SDK emit the login URL (AUTH_URL) instead of opening a
                // browser itself; the worker opens it via the shell so the package never has to. The
                // native call then blocks until the user finishes signing in, hence the long timeout.
                await RunExclusiveAsync(
                    () =>
                    {
                        var globalArgs = new LoreGlobalArgs { Offline = false };
                        if (!string.IsNullOrWhiteSpace(workingDirectory))
                        {
                            globalArgs.RepositoryPath = workingDirectory;
                            globalArgs.WorkingDirectory = workingDirectory;
                        }

                        try
                        {
                            using var args = new LoreAuthLoginInteractiveArgs
                            {
                                RemoteUrl = remoteUrl ?? string.Empty,
                                NoBrowser = true,
                            };

                            Lore.AuthLoginInteractive(globalArgs, args).Callback((loreEvent, userContext) =>
                            {
                                switch (loreEvent.Tag)
                                {
                                    case LoreEventTag.AUTH_URL:
                                        LoreAuthUrlEventDataFFI url = loreEvent.GetData<LoreAuthUrlEventDataFFI>();
                                        result.LoginUrl = url.Url ?? string.Empty;
                                        OpenBrowser(result.LoginUrl);
                                        break;
                                    case LoreEventTag.AUTH_USER_INFO:
                                        LoreAuthUserInfoEventDataFFI info = loreEvent.GetData<LoreAuthUserInfoEventDataFFI>();
                                        result.UserId = info.Id ?? string.Empty;
                                        result.UserName = info.Name ?? string.Empty;
                                        break;
                                    case LoreEventTag.AUTH_USER_TOKEN:
                                        LoreAuthUserTokenEventDataFFI token = loreEvent.GetData<LoreAuthUserTokenEventDataFFI>();
                                        if (string.IsNullOrEmpty(result.UserId))
                                        {
                                            result.UserId = token.Id ?? string.Empty;
                                        }

                                        if (string.IsNullOrEmpty(result.UserName))
                                        {
                                            result.UserName = !string.IsNullOrEmpty(token.PreferredUsername)
                                                ? token.PreferredUsername
                                                : token.Name ?? string.Empty;
                                        }

                                        break;
                                    case LoreEventTag.ERROR:
                                        LoreErrorEventDataFFI err = loreEvent.GetData<LoreErrorEventDataFFI>();
                                        if (!string.IsNullOrEmpty(err.ErrorInner))
                                        {
                                            result.ErrorMessage = err.ErrorInner;
                                        }

                                        break;
                                }
                            }).Wait();
                        }
                        finally
                        {
                            globalArgs.Dispose();
                        }
                    },
                    LoginTimeoutMs).ConfigureAwait(false);

                // The SDK signals failure either by throwing (caught below) or by emitting an ERROR
                // event; absent both, sign-in succeeded and the credential store has been updated.
                result.Success = string.IsNullOrEmpty(result.ErrorMessage);
                result.LoginUrl = string.Empty;
                return result;
            }
            catch (Exception ex)
            {
                result.Success = false;
                if (string.IsNullOrEmpty(result.ErrorMessage))
                {
                    result.ErrorMessage = Unwrap(ex).Message;
                }

                return result;
            }
        }

        /// <inheritdoc/>
        public async Task<LoreStatusEntry[]> GetRepositoryStatusAsync(string repositoryRoot, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return Array.Empty<LoreStatusEntry>();
            }

            var entries = new ConcurrentQueue<LoreStatusEntry>();

            // The native SDK drives its own runtime synchronously; run it on a worker thread under
            // the SDK gate and block there with Wait() so we never start a tokio runtime from within
            // another one. The disposable value-type args are built inside the worker thread so they
            // are not captured (and disposed) across the await boundary.
            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, WorkingDirectory = repositoryRoot, Offline = true };
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
                        entries.Enqueue(new LoreStatusEntry
                        {
                            Path = NormalizePath(repositoryRoot, file.Path),
                            Status = LoreStatusMapper.Map(file),
                            OriginalPath = ResolveOriginalPath(repositoryRoot, file),
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

            var entries = new ConcurrentQueue<LoreStatusEntry>();
            var info = new LoreRepositoryInfo();
            var sawRevision = false;

            // Gather the file list AND the revision summary from a SINGLE status pass. The native SDK
            // emits both REPOSITORY_STATUS_FILE and REPOSITORY_STATUS_REVISION events during one scan,
            // and it does not reliably tolerate two back-to-back RepositoryStatus invocations (a second
            // call can hang). Folding both into the one proven status call avoids that hang entirely.
            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, WorkingDirectory = repositoryRoot, Offline = true };
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
                            string normalized = NormalizePath(repositoryRoot, file.Path);

                            // The scan can surface a directory entry (e.g. the project folder); those
                            // are not changed files and would render as a phantom leaf in the Changes
                            // tree, so skip anything that resolves to an existing directory.
                            if (!Directory.Exists(normalized))
                            {
                                entries.Enqueue(new LoreStatusEntry
                                {
                                    Path = normalized,
                                    Status = LoreStatusMapper.Map(file),
                                    OriginalPath = ResolveOriginalPath(repositoryRoot, file),
                                });
                            }
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
                            info.LocalRevisionHash = HashToHex(rev.RevisionLocal);
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
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, WorkingDirectory = repositoryRoot, Offline = true };
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
                        info.LocalRevisionHash = HashToHex(rev.RevisionLocal);
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

            // A lore:// URL creates the repository on that server and binds the remote in
            // .lore/config.toml; a bare name creates a fully offline repo (git-init style) with no
            // remote. Either way the .lore working tree is laid down in the working directory.
            bool offline = !repositoryUrl.StartsWith("lore://", StringComparison.OrdinalIgnoreCase);
            return ExecuteAsync(identity, offline, workingDirectory, globalArgs =>
            {
                using var args = new LoreRepositoryCreateArgs { RepositoryUrl = repositoryUrl };
                Lore.RepositoryCreate(globalArgs, args).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(repositoryUrl) || string.IsNullOrWhiteSpace(targetDirectory))
            {
                return Task.FromResult(LoreCommandResult.Failed("A repository URL and a target directory are required."));
            }

            // Clone is inherently online: it pulls the working tree and the .lore config (remote +
            // identity) from the server into the target directory. RepositoryPath is the destination.
            return ExecuteAsync(identity, offline: false, targetDirectory, globalArgs =>
            {
                using var args = new LoreRepositoryCloneArgs { RepositoryUrl = repositoryUrl };
                Lore.RepositoryClone(globalArgs, args).Wait();
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
        public Task<LoreCommandResult> RecordMoveAsync(string workingDirectory, string oldPath, string newPath, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(oldPath) || string.IsNullOrWhiteSpace(newPath))
            {
                return Task.FromResult(LoreCommandResult.Failed("Both the original and new paths are required to record a move."));
            }

            return ExecuteAsync(identity: null, offline: true, workingDirectory, globalArgs =>
            {
                using var args = new LoreFileStageMoveArgs
                {
                    FromPath = oldPath,
                    ToPath = newPath,
                };
                Lore.FileStageMove(globalArgs, args).Wait();
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
        public Task<LoreCommandResult> CommitFilesAsync(string workingDirectory, string[] paths, LoreMoveEntry[] moves, string message, string identity, bool amend, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(message))
            {
                return Task.FromResult(LoreCommandResult.Failed("A commit message is required."));
            }

            if (paths == null || paths.Length == 0)
            {
                return Task.FromResult(LoreCommandResult.Failed("Select at least one file to commit."));
            }

            return ExecuteAsync(identity, offline: true, workingDirectory, globalArgs =>
            {
                bool mergeInProgress = false;
                var callbackErrors = new ConcurrentQueue<Exception>();
                using (var statusArgs = new LoreRepositoryStatusArgs { RevisionOnly = true })
                {
                    Lore.RepositoryStatus(globalArgs, statusArgs).Callback((loreEvent, userContext) =>
                    {
                        try
                        {
                            if (loreEvent.Tag == LoreEventTag.REPOSITORY_STATUS_REVISION)
                            {
                                LoreRepositoryStatusRevisionEventDataFFI revision = loreEvent.GetData<LoreRepositoryStatusRevisionEventDataFFI>();
                                mergeInProgress = !LoreRevisionMapper.IsEmptyHash(HashToHex(revision.RevisionMerged));
                            }
                        }
                        catch (Exception ex)
                        {
                            _ = ex.LogAsync();
                            callbackErrors.Enqueue(ex);
                        }
                    }).Wait();
                }

                if (callbackErrors.TryDequeue(out Exception? callbackError))
                {
                    throw new InvalidOperationException("Could not read Lore revision status.", callbackError);
                }

                if (mergeInProgress)
                {
                    throw new InvalidOperationException("Resolve or abort the current Lore merge before making a partial commit.");
                }

                // Unstage retains dirty action flags in newer Lore builds. Reset the staged anchor
                // instead, without changing working files, then reconstruct only the selected work.
                using (var resetArgs = new LoreRepositoryStatusArgs { Reset = true, RevisionOnly = true })
                {
                    Lore.RepositoryStatus(globalArgs, resetArgs).Wait();
                }

                var movedPaths = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
                foreach (LoreMoveEntry move in moves ?? Array.Empty<LoreMoveEntry>())
                {
                    using var moveArgs = new LoreFileStageMoveArgs { FromPath = move.OldPath, ToPath = move.NewPath };
                    Lore.FileStageMove(globalArgs, moveArgs).Wait();
                    movedPaths.Add(NormalizePath(workingDirectory, move.NewPath));
                }

                if (movedPaths.Count > 0)
                {
                    using var moveStageArgs = new LoreFileStageArgs { Paths = movedPaths.ToArray(), Scan = false };
                    Lore.FileStage(globalArgs, moveStageArgs).Wait();
                }

                string[] ordinaryPaths = paths
                    .Where(path => !movedPaths.Contains(NormalizePath(workingDirectory, path)))
                    .ToArray();
                if (ordinaryPaths.Length > 0)
                {
                    using var stageArgs = new LoreFileStageArgs { Paths = ordinaryPaths, Scan = true };
                    Lore.FileStage(globalArgs, stageArgs).Wait();
                }

                if (amend)
                {
                    using var amendArgs = new LoreRevisionAmendArgs { Message = message };
                    Lore.RevisionAmend(globalArgs, amendArgs).Wait();
                }
                else
                {
                    using var commitArgs = new LoreRevisionCommitArgs { Message = message };
                    Lore.RevisionCommit(globalArgs, commitArgs).Wait();
                }
            });
        }

        /// <inheritdoc/>
        public async Task<LoreBranchEntry[]> ListBranchesAsync(string repositoryRoot, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(repositoryRoot))
            {
                return Array.Empty<LoreBranchEntry>();
            }

            var entries = new List<LoreBranchEntry>();

            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, WorkingDirectory = repositoryRoot, Offline = true };
                using var args = new LoreBranchListArgs { Archived = false };

                Lore.BranchList(globalArgs, args).Callback((loreEvent, userContext) =>
                {
                    // Never let an exception escape into the native callback: a throw across the
                    // managed/native boundary can leave the SDK's event loop waiting forever.
                    try
                    {
                        if (loreEvent.Tag != LoreEventTag.BRANCH_LIST_ENTRY)
                        {
                            return;
                        }

                        LoreBranchListEntryEventDataFFI entry = loreEvent.GetData<LoreBranchListEntryEventDataFFI>();
                        entries.Add(new LoreBranchEntry
                        {
                            Name = entry.Name ?? string.Empty,
                            Category = entry.Category ?? string.Empty,
                            IsCurrent = entry.IsCurrent,
                            IsRemote = entry.Location == LoreBranchLocation.REMOTE,
                            Archived = entry.Archived,
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[LoreVS.Worker] branch list callback failed: " + ex);
                    }
                }).Wait();
            }).ConfigureAwait(false);

            return entries.ToArray();
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> CreateBranchAsync(string workingDirectory, string branchName, string identity, bool checkout, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return Task.FromResult(LoreCommandResult.Failed("A branch name is required."));
            }

            return ExecuteAsync(identity, offline: true, workingDirectory, globalArgs =>
            {
                // Lore's branch-create checks out the new branch as a side effect. When the caller
                // opts out of checkout, capture the branch that is current beforehand so we can
                // switch back to it once the new branch has been created.
                string? previousBranch = null;
                if (!checkout)
                {
                    using var listArgs = new LoreBranchListArgs { Archived = false };
                    Lore.BranchList(globalArgs, listArgs).Callback((loreEvent, userContext) =>
                    {
                        try
                        {
                            if (loreEvent.Tag != LoreEventTag.BRANCH_LIST_ENTRY)
                            {
                                return;
                            }

                            LoreBranchListEntryEventDataFFI entry = loreEvent.GetData<LoreBranchListEntryEventDataFFI>();
                            if (entry.IsCurrent && entry.Location != LoreBranchLocation.REMOTE)
                            {
                                previousBranch = entry.Name;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("[LoreVS.Worker] create-branch current lookup failed: " + ex);
                        }
                    }).Wait();
                }

                // Create the branch at the current revision; the SDK leaves the working tree on the
                // new branch. Lore reports a refused create (for example, uncommitted changes in the
                // working tree block the implicit checkout, or the name already exists) through an
                // ERROR event rather than a thrown exception, so capture it and surface it as a real
                // failure instead of a silent no-op.
                string? createError = null;
                using (var createArgs = new LoreBranchCreateArgs { Branch = branchName })
                {
                    Lore.BranchCreate(globalArgs, createArgs).Callback((loreEvent, userContext) =>
                    {
                        try
                        {
                            if (loreEvent.Tag == LoreEventTag.ERROR)
                            {
                                LoreErrorEventDataFFI err = loreEvent.GetData<LoreErrorEventDataFFI>();
                                createError = string.IsNullOrEmpty(err.ErrorInner) ? "Creating the branch failed." : err.ErrorInner;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("[LoreVS.Worker] branch create callback failed: " + ex);
                        }
                    }).Wait();
                }

                if (createError != null)
                {
                    throw new InvalidOperationException(DescribeBranchError(createError));
                }

                // Honor "do not checkout" by switching back to the previously current branch.
                if (!checkout && !string.IsNullOrEmpty(previousBranch))
                {
                    string? switchBackError = null;
                    using var switchArgs = new LoreBranchSwitchArgs { Branch = previousBranch };
                    Lore.BranchSwitch(globalArgs, switchArgs).Callback((loreEvent, userContext) =>
                    {
                        try
                        {
                            if (loreEvent.Tag == LoreEventTag.ERROR)
                            {
                                LoreErrorEventDataFFI err = loreEvent.GetData<LoreErrorEventDataFFI>();
                                switchBackError = string.IsNullOrEmpty(err.ErrorInner) ? "Switching back to the previous branch failed." : err.ErrorInner;
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("[LoreVS.Worker] create-branch switch-back callback failed: " + ex);
                        }
                    }).Wait();

                    if (switchBackError != null)
                    {
                        throw new InvalidOperationException(DescribeBranchError(switchBackError));
                    }
                }
            });
        }

        /// <inheritdoc/>
        public async Task<LoreCommandResult> SwitchBranchAsync(string workingDirectory, string branchName, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(branchName))
            {
                return LoreCommandResult.Failed("A branch name is required.");
            }

            // Lore's local store is a fragment cache (see Lore system-design 12.6): switching to a
            // branch whose content is still cached needs no network, but the LRU cache can evict the
            // target's fragments, in which case the switch must re-fetch them from the remote. Try
            // offline first for the fast/disconnected path; only reach for the network when the local
            // content is genuinely missing.
            LoreCommandResult offline = await TrySwitchBranchAsync(workingDirectory, branchName, offline: true).ConfigureAwait(false);
            if (offline.Success || !RequiresRemoteContent(offline.Error))
            {
                return offline;
            }

            WorkerLog.Write("SwitchBranchAsync: target content not cached locally; retrying online");
            LoreCommandResult online = await TrySwitchBranchAsync(workingDirectory, branchName, offline: false).ConfigureAwait(false);
            if (online.Success)
            {
                return online;
            }

            // The online retry is the only way to materialize evicted content, so a connection failure
            // here means the user simply has no server to fetch from. Replace the raw transport error
            // with an actionable message.
            return IsRemoteUnreachable(online.Error)
                ? LoreCommandResult.Failed(
                    "Switching to '" + branchName + "' needs file content that isn't cached locally, and the " +
                    "Lore server could not be reached to fetch it.\r\n\r\nStart (or reconnect to) your Lore " +
                    "server and try again.\r\n\r\nDetails: " + online.Error)
                : online;
        }

        /// <summary>
        /// Runs a single branch-switch attempt with the given connectivity mode, translating Lore's
        /// ERROR events (which the SDK reports instead of throwing) into a failed result.
        /// </summary>
        private static async Task<LoreCommandResult> TrySwitchBranchAsync(string workingDirectory, string branchName, bool offline)
        {
            string? switchError = null;
            try
            {
                await RunExclusiveAsync(
                    () =>
                    {
                        var globalArgs = new LoreGlobalArgs { RepositoryPath = workingDirectory, WorkingDirectory = workingDirectory, Offline = offline };
                        try
                        {
                            using var args = new LoreBranchSwitchArgs { Branch = branchName };
                            Lore.BranchSwitch(globalArgs, args).Callback((loreEvent, userContext) =>
                            {
                                // Never let an exception escape into the native callback: a throw across
                                // the managed/native boundary can leave the SDK's event loop waiting forever.
                                try
                                {
                                    if (loreEvent.Tag == LoreEventTag.ERROR)
                                    {
                                        LoreErrorEventDataFFI err = loreEvent.GetData<LoreErrorEventDataFFI>();
                                        switchError = string.IsNullOrEmpty(err.ErrorInner) ? "The branch switch failed." : err.ErrorInner;
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine("[LoreVS.Worker] branch switch callback failed: " + ex);
                                }
                            }).Wait();
                        }
                        finally
                        {
                            globalArgs.Dispose();
                        }
                    },
                    operation: offline ? "SwitchBranchAsync(offline)" : "SwitchBranchAsync(online)").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return LoreCommandResult.Failed(Unwrap(ex).Message);
            }

            // Lore reports a refused switch (e.g. uncommitted changes, missing content) through an
            // ERROR event rather than a thrown exception; surface it as a failure instead of a false
            // success so the caller does not silently believe the branch changed.
            return switchError != null
                ? LoreCommandResult.Failed(switchError)
                : new LoreCommandResult(true, 0, string.Empty, string.Empty);
        }

        /// <summary>
        /// True when a Lore error indicates the operation needs content that is not materialized in the
        /// local fragment cache and must be fetched from the remote (so an online retry can help).
        /// </summary>
        private static bool RequiresRemoteContent(string error)
        {
            return !string.IsNullOrEmpty(error) &&
                   (error.IndexOf("Address not found", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    error.IndexOf("synchronize state", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>True when a Lore error indicates the remote server could not be reached.</summary>
        private static bool IsRemoteUnreachable(string error)
        {
            return !string.IsNullOrEmpty(error) &&
                   (error.IndexOf("Not connected to remote", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    error.IndexOf("transport error", StringComparison.OrdinalIgnoreCase) >= 0 ||
                    error.IndexOf("gRPC connection", StringComparison.OrdinalIgnoreCase) >= 0);
        }

        /// <summary>
        /// Adds an actionable hint to Lore branch errors caused by a dirty working tree, which blocks
        /// the implicit checkout that branch-create performs. Other errors are returned unchanged.
        /// </summary>
        private static string DescribeBranchError(string error)
        {
            if (!string.IsNullOrEmpty(error) &&
                (error.IndexOf("uncommitted", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 error.IndexOf("working tree", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 error.IndexOf("working copy", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 error.IndexOf("would be overwritten", StringComparison.OrdinalIgnoreCase) >= 0 ||
                 error.IndexOf("pending change", StringComparison.OrdinalIgnoreCase) >= 0))
            {
                return "Commit your pending changes first, then create the branch.\r\n\r\nDetails: " + error;
            }

            return error;
        }

        /// <inheritdoc/>
        public async Task<LoreMergeResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string identity, CancellationToken cancellationToken)
        {
            if (string.IsNullOrWhiteSpace(sourceBranch))
            {
                return new LoreMergeResult { Success = false, ErrorMessage = "A branch name is required." };
            }

            // Like switching, a merge must materialize the source branch's content. Try offline first
            // (no network when the content is cached); only retry online when Lore reports the content
            // is missing from the local cache.
            LoreMergeResult offline = await TryMergeBranchAsync(workingDirectory, sourceBranch, identity, offline: true).ConfigureAwait(false);
            if (offline.Success || offline.HasConflicts || !RequiresRemoteContent(offline.ErrorMessage))
            {
                return offline;
            }

            WorkerLog.Write("MergeBranchAsync: source content not cached locally; retrying online");
            LoreMergeResult online = await TryMergeBranchAsync(workingDirectory, sourceBranch, identity, offline: false).ConfigureAwait(false);
            if (online.Success || online.HasConflicts)
            {
                return online;
            }

            if (IsRemoteUnreachable(online.ErrorMessage))
            {
                online.ErrorMessage =
                    "Merging '" + sourceBranch + "' needs file content that isn't cached locally, and the " +
                    "Lore server could not be reached to fetch it.\r\n\r\nStart (or reconnect to) your Lore " +
                    "server and try again.\r\n\r\nDetails: " + online.ErrorMessage;
            }

            return online;
        }

        /// <summary>
        /// Runs a single merge attempt with the given connectivity mode. Conflicts and ERROR events are
        /// translated into the appropriate <see cref="LoreMergeResult"/> outcome.
        /// </summary>
        private static async Task<LoreMergeResult> TryMergeBranchAsync(string workingDirectory, string sourceBranch, string identity, bool offline)
        {
            string? mergeError = null;
            var conflicts = new List<string>();

            try
            {
                await RunExclusiveAsync(
                    () =>
                    {
                        var globalArgs = new LoreGlobalArgs { RepositoryPath = workingDirectory, WorkingDirectory = workingDirectory, Offline = offline };
                        if (!string.IsNullOrWhiteSpace(identity))
                        {
                            globalArgs.Identity = identity;
                        }

                        // BranchMergeStart folds the named branch into the CURRENT branch: it syncs the
                        // working tree and auto-commits the merge revision when there are no conflicts. On
                        // conflicts it leaves the merge in progress and writes diff3 markers into the
                        // conflicted working files. The native SDK surfaces failures as ERROR events here
                        // (it does not always throw), so the events are inspected rather than relying on a throw.
                        try
                        {
                            using var args = new LoreBranchMergeStartArgs
                            {
                                Branch = sourceBranch,
                                Message = $"Merge branch '{sourceBranch}'",
                            };

                            Lore.BranchMergeStart(globalArgs, args).Callback((loreEvent, userContext) =>
                            {
                                // Never let an exception escape into the native callback: a throw across the
                                // managed/native boundary can leave the SDK's event loop waiting forever.
                                try
                                {
                                    if (loreEvent.Tag == LoreEventTag.ERROR)
                                    {
                                        LoreErrorEventDataFFI err = loreEvent.GetData<LoreErrorEventDataFFI>();
                                        mergeError = string.IsNullOrEmpty(err.ErrorInner) ? "The merge failed." : err.ErrorInner;
                                    }
                                    else if (loreEvent.Tag == LoreEventTag.BRANCH_MERGE_CONFLICT_FILE)
                                    {
                                        LoreBranchMergeConflictFileEventDataFFI conflict = loreEvent.GetData<LoreBranchMergeConflictFileEventDataFFI>();
                                        if (!string.IsNullOrEmpty(conflict.Path))
                                        {
                                            conflicts.Add(NormalizePath(workingDirectory, conflict.Path));
                                        }
                                    }
                                }
                                catch (Exception ex)
                                {
                                    Console.Error.WriteLine("[LoreVS.Worker] merge callback failed: " + ex);
                                }
                            }).Wait();
                        }
                        finally
                        {
                            globalArgs.Dispose();
                        }
                    },
                    operation: offline ? "MergeBranchAsync(offline)" : "MergeBranchAsync(online)").ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                return new LoreMergeResult { Success = false, ErrorMessage = Unwrap(ex).Message };
            }

            if (conflicts.Count > 0)
            {
                return new LoreMergeResult { Success = false, HasConflicts = true, ConflictPaths = conflicts.ToArray() };
            }

            if (mergeError != null)
            {
                return new LoreMergeResult { Success = false, ErrorMessage = mergeError };
            }

            return new LoreMergeResult { Success = true };
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> ResolveMergeAsync(string workingDirectory, string[] paths, string message, string identity, CancellationToken cancellationToken)
        {
            if (paths == null || paths.Length == 0)
            {
                return Task.FromResult(LoreCommandResult.Failed("At least one resolved path is required."));
            }

            if (string.IsNullOrWhiteSpace(message))
            {
                return Task.FromResult(LoreCommandResult.Failed("A commit message is required."));
            }

            return ExecuteAsync(identity, offline: true, workingDirectory, globalArgs =>
            {
                // Stage the resolved content, mark the files resolved, then commit the merge revision.
                using (var stageArgs = new LoreFileStageMergeArgs { Paths = paths })
                {
                    Lore.FileStageMerge(globalArgs, stageArgs).Wait();
                }

                using (var resolveArgs = new LoreBranchMergeResolveArgs { Paths = paths })
                {
                    Lore.BranchMergeResolve(globalArgs, resolveArgs).Wait();
                }

                using var commitArgs = new LoreRevisionCommitArgs { Message = message };
                Lore.RevisionCommit(globalArgs, commitArgs).Wait();
            });
        }

        /// <inheritdoc/>
        public Task<LoreCommandResult> AbortMergeAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            return ExecuteAsync(identity: null, offline: true, workingDirectory, globalArgs =>
            {
                using var args = new LoreBranchMergeAbortArgs();
                Lore.BranchMergeAbort(globalArgs, args).Wait();
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
        public async Task<LoreCommandResult> SyncAsync(string workingDirectory, CancellationToken cancellationToken)
        {
            var warnings = new ConcurrentQueue<string>();
            var callbackErrors = new ConcurrentQueue<Exception>();
            LoreCommandResult result = await ExecuteAsync(identity: null, offline: false, workingDirectory, globalArgs =>
            {
                using var args = new LoreRevisionSyncArgs();
                Lore.RevisionSync(globalArgs, args).Callback((loreEvent, userContext) =>
                {
                    try
                    {
                        if (loreEvent.Tag == LoreEventTag.REVISION_SYNC_TARGET)
                        {
                            LoreRevisionSyncTargetEventDataFFI target = loreEvent.GetData<LoreRevisionSyncTargetEventDataFFI>();
                            string warning = GetSyncWarning(target.Local, target.RemoteAvailable, target.RemoteAuthorized);
                            if (!string.IsNullOrEmpty(warning))
                            {
                                warnings.Enqueue(warning);
                            }
                        }
                    }
                    catch (Exception ex)
                    {
                        _ = ex.LogAsync();
                        callbackErrors.Enqueue(ex);
                    }
                }).Wait();
            }).ConfigureAwait(false);

            if (result.Success && callbackErrors.TryDequeue(out Exception? callbackError))
            {
                result = MapFailure(callbackError);
            }

            result.Warning = string.Join(Environment.NewLine, warnings.Distinct());
            return result;
        }

        internal static string GetSyncWarning(bool local, bool remoteAvailable, bool remoteAuthorized)
        {
            if (!local)
            {
                return string.Empty;
            }

            if (!remoteAvailable)
            {
                return "No Lore remote is configured. Only local revisions were synchronized; no remote changes were fetched.";
            }

            return remoteAuthorized
                ? string.Empty
                : "Lore could not query the remote branch with the current connection or permissions. " +
                    "Only local revisions were synchronized; remote changes may be missing.";
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
                // A moved destination is absent from the committed tree. Clear its staged flags
                // before reset so Lore can discard the dirty move without rejecting a staged node.
                using (var unstageArgs = new LoreFileUnstageArgs { Paths = paths })
                {
                    Lore.FileUnstage(globalArgs, unstageArgs).Wait();
                }

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

        /// <inheritdoc/>
        public async Task<LoreRevisionPage> GetRevisionHistoryAsync(string repositoryRoot, string startAfterRevision, int pageSize, CancellationToken cancellationToken)
        {
            var page = new LoreRevisionPage();
            if (string.IsNullOrEmpty(repositoryRoot) || pageSize <= 0)
            {
                return page;
            }

            startAfterRevision ??= string.Empty;
            bool continuation = !string.IsNullOrEmpty(startAfterRevision);

            // Over-fetch by one to detect whether older revisions remain. When paging from a given
            // hash the native API repeats that (inclusive) start revision, so request one more to
            // absorb the duplicate that is dropped below.
            int fetch = pageSize + 1 + (continuation ? 1 : 0);

            // Branch is opaque (a CONTEXT metadata value) in the history stream, so stamp each entry
            // with the current branch name looked up separately. Best-effort - a failure here must
            // not fail the whole history request.
            string branchName = await TryGetCurrentBranchNameAsync(repositoryRoot).ConfigureAwait(false);

            var entries = new List<LoreRevisionEntry>();
            LoreRevisionEntry? current = null;

            // The history stream emits one REVISION_HISTORY_ENTRY per revision followed by that
            // revision's METADATA events (message, created-by author, timestamp), so the metadata is
            // attributed to the most recent entry. Gathering both from the single pass avoids a
            // per-revision RevisionInfo round trip.
            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, WorkingDirectory = repositoryRoot, Offline = true };
                using var historyArgs = new LoreRevisionHistoryArgs
                {
                    Revision = startAfterRevision,
                    Length = (uint)fetch,
                };

                Lore.RevisionHistory(globalArgs, historyArgs).Callback((loreEvent, userContext) =>
                {
                    // Never let an exception escape into the native callback: a throw across the
                    // managed/native boundary can leave the SDK's event loop waiting forever.
                    try
                    {
                        if (loreEvent.Tag == LoreEventTag.REVISION_HISTORY_ENTRY)
                        {
                            LoreRevisionHistoryEntryEventDataFFI entry = loreEvent.GetData<LoreRevisionHistoryEntryEventDataFFI>();
                            current = new LoreRevisionEntry
                            {
                                RevisionHash = LoreRevisionMapper.ToHex(entry.Revision.Data),
                                RevisionNumber = (long)entry.RevisionNumber,
                                ParentRevisionHash = LoreRevisionMapper.ParentHash(entry.Parent),
                                BranchName = branchName,
                            };
                            entries.Add(current);
                        }
                        else if (loreEvent.Tag == LoreEventTag.METADATA && current != null)
                        {
                            LoreMetadataEventDataFFI meta = loreEvent.GetData<LoreMetadataEventDataFFI>();
                            ApplyRevisionMetadata(current, meta);
                        }
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[LoreVS.Worker] revision history callback failed: " + ex);
                    }
                }).Wait();
            }).ConfigureAwait(false);

            // Drop the inclusive start revision the native API repeats when paging from a hash.
            if (continuation && entries.Count > 0 &&
                string.Equals(entries[0].RevisionHash, startAfterRevision, StringComparison.OrdinalIgnoreCase))
            {
                entries.RemoveAt(0);
            }

            page.HasMore = entries.Count > pageSize;
            if (entries.Count > pageSize)
            {
                entries.RemoveRange(pageSize, entries.Count - pageSize);
            }

            page.Entries = entries.ToArray();
            return page;
        }

        /// <inheritdoc/>
        public async Task<LoreRevisionChange[]> GetRevisionChangesAsync(string repositoryRoot, string revision, CancellationToken cancellationToken)
        {
            if (string.IsNullOrEmpty(repositoryRoot) || string.IsNullOrEmpty(revision))
            {
                return Array.Empty<LoreRevisionChange>();
            }

            var changes = new List<LoreRevisionChange>();

            // RevisionInfo with Delta emits one REVISION_INFO_DELTA per file the revision touched,
            // relative to its direct parent. This is read-only and never mutates the working tree.
            await RunExclusiveAsync(() =>
            {
                using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, WorkingDirectory = repositoryRoot, Offline = true };
                using var infoArgs = new LoreRevisionInfoArgs { Revision = revision, Delta = true };

                Lore.RevisionInfo(globalArgs, infoArgs).Callback((loreEvent, userContext) =>
                {
                    try
                    {
                        if (loreEvent.Tag != LoreEventTag.REVISION_INFO_DELTA)
                        {
                            return;
                        }

                        LoreRevisionInfoDeltaEventDataFFI delta = loreEvent.GetData<LoreRevisionInfoDeltaEventDataFFI>();
                        LoreFileStatus status = LoreRevisionMapper.MapDelta(delta.Action, delta.FlagModify);

                        // A clean KEEP is a file carried unchanged into the revision; only surface the
                        // files the revision actually changed.
                        if (status == LoreFileStatus.Unchanged)
                        {
                            return;
                        }

                        changes.Add(new LoreRevisionChange
                        {
                            Path = NormalizePath(repositoryRoot, delta.Path),
                            Status = status,
                        });
                    }
                    catch (Exception ex)
                    {
                        Console.Error.WriteLine("[LoreVS.Worker] revision changes callback failed: " + ex);
                    }
                }).Wait();
            }).ConfigureAwait(false);

            return changes.ToArray();
        }

        /// <summary>
        /// Copies a revision metadata event into the corresponding <see cref="LoreRevisionEntry"/>
        /// member. Only the well-known keys the history window needs are read; unknown keys and
        /// non-matching value tags are ignored so an SDK addition can never throw here.
        /// </summary>
        private static void ApplyRevisionMetadata(LoreRevisionEntry entry, LoreMetadataEventDataFFI meta)
        {
            switch (meta.Key)
            {
                case "message":
                    if (meta.Value.Tag == LoreMetadataType.STRING)
                    {
                        entry.Message = meta.Value.String ?? string.Empty;
                    }

                    break;
                case "created-by":
                    if (meta.Value.Tag == LoreMetadataType.STRING)
                    {
                        entry.Author = meta.Value.String ?? string.Empty;
                    }

                    break;
                case "timestamp":
                    if (meta.Value.Tag == LoreMetadataType.NUMERIC)
                    {
                        entry.TimestampUnixMs = (long)meta.Value.Numeric;
                    }

                    break;
            }
        }

        /// <summary>
        /// Returns the name of the current branch, or an empty string when it cannot be determined.
        /// Used to stamp history entries with a branch name (the history stream only carries the
        /// branch as an opaque context value). Best-effort and offline.
        /// </summary>
        private static async Task<string> TryGetCurrentBranchNameAsync(string repositoryRoot)
        {
            string branch = string.Empty;
            try
            {
                await RunExclusiveAsync(() =>
                {
                    using var globalArgs = new LoreGlobalArgs { RepositoryPath = repositoryRoot, WorkingDirectory = repositoryRoot, Offline = true };
                    using var args = new LoreBranchListArgs { Archived = false };

                    Lore.BranchList(globalArgs, args).Callback((loreEvent, userContext) =>
                    {
                        try
                        {
                            if (loreEvent.Tag == LoreEventTag.BRANCH_LIST_ENTRY)
                            {
                                LoreBranchListEntryEventDataFFI entry = loreEvent.GetData<LoreBranchListEntryEventDataFFI>();
                                if (entry.IsCurrent)
                                {
                                    branch = entry.Name ?? string.Empty;
                                }
                            }
                        }
                        catch (Exception ex)
                        {
                            Console.Error.WriteLine("[LoreVS.Worker] current branch callback failed: " + ex);
                        }
                    }).Wait();
                }).ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine("[LoreVS.Worker] current branch lookup failed: " + ex);
            }

            return branch;
        }

        /// <summary>
        /// Runs a write operation against a configured <see cref="LoreGlobalArgs"/> and maps the
        /// outcome to a <see cref="LoreCommandResult"/>. The SDK throws on a non-zero result; any
        /// exception becomes a failed result carrying the error detail.
        /// </summary>
        private static async Task<LoreCommandResult> ExecuteAsync(string? identity, bool offline, string workingDirectory, Action<LoreGlobalArgs> operation)
        {
            try
            {
                // Build, use, and dispose the disposable value-type args entirely inside the worker
                // thread (the same pattern the status methods use). On a timeout, RunExclusiveAsync
                // abandons this action while it is still running the native call, so the args lifetime
                // MUST be owned by that thread - disposing them from an outer 'finally' would free the
                // native handle while the abandoned native operation is still using it (a use-after-free
                // across the managed/native boundary that crashes the whole worker process).
                await RunExclusiveAsync(() =>
                {
                    var globalArgs = new LoreGlobalArgs { RepositoryPath = workingDirectory, WorkingDirectory = workingDirectory, Offline = offline };
                    if (!string.IsNullOrWhiteSpace(identity))
                    {
                        globalArgs.Identity = identity;
                    }

                    try
                    {
                        operation(globalArgs);
                    }
                    finally
                    {
                        globalArgs.Dispose();
                    }
                }).ConfigureAwait(false);

                return new LoreCommandResult(true, 0, string.Empty, string.Empty);
            }
            catch (Exception ex)
            {
                await ex.LogAsync().ConfigureAwait(false);
                return MapFailure(ex);
            }
        }

        /// <summary>
        /// Opens <paramref name="url"/> in the user's default browser for the interactive sign-in
        /// flow. Best effort: a failure to launch is logged and the URL is still returned to the
        /// package (in <see cref="LoreAuthResult.LoginUrl"/>) so it can be surfaced as a fallback.
        /// </summary>
        private static void OpenBrowser(string url)
        {
            if (string.IsNullOrWhiteSpace(url))
            {
                return;
            }

            try
            {
                using var process = Process.Start(new ProcessStartInfo(url) { UseShellExecute = true });
                WorkerLog.Write("login: opened browser for sign-in");
            }
            catch (Exception ex)
            {
                WorkerLog.Write($"login: failed to open browser ({ex.GetType().Name}: {ex.Message})");
            }
        }

        /// <summary>
        /// Preserves the SDK status and only offers sign-in for NotAuthenticated (12), not permission
        /// denial or transport failures. Non-SDK exceptions do not carry an authentication status.
        /// </summary>
        internal static LoreCommandResult MapFailure(Exception exception)
        {
            Exception error = Unwrap(exception);
            if (error is LoreError loreError)
            {
                const int notAuthenticated = 12;
                string? message = loreError.ErrorDetail?.Message;
                return new LoreCommandResult(false, loreError.ReturnCode, string.Empty,
                    string.IsNullOrWhiteSpace(message) ? $"Lore operation failed with code {loreError.ReturnCode}" : message)
                {
                    RequiresAuthentication = loreError.ReturnCode == notAuthenticated,
                };
            }

            return LoreCommandResult.Failed(error.Message);
        }

        /// <summary>Converts a Lore revision hash to the lowercase hex string the SDK expects.</summary>
        private static string HashToHex(LoreHash hash)
        {
            byte[] data = hash.Data;
            return data == null || data.Length == 0 ? string.Empty : BitConverter.ToString(data).Replace("-", "").ToLowerInvariant();
        }

        /// <summary>Unwraps the inner exception from the SDK's blocking call, if present.</summary>
        private static Exception Unwrap(Exception ex)
        {
            while (ex is AggregateException aggregate && aggregate.InnerExceptions.Count == 1)
            {
                ex = aggregate.InnerExceptions[0];
            }

            return ex;
        }

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

        /// <summary>
        /// Returns the absolute, normalized path a moved/renamed file came from, or an empty string
        /// when the event is not a move (or the SDK did not report a source path). Only
        /// <see cref="LoreFileAction.MOVE"/> events carry a meaningful <c>FromPath</c>.
        /// </summary>
        private static string ResolveOriginalPath(string repositoryRoot, LoreRepositoryStatusFileEventDataFFI file)
        {
            if (file.Action != LoreFileAction.MOVE || string.IsNullOrEmpty(file.FromPath))
            {
                return string.Empty;
            }

            return NormalizePath(repositoryRoot, file.FromPath);
        }
    }
}
