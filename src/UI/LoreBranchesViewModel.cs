using System;
using System.Collections.ObjectModel;
using System.ComponentModel;
using System.Linq;
using System.Runtime.CompilerServices;
using LoreVS.SourceControl;

namespace LoreVS.UI
{
    public sealed class LoreBranchesViewModel : INotifyPropertyChanged
    {
        private LoreBranchEntry? _selectedBranch;
        private LoreBranchEntry? _sourceBranch;
        private LoreBranchEntry? _targetBranch;
        private string _statusText = "Open a Lore solution or folder, then refresh.";
        private string _infoText = string.Empty;
        private string _comparisonText = string.Empty;
        private bool _isBusy;
        private Guid _infoRequest;
        private Guid _comparisonRequest;

        public event PropertyChangedEventHandler? PropertyChanged;
        public ObservableCollection<LoreBranchEntry> Branches { get; } = new();
        public ObservableCollection<LoreChangeItem> Changes { get; } = new();
        public string? RepositoryRoot { get; private set; }
        public LoreBranchInfo? BranchInfo { get; private set; }
        public LoreBranchComparison? Comparison { get; private set; }
        public bool CanRefresh => !IsBusy;
        public bool CanInteract => !IsBusy && RepositoryRoot != null;

        public bool IsBusy
        {
            get => _isBusy;
            set
            {
                _isBusy = value;
                Changed();
                Changed(nameof(CanRefresh));
                Changed(nameof(CanInteract));
            }
        }

        public LoreBranchEntry? SelectedBranch
        {
            get => _selectedBranch;
            set
            {
                _selectedBranch = value;
                BeginInfoLoad();
                Changed();
            }
        }

        public LoreBranchEntry? SourceBranch
        {
            get => _sourceBranch;
            set { _sourceBranch = value; BeginComparison(); Changed(); }
        }

        public LoreBranchEntry? TargetBranch
        {
            get => _targetBranch;
            set { _targetBranch = value; BeginComparison(); Changed(); }
        }

        public string StatusText { get => _statusText; set { _statusText = value; Changed(); } }
        public string InfoText { get => _infoText; private set { _infoText = value; Changed(); } }
        public string ComparisonText { get => _comparisonText; private set { _comparisonText = value; Changed(); } }

        public void ApplyBranches(string? root, LoreBranchEntry[] branches)
        {
            RepositoryRoot = root;
            Changed(nameof(RepositoryRoot));
            Branches.Clear();
            foreach (LoreBranchEntry branch in branches.Where(branch => !branch.Archived)
                .OrderBy(branch => branch.IsRemote).GroupBy(branch => branch.Name, StringComparer.Ordinal)
                .Select(group => group.First()).OrderBy(branch => branch.Name, StringComparer.OrdinalIgnoreCase))
            {
                Branches.Add(branch);
            }

            SelectedBranch = Branches.FirstOrDefault(branch => branch.IsCurrent) ?? Branches.FirstOrDefault();
            SourceBranch = SelectedBranch;
            TargetBranch = Branches.FirstOrDefault(branch => branch != SourceBranch) ?? SourceBranch;
            Changed(nameof(CanInteract));
        }

        public Guid BeginInfoLoad()
        {
            _infoRequest = Guid.NewGuid();
            BranchInfo = null;
            InfoText = string.Empty;
            return _infoRequest;
        }

        public bool ApplyInfo(Guid request, LoreBranchInfo info)
        {
            if (request != _infoRequest)
            {
                return false;
            }

            BranchInfo = info.Result.Success ? info : null;
            InfoText = info.Result.Success
                ? $"Branch: {info.Name}\nId: {info.Id}\nCategory: {info.Category}\nCreator: {info.Creator}\n" +
                  $"Created (Unix milliseconds): {info.Created}\nLocal tip: {info.LatestRevision}\nRemote tip: {info.RemoteRevision}\n" +
                  $"Parent branch: {info.ParentBranchId}\nBranch point: {info.BranchPoint}\nArchived: {info.Archived}\n" +
                  "Stack:\n" + string.Join("\n", info.Stack.Select(point => point.BranchId + " @ " + point.Revision))
                : info.Result.CombinedText;
            return true;
        }

        public Guid BeginComparison()
        {
            _comparisonRequest = Guid.NewGuid();
            Comparison = null;
            Changes.Clear();
            ComparisonText = string.Empty;
            return _comparisonRequest;
        }

        public bool ApplyComparison(Guid request, LoreBranchComparison comparison)
        {
            if (request != _comparisonRequest)
            {
                return false;
            }

            Changes.Clear();
            Comparison = comparison.Result.Success ? comparison : null;
            if (!comparison.Result.Success)
            {
                ComparisonText = comparison.Result.CombinedText;
                return true;
            }

            foreach (LoreRevisionChange change in comparison.Changes)
            {
                Changes.Add(new LoreChangeItem(change.Path, RepositoryRoot ?? string.Empty, change.Status, change.OriginalPath));
            }

            ComparisonText = $"Snapshot differences: {Changes.Count}. Left: {comparison.SourceRevision}. Right: {comparison.TargetRevision}.\n" +
                $"Merge base: {comparison.BaseRevision}. Potential merge conflicts: {comparison.ConflictPaths.Length}.";
            if (comparison.ConflictPaths.Length > 0)
            {
                ComparisonText += "\n" + string.Join("\n", comparison.ConflictPaths);
            }

            return true;
        }

        private void Changed([CallerMemberName] string? name = null) =>
            PropertyChanged?.Invoke(this, new PropertyChangedEventArgs(name));
    }
}