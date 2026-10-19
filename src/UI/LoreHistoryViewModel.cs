using System;
using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using System.Threading.Tasks;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
using Microsoft.VisualStudio.Threading;

namespace LoreVS.UI
{
    /// <summary>
    /// View model behind the Lore History tool window. Owns the paged, newest-first list of
    /// revisions and the changed files of the selected revision. Revision enumeration and per-revision
    /// change loading run on a background thread through <see cref="ILoreClient"/>; observable state is
    /// updated back on the UI thread. Read-only: nothing here mutates the repository.
    /// </summary>
    /// <remarks>
    /// The list is loaded a bounded page at a time (<see cref="PageSize"/>) so a repository with
    /// thousands of revisions does not materialize a visual element per revision. The pure state
    /// helpers (<see cref="ApplyRevisionPage"/>, <see cref="ApplyRevisionChanges"/>,
    /// <see cref="NextStartRevision"/>) contain the pagination and stale-guard logic and are unit
    /// tested directly, while the thin async wrappers handle threading and are not.
    /// </remarks>
    public sealed class LoreHistoryViewModel : INotifyPropertyChanged
    {
        /// <summary>Number of revisions fetched per page.</summary>
        public const int PageSize = 50;

        private readonly HashSet<string> _seenHashes = new HashSet<string>(StringComparer.OrdinalIgnoreCase);

        private ILoreClient? _client;
        private LoreSccService? _sccService;
        private string? _repositoryRoot;

        private bool _hasRepository;
        private bool _isBusy;
        private bool _canLoadMore;
        private string _statusText = string.Empty;
        private LoreRevisionItem? _selectedRevision;
        private string _comparisonBaseRevisionHash = string.Empty;
        private string _changesTitle = "Changed files";

        // The revision whose changes are currently being (or have been) loaded. Used to discard the
        // result of a superseded load when the user quickly selects another revision.
        private string _pendingChangesRevision = string.Empty;
        private bool _subscribed;

        /// <summary>Creates a view model that resolves its Lore binding from Visual Studio services.</summary>
        public LoreHistoryViewModel()
        {
            Revisions = new ObservableCollection<LoreRevisionItem>();
            SelectedRevisionChanges = new ObservableCollection<LoreChangeItem>();
        }

