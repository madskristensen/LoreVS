using System;
using System.Collections.Generic;
using System.ComponentModel;
using System.Runtime.CompilerServices;
using System.Windows;
using Microsoft.VisualStudio.Imaging;
using Microsoft.VisualStudio.Imaging.Interop;

namespace LoreVS.UI
{
    /// <summary>
    /// A single row in the Lore Changes tree. A node is either a folder (a grouping segment of the
    /// path back up to the repository root) or a file leaf wrapping a <see cref="LoreChangeItem"/>.
    /// The tree is rendered as a flattened, indentation-aware list so the existing themed
    /// <c>ListBox</c> keeps multi-select while still showing a folder hierarchy.
    /// </summary>
    public sealed class LoreTreeNode : INotifyPropertyChanged
    {
        private const double IndentPerLevel = 16d;

        private bool _isExpanded = true;
        private bool _isChecked = true;
        private ImageMoniker _fileIcon;

        public LoreTreeNode(string name, bool isFolder)
        {
            Name = name;
            IsFolder = isFolder;
        }

        public event PropertyChangedEventHandler? PropertyChanged;

        /// <summary>
        /// Raised when a file leaf's <see cref="IsChecked"/> state changes (whether toggled directly
        /// or via a folder/select-all gesture). Folders never raise it themselves; they propagate the
        /// change down to their file leaves, which each raise it. The Lore Changes view model uses it
        /// to recompute the selected-file count and the excluded-paths set.
        /// </summary>
        public event EventHandler? CheckedChanged;

        /// <summary>The parent node, or null for root-level nodes. Set while the tree is built.</summary>
        public LoreTreeNode? Parent { get; set; }

        /// <summary>Display name: the folder segment, or the file name for a leaf.</summary>
        public string Name { get; }

        /// <summary>True for a folder grouping node, false for a file leaf.</summary>
        public bool IsFolder { get; }

        /// <summary>The changed file behind a leaf node; null for folders.</summary>
        public LoreChangeItem? File { get; set; }

        /// <summary>Child nodes (folders first, then files); empty for leaves.</summary>
        public List<LoreTreeNode> Children { get; } = new List<LoreTreeNode>();

        /// <summary>Nesting depth from the repository root (root-level nodes are 0).</summary>
        public int Depth { get; set; }

        /// <summary>Whether a folder's children are shown. Files are always considered expanded.</summary>
        public bool IsExpanded
        {
            get => _isExpanded;
            set
            {
                if (_isExpanded != value)
                {
                    _isExpanded = value;
                    OnPropertyChanged();
                    OnPropertyChanged(nameof(ExpanderGlyph));
                    OnPropertyChanged(nameof(Icon));
                }
            }
        }

        /// <summary>Left indentation for this row, growing one step per nesting level.</summary>
        public Thickness RowMargin => new Thickness(Depth * IndentPerLevel, 0, 0, 0);

        /// <summary>
        /// Whether this row is selected to be included in the next commit. For a file leaf it is a
        /// plain checked/unchecked flag. For a folder it is the aggregate of its descendant files:
        /// <see langword="true"/> when all are checked, <see langword="false"/> when none are, and
        /// <see langword="null"/> (indeterminate) when mixed. Setting a folder applies the value to
        /// every descendant file.
        /// </summary>
        public bool? IsChecked
        {
            get
            {
                if (!IsFolder)
                {
                    return _isChecked;
                }

                bool? state = null;
                bool first = true;
                foreach (LoreTreeNode leaf in EnumerateFileLeaves())
                {
                    if (first)
                    {
                        state = leaf._isChecked;
                        first = false;
                    }
                    else if (leaf._isChecked != state)
                    {
                        return null;
                    }
                }

                return state;
            }
            set
            {
                bool target = value ?? false;
                if (IsFolder)
                {
                    foreach (LoreTreeNode leaf in EnumerateFileLeaves())
                    {
                        leaf.SetCheckedInternal(target);
                    }
                }
                else
                {
                    SetCheckedInternal(target);
                }
            }
        }

        /// <summary>
        /// Sets the initial checked state of a file leaf without raising <see cref="CheckedChanged"/>,
        /// used when (re)building the tree so applying the persisted selection does not feed back into
        /// the view model's change handler.
        /// </summary>
        public void InitializeChecked(bool isChecked) => _isChecked = isChecked;

        /// <summary>Enumerates every file leaf at or beneath this node (the node itself when it is a leaf).</summary>
        public IEnumerable<LoreTreeNode> EnumerateFileLeaves()
        {
            if (!IsFolder)
            {
                if (File != null)
                {
                    yield return this;
                }

                yield break;
            }

            foreach (LoreTreeNode child in Children)
            {
                foreach (LoreTreeNode leaf in child.EnumerateFileLeaves())
                {
                    yield return leaf;
                }
            }
        }

        private void SetCheckedInternal(bool value)
        {
            if (_isChecked == value)
            {
                return;
            }

            _isChecked = value;
            OnPropertyChanged(nameof(IsChecked));
            CheckedChanged?.Invoke(this, EventArgs.Empty);

            // The aggregate checkbox of every ancestor folder may now read differently.
            for (LoreTreeNode? ancestor = Parent; ancestor != null; ancestor = ancestor.Parent)
            {
                ancestor.OnPropertyChanged(nameof(IsChecked));
            }
        }

        /// <summary>The expand/collapse chevron for folders; empty for files.</summary>
        public string ExpanderGlyph => !IsFolder ? string.Empty : (IsExpanded ? "\u25BE" : "\u25B8");

