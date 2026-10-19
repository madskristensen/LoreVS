using System;
using System.IO;
using System.Threading.Tasks;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace LoreVS.UI
{
    public partial class LoreBranchesControl : UserControl
    {
        private readonly LoreBranchesViewModel _model = new();
        private readonly JoinableTaskFactory _tasks;
        private ILoreClient? _client;
        private LoreSccService? _service;
        private bool _subscribed;
        private bool _applyingBranches;
        private bool _reloadPending;
        private int _workspaceVersion;

        public LoreBranchesControl(JoinableTaskFactory tasks)
        {
            _tasks = tasks ?? throw new ArgumentNullException(nameof(tasks));
            InitializeComponent();
            DataContext = _model;
            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_subscribed)
            {
                _subscribed = true;
                VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionOpened;
                VS.Events.SolutionEvents.OnAfterCloseSolution += OnWorkspaceClosed;
                VS.Events.SolutionEvents.OnAfterOpenFolder += OnFolderChanged;
                VS.Events.SolutionEvents.OnAfterCloseFolder += OnFolderChanged;
            }

            RequestReload();
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            VS.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionOpened;
            VS.Events.SolutionEvents.OnAfterCloseSolution -= OnWorkspaceClosed;
            VS.Events.SolutionEvents.OnAfterOpenFolder -= OnFolderChanged;
            VS.Events.SolutionEvents.OnAfterCloseFolder -= OnFolderChanged;
            _subscribed = false;
        }

        private void OnSolutionOpened(Solution? solution) => RequestReload();
        private void OnWorkspaceClosed() => RequestReload();
        private void OnFolderChanged(string folder) => RequestReload();
        private void OnRefreshClick(object sender, RoutedEventArgs e) => RequestReload();

        private void RequestReload()
        {
            _tasks.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                _workspaceVersion++;
                ResetRevision.Clear();
                _applyingBranches = true;
                _model.ApplyBranches(null, Array.Empty<LoreBranchEntry>());
                _applyingBranches = false;
                if (_model.IsBusy)
                {
                    _reloadPending = true;
                }
                else
                {
                    Run(ReloadAsync);
                }
            }).FileAndForget("LoreVS/BranchesReload");
        }

        private async Task ReloadAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            int version = _workspaceVersion;
            _service = await VS.GetServiceAsync<LoreSccService, LoreSccService>();
            _client = _service?.Client;
            IVsSolution solution = await VS.Services.GetSolutionAsync();
            string? directory = SolutionScc.GetSolutionDirectory(solution);
            ILoreClient? client = _client;
            string? root = directory == null || client == null ? null : await Task.Run(() => client.FindRepositoryRoot(directory));
            if (root == null || client == null)
            {
                _model.StatusText = "This solution or folder is not under Lore source control.";
                return;
            }

            LoreBranchEntry[] branches = await Task.Run(() => client.ListBranches(root));
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (version != _workspaceVersion)
            {
                return;
            }

            _applyingBranches = true;
            _model.ApplyBranches(root, branches);
            _applyingBranches = false;
            _model.StatusText = branches.Length == 0 ? "No branches were returned. Check the Lore Output window and refresh." : "Select a branch to inspect or manage.";
            await LoadInfoAsync();
        }

        private void OnBranchChanged(object sender, System.Windows.Controls.SelectionChangedEventArgs e)
        {
            if (!_applyingBranches)
            {
                Run(LoadInfoAsync);
            }
        }

        private async Task LoadInfoAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            if (_client == null || _model.RepositoryRoot == null || _model.SelectedBranch == null)
            {
                return;
            }

            ILoreClient client = _client;
            string root = _model.RepositoryRoot;
            string branch = _model.SelectedBranch.Name;
            Guid request = _model.BeginInfoLoad();
            var info = new LoreBranchInfo();
            await LoreAuthFlow.ExecuteAsync(client, root, string.Empty, () => (info = client.GetBranchInfo(root, branch)).Result);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _model.ApplyInfo(request, info);
        }

        private void OnCompareClick(object sender, RoutedEventArgs e) => Run(async () =>
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ILoreClient client = _client ?? throw new InvalidOperationException("The Lore client is unavailable.");
            string root = _model.RepositoryRoot ?? throw new InvalidOperationException("Open a Lore repository first.");
            string source = _model.SourceBranch?.Name ?? throw new InvalidOperationException("Select a source branch.");
            string target = _model.TargetBranch?.Name ?? throw new InvalidOperationException("Select a target branch.");
            Guid request = _model.BeginComparison();
            var comparison = new LoreBranchComparison();
            await LoreAuthFlow.ExecuteAsync(client, root, string.Empty, () => (comparison = client.CompareBranches(root, source, target)).Result);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _model.ApplyComparison(request, comparison);
        });

        private void OnProtectClick(object sender, RoutedEventArgs e) => Run(() => MutateAsync("Protect"));
        private void OnUnprotectClick(object sender, RoutedEventArgs e) => Run(() => MutateAsync("Unprotect"));
        private void OnArchiveClick(object sender, RoutedEventArgs e) => Run(() => MutateAsync("Archive"));
        private void OnResetClick(object sender, RoutedEventArgs e) => Run(() => MutateAsync("Reset"));

        private async Task MutateAsync(string action)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ILoreClient client = _client ?? throw new InvalidOperationException("The Lore client is unavailable.");
            string root = _model.RepositoryRoot ?? throw new InvalidOperationException("Open a Lore repository first.");
            LoreBranchEntry branch = _model.SelectedBranch ?? throw new InvalidOperationException("Select a branch first.");
            int version = _workspaceVersion;
            string revision = ResetRevision.Text.Trim();
            string expectedTip = _model.BranchInfo?.LatestRevision ?? string.Empty;
            if (action == "Archive" && branch.IsCurrent)
            {
                throw new InvalidOperationException("Switch to another branch before archiving the current branch.");
            }

            if (action == "Reset")
            {
                if (string.IsNullOrEmpty(revision) || string.IsNullOrEmpty(expectedTip))
                {
                    throw new InvalidOperationException("Load branch information and enter a target revision before resetting.");
                }

                await EnsureDocumentsSavedAsync(root);
            }

            string detail = action == "Archive"
                ? "This archives the branch locally and on its remote, without merging it. Layers and links are not included. Protected and default branches cannot be archived. A remote failure may leave a partial archive; refresh before retrying."
                : action == "Reset"
                    ? $"Reset to {revision}?\nPrevious tip: {expectedTip}\nThis rewinds the local branch and updates working files if it is current. Uncommitted changes or an active merge block the reset. The server branch is not reset and no push is performed."
                    : "This changes native Lore branch protection using the server's permissions, or local metadata when there is no remote.";
            if (!await VS.MessageBox.ShowConfirmAsync("Lore", $"{action} branch '{branch.Name}'?\n\n{detail}"))
            {
                return;
            }

            if (action == "Reset")
            {
                await EnsureDocumentsSavedAsync(root);
            }

            LoreCommandResult result = await LoreAuthFlow.ExecuteAsync(client, root, string.Empty, () =>
            {
                if (version != _workspaceVersion)
                {
                    return LoreCommandResult.Failed("The workspace changed. Refresh and confirm the operation again.");
                }

                return action == "Archive" ? client.ArchiveBranch(root, branch.Name)
                    : action == "Reset" ? client.ResetBranch(root, branch.Name, revision, expectedTip)
                    : client.SetBranchProtection(root, branch.Name, action == "Protect");
            });
            await LoreLog.WriteCommandAsync("branch " + action.ToLowerInvariant(), result.CombinedText);
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            _service?.RefreshAllGlyphs();
            _service?.NotifyHistoryChanged();
            await ReloadAsync();
            _model.StatusText = result.Success ? action + " completed. " + result.Warning : action + " failed: " + result.CombinedText;
            if (!result.Success)
            {
                await VS.MessageBox.ShowErrorAsync("Lore", _model.StatusText);
            }
        }

        private static async Task EnsureDocumentsSavedAsync(string root)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            EnvDTE.DTE dte = await VS.GetRequiredServiceAsync<EnvDTE.DTE, EnvDTE.DTE>();
            string prefix = Path.GetFullPath(root).TrimEnd('\\') + "\\";
            foreach (EnvDTE.Document document in dte.Documents)
            {
                if (!document.Saved && document.FullName.StartsWith(prefix, StringComparison.OrdinalIgnoreCase))
                {
                    throw new InvalidOperationException("Save or close unsaved documents before resetting a branch: " + document.FullName);
                }
            }
        }

        private void OnFileDoubleClick(object sender, MouseButtonEventArgs e) => OpenDiff();
        private void OnOpenDiffClick(object sender, RoutedEventArgs e) => OpenDiff();
        private void OnFileKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Enter)
            {
                e.Handled = true;
                OpenDiff();
            }
        }

        private void OpenDiff()
        {
            if (Files.SelectedItem is LoreChangeItem file && _model.Comparison is LoreBranchComparison comparison &&
                _client != null && _model.RepositoryRoot is string root)
            {
                ILoreClient client = _client;
                Run(() => LoreHistoryDiffPresenter.ShowComparisonAsync(client, root, file, comparison.SourceRevision, comparison.TargetRevision));
            }
        }

        private void Run(Func<Task> operation)
        {
            _tasks.RunAsync(async () =>
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (_model.IsBusy)
                {
                    return;
                }

                _model.IsBusy = true;
                try
                {
                    await operation();
                }
                catch (Exception ex)
                {
                    await ex.LogAsync();
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _model.StatusText = ex.Message;
                    await VS.MessageBox.ShowErrorAsync("Lore", ex.Message);
                }
                finally
                {
                    await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                    _model.IsBusy = false;
                    if (_reloadPending)
                    {
                        _reloadPending = false;
                        Run(ReloadAsync);
                    }
                }
            }).FileAndForget("LoreVS/Branches");
        }
    }
}