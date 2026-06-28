using System.Collections.Generic;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using Community.VisualStudio.Toolkit;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Threading;

namespace LoreVS.UI
{
    /// <summary>
    /// WPF content of the Lore Changes tool window. Hosts the commit box, the list of changed
    /// files, and the commit/discard/diff gestures, all backed by <see cref="LoreChangesViewModel"/>.
    /// </summary>
    public partial class LoreChangesControl : UserControl
    {
        /// <summary>The control currently hosted in the tool window, used by the toolbar commands.</summary>
        internal static LoreChangesControl Current { get; private set; }

        private bool _initialized;

        public LoreChangesControl(LoreChangesViewModel viewModel)
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
        public LoreChangesViewModel ViewModel { get; }

        private void OnLoaded(object sender, RoutedEventArgs e)
        {
            Current = this;

            // Loaded can fire again after docking or auto-hide, so only kick off the (one-time)
            // initialization the first time. Later workspace changes are handled by the view model.
            if (!_initialized)
            {
                _initialized = true;
                Run(ViewModel.InitializeAsync);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            if (Current == this)
            {
                Current = null;
            }
        }

        private void OnViewModelPropertyChanged(object sender, PropertyChangedEventArgs e)
        {
            if (e.PropertyName == nameof(LoreChangesViewModel.HasRepository))
            {
                UpdateRepositoryVisibility();
            }
        }

        private void UpdateRepositoryVisibility()
        {
            bool hasRepo = ViewModel.HasRepository;
            RepositoryPanel.Visibility = hasRepo ? Visibility.Visible : Visibility.Collapsed;
            NoRepositoryMessage.Visibility = hasRepo ? Visibility.Collapsed : Visibility.Visible;
        }

        private void OnCommitClick(object sender, RoutedEventArgs e) =>
            Run(() => ViewModel.CommitAsync(push: false));

        private void OnCommitAndPushClick(object sender, RoutedEventArgs e) =>
            Run(() => ViewModel.CommitAsync(push: true));

        private void OnChangeDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ChangesList.SelectedItem is LoreChangeItem item)
            {
                Run(() => ViewModel.ShowDiffAsync(item));
            }
        }

        private void OnOpenDiffMenu(object sender, RoutedEventArgs e)
        {
            if (ChangesList.SelectedItem is LoreChangeItem item)
            {
                Run(() => ViewModel.ShowDiffAsync(item));
            }
        }

        private void OnOpenFileMenu(object sender, RoutedEventArgs e)
        {
            if (ChangesList.SelectedItem is LoreChangeItem item)
            {
                Run(() => VS.Documents.OpenAsync(item.FullPath));
            }
        }

        private void OnDiscardMenu(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<LoreChangeItem> items = ChangesList.SelectedItems.Cast<LoreChangeItem>().ToList();
            if (items.Count > 0)
            {
                Run(() => ViewModel.DiscardAsync(items));
            }
        }

        private static void Run(System.Func<System.Threading.Tasks.Task> operation)
        {
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(operation).FileAndForget("LoreVS/LoreChanges");
        }
    }
}