        /// <summary>Hidden (but space-reserving) for files so file names line up under their folder.</summary>
        public Visibility ExpanderVisibility => IsFolder ? Visibility.Visible : Visibility.Hidden;

        /// <summary>
        /// The icon shown to the left of the name: an open/closed folder for folders, or the
        /// shell's file-type icon (resolved via <c>IVsImageService2</c>) for file leaves. Reading
        /// this touches the VS imaging assemblies, so it is only evaluated when the row renders.
        /// </summary>
        public ImageMoniker Icon
        {
            get
            {
                if (IsFolder)
                {
                    return IsExpanded ? KnownMonikers.FolderOpened : KnownMonikers.FolderClosed;
                }

                return _fileIcon.Guid == Guid.Empty && _fileIcon.Id == 0
                    ? KnownMonikers.Document
                    : _fileIcon;
            }
        }

        /// <summary>Single-letter status badge for a file leaf; empty for folders.</summary>
        public string StatusBadge => File?.StatusBadge ?? string.Empty;

        /// <summary>
        /// True when this row is a deleted file leaf. Drives the strikethrough styling so deleted
        /// files read as removed at a glance.
        /// </summary>
        public bool IsDeleted => File?.Status == LoreVS.SourceControl.LoreFileStatus.Deleted;

        /// <summary>Human-readable status for the tooltip; empty for folders.</summary>
        public string StatusText => File?.StatusText ?? string.Empty;

        /// <summary>Absolute path for a file leaf; null for folders.</summary>
        public string? FullPath => File?.FullPath;

        /// <summary>Row tooltip: the relative path for files, the folder name for folders.</summary>
        public string? RowToolTip => IsFolder ? Name : File?.RelativePath;

        /// <summary>
        /// Builds the folder tree (roots back to the repository root) from a flat set of changed
        /// files. Folders are created for each path segment, files become leaves, and every level is
        /// sorted folders-first then alphabetically. Returns the root-level nodes.
        /// </summary>
        /// <param name="items">The changed files to arrange.</param>
        /// <param name="fileIconResolver">
        /// Optional callback that maps a file's absolute path to its shell icon moniker (typically
        /// <c>IVsImageService2.GetImageMonikerForFile</c>). When null, file leaves use a generic
        /// document icon.
        /// </param>
        public static List<LoreTreeNode> BuildTree(IEnumerable<LoreChangeItem> items, Func<string, ImageMoniker>? fileIconResolver = null)
        {
            var root = new LoreTreeNode(string.Empty, isFolder: true);
            var folderCache = new Dictionary<string, LoreTreeNode>(StringComparer.OrdinalIgnoreCase)
            {
                [string.Empty] = root,
            };

            if (items != null)
            {
                foreach (LoreChangeItem item in items)
                {
                    LoreTreeNode parent = EnsureFolder(root, folderCache, item.Directory ?? string.Empty);
                    var leaf = new LoreTreeNode(item.FileName, isFolder: false) { File = item };
                    if (fileIconResolver != null)
                    {
                        leaf._fileIcon = fileIconResolver(item.FullPath);
                    }

                    parent.Children.Add(leaf);
                }
            }

            SortAndAssignDepth(root, -1);
            return root.Children;
        }

        /// <summary>
        /// Walks <paramref name="roots"/> in display order, including a folder's children only when
        /// the folder is expanded, and appends each visible node to <paramref name="output"/>.
        /// </summary>
        public static void Flatten(IEnumerable<LoreTreeNode> roots, IList<LoreTreeNode> output)
        {
            if (roots == null)
            {
                return;
            }

            foreach (LoreTreeNode node in roots)
            {
                output.Add(node);
                if (node.IsFolder && node.IsExpanded)
                {
                    Flatten(node.Children, output);
                }
            }
        }

        private static LoreTreeNode EnsureFolder(LoreTreeNode root, Dictionary<string, LoreTreeNode> cache, string directory)
        {
            if (string.IsNullOrEmpty(directory))
            {
                return root;
            }

            if (cache.TryGetValue(directory, out LoreTreeNode cached))
            {
                return cached;
            }

            string[] parts = directory.Split(new[] { '\\', '/' }, StringSplitOptions.RemoveEmptyEntries);
            LoreTreeNode current = root;
            string path = string.Empty;
            foreach (string part in parts)
            {
                path = path.Length == 0 ? part : path + "\\" + part;
                if (!cache.TryGetValue(path, out LoreTreeNode next))
                {
                    next = new LoreTreeNode(part, isFolder: true);
                    current.Children.Add(next);
                    cache[path] = next;
                }

                current = next;
            }

            return current;
        }

        private static void SortAndAssignDepth(LoreTreeNode node, int depth)
        {
            node.Depth = depth;
            node.Children.Sort(CompareNodes);
            foreach (LoreTreeNode child in node.Children)
            {
                child.Parent = depth < 0 ? null : node;
                SortAndAssignDepth(child, depth + 1);
            }
        }

        private static int CompareNodes(LoreTreeNode a, LoreTreeNode b)
        {
            if (a.IsFolder != b.IsFolder)
            {
                return a.IsFolder ? -1 : 1;
            }

            return string.Compare(a.Name, b.Name, StringComparison.OrdinalIgnoreCase);
        }

        private void OnPropertyChanged([CallerMemberName] string? propertyName = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(propertyName));
    }
}
