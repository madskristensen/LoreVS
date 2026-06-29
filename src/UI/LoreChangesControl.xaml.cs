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
        internal static LoreChangesControl? Current { get; private set; }

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

        private void OnChangeDoubleClick(object sender, MouseButtonEventArgs e)
        {
            if (ChangesList.SelectedItem is LoreTreeNode node)
            {
                if (node.IsFolder)
                {
                    ViewModel.ToggleFolder(node);
                }
                else if (node.File != null)
                {
                    Run(() => ViewModel.ShowDiffAsync(node.File));
                }
            }
        }

        private void OnExpanderClick(object sender, MouseButtonEventArgs e)
        {
            if (sender is FrameworkElement element && element.DataContext is LoreTreeNode node)
            {
                ViewModel.ToggleFolder(node);
                e.Handled = true;
            }
        }

        // "Open Diff" only makes sense for file leaves; hide it for folder rows and empty space
        // since the context menu is shared across every row in the list.
        private void OnContextMenuOpening(object sender, ContextMenuEventArgs e)
        {
            bool isFile = ChangesList.SelectedItem is LoreTreeNode node && node.File != null;
            OpenDiffMenuItem.Visibility = isFile ? Visibility.Visible : Visibility.Collapsed;
        }

        private void OnOpenDiffMenu(object sender, RoutedEventArgs e)
        {
            if (ChangesList.SelectedItem is LoreTreeNode node && node.File != null)
            {
                Run(() => ViewModel.ShowDiffAsync(node.File));
            }
        }

        private void OnOpenFileMenu(object sender, RoutedEventArgs e)
        {
            if (ChangesList.SelectedItem is LoreTreeNode node && node.File != null)
            {
                Run(() => VS.Documents.OpenAsync(node.File.FullPath));
            }
        }

        private void OnDiscardMenu(object sender, RoutedEventArgs e)
        {
            IReadOnlyList<LoreChangeItem> items = ChangesList.SelectedItems
                .Cast<LoreTreeNode>()
                .Where(n => n.File != null)
                .Select(n => n.File!)
                .ToList();
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
