using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.IO;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.Options;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Imaging.Interop;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace LoreVS.UI
{
    /// <summary>
    /// View model behind the Lore Changes tool window. Owns the list of changed files, the commit
    /// message, branch/ahead-behind state, and the commit/push/pull/discard/diff operations. All
    /// Lore calls run on a background thread; observable state is updated back on the UI thread.
    /// </summary>
    public sealed class LoreChangesViewModel : INotifyPropertyChanged
    {
        private ILoreClient _client;
        private LoreSccService _sccService;
        private string _repositoryRoot;
        private string _identity = string.Empty;
        private bool _autoPushOnCommit;

        private bool _hasRepository;
        private bool _subscribed;
        private string _branchText = string.Empty;
        private string _aheadBehindText = string.Empty;
        private string _commitMessage = string.Empty;
        private bool _amend;
        private bool _isBusy;
        private string _statusText = string.Empty;
        private List<LoreTreeNode> _treeRoots = new List<LoreTreeNode>();
        private IVsImageService2 _imageService;
        private readonly Dictionary<string, ImageMoniker> _fileIconCache =
            new Dictionary<string, ImageMoniker>(StringComparer.OrdinalIgnoreCase);

        public LoreChangesViewModel()
        {
            Changes = new ObservableCollection<LoreChangeItem>();
            Nodes = new ObservableCollection<LoreTreeNode>();
        }

        public event PropertyChangedEventHandler PropertyChanged;

        /// <summary>The changed files currently displayed.</summary>
        public ObservableCollection<LoreChangeItem> Changes { get; }

        /// <summary>
        /// The changed files arranged as a flattened folder tree (folders back to the repository
        /// root, files as leaves), honoring each folder's expand/collapse state. Bound to the list.
        /// </summary>
        public ObservableCollection<LoreTreeNode> Nodes { get; }

        /// <summary>True when the open solution/folder is under Lore source control.</summary>
        public bool HasRepository
        {
            get => _hasRepository;
            private set { if (SetProperty(ref _hasRepository, value)) { OnPropertyChanged(nameof(CanInteract)); } }
        }

        /// <summary>Current branch description (e.g. <c>main</c> or <c>main (no remote)</c>).</summary>
        public string BranchText
        {
            get => _branchText;
            private set => SetProperty(ref _branchText, value);
        }

        /// <summary>Incoming/outgoing indicator (e.g. <c>Outgoing 2  Incoming 1</c>).</summary>
        public string AheadBehindText
        {
            get => _aheadBehindText;
            private set => SetProperty(ref _aheadBehindText, value);
        }

        /// <summary>The commit message entered by the user.</summary>
        public string CommitMessage
        {
            get => _commitMessage;
            set => SetProperty(ref _commitMessage, value);
        }

        /// <summary>When checked, the commit amends the latest revision instead of creating a new one.</summary>
        public bool Amend
        {
            get => _amend;
            set => SetProperty(ref _amend, value);
        }

        /// <summary>True while a Lore operation is running (disables the UI).</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set { if (SetProperty(ref _isBusy, value)) { OnPropertyChanged(nameof(CanInteract)); } }
        }

        /// <summary>Status line shown at the bottom of the window.</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>True when the window is bound to a repository and idle.</summary>
        public bool CanInteract => HasRepository && !IsBusy;

        /// <summary>The repository root bound to this window, or null.</summary>
        public string RepositoryRoot => _repositoryRoot;

        /// <summary>The Lore client backing this window, or null when unavailable.</summary>
        internal ILoreClient Client => _client;

        /// <summary>
        /// Resolves the Lore client and options once, subscribes to solution and folder open/close
        /// events so the window stays in sync with the open workspace, and loads the initial change
        /// list. Called once when the window first appears.
        /// </summary>
        public async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _sccService = await VS.GetServiceAsync<LoreSccService, LoreSccService>();
            _client = _sccService?.Client;

            General options = await General.GetLiveInstanceAsync();
            _identity = options.Identity ?? string.Empty;
            _autoPushOnCommit = options.AutoPushOnCommit;

            if (!_subscribed)
            {
                _subscribed = true;

                // A controlled solution is usually opened AFTER this window is first shown, so the
                // binding has to be re-resolved whenever the open solution or folder changes -
                // otherwise the window would stay stuck on "not under Lore source control".
                VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionOpened;
                VS.Events.SolutionEvents.OnAfterCloseSolution += ReloadSafe;
                VS.Events.SolutionEvents.OnAfterOpenFolder += OnFolderChanged;
                VS.Events.SolutionEvents.OnAfterCloseFolder += OnFolderChanged;

                // Saving a document changes its Lore status, so refresh the list to reflect it -
                // the same trigger the package uses to update Solution Explorer glyphs.
                VS.Events.DocumentEvents.Saved += OnDocumentSaved;
            }

            await ReloadAsync();
        }

        private void OnDocumentSaved(string filePath) => RefreshSafe();

        /// <summary>
        /// Re-resolves the Lore repository binding for the currently open solution or folder and
        /// reloads the change list. Used on first load, whenever the workspace changes, and by the
        /// Refresh toolbar button, so a repository bound after the window opened is picked up.
        /// </summary>
        public async Task ReloadAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string solutionDir = await ResolveSolutionDirectoryAsync();
            ILoreClient client = _client;
            _repositoryRoot = client != null && solutionDir != null
                ? await Task.Run(() => client.FindRepositoryRoot(solutionDir))
                : null;

            HasRepository = _repositoryRoot != null;

            if (!HasRepository)
            {
                Changes.Clear();
                _treeRoots = new List<LoreTreeNode>();
                Nodes.Clear();
                BranchText = string.Empty;
                AheadBehindText = string.Empty;
                OnPropertyChanged(nameof(HasChanges));
                StatusText = "This solution or folder is not under Lore source control.";
                return;
            }

            await RefreshAsync();
        }

        private void OnSolutionOpened(Solution? solution) => ReloadSafe();

        private void OnFolderChanged(string folder) => ReloadSafe();

        private void ReloadSafe()
        {
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(ReloadAsync).FileAndForget("LoreVS/ReloadChanges");
        }

        private void RefreshSafe()
        {
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(RefreshAsync).FileAndForget("LoreVS/RefreshChanges");
        }

        /// <summary>Re-queries Lore status and branch info and repopulates the change list.</summary>
        public async Task RefreshAsync()
        {
            if (!HasRepository || _client == null)
            {
                return;
            }

            await RunAsync("Refreshing Lore status...", async () =>
            {
                string root = _repositoryRoot;
                ILoreClient client = _client;

                // 1) File status drives the change list and uses the same scan that powers Solution
                //    Explorer glyphs, so it is reliable. Fetch and render it FIRST so the list always
                //    appears even if the branch-info lookup below is slow or unavailable.
                IReadOnlyDictionary<string, LoreFileStatus> statuses =
                    await Task.Run(() => client.GetRepositoryStatus(root));

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

                Changes.Clear();
                IEnumerable<LoreChangeItem> items = statuses
                    .Where(kvp => IsChange(kvp.Value))
                    .Select(kvp => new LoreChangeItem(kvp.Key, root, kvp.Value))
                    .OrderBy(i => i.RelativePath, StringComparer.OrdinalIgnoreCase);

                foreach (LoreChangeItem item in items)
                {
                    Changes.Add(item);
                }

                _treeRoots = LoreTreeNode.BuildTree(Changes, ResolveFileIcon);
                RebuildVisibleNodes();

                StatusText = Changes.Count == 0
                    ? "No changes."
                    : $"{Changes.Count} change(s).";
                OnPropertyChanged(nameof(HasChanges));

                // 2) Branch / ahead-behind info is best-effort: on a repository with a remote the
                //    revision lookup can block, so it is time-bounded and never allowed to hang the
                //    window or hide the change list. On timeout/failure the branch label is cleared.
                LoreRepositoryInfo? info = await GetBranchInfoWithTimeoutAsync(client, root);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                UpdateBranchInfo(info);
            });
        }

        /// <summary>True when there is at least one changed file.</summary>
        public bool HasChanges => Changes.Count > 0;

        /// <summary>
        /// Resolves the shell file-type icon for <paramref name="fullPath"/> via
        /// <c>IVsImageService2</c>, cached by file extension so each extension is looked up once.
        /// Must be called on the UI thread. Falls back to a generic document icon on failure.
        /// </summary>
        private ImageMoniker ResolveFileIcon(string fullPath)
        {
            ThreadHelper.ThrowIfNotOnUIThread();

            string ext = Path.GetExtension(fullPath) ?? string.Empty;
            if (_fileIconCache.TryGetValue(ext, out ImageMoniker cached))
            {
                return cached;
            }

            ImageMoniker moniker = Microsoft.VisualStudio.Imaging.KnownMonikers.Document;
            try
            {
                _imageService ??= ServiceProvider.GlobalProvider.GetService(typeof(SVsImageService)) as IVsImageService2;
                if (_imageService != null)
                {
                    moniker = _imageService.GetImageMonikerForFile(fullPath);
                }
            }
            catch (Exception ex)
            {
                ex.LogAsync().FireAndForget();
            }

            _fileIconCache[ext] = moniker;
            return moniker;
        }

        /// <summary>
        /// Toggles a folder node's expand/collapse state and refreshes the visible rows. No-op for
        /// file leaves.
        /// </summary>
        public void ToggleFolder(LoreTreeNode node)
        {
            if (node == null || !node.IsFolder)
            {
                return;
            }

            node.IsExpanded = !node.IsExpanded;
            RebuildVisibleNodes();
        }

        /// <summary>Re-flattens the folder tree into <see cref="Nodes"/>, honoring expand state.</summary>
        private void RebuildVisibleNodes()
        {
            var visible = new List<LoreTreeNode>();
            LoreTreeNode.Flatten(_treeRoots, visible);

            Nodes.Clear();
            foreach (LoreTreeNode node in visible)
            {
                Nodes.Add(node);
            }
        }

        /// <summary>
        /// Stages all changes and commits them with the current message (optionally pushing). When
        /// <see cref="Amend"/> is set the latest revision is amended instead.
        /// </summary>
        public async Task CommitAsync(bool push)
        {
            if (!HasRepository || _client == null)
            {
                return;
            }

            string message = (CommitMessage ?? string.Empty).Trim();
            if (message.Length == 0)
            {
                await VS.MessageBox.ShowWarningAsync("Lore", "Enter a commit message first.");
                return;
            }

            if (!await EnsureAvailableAsync())
            {
                return;
            }

            bool amend = Amend;
            await RunAsync(amend ? "Amending..." : "Committing...", async () =>
            {
                string root = _repositoryRoot;
                ILoreClient client = _client;
                string identity = _identity;

                LoreCommandResult result = await Task.Run(() =>
                {
                    if (!amend)
                    {
                        LoreCommandResult stage = client.StageAll(root);
                        if (!stage.Success)
                        {
                            return stage;
                        }
                    }

                    return amend
                        ? client.Amend(root, message, identity)
                        : client.Commit(root, message, identity);
                });

                await LoreLog.WriteCommandAsync(amend ? $"revision amend \"{message}\"" : $"commit \"{message}\"", result.CombinedText);

                if (!result.Success)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await VS.MessageBox.ShowErrorAsync("Lore", (amend ? "Amend failed:\n\n" : "Commit failed:\n\n") + result.CombinedText);
                    return;
                }

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                CommitMessage = string.Empty;
                Amend = false;

                if (push || _autoPushOnCommit)
                {
                    await PushCoreAsync();
                }
            });

            await RefreshAsync();
            _sccService?.RefreshAllGlyphs();
        }

        /// <summary>Pushes local commits to the remote.</summary>
        public async Task PushAsync()
        {
            if (!HasRepository || _client == null || !await EnsureAvailableAsync())
            {
                return;
            }

            await RunAsync("Pushing...", PushCoreAsync);
            await RefreshAsync();
        }

        /// <summary>Synchronizes the working tree to the latest remote revision (pull).</summary>
        public async Task PullAsync()
        {
            if (!HasRepository || _client == null || !await EnsureAvailableAsync())
            {
                return;
            }

            await RunAsync("Pulling...", async () =>
            {
                string root = _repositoryRoot;
                ILoreClient client = _client;
                LoreCommandResult result = await Task.Run(() => client.Sync(root));
                await LoreLog.WriteCommandAsync("sync", result.CombinedText);

                if (!result.Success)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await VS.MessageBox.ShowErrorAsync("Lore", "Pull (sync) failed:\n\n" + result.CombinedText);
                }
            });

            await RefreshAsync();
            _sccService?.RefreshAllGlyphs();
        }

        /// <summary>Discards working-tree changes for the supplied files after confirmation.</summary>
        public async Task DiscardAsync(IReadOnlyList<LoreChangeItem> items)
        {
            if (!HasRepository || _client == null || items == null || items.Count == 0)
            {
                return;
            }

            string prompt = items.Count == 1
                ? $"Discard changes to '{items[0].RelativePath}'? This cannot be undone."
                : $"Discard changes to {items.Count} files? This cannot be undone.";

            bool confirmed = await VS.MessageBox.ShowConfirmAsync("Lore", prompt);
            if (!confirmed)
            {
                return;
            }

            await RunAsync("Discarding changes...", async () =>
            {
                string root = _repositoryRoot;
                ILoreClient client = _client;
                string[] paths = items.Select(i => i.RelativePath.Replace('\\', '/')).ToArray();

                LoreCommandResult result = await Task.Run(() => client.ResetFiles(root, paths));
                await LoreLog.WriteCommandAsync("file reset", result.CombinedText);

                if (!result.Success)
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    await VS.MessageBox.ShowErrorAsync("Lore", "Failed to discard changes:\n\n" + result.CombinedText);
                }
            });

            await RefreshAsync();
            _sccService?.RefreshAllGlyphs();
        }

        /// <summary>Opens the diff for a changed file.</summary>
        public Task ShowDiffAsync(LoreChangeItem item) =>
            LoreDiffPresenter.ShowAsync(_client, _repositoryRoot, item);

        private async Task PushCoreAsync()
        {
            string root = _repositoryRoot;
            ILoreClient client = _client;
            LoreCommandResult result = await Task.Run(() => client.Push(root));
            await LoreLog.WriteCommandAsync("push", result.CombinedText);

            if (!result.Success)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.MessageBox.ShowWarningAsync("Lore", "Push failed:\n\n" + result.CombinedText);
            }
        }

        private async Task<bool> EnsureAvailableAsync()
        {
            ILoreClient client = _client;
            bool available = client != null && await Task.Run(() => client.IsAvailable);
            if (!available)
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.MessageBox.ShowErrorAsync("Lore",
                    "The Lore worker could not be started, so the SDK is unavailable. Reinstall the " +
                    "extension so the worker payload is deployed, then try again.");
            }

            return available;
        }

        /// <summary>
        /// Fetches branch / ahead-behind info off the UI thread, but gives up after a short timeout
        /// so a revision lookup that blocks (for example contacting a remote) can never freeze the
        /// window. Returns <see langword="null"/> on timeout or failure, which the caller renders as
        /// an unknown branch rather than an error.
        /// </summary>
        private static async Task<LoreRepositoryInfo?> GetBranchInfoWithTimeoutAsync(ILoreClient client, string root)
        {
            const int timeoutMs = 8000;
            try
            {
                Task<LoreRepositoryInfo> work = Task.Run(() => client.GetRepositoryInfo(root));
                Task completed = await Task.WhenAny(work, Task.Delay(timeoutMs)).ConfigureAwait(false);
                if (completed != work)
                {
                    return null;
                }

                return await work.ConfigureAwait(false);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                return null;
            }
        }

        private void UpdateBranchInfo(LoreRepositoryInfo? info)
        {
            if (info == null || string.IsNullOrEmpty(info.BranchName))
            {
                BranchText = "(unknown branch)";
                AheadBehindText = string.Empty;
                return;
            }

            BranchText = info.HasRemote ? info.BranchName : info.BranchName + " (no remote)";

            var parts = new List<string>();
            if (info.IsLocalAhead)
            {
                parts.Add("Outgoing");
            }

            if (info.IsRemoteAhead)
            {
                parts.Add("Incoming");
            }

            AheadBehindText = parts.Count > 0 ? string.Join("  ", parts) : string.Empty;
        }

        private async Task RunAsync(string status, Func<Task> operation)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IsBusy = true;
            StatusText = status;
            try
            {
                await operation();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                await VS.MessageBox.ShowErrorAsync("Lore", "The Lore operation failed:\n\n" + ex.Message);
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                IsBusy = false;
            }
        }

        private static async Task<string> ResolveSolutionDirectoryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IVsSolution solution = await VS.Services.GetSolutionAsync();
            return SolutionScc.GetSolutionDirectory(solution);
        }

        private static bool IsChange(LoreFileStatus status)
        {
            switch (status)
            {
                case LoreFileStatus.Modified:
                case LoreFileStatus.Added:
                case LoreFileStatus.Deleted:
                case LoreFileStatus.Conflicted:
                    return true;
                default:
                    return false;
            }
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
