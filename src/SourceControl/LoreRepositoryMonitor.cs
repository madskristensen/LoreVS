using System;
using System.Collections.Generic;
using System.Diagnostics.CodeAnalysis;
using System.IO;
using System.Threading;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace LoreVS.SourceControl
{
    /// <summary>Tracks project renames and external repository changes through shared VS services.</summary>
    [SuppressMessage("Usage", "VSTHRD010:Invoke single-threaded types on Main thread",
        Justification = "IVsFreeThreadedFileChangeEvents2 callbacks only touch thread-safe state and marshal UI work explicitly.")]
    internal sealed class LoreRepositoryMonitor : IVsTrackProjectDocumentsEvents2, IVsFreeThreadedFileChangeEvents2, IDisposable
    {
        private const int RefreshDelayMilliseconds = 400;
        private readonly LoreSccService _sccService;
        private readonly SemaphoreSlim _moveGate = new SemaphoreSlim(1, 1);
        private readonly SemaphoreSlim _subscriptionGate = new SemaphoreSlim(1, 1);
        private IVsAsyncFileChangeEx? _fileChangeService;
        private IVsTrackProjectDocuments2? _trackProjectDocuments;
        private string? _repositoryRoot;
        private uint _directoryCookie;
        private uint _projectDocumentsCookie;
        private int _refreshVersion;
        private bool _disposed;

        public LoreRepositoryMonitor(LoreSccService sccService)
        {
            _sccService = sccService ?? throw new ArgumentNullException(nameof(sccService));
        }

        public async Task InitializeAsync(CancellationToken cancellationToken)
        {
            _fileChangeService = await VS.GetServiceAsync<SVsFileChangeEx, IVsAsyncFileChangeEx>();
            _trackProjectDocuments = await VS.GetServiceAsync<SVsTrackProjectDocuments, IVsTrackProjectDocuments2>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync(cancellationToken);
            if (_trackProjectDocuments != null)
            {
                ErrorHandler.ThrowOnFailure(_trackProjectDocuments.AdviseTrackProjectDocumentsEvents(this, out _projectDocumentsCookie));
            }
        }

        public async Task SetRepositoryAsync(string? repositoryRoot, CancellationToken cancellationToken = default)
        {
            string? normalizedRoot = string.IsNullOrWhiteSpace(repositoryRoot)
                ? null
                : Path.GetFullPath(repositoryRoot).TrimEnd(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);

            await _subscriptionGate.WaitAsync(cancellationToken).ConfigureAwait(false);
            try
            {
                if (_disposed || string.Equals(_repositoryRoot, normalizedRoot, StringComparison.OrdinalIgnoreCase))
                {
                    return;
                }

                if (_directoryCookie != 0 && _fileChangeService != null)
                {
                    await _fileChangeService.UnadviseDirChangeAsync(_directoryCookie, cancellationToken).ConfigureAwait(false);
                    _directoryCookie = 0;
                }

                _repositoryRoot = normalizedRoot;
                if (_repositoryRoot != null && _fileChangeService != null)
                {
                    _directoryCookie = await _fileChangeService.AdviseDirChangeAsync(
                        _repositoryRoot, watchSubdirectories: true, this, cancellationToken).ConfigureAwait(false);
                    if (_disposed && _directoryCookie != 0)
                    {
                        await _fileChangeService.UnadviseDirChangeAsync(_directoryCookie, cancellationToken).ConfigureAwait(false);
                        _directoryCookie = 0;
                    }
                }
            }
            finally
            {
                _subscriptionGate.Release();
            }
        }

        private void QueueMoves(int count, string[] oldPaths, string[] newPaths)
        {
            int pairCount = Math.Min(count, Math.Min(oldPaths.Length, newPaths.Length));
            if (pairCount == 0)
            {
                return;
            }

            var oldCopy = new string[pairCount];
            var newCopy = new string[pairCount];
            Array.Copy(oldPaths, oldCopy, pairCount);
            Array.Copy(newPaths, newCopy, pairCount);
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(() => RecordMovesAsync(oldCopy, newCopy)).FileAndForget("LoreVS/RecordMoves");
        }

        private async Task RecordMovesAsync(string[] oldPaths, string[] newPaths)
        {
            await _moveGate.WaitAsync();
            try
            {
                await RecordMovesCoreAsync(oldPaths, newPaths);
            }
            finally
            {
                _moveGate.Release();
            }
        }

        private async Task RecordMovesCoreAsync(string[] oldPaths, string[] newPaths)
        {
            string? root = _repositoryRoot;
            if (_disposed || root == null)
            {
                return;
            }

            List<LoreMoveEntry> moves = await Task.Run(() => ExpandFileMoves(root, oldPaths, newPaths));
            var failures = new List<string>();
            await Task.Run(() =>
            {
                foreach (LoreMoveEntry move in moves)
                {
                    LoreCommandResult result = _sccService.Client.RecordMove(root, move.OldPath, move.NewPath);
                    if (!result.Success)
                    {
                        failures.Add(move.NewPath + ": " + result.CombinedText);
                    }
                }
            });

            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (failures.Count > 0)
            {
                await VS.MessageBox.ShowErrorAsync("Lore", "Lore could not record one or more renamed items:\n\n" + string.Join("\n", failures));
            }

            _sccService.NotifyStatusChanged();
        }

        internal static List<LoreMoveEntry> ExpandFileMoves(string repositoryRoot, string[] oldPaths, string[] newPaths)
        {
            var moves = new List<LoreMoveEntry>();
            int pairCount = Math.Min(oldPaths.Length, newPaths.Length);
            for (int i = 0; i < pairCount; i++)
            {
                string oldPath = Path.GetFullPath(oldPaths[i]);
                string newPath = Path.GetFullPath(newPaths[i]);
                if (!LoreBrokeredClient.IsPathWithinRoot(repositoryRoot, oldPath) ||
                    !LoreBrokeredClient.IsPathWithinRoot(repositoryRoot, newPath) || IsLoreMetadata(repositoryRoot, oldPath) || IsLoreMetadata(repositoryRoot, newPath))
                {
                    continue;
                }

                if (Directory.Exists(newPath))
                {
                    foreach (string newFile in Directory.EnumerateFiles(newPath, "*", SearchOption.AllDirectories))
                    {
                        string relativePath = newFile.Substring(newPath.Length).TrimStart(Path.DirectorySeparatorChar, Path.AltDirectorySeparatorChar);
                        moves.Add(new LoreMoveEntry { OldPath = Path.Combine(oldPath, relativePath), NewPath = newFile });
                    }
                }
                else
                {
                    moves.Add(new LoreMoveEntry { OldPath = oldPath, NewPath = newPath });
                }
            }

            return moves;
        }

        private void ScheduleRefresh(string? changedPath)
        {
            string? root = _repositoryRoot;
            if (_disposed || root == null || (changedPath != null && IsLoreMetadata(root, changedPath)))
            {
                return;
            }

            int version = Interlocked.Increment(ref _refreshVersion);
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(async () =>
            {
                await Task.Delay(RefreshDelayMilliseconds);
                if (_disposed || version != Volatile.Read(ref _refreshVersion))
                {
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _sccService.NotifyStatusChanged();
            }).FileAndForget("LoreVS/FileChangeRefresh");
        }

        private static bool IsLoreMetadata(string repositoryRoot, string path)
        {
            string metadataRoot = Path.Combine(Path.GetFullPath(repositoryRoot), ".lore");
            string candidate = Path.GetFullPath(path);
            return string.Equals(metadataRoot, candidate, StringComparison.OrdinalIgnoreCase) ||
                candidate.StartsWith(metadataRoot + Path.DirectorySeparatorChar, StringComparison.OrdinalIgnoreCase);
        }

        public int OnAfterRenameFiles(int cProjects, int cFiles, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] oldNames, string[] newNames, VSRENAMEFILEFLAGS[] flags)
        {
            QueueMoves(cFiles, oldNames, newNames);
            return VSConstants.S_OK;
        }

        public int OnAfterRenameDirectories(int cProjects, int cDirs, IVsProject[] rgpProjects, int[] rgFirstIndices, string[] oldNames, string[] newNames, VSRENAMEDIRECTORYFLAGS[] flags)
        {
            QueueMoves(cDirs, oldNames, newNames);
            return VSConstants.S_OK;
        }

        public int DirectoryChanged(string directory) { ScheduleRefresh(null); return VSConstants.S_OK; }
        public int DirectoryChangedEx(string directory, string file) { ScheduleRefresh(string.IsNullOrEmpty(file) ? null : Path.Combine(directory, file)); return VSConstants.S_OK; }
        public int DirectoryChangedEx2(string directory, uint count, string[] files, uint[] flags) { ScheduleRefreshes(directory, count, files); return VSConstants.S_OK; }
        public int FilesChanged(uint count, string[] files, uint[] flags) { ScheduleRefreshes(null, count, files); return VSConstants.S_OK; }

        private void ScheduleRefreshes(string? directory, uint count, string[] files)
        {
            int fileCount = Math.Min((int)count, files.Length);
            for (int i = 0; i < fileCount; i++)
            {
                string path = directory != null && !Path.IsPathRooted(files[i]) ? Path.Combine(directory, files[i]) : files[i];
                ScheduleRefresh(path);
            }
        }

        public int OnQueryAddFiles(IVsProject project, int count, string[] paths, VSQUERYADDFILEFLAGS[] flags, VSQUERYADDFILERESULTS[] summary, VSQUERYADDFILERESULTS[] results) => VSConstants.S_OK;
        public int OnAfterAddFilesEx(int projectCount, int fileCount, IVsProject[] projects, int[] firstIndices, string[] paths, VSADDFILEFLAGS[] flags) => VSConstants.S_OK;
        public int OnAfterAddDirectoriesEx(int projectCount, int directoryCount, IVsProject[] projects, int[] firstIndices, string[] paths, VSADDDIRECTORYFLAGS[] flags) => VSConstants.S_OK;
        public int OnAfterRemoveFiles(int projectCount, int fileCount, IVsProject[] projects, int[] firstIndices, string[] paths, VSREMOVEFILEFLAGS[] flags) => VSConstants.S_OK;
        public int OnAfterRemoveDirectories(int projectCount, int directoryCount, IVsProject[] projects, int[] firstIndices, string[] paths, VSREMOVEDIRECTORYFLAGS[] flags) => VSConstants.S_OK;
        public int OnQueryRenameFiles(IVsProject project, int count, string[] oldNames, string[] newNames, VSQUERYRENAMEFILEFLAGS[] flags, VSQUERYRENAMEFILERESULTS[] summary, VSQUERYRENAMEFILERESULTS[] results) => VSConstants.S_OK;
        public int OnQueryRenameDirectories(IVsProject project, int count, string[] oldNames, string[] newNames, VSQUERYRENAMEDIRECTORYFLAGS[] flags, VSQUERYRENAMEDIRECTORYRESULTS[] summary, VSQUERYRENAMEDIRECTORYRESULTS[] results) => VSConstants.S_OK;
        public int OnQueryAddDirectories(IVsProject project, int count, string[] paths, VSQUERYADDDIRECTORYFLAGS[] flags, VSQUERYADDDIRECTORYRESULTS[] summary, VSQUERYADDDIRECTORYRESULTS[] results) => VSConstants.S_OK;
        public int OnQueryRemoveFiles(IVsProject project, int count, string[] paths, VSQUERYREMOVEFILEFLAGS[] flags, VSQUERYREMOVEFILERESULTS[] summary, VSQUERYREMOVEFILERESULTS[] results) => VSConstants.S_OK;
        public int OnQueryRemoveDirectories(IVsProject project, int count, string[] paths, VSQUERYREMOVEDIRECTORYFLAGS[] flags, VSQUERYREMOVEDIRECTORYRESULTS[] summary, VSQUERYREMOVEDIRECTORYRESULTS[] results) => VSConstants.S_OK;
        public int OnAfterSccStatusChanged(int projectCount, int fileCount, IVsProject[] projects, int[] firstIndices, string[] paths, uint[] status) => VSConstants.S_OK;

        public void Dispose()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            Interlocked.Increment(ref _refreshVersion);
            if (_trackProjectDocuments != null && _projectDocumentsCookie != 0)
            {
                _trackProjectDocuments.UnadviseTrackProjectDocumentsEvents(_projectDocumentsCookie);
                _projectDocumentsCookie = 0;
            }

            if (_directoryCookie != 0 && _fileChangeService != null)
            {
                _fileChangeService.UnadviseDirChangeAsync(_directoryCookie, CancellationToken.None).FileAndForget("LoreVS/UnadviseFileChanges");
                _directoryCookie = 0;
            }
        }
    }
}
