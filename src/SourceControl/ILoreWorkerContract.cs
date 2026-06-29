using System.Threading;
using System.Threading.Tasks;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Cross-process contract between the in-process VSPackage (.NET Framework 4.8) and the
    /// out-of-process <c>LoreVS.Worker</c> (.NET 9/10) that hosts the native <c>LoreVcs</c> SDK.
    /// The native <c>lorelib</c> cannot be loaded into the .NET Framework Visual Studio process, so
    /// every Lore operation is marshalled over a named pipe to the worker.
    /// </summary>
    /// <remarks>
    /// This is the same logical seam as <see cref="ILoreClient"/>, but expressed asynchronously for
    /// the cross-process boundary. <see cref="LoreBrokeredClient"/> adapts it back to the synchronous
    /// <see cref="ILoreClient"/> the rest of the extension already consumes. The source file is shared
    /// (linked) into both the package and the worker so the contract can never drift between the two ends.
    /// </remarks>
    public interface ILoreWorkerContract
    {
        /// <summary>True when the native Lore SDK loaded and is usable in the worker.</summary>
        Task<bool> IsAvailableAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Returns the status of every changed file under <paramref name="repositoryRoot"/> as
        /// absolute-path / normalized-status pairs. Runs offline (no server required).
        /// </summary>
        Task<LoreStatusEntry[]> GetRepositoryStatusAsync(string repositoryRoot, CancellationToken cancellationToken);

        /// <summary>
        /// Returns the changed files and the branch/revision summary for the repository at
        /// <paramref name="repositoryRoot"/> in a single status pass. Preferred over calling
        /// <see cref="GetRepositoryStatusAsync"/> and <see cref="GetRepositoryInfoAsync"/> separately:
        /// the native SDK does not reliably tolerate two back-to-back status invocations, so the
        /// file list and the revision summary are gathered from the same scan. Runs offline.
        /// </summary>
        Task<LoreRepositorySnapshot> GetRepositorySnapshotAsync(string repositoryRoot, CancellationToken cancellationToken);

        /// <summary>
        /// Returns branch and local/remote revision information for the repository at
        /// <paramref name="repositoryRoot"/> (current branch name and how far the working
        /// branch is ahead/behind its remote). Runs offline.
        /// </summary>
        Task<LoreRepositoryInfo> GetRepositoryInfoAsync(string repositoryRoot, CancellationToken cancellationToken);
        /// <summary>Creates a repository on the server identified by <paramref name="repositoryUrl"/>.</summary>
        Task<LoreCommandResult> CreateRepositoryAsync(string workingDirectory, string repositoryUrl, string identity, CancellationToken cancellationToken);

        /// <summary>
        /// Clones the remote repository at <paramref name="repositoryUrl"/> into
        /// <paramref name="targetDirectory"/>, materializing a working tree from the server.
        /// </summary>
        Task<LoreCommandResult> CloneRepositoryAsync(string repositoryUrl, string targetDirectory, string identity, CancellationToken cancellationToken);

        /// <summary>Stages every changed file under <paramref name="workingDirectory"/> (scan).</summary>
        Task<LoreCommandResult> StageAllAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>Commits the staged revision with <paramref name="message"/>.</summary>
        Task<LoreCommandResult> CommitAsync(string workingDirectory, string message, string identity, CancellationToken cancellationToken);

        /// <summary>
        /// Stages every changed file and amends the latest revision, replacing its message with
        /// <paramref name="message"/> and folding the staged changes into it.
        /// </summary>
        Task<LoreCommandResult> AmendAsync(string workingDirectory, string message, string identity, CancellationToken cancellationToken);

        /// <summary>
        /// Commits (or amends, when <paramref name="amend"/> is set) only the supplied
        /// <paramref name="paths"/>. The staging area is reset first so the revision contains exactly
        /// the selected files regardless of what was staged before (equivalent to
        /// <c>lore unstage</c> + <c>lore stage &lt;paths&gt;</c> + <c>lore commit</c>). Enables partial
        /// commits from the Lore Changes window.
        /// </summary>
        Task<LoreCommandResult> CommitFilesAsync(string workingDirectory, string[] paths, string message, string identity, bool amend, CancellationToken cancellationToken);

        /// <summary>
        /// Returns every branch in the repository at <paramref name="repositoryRoot"/> (local and
        /// remote), with the active branch flagged. Runs offline.
        /// </summary>
        Task<LoreBranchEntry[]> ListBranchesAsync(string repositoryRoot, CancellationToken cancellationToken);

        /// <summary>
        /// Creates a branch named <paramref name="branchName"/> from the current revision. When
        /// <paramref name="checkout"/> is <see langword="true"/> the working tree is switched to the
        /// new branch (equivalent to <c>lore branch create</c> followed by a switch); otherwise the
        /// current branch is left checked out.
        /// </summary>
        Task<LoreCommandResult> CreateBranchAsync(string workingDirectory, string branchName, string identity, bool checkout, CancellationToken cancellationToken);

        /// <summary>
        /// Switches the working tree at <paramref name="workingDirectory"/> to the existing branch
        /// <paramref name="branchName"/> (equivalent to <c>lore branch switch</c>).
        /// </summary>
        Task<LoreCommandResult> SwitchBranchAsync(string workingDirectory, string branchName, CancellationToken cancellationToken);

        /// <summary>
        /// Merges the branch <paramref name="sourceBranch"/> into the current branch
        /// (equivalent to <c>lore branch merge-into</c>). When the merge completes cleanly the
        /// revision is committed automatically. When it produces conflicts the merge is left in
        /// progress, the conflicted working files are written with diff3 markers, and their absolute
        /// paths are returned so the caller can resolve them (e.g. in the IDE merge tool) and then
        /// call <see cref="ResolveMergeAsync"/>, or <see cref="AbortMergeAsync"/> to back out.
        /// </summary>
        Task<LoreMergeResult> MergeBranchAsync(string workingDirectory, string sourceBranch, string identity, CancellationToken cancellationToken);

        /// <summary>
        /// Finalizes an in-progress merge: stages the resolved content of <paramref name="paths"/>,
        /// marks those files resolved, and commits the merge revision with <paramref name="message"/>
        /// (equivalent to <c>lore file stage-merge</c> + <c>lore branch merge resolve</c> + commit).
        /// </summary>
        Task<LoreCommandResult> ResolveMergeAsync(string workingDirectory, string[] paths, string message, string identity, CancellationToken cancellationToken);

        /// <summary>
        /// Aborts an in-progress merge, restoring the working tree to its pre-merge state
        /// (equivalent to <c>lore branch merge abort</c>).
        /// </summary>
        Task<LoreCommandResult> AbortMergeAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>Pushes local commits to the remote.</summary>
        Task<LoreCommandResult> PushAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>Synchronizes the working tree to the latest remote revision.</summary>
        Task<LoreCommandResult> SyncAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>
        /// Discards working-tree changes for <paramref name="paths"/>, resetting them to the
        /// current revision (equivalent to <c>lore file reset</c>).
        /// </summary>
        Task<LoreCommandResult> ResetFilesAsync(string workingDirectory, string[] paths, CancellationToken cancellationToken);

        /// <summary>
        /// Writes the content of <paramref name="relativePath"/> as it exists at
        /// <paramref name="revision"/> (empty for the current revision) to <paramref name="outputPath"/>
        /// on disk. Used to materialize the committed version of a file for diffing.
        /// </summary>
        Task<LoreCommandResult> WriteFileAtRevisionAsync(string workingDirectory, string relativePath, string revision, string outputPath, CancellationToken cancellationToken);
    }

    /// <summary>
    /// Branch and revision summary for a Lore repository. The wire shape for
    /// <see cref="ILoreWorkerContract.GetRepositoryInfoAsync"/>; settable members keep it
    /// trivially serializable by System.Text.Json across the pipe.
    /// </summary>
    public sealed class LoreRepositoryInfo
    {
        /// <summary>Name of the current branch (e.g. <c>main</c>), or empty when unknown.</summary>
        public string BranchName { get; set; } = string.Empty;

        /// <summary>True when the branch has a remote counterpart on the server.</summary>
        public bool HasRemote { get; set; }

        /// <summary>True when the local branch has revisions the remote does not (outgoing).</summary>
        public bool IsLocalAhead { get; set; }

        /// <summary>True when the remote branch has revisions the local does not (incoming).</summary>
        public bool IsRemoteAhead { get; set; }

        /// <summary>Local revision number (the working branch tip).</summary>
        public long LocalRevisionNumber { get; set; }

        /// <summary>Remote revision number (the server branch tip), when known.</summary>
        public long RemoteRevisionNumber { get; set; }

        /// <summary>Hex hash of the local branch tip revision; the committed base for diffs.</summary>
        public string LocalRevisionHash { get; set; } = string.Empty;
    }

    /// <summary>
    /// A single branch in a Lore repository. The wire shape for
    /// <see cref="ILoreWorkerContract.ListBranchesAsync"/>; settable members keep it trivially
    /// serializable by System.Text.Json across the pipe.
    /// </summary>
    public sealed class LoreBranchEntry
    {
        /// <summary>The branch name (e.g. <c>main</c>).</summary>
        public string Name { get; set; } = string.Empty;

        /// <summary>The branch category, or empty when uncategorized.</summary>
        public string Category { get; set; } = string.Empty;

        /// <summary>True when this is the branch the working tree is currently on.</summary>
        public bool IsCurrent { get; set; }

        /// <summary>True when the branch lives on the remote rather than locally.</summary>
        public bool IsRemote { get; set; }

        /// <summary>True when the branch has been archived.</summary>
        public bool Archived { get; set; }
    }

    /// <summary>
    /// Outcome of a branch merge. The wire shape for <see cref="ILoreWorkerContract.MergeBranchAsync"/>;
    /// settable members keep it trivially serializable by System.Text.Json across the pipe.
    /// </summary>
    public sealed class LoreMergeResult
    {
        /// <summary>True when the merge completed and committed cleanly with no conflicts.</summary>
        public bool Success { get; set; }

        /// <summary>True when the merge stopped on conflicts that must be resolved.</summary>
        public bool HasConflicts { get; set; }

        /// <summary>
        /// Absolute paths of the conflicted working files (each written with diff3 markers), empty
        /// when the merge succeeded or failed outright.
        /// </summary>
        public string[] ConflictPaths { get; set; } = System.Array.Empty<string>();

        /// <summary>Error detail when the merge failed for a reason other than conflicts.</summary>
        public string ErrorMessage { get; set; } = string.Empty;
    }

    /// <summary>
    /// Combined result of a single status pass: the changed files plus the branch/revision
    /// summary. The wire shape for <see cref="ILoreWorkerContract.GetRepositorySnapshotAsync"/>;
    /// settable members keep it trivially serializable by System.Text.Json across the pipe.
    /// </summary>
    public sealed class LoreRepositorySnapshot
    {
        /// <summary>The changed files under the repository root.</summary>
        public LoreStatusEntry[] Files { get; set; } = System.Array.Empty<LoreStatusEntry>();

        /// <summary>The branch/revision summary, or <see langword="null"/> when unavailable.</summary>
        public LoreRepositoryInfo? Info { get; set; }
    }

    /// <summary>
    /// A single file's normalized source-control status. The wire shape for
    /// <see cref="ILoreWorkerContract.GetRepositoryStatusAsync"/>; settable members keep it
    /// trivially serializable by System.Text.Json across the pipe.
    /// </summary>
    public sealed class LoreStatusEntry
    {
        /// <summary>Absolute, normalized path of the file.</summary>
        public string Path { get; set; } = string.Empty;

        /// <summary>The file's normalized Lore status.</summary>
        public LoreFileStatus Status { get; set; }
    }
}
