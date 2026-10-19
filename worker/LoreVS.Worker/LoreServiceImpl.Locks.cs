using System;
using System.Collections.Concurrent;
using System.Collections.Generic;
using System.IO;
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
        public Task<LoreCommandResult> AcquireLockAsync(string repositoryRoot, string[] paths, CancellationToken cancellationToken)
        {
            return ExecuteAsync(identity: null, offline: false, repositoryRoot, globals =>
            {
                using var args = new LoreLockFileAcquireArgs { Paths = ValidateLockPaths(repositoryRoot, paths) };
                Lore.LockFileAcquire(globals, args).Wait();
            });
        }

        public Task<LoreCommandResult> ReleaseLockAsync(string repositoryRoot, string[] paths, CancellationToken cancellationToken)
        {
            return ExecuteAsync(identity: null, offline: false, repositoryRoot, globals =>
            {
                using var args = new LoreLockFileReleaseArgs { Paths = ValidateLockPaths(repositoryRoot, paths) };
                Lore.LockFileRelease(globals, args).Wait();
            });
        }

        public Task<LoreLockQueryResult> QueryLocksAsync(string repositoryRoot, string branch, CancellationToken cancellationToken) =>
            ReadLocksAsync(repositoryRoot, branch, paths: null);

        public Task<LoreLockQueryResult> LockStatusAsync(string repositoryRoot, string[] paths, CancellationToken cancellationToken) =>
            ReadLocksAsync(repositoryRoot, string.Empty, paths ?? Array.Empty<string>());

        private static async Task<LoreLockQueryResult> ReadLocksAsync(string root, string branch, string[]? paths)
        {
            var locks = new List<LoreFileLock>();
            var errors = new ConcurrentQueue<Exception>();
            LoreCommandResult result = await ExecuteAsync(identity: null, offline: false, root, globals =>
            {
                if (string.IsNullOrWhiteSpace(root))
                {
                    throw new ArgumentException("A repository is required to query locks.");
                }

                if (paths == null)
                {
                    using var args = new LoreLockFileQueryArgs { Branch = branch ?? string.Empty };
                    Lore.LockFileQuery(globals, args).Callback((ev, context) =>
                    {
                        try
                        {
                            if (ev.Tag == LoreEventTag.LOCK_FILE_QUERY)
                            {
                                LoreLockFileQueryEventDataFFI entry = ev.GetData<LoreLockFileQueryEventDataFFI>();
                                locks.Add(new LoreFileLock
                                {
                                    Path = NormalizePath(root, entry.Path), Owner = entry.Owner ?? string.Empty,
                                    BranchId = LoreRevisionMapper.ToHex(entry.Branch.Data), LockedAt = entry.LockedAt,
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _ = ex.LogAsync();
                            errors.Enqueue(ex);
                        }
                    }).Wait();
                }
                else
                {
                    using var args = new LoreLockFileStatusArgs { Paths = ValidateLockPaths(root, paths) };
                    Lore.LockFileStatus(globals, args).Callback((ev, context) =>
                    {
                        try
                        {
                            if (ev.Tag == LoreEventTag.LOCK_FILE_STATUS)
                            {
                                LoreLockFileStatusEventDataFFI entry = ev.GetData<LoreLockFileStatusEventDataFFI>();
                                locks.Add(new LoreFileLock
                                {
                                    Path = NormalizePath(root, entry.Path), Owner = entry.Owner ?? string.Empty,
                                    LockedAt = entry.LockedAt,
                                });
                            }
                        }
                        catch (Exception ex)
                        {
                            _ = ex.LogAsync();
                            errors.Enqueue(ex);
                        }
                    }).Wait();
                }
            }).ConfigureAwait(false);

            if (result.Success && errors.TryDequeue(out Exception? error))
            {
                result = MapFailure(error);
            }

            return new LoreLockQueryResult { Result = result, Locks = result.Success ? locks.ToArray() : Array.Empty<LoreFileLock>() };
        }

        internal static string[] ValidateLockPaths(string root, string[] paths)
        {
            if (string.IsNullOrWhiteSpace(root) || paths == null || paths.Length == 0)
            {
                throw new ArgumentException("A repository and at least one file are required.");
            }

            string prefix = Path.GetFullPath(root).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar) + Path.DirectorySeparatorChar;
            return paths.Select(path =>
            {
                if (string.IsNullOrWhiteSpace(path) || path.IndexOfAny(new[] { '*', '?' }) >= 0)
                {
                    throw new ArgumentException("Lock operations require explicit file paths.");
                }

                string full = Path.GetFullPath(Path.IsPathRooted(path) ? path : Path.Combine(prefix, path));
                if (!full.StartsWith(prefix, StringComparison.OrdinalIgnoreCase) || Directory.Exists(full))
                {
                    throw new ArgumentException("The lock path must be a file inside the repository: " + path);
                }

                return full;
            }).Distinct(StringComparer.OrdinalIgnoreCase).ToArray();
        }
    }
}