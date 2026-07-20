using System;
using System.ComponentModel;
using System.Windows;
using System.Windows.Controls;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace LoreVS.UI
{
    /// <summary>
    /// WPF content of the Lore History tool window. Hosts the virtualized revision list, the changed
    /// files of the selected revision, and the "Load more" and diff gestures, all backed by
    /// <see cref="LoreHistoryViewModel"/>. Read-only.
    /// </summary>
    public partial class LoreHistoryControl : UserControl
    {
        /// <summary>The control currently hosted in the tool window, used by the toolbar commands.</summary>
        internal static LoreHistoryControl? Current { get; private set; }

        private bool _initialized;

        public LoreHistoryControl(LoreHistoryViewModel viewModel)
        {
            ViewModel = viewModel;
            InitializeComponent();
            DataContext = viewModel;

            viewModel.PropertyChanged += OnViewModelPropertyChanged;
            UpdateRepositoryVisibility();

            Loaded += OnLoaded;
            Unloaded += OnUnloaded;
        }

        /// <summary>The view model behind this control.</summary>
        public LoreHistoryViewModel ViewModel { get; }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            Current = this;
            ViewModel.SubscribeEvents();

            // Loaded fires again after docking, auto-hide, or a tab switch, so only run the
            // (one-time) initialization the first time. On later loads we deliberately do NOT reload
            // the log just because the window regained focus - that was needlessly disruptive. The
            // list stays put and refreshes only on an explicit Refresh, a Lore operation (via
            // RepositoryHistoryChanged), or an actual workspace change; the lightweight check below
            // still catches a solution that was swapped while the window was hidden.
            if (!_initialized)
            {
                _initialized = true;
                Run(ViewModel.InitializeAsync);
            }
            else
            {
                Run(ViewModel.ReloadIfRepositoryChangedAsync);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            ThreadHelper.ThrowIfNotOnUIThread();
            ViewModel.UnsubscribeEvents();

            if (Current == this)
            {
                Current = null;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoreHistoryViewModel.HasRepository))
            {
                UpdateRepositoryVisibility();
            }
        }

        private void UpdateRepositoryVisibility()
        {
            bool has = ViewModel.HasRepository;
            RepositoryPanel.Visibility = has ? Visibility.Visible : Visibility.Collapsed;
            NoRepositoryMessage.Visibility = has ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnLoadMoreClick(object sender, RoutedEventArgs e) => Run(ViewModel.LoadMoreAsync);

        private void OnFileDoubleClick(object sender, System.Windows.Input.MouseButtonEventArgs e)
        {
            if (FilesList.SelectedItem is not LoreChangeItem item)
            {
                return;
            }

            LoreRevisionItem? revision = ViewModel.SelectedRevision;
            ILoreClient? client = ViewModel.Client;
            string? root = ViewModel.RepositoryRoot;
            if (revision == null || client == null || string.IsNullOrEmpty(root))
            {
                return;
            }

            Run(() => LoreHistoryDiffPresenter.ShowAsync(client, root!, item, revision.RevisionHash, revision.ParentRevisionHash));
        }

        // Runs a view-model task on the UI thread's joinable task factory. FileAndForget logs any
        // fault to the activity log so a background exception never crashes the tool window.
        private static void Run(Func<System.Threading.Tasks.Task> operation)
        {
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(operation).FileAndForget("LoreVS/LoreHistory");
        }
    }
}