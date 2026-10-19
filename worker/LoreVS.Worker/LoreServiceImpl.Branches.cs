using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
using LoreVcs;
using LoreVcs.Types.Args;
using LoreVcs.Types.Enums;
using LoreVcs.Types.Events;
using LoreVS.SourceControl;

namespace LoreVS.Worker
{
    internal sealed partial class LoreServiceImpl
    {
        public async Task<LoreBranchInfo> GetBranchInfoAsync(string repositoryRoot, string branch, CancellationToken cancellationToken)
        {
            var info = new LoreBranchInfo();
            LoreCommandResult result = await ExecuteAsync(null, offline: false, repositoryRoot, globals =>
            {
                ValidateBranch(repositoryRoot, branch);
                info = ReadBranchInfo(globals, branch);
            }).ConfigureAwait(false);
            info.Result = result;
            return info;
        }

        public async Task<LoreBranchComparison> CompareBranchesAsync(string repositoryRoot, string sourceBranch,
            string targetBranch, CancellationToken cancellationToken)
        {
            var comparison = new LoreBranchComparison();
            var conflicts = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
            comparison.Result = await ExecuteAsync(null, offline: false, repositoryRoot, globals =>
            {
                ValidateBranch(repositoryRoot, sourceBranch);
                ValidateBranch(repositoryRoot, targetBranch);
                var events = new BranchEvents(LoreEventTag.BRANCH_DIFF_END);
                using var args = new LoreBranchDiffArgs { Source = sourceBranch, Target = targetBranch };
                Lore.BranchDiff(globals, args).Callback((ev, _) => events.Read(ev, () =>
                {
                    if (ev.Tag == LoreEventTag.BRANCH_DIFF_BEGIN)
                    {
                        var data = ev.GetData<LoreBranchDiffBeginEventDataFFI>();
                        comparison.SourceRevision = LoreRevisionMapper.ToHex(data.SourceRevision.Data);
                        comparison.TargetRevision = LoreRevisionMapper.ToHex(data.TargetRevision.Data);
                        comparison.BaseRevision = LoreRevisionMapper.ToHex(data.BaseRevision.Data);
                    }
                    else if (ev.Tag == LoreEventTag.BRANCH_DIFF_CONFLICT)
                    {
                        var data = ev.GetData<LoreBranchDiffConflictEventDataFFI>();
                        foreach (string path in new[] { data.SourceChange.Path, data.TargetChange.Path })
                        {
                            if (!string.IsNullOrEmpty(path))
                            {
                                conflicts.Add(NormalizePath(repositoryRoot, path));
                            }
                        }
                    }
                })).Wait();
                events.Check();
                if (string.IsNullOrEmpty(comparison.SourceRevision) || string.IsNullOrEmpty(comparison.TargetRevision))
                {
                    throw new InvalidOperationException("Lore did not return the branch revisions being compared.");
                }
            }).ConfigureAwait(false);

            if (comparison.Result.Success)
            {
                // BranchDiff is a three-way merge preview. Use its immutable endpoints for the
                // two-way file viewer rather than misrepresenting its merge changes as a snapshot diff.
                LoreRevisionComparison snapshots = await CompareRevisionsAsync(repositoryRoot,
                    comparison.SourceRevision, comparison.TargetRevision, cancellationToken).ConfigureAwait(false);
                comparison.Result = snapshots.Result;
                if (snapshots.Result.Success)
                {
                    comparison.Changes = snapshots.Changes;
                    comparison.ConflictPaths = conflicts.OrderBy(path => path, StringComparer.OrdinalIgnoreCase).ToArray();
                }
            }

            return comparison;
        }

        public Task<LoreCommandResult> ArchiveBranchAsync(string repositoryRoot, string branch, CancellationToken cancellationToken)
        {
            return ExecuteAsync(null, offline: false, repositoryRoot, globals =>
            {
                ValidateBranch(repositoryRoot, branch);
                var events = new BranchEvents(LoreEventTag.BRANCH_ARCHIVE);
                using var args = new LoreBranchArchiveArgs { Branch = branch };
                Lore.BranchArchive(globals, args).Callback((ev, _) => events.Read(ev)).Wait();
                events.Check();
            });
        }

        public Task<LoreCommandResult> SetBranchProtectionAsync(string repositoryRoot, string branch, bool protect,
            CancellationToken cancellationToken)
        {
            return ExecuteAsync(null, offline: false, repositoryRoot, globals =>
            {
                ValidateBranch(repositoryRoot, branch);
                var events = new BranchEvents(protect ? LoreEventTag.BRANCH_PROTECT : LoreEventTag.BRANCH_UNPROTECT);
                if (protect)
                {
                    using var args = new LoreBranchProtectArgs { Branch = branch };
                    Lore.BranchProtect(globals, args).Callback((ev, _) => events.Read(ev)).Wait();
                }
                else
                {
                    using var args = new LoreBranchUnprotectArgs { Branch = branch };
                    Lore.BranchUnprotect(globals, args).Callback((ev, _) => events.Read(ev)).Wait();
                }

                events.Check();
            });
        }

