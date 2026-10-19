using System.Collections.Generic;
using System.Collections.ObjectModel;
using System.Collections.Specialized;
using System.ComponentModel;
using System.Linq;
using System.Windows;
using System.Windows.Controls;
using System.Windows.Input;
using System.Windows.Media;
using Community.VisualStudio.Toolkit;
using LoreVS.SourceControl;
using Microsoft.VisualStudio;
using Microsoft.VisualStudio.Shell;
using Microsoft.VisualStudio.Shell.Interop;
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
        internal static LoreChangesControl? Current { get; private set; }

        internal static LoreChangesControl? ContextMenuOwner { get; private set; }
        internal LoreChangeItem? ContextFile { get; private set; }
        internal string[] ContextPaths { get; private set; } = System.Array.Empty<string>();
        internal IReadOnlyList<LoreChangeItem> ContextDiscardItems { get; private set; } = System.Array.Empty<LoreChangeItem>();
        private LoreTreeNode? _contextNode;
        private LoreFileLock? _contextLock;

        // The TreeView is bound to the repository-root nodes; their Children carry the full subtree so
        // the control renders the hierarchy natively. The view model still exposes a flattened Nodes
        // collection, so the roots are re-derived (those without a parent) whenever it changes.
        private readonly ObservableCollection<LoreTreeNode> _rootNodes = new ObservableCollection<LoreTreeNode>();

        private bool _initialized;

        public LoreChangesControl(LoreChangesViewModel viewModel)
        {
            ViewModel = viewModel;
            InitializeComponent();
            DataContext = viewModel;

            ChangesTree.ItemsSource = _rootNodes;
            viewModel.Nodes.CollectionChanged += OnNodesChanged;
            SyncRootNodes();

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
            ViewModel.SubscribeEvents();

            // Loaded can fire again after docking or auto-hide, so only kick off the (one-time)
            // initialization the first time. On later loads, re-resolve the binding to catch any
            // workspace change that happened while the window was unloaded (events were detached).
            if (!_initialized)
            {
                _initialized = true;
                Run(ViewModel.InitializeAsync);
            }
            else
            {
                Run(ViewModel.ReloadAsync);
            }
        }

        private void OnUnloaded(object sender, RoutedEventArgs e)
        {
            // Detach the long-lived static VS events so a docked-away/closed window is not rooted
            // (and re-subscribed) - otherwise every reopen would leak this control and fire stale
            // refreshes. Re-subscribed on the next Loaded.
            ViewModel.UnsubscribeEvents();

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

        /// <summary>
        /// Shows the branch switch/create/merge menu, anchored to the top-left of the control
        /// (just below the tool window toolbar). Invoked by the toolbar branch dropdown command.
        /// </summary>
        internal System.Threading.Tasks.Task ShowBranchMenuAsync() => ShowBranchMenuAsync(this);

        private async System.Threading.Tasks.Task ShowBranchMenuAsync(UIElement placementTarget)
        {
            // Prefer the cached branch list so the menu opens instantly; fall back to a worker
            // round-trip only if the cache hasn't been warmed yet, and refresh it in the background.
            IReadOnlyList<LoreVS.SourceControl.LoreBranchEntry> branches = ViewModel.CachedBranches;
            if (branches.Count == 0)
            {
                branches = await ViewModel.GetBranchesAsync();
            }
            else
            {
                ViewModel.RefreshBranchCacheAsync().FileAndForget("LoreVS/RefreshBranchCache");
            }

            // Only local branches can be switched to or merged; remote-only branches are informational.
            List<LoreVS.SourceControl.LoreBranchEntry> local = branches
                .Where(b => !b.IsRemote)
                .ToList();

            // Anchor at the control's top-left corner, just below the toolbar where the branch
            // button lives. (Bottom placement would push the menu off the bottom of the window.)
            var menu = new ContextMenu
            {
                PlacementTarget = placementTarget,
                Placement = System.Windows.Controls.Primitives.PlacementMode.Relative,
                HorizontalOffset = 0,
                VerticalOffset = 0,
            };

            // Re-template the popup against VS environment colors so it matches the IDE chrome
            // (the toolkit's UseVsTheme only recolors a few brushes and still looks like plain WPF).
            ThemedContextMenuHelper.ApplyVsTheme(menu);

            foreach (LoreVS.SourceControl.LoreBranchEntry branch in local)
            {
                string name = branch.Name;
                var item = new MenuItem
                {
                    Header = branch.IsCurrent ? name + "  (current)" : name,
                    IsEnabled = !branch.IsCurrent,
                };
                if (!branch.IsCurrent)
                {
                    item.Click += (s, ev) => Run(() => ViewModel.SwitchBranchAsync(name));
                }

                menu.Items.Add(item);
            }

            if (local.Count > 0)
            {
                menu.Items.Add(new Separator());
            }

            var newBranch = new MenuItem
            {
                Header = "New Branch...",
                Icon = new Microsoft.VisualStudio.Imaging.CrispImage
                {
                    Moniker = Microsoft.VisualStudio.Imaging.KnownMonikers.NewBranch,
                    Width = 16,
                    Height = 16,
                },
            };
            newBranch.Click += (s, ev) => Run(ViewModel.CreateBranchAsync);
            menu.Items.Add(newBranch);

            var merge = new MenuItem { Header = "Merge Branch into Current..." };
            foreach (LoreVS.SourceControl.LoreBranchEntry branch in local.Where(b => !b.IsCurrent))
            {
                string name = branch.Name;
                var mi = new MenuItem { Header = name };
                mi.Click += (s, ev) => Run(() => ViewModel.MergeBranchAsync(name));
                merge.Items.Add(mi);
            }

            merge.IsEnabled = merge.Items.Count > 0;
            menu.Items.Add(merge);

            // Defer opening until the toolbar command click has fully unwound; opening synchronously
            // lets VS hand focus back to the window content (the commit box) and dismiss the popup.
            Dispatcher.BeginInvoke(
                new System.Action(() => menu.IsOpen = true),
                System.Windows.Threading.DispatcherPriority.Background);
        }

        private void OnNodesChanged(object sender, NotifyCollectionChangedEventArgs e) => SyncRootNodes();

        // Rebuild the bound roots from the flattened view-model collection. The same node instances are
        // reused, so their expand/checked state (and the TreeView's selection) survive a refresh.
        private void SyncRootNodes()
        {
            _rootNodes.Clear();
            foreach (LoreTreeNode node in ViewModel.Nodes)
            {
                if (node.Parent == null)
                {
                    _rootNodes.Add(node);
                }
            }
        }

        private void OnChangeDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ChangesTree.SelectedItem is LoreTreeNode node)
            {
                if (node.IsFolder)
                {
                    node.IsExpanded = !node.IsExpanded;
                }
                else if (node.File != null)
                {
                    Run(() => ViewModel.ShowDiffAsync(node.File));
                }
            }
        }

        // A right-click does not select the row in a WPF TreeView, so select the item under the cursor
        // before the context menu opens to keep the menu actions targeting the clicked row.
        private void OnTreeRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _contextNode = null;
            _contextLock = null;
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && !(source is TreeViewItem))
            {
                source = VisualTreeHelper.GetParent(source);
            }

            if (source is TreeViewItem item)
            {
                item.IsSelected = true;
                item.Focus();
                _contextNode = item.DataContext as LoreTreeNode;
            }
        }

        private void OnLockRightButtonDown(object sender, MouseButtonEventArgs e)
        {
            _contextNode = null;
            _contextLock = null;
            DependencyObject? source = e.OriginalSource as DependencyObject;
            while (source != null && !(source is ListBoxItem))
            {
                source = VisualTreeHelper.GetParent(source);
            }
            if (source is ListBoxItem item)
            {
                item.IsSelected = true;
                item.Focus();
                _contextLock = item.DataContext as LoreFileLock;
            }
        }

        private void OnContextRightButtonUp(object sender, MouseButtonEventArgs e)
        {
            Point point = PointToScreen(e.GetPosition(this));
            Run(() => ShowContextMenuAsync(point));
            e.Handled = true;
        }

        private void OnContextKeyDown(object sender, KeyEventArgs e)
        {
            if (e.Key == Key.Apps || (e.Key == Key.F10 && Keyboard.Modifiers == ModifierKeys.Shift))
            {
                _contextNode = sender == ChangesTree ? ChangesTree.SelectedItem as LoreTreeNode : null;
                _contextLock = sender == LockedFilesList ? LockedFilesList.SelectedItem as LoreFileLock : null;
                var element = Keyboard.FocusedElement as FrameworkElement ?? (FrameworkElement)sender;
                Point point = element.PointToScreen(new Point(0, element.ActualHeight));
                Run(() => ShowContextMenuAsync(point));
                e.Handled = true;
            }
        }

        private async System.Threading.Tasks.Task ShowContextMenuAsync(Point point)
        {
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ContextFile = _contextNode?.File;
            ContextPaths = ContextFile != null ? new[] { ContextFile.FullPath }
                : _contextLock != null ? new[] { _contextLock.Path } : System.Array.Empty<string>();
            ContextDiscardItems = _contextLock == null ? GetCheckedFiles() : System.Array.Empty<LoreChangeItem>();
            if (ContextDiscardItems.Count == 0 && _contextNode != null)
            {
                ContextDiscardItems = _contextNode.EnumerateFileLeaves().Select(node => node.File!).ToArray();
            }
            IVsUIShell shell = await VS.GetRequiredServiceAsync<SVsUIShell, IVsUIShell>();
            await ThreadHelper.JoinableTaskFactory.SwitchToMainThreadAsync();
            ContextMenuOwner = this;
            try
            {
                ErrorHandler.ThrowOnFailure(shell.UpdateCommandUI(1));
                System.Guid guid = PackageGuids.LoreVS;
                var points = new[] { new POINTS { x = (short)point.X, y = (short)point.Y } };
                ErrorHandler.ThrowOnFailure(shell.ShowContextMenu(0, ref guid, PackageIds.LoreChangesContextMenu, points, null));
            }
            finally
            {
                ContextMenuOwner = null;
            }
        }

        private IReadOnlyList<LoreChangeItem> GetCheckedFiles() =>
            _rootNodes
                .SelectMany(n => n.EnumerateFileLeaves())
                .Where(n => n.IsChecked == true && n.File != null)
                .Select(n => n.File!)
                .ToList();

        // The inline (hover-revealed) discard button on a file row: same effect as "Discard Changes..."
        // in the context menu, but scoped to that single file.
        private void OnUndoFileClick(object sender, RoutedEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is LoreTreeNode node && node.File != null)
            {
                Run(() => ViewModel.DiscardAsync(new[] { node.File! }));
                e.Handled = true;
            }
        }

        private static void Run(System.Func<System.Threading.Tasks.Task> operation)
        {
            JoinableTaskFactory jtf = ThreadHelper.JoinableTaskFactory;
            jtf.RunAsync(operation).FileAndForget("LoreVS/LoreChanges");
        }
    }
}