        /// <summary>
        /// Creates a view model bound to an explicit client and repository root. Used by unit tests to
        /// exercise the pagination and change-loading logic without Visual Studio services.
        /// </summary>
        public LoreHistoryViewModel(ILoreClient client, string repositoryRoot)
            : this()
        {
            _client = client;
            _repositoryRoot = repositoryRoot;
            _hasRepository = !string.IsNullOrEmpty(repositoryRoot);
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>The loaded revisions, newest first.</summary>
        public ObservableCollection<LoreRevisionItem> Revisions { get; }

        /// <summary>The changed files of <see cref="SelectedRevision"/> (loaded lazily on selection).</summary>
        public ObservableCollection<LoreChangeItem> SelectedRevisionChanges { get; }

        /// <summary>The repository root the history is loaded from, or <see langword="null"/>.</summary>
        public string? RepositoryRoot => _repositoryRoot;

        /// <summary>The client used for revision queries, or <see langword="null"/> before init.</summary>
        public ILoreClient? Client => _client;

        /// <summary>True when the open solution/folder is under Lore source control.</summary>
        public bool HasRepository
        {
            get => _hasRepository;
            private set => SetProperty(ref _hasRepository, value);
        }

        /// <summary>True while a revision page or changed-file list is loading (disables the UI).</summary>
        public bool IsBusy
        {
            get => _isBusy;
            private set
            {
                if (SetProperty(ref _isBusy, value))
                {
                    OnPropertyChanged(nameof(CanLoadMore));
                }
            }
        }

        /// <summary>True when at least one older revision remains and the UI is idle.</summary>
        public bool CanLoadMore
        {
            get => _canLoadMore && !_isBusy;
            private set => SetProperty(ref _canLoadMore, value);
        }

        /// <summary>Status/summary line shown at the bottom of the window.</summary>
        public string StatusText
        {
            get => _statusText;
            private set => SetProperty(ref _statusText, value);
        }

        /// <summary>The currently selected revision, whose changed files are shown.</summary>
        public LoreRevisionItem? SelectedRevision
        {
            get => _selectedRevision;
            private set => SetProperty(ref _selectedRevision, value);
        }

        public string ComparisonBaseRevisionHash
        {
            get => _comparisonBaseRevisionHash;
            private set => SetProperty(ref _comparisonBaseRevisionHash, value);
        }

        public string ChangesTitle
        {
            get => _changesTitle;
            private set => SetProperty(ref _changesTitle, value);
        }

        internal string SelectionRequestKey => _pendingChangesRevision;

        /// <summary>Orders a comparison by the displayed history, not by click order.</summary>
        public void ApplySelection(IEnumerable<LoreRevisionItem> selection)
        {
            LoreRevisionItem[] selected = selection.Where(Revisions.Contains).Distinct()
                .OrderBy(item => Revisions.IndexOf(item)).ToArray();
            SelectedRevision = selected.Length == 1 || selected.Length == 2 ? selected[0] : null;
            ComparisonBaseRevisionHash = selected.Length == 2 ? selected[1].RevisionHash : string.Empty;
            ChangesTitle = selected.Length == 2
                ? "Changes: " + selected[1].ShortHash + " to " + selected[0].ShortHash
                : selected.Length > 2 ? "Select at most two revisions to compare" : "Changed files";
            BeginChangesLoad(Guid.NewGuid().ToString("N"));
        }

        /// <summary>
        /// Resolves the Lore binding from Visual Studio services and loads the first page. Called once
        /// when the window is first shown.
        /// </summary>
        public async Task InitializeAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            _sccService = await VS.GetServiceAsync<LoreSccService, LoreSccService>();
            _client = _sccService?.Client;

            // SubscribeEvents may have run before the service was resolved, so attach the
            // history hook now (AttachHistoryEvent guards against a double subscribe).
            if (_subscribed)
            {
                AttachHistoryEvent();
            }

            await ReloadAsync();
        }

        /// <summary>
        /// Re-resolves the repository binding for the open solution/folder and reloads the first page,
        /// clearing any previously loaded revisions. Used on first load and whenever the workspace changes.
        /// </summary>
        public async Task ReloadAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string? solutionDir = await ResolveSolutionDirectoryAsync();
            ILoreClient? client = _client;
            _repositoryRoot = client != null && solutionDir != null
                ? await Task.Run(() => client.FindRepositoryRoot(solutionDir))
                : null;

            HasRepository = _repositoryRoot != null;

            Revisions.Clear();
            SelectedRevisionChanges.Clear();
            _seenHashes.Clear();
            _selectedRevision = null;
            ComparisonBaseRevisionHash = string.Empty;
            ChangesTitle = "Changed files";
            _pendingChangesRevision = string.Empty;
            OnPropertyChanged(nameof(SelectedRevision));

            if (!HasRepository)
            {
                CanLoadMore = false;
                StatusText = "This solution or folder is not under Lore source control.";
                return;
            }

            await LoadPageAsync(isInitial: true);
        }

        /// <summary>
        /// Re-resolves the repository binding and reloads only when the bound repository actually
        /// changed - for example a different solution was opened while the window was hidden. Used on
        /// later window loads so simply giving the window focus does not trigger a visible reload.
        /// </summary>
        public async Task ReloadIfRepositoryChangedAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            string? solutionDir = await ResolveSolutionDirectoryAsync();
            ILoreClient? client = _client;
            string? newRoot = client != null && solutionDir != null
                ? await Task.Run(() => client.FindRepositoryRoot(solutionDir))
                : null;

            if (RepositoryChanged(_repositoryRoot, newRoot))
            {
                await ReloadAsync();
            }
        }

        /// <summary>
        /// Returns whether the bound repository root changed between two resolutions, treating a
        /// null/empty root (no repository) and a path difference as a change but ignoring case. Pure
        /// so the "reload only when the repository actually changed" rule is unit tested directly.
        /// </summary>
        public static bool RepositoryChanged(string? oldRoot, string? newRoot)
        {
            return !string.Equals(oldRoot ?? string.Empty, newRoot ?? string.Empty, StringComparison.OrdinalIgnoreCase);
        }

        /// <summary>Loads and appends the next page of older revisions.</summary>
        public async Task LoadMoreAsync()
        {
            await LoadPageAsync(isInitial: false);
        }

        /// <summary>
        /// Returns the start revision for the next page: the hash of the last loaded revision (the
        /// native API repeats this inclusive start, so the worker excludes it), or an empty string to
        /// read from the branch tip.
        /// </summary>
        public string NextStartRevision()
        {
            return Revisions.Count > 0 ? Revisions[Revisions.Count - 1].RevisionHash : string.Empty;
        }

        /// <summary>
        /// Appends a page of revisions, skipping any hash already shown, and updates the load-more
        /// state. When <paramref name="isInitial"/> is true the existing list is cleared first. Pure
        /// state logic - performs no threading or Lore calls - so it is unit tested directly.
        /// </summary>
        public bool ApplyRevisionPage(LoreRevisionPage? page, bool isInitial)
        {
            if (isInitial)
            {
                Revisions.Clear();
                _seenHashes.Clear();
            }

            if (page == null)
            {
                CanLoadMore = false;
                return false;
            }

            if (page.Entries != null)
            {
                foreach (LoreRevisionEntry entry in page.Entries)
                {
                    if (entry == null || string.IsNullOrEmpty(entry.RevisionHash) || !_seenHashes.Add(entry.RevisionHash))
                    {
                        continue;
                    }

                    Revisions.Add(new LoreRevisionItem(entry));
                }
            }

            CanLoadMore = page.HasMore;
            return true;
        }

        /// <summary>
        /// Marks <paramref name="revisionHash"/> as the revision whose changes are being loaded and
        /// clears the current change list. Later results are only applied by
        /// <see cref="ApplyRevisionChanges"/> if they match this revision, discarding a superseded load.
        /// </summary>
        public void BeginChangesLoad(string revisionHash)
        {
            _pendingChangesRevision = revisionHash ?? string.Empty;
            SelectedRevisionChanges.Clear();
        }

        /// <summary>
        /// Replaces the changed-file list with <paramref name="changes"/> when they belong to the
        /// revision the user last selected; otherwise ignores them as a stale result. Returns whether
        /// the changes were applied. Pure state logic, unit tested directly.
        /// </summary>
        public bool ApplyRevisionChanges(string revisionHash, LoreRevisionChange[]? changes)
        {
            if (!string.Equals(revisionHash, _pendingChangesRevision, StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            SelectedRevisionChanges.Clear();

            if (changes != null)
            {
                string root = _repositoryRoot ?? string.Empty;
                foreach (LoreRevisionChange change in changes)
                {
                    if (change == null || string.IsNullOrEmpty(change.Path))
                    {
                        continue;
                    }

                    SelectedRevisionChanges.Add(new LoreChangeItem(change.Path, root, change.Status, change.OriginalPath));
                }
            }

            return true;
        }

        /// <summary>
        /// Subscribes to solution/folder events so the history re-resolves when the open workspace
        /// changes. Paired with <see cref="UnsubscribeEvents"/> from the hosting control's
        /// Loaded/Unloaded handlers so the long-lived static VS events never root a closed window.
        /// </summary>
        public void SubscribeEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (_subscribed)
            {
                return;
            }

            _subscribed = true;
            VS.Events.SolutionEvents.OnAfterOpenSolution += OnSolutionOpened;
            VS.Events.SolutionEvents.OnAfterCloseSolution += OnWorkspaceClosed;
            VS.Events.SolutionEvents.OnAfterOpenFolder += OnFolderChanged;
            VS.Events.SolutionEvents.OnAfterCloseFolder += OnFolderChanged;

            // The scc service is resolved lazily in InitializeAsync (which runs after the first
            // Loaded), so it may still be null here on the very first subscribe; InitializeAsync
            // attaches this hook itself once the service is available.
            AttachHistoryEvent();
        }

        /// <summary>Detaches every workspace event subscribed by <see cref="SubscribeEvents"/>.</summary>
        public void UnsubscribeEvents()
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            if (!_subscribed)
            {
                return;
            }

            _subscribed = false;
            VS.Events.SolutionEvents.OnAfterOpenSolution -= OnSolutionOpened;
            VS.Events.SolutionEvents.OnAfterCloseSolution -= OnWorkspaceClosed;
            VS.Events.SolutionEvents.OnAfterOpenFolder -= OnFolderChanged;
            VS.Events.SolutionEvents.OnAfterCloseFolder -= OnFolderChanged;

            if (_sccService != null)
            {
                _sccService.RepositoryHistoryChanged -= OnRepositoryHistoryChanged;
            }
        }

        private void OnSolutionOpened(Community.VisualStudio.Toolkit.Solution? solution) => ReloadSafe();

        private void OnFolderChanged(string folder) => ReloadSafe();

        private void OnWorkspaceClosed() => ReloadSafe();

        private void ReloadSafe()
        {
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(ReloadAsync).FileAndForget("LoreVS/LoreHistory");
        }

        // Attaches the history-changed hook to the scc service, first detaching any existing
        // handler so repeated calls (subscribe + late init) never double-subscribe.
        private void AttachHistoryEvent()
        {
            if (_sccService == null)
            {
                return;
            }

            _sccService.RepositoryHistoryChanged -= OnRepositoryHistoryChanged;
            _sccService.RepositoryHistoryChanged += OnRepositoryHistoryChanged;
        }

        // A Lore operation (commit, pull, branch change) created or moved revisions, so reload
        // the log. This is the only automatic refresh trigger besides an actual workspace change.
        private void OnRepositoryHistoryChanged(object sender, EventArgs e) => ReloadSafe();

        private async Task LoadPageAsync(bool isInitial)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            if (!HasRepository || _client == null || IsBusy)
            {
                return;
            }

            IsBusy = true;
            StatusText = isInitial ? "Loading history..." : "Loading more revisions...";

            try
            {
                ILoreClient client = _client;
                string root = _repositoryRoot!;
                string start = isInitial ? string.Empty : NextStartRevision();

                LoreRevisionPage page = await Task.Run(() => client.GetRevisionHistory(root, start, PageSize))
                    .ConfigureAwait(true);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ApplyRevisionPage(page, isInitial);
                UpdateHistoryStatus();
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                StatusText = "Could not load revision history.";
            }
            finally
            {
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                IsBusy = false;
            }
        }

        public async Task LoadSelectedChangesAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();

            LoreRevisionItem? revision = SelectedRevision;
            if (!HasRepository || _client == null || revision == null || string.IsNullOrEmpty(revision.RevisionHash))
            {
                return;
            }

            string requestKey = _pendingChangesRevision;
            string sourceHash = ComparisonBaseRevisionHash;

            try
            {
                ILoreClient client = _client;
                string root = _repositoryRoot!;
                string hash = revision.RevisionHash;

                LoreRevisionChange[] changes = await Task.Run(() =>
                {
                    if (string.IsNullOrEmpty(sourceHash))
                    {
                        return client.GetRevisionChanges(root, hash);
                    }

                    LoreRevisionComparison comparison = client.CompareRevisions(root, sourceHash, hash);
                    if (!comparison.Result.Success)
                    {
                        throw new InvalidOperationException(comparison.Result.CombinedText);
                    }

                    return comparison.Changes;
                }).ConfigureAwait(true);

                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                ApplyRevisionChanges(requestKey, changes);
            }
            catch (Exception ex)
            {
                await ex.LogAsync();
                await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
                if (requestKey == _pendingChangesRevision)
                {
                    StatusText = "Could not load changed files: " + ex.Message;
                }
            }
        }

        private void UpdateHistoryStatus()
        {
            string suffix = CanLoadMore ? "+" : string.Empty;
            StatusText = Revisions.Count == 1
                ? "1 revision" + suffix
                : Revisions.Count + " revisions" + suffix;
        }

        private static async Task<string?> ResolveSolutionDirectoryAsync()
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            IVsSolution solution = await VS.Services.GetSolutionAsync();
            return SolutionScc.GetSolutionDirectory(solution);
        }

        private bool SetProperty<T>(ref T field, T value, [CallerMemberName] string? propertyName = null)
        {
            if (EqualityComparer<T>.Default.Equals(field, value))
            {
                return false;
            }

            field = value;
            OnPropertyChanged(propertyName);
            return true;
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