        public async Task<LoreCommandResult> ResetBranchAsync(string repositoryRoot, string branch, string revision,
            string expectedTip, CancellationToken cancellationToken)
        {
            LoreCommandResult result = await ExecuteAsync(null, offline: true, repositoryRoot, globals =>
            {
                ValidateBranch(repositoryRoot, branch);
                if (string.IsNullOrWhiteSpace(revision) || string.IsNullOrWhiteSpace(expectedTip))
                {
                    throw new ArgumentException("A target revision and the confirmed current branch tip are required.");
                }

                globals.Local = true;
                LoreBranchInfo info = ReadBranchInfo(globals, branch);
                if (!string.Equals(info.LatestRevision, expectedTip, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("The branch tip changed. Refresh and confirm the reset again.");
                }

                EnsureCleanForBranchReset(globals, repositoryRoot);
                var events = new BranchEvents(LoreEventTag.BRANCH_RESET);
                using var args = new LoreBranchResetArgs { Branch = branch, Revision = revision };
                Lore.BranchReset(globals, args).Callback((ev, _) => events.Read(ev)).Wait();
                events.Check();
            }).ConfigureAwait(false);

            if (result.Success)
            {
                result.Warning = "Reset is local only; no push was performed. Previous branch tip: " + expectedTip;
            }

            return result;
        }

        private static LoreBranchInfo ReadBranchInfo(LoreGlobalArgs globals, string branch)
        {
            var info = new LoreBranchInfo();
            var events = new BranchEvents(LoreEventTag.BRANCH_INFO);
            using var args = new LoreBranchInfoArgs { Branch = branch };
            Lore.BranchInfo(globals, args).Callback((ev, _) => events.Read(ev, () =>
            {
                if (ev.Tag != LoreEventTag.BRANCH_INFO)
                {
                    return;
                }

                var data = ev.GetData<LoreBranchInfoEventDataFFI>();
                info.Id = LoreRevisionMapper.ToHex(data.Id.Data);
                info.Name = data.Name ?? string.Empty;
                info.Category = data.Category ?? string.Empty;
                info.LatestRevision = LoreRevisionMapper.ToHex(data.Latest.Data);
                info.RemoteRevision = LoreRevisionMapper.ToHex(data.LatestRemote.Data);
                info.ParentBranchId = LoreRevisionMapper.ToHex(data.Parent.Data);
                info.BranchPoint = LoreRevisionMapper.ToHex(data.BranchPoint.Data);
                info.Stack = data.Stack?.Select(point => new LoreBranchAncestor
                {
                    BranchId = LoreRevisionMapper.ToHex(point.Branch.Data),
                    Revision = LoreRevisionMapper.ToHex(point.Revision.Data),
                }).ToArray() ?? Array.Empty<LoreBranchAncestor>();
                info.Creator = data.Creator ?? string.Empty;
                info.Created = data.Created;
                info.Archived = data.Archived;
            })).Wait();
            events.Check();
            return info;
        }

        private static void EnsureCleanForBranchReset(LoreGlobalArgs globals, string root)
        {
            bool dirty = false;
            bool merge = false;
            var events = new BranchEvents(LoreEventTag.REPOSITORY_STATUS_REVISION);
            using var args = new LoreRepositoryStatusArgs { Scan = true, CheckDirty = true, Paths = new[] { root } };
            Lore.RepositoryStatus(globals, args).Callback((ev, _) => events.Read(ev, () =>
            {
                if (ev.Tag == LoreEventTag.REPOSITORY_STATUS_FILE)
                {
                    var file = ev.GetData<LoreRepositoryStatusFileEventDataFFI>();
                    dirty |= file.FlagDirty || file.FlagStaged || file.FlagMerged || file.FlagConflict ||
                        LoreStatusMapper.Map(file) != LoreFileStatus.Unchanged;
                }
                else if (ev.Tag == LoreEventTag.REPOSITORY_STATUS_REVISION)
                {
                    var data = ev.GetData<LoreRepositoryStatusRevisionEventDataFFI>();
                    merge |= data.RevisionMerged.Data?.Any(value => value != 0) == true;
                }
            })).Wait();
            events.Check();
            if (dirty || merge)
            {
                throw new InvalidOperationException("Commit or discard all changes and finish or abort any merge before resetting a branch.");
            }
        }

        private static void ValidateBranch(string root, string branch)
        {
            if (string.IsNullOrWhiteSpace(root) || string.IsNullOrWhiteSpace(branch))
            {
                throw new ArgumentException("A repository and an explicit branch are required.");
            }
        }

        private sealed class BranchEvents
        {
            private readonly LoreEventTag _expected;
            private readonly ConcurrentQueue<Exception> _errors = new();
            private bool _confirmed;

            public BranchEvents(LoreEventTag expected) => _expected = expected;

            public void Read(LoreEventFFI ev, Action? read = null)
            {
                try
                {
                    if (ev.Tag == LoreEventTag.ERROR)
                    {
                        var error = ev.GetData<LoreErrorEventDataFFI>();
                        throw new BranchOperationException(new LoreCommandResult(false, (int)error.ErrorType, string.Empty,
                            DescribeBranchError(string.IsNullOrWhiteSpace(error.ErrorInner) ? "The branch operation failed." : error.ErrorInner))
                        {
                            RequiresAuthentication = error.ErrorType == 12,
                        });
                    }

                    read?.Invoke();
                    _confirmed |= ev.Tag == _expected;
                }
                catch (Exception ex)
                {
                    _ = ex.LogAsync();
                    _errors.Enqueue(ex);
                }
            }

            public void Check()
            {
                if (_errors.TryDequeue(out Exception? error))
                {
                    throw error;
                }

                if (!_confirmed)
                {
                    throw new InvalidOperationException("Lore did not confirm the branch operation. Refresh branch information before retrying.");
                }
            }
        }

        private sealed class BranchOperationException : Exception
        {
            public BranchOperationException(LoreCommandResult result) : base(result.Error) => Result = result;
            public LoreCommandResult Result { get; }
        }
    }
}