using System.Collections.Generic;

namespace LoreVS.SourceControl
{
    /// <summary>
    /// Abstraction over the Lore version control system used by the in-process
    /// VSPackage. Provides repository discovery, file status, and the write
    /// operations (create/stage/commit/push/sync) needed by the SCC provider.
    /// </summary>
    /// <remarks>
    /// The extension ships <see cref="LoreBrokeredClient"/>, which runs the native
    /// <c>LoreVcs</c> .NET SDK in an out-of-process worker and talks to it over JSON-RPC.
    /// </remarks>
    public interface ILoreClient
    {
        /// <summary>
        /// True when the Lore backend can be reached (the worker launched and the native
        /// SDK loaded). Used to decide whether write commands are offered.
        /// </summary>
        bool IsAvailable { get; }

        /// <summary>
        /// Returns the root of the Lore repository that contains <paramref name="path"/>,
        /// walking up the directory tree, or <see langword="null"/> if none is found.
        /// </summary>
        string? FindRepositoryRoot(string path);

        /// <summary>
        /// Returns the source control status of a single file. Implementations should
        /// be resilient: when Lore is unavailable or the path is unknown they must
        /// return <see cref="LoreFileStatus.NotControlled"/> rather than throw.
        /// </summary>
        LoreFileStatus GetStatus(string filePath);

        /// <summary>
        /// Returns the status for every changed file under <paramref name="repositoryRoot"/>,
        /// keyed by absolute file path. Used to populate/refresh the in-memory status cache.
        /// </summary>
        IReadOnlyDictionary<string, LoreFileStatus> GetRepositoryStatus(string repositoryRoot);

        /// <summary>
        /// Returns the changed files and branch/revision summary for the repository at
        /// <paramref name="repositoryRoot"/> in a single status pass. Preferred over calling
        /// <see cref="GetRepositoryStatus"/> and <see cref="GetRepositoryInfo"/> separately, which
        /// the native SDK does not reliably tolerate back-to-back.
        /// </summary>
        LoreRepositorySnapshot GetRepositorySnapshot(string repositoryRoot);

        /// <summary>
        /// Returns branch and local/remote revision information (current branch name and how far
        /// the branch is ahead/behind its remote) for the repository at <paramref name="repositoryRoot"/>,
        /// or <see langword="null"/> when it cannot be determined.
        /// </summary>
        LoreRepositoryInfo? GetRepositoryInfo(string repositoryRoot);

        /// <summary>
        /// Returns every branch in the repository at <paramref name="repositoryRoot"/> (local and
        /// remote), with the active branch flagged.
        /// </summary>
        LoreBranchEntry[] ListBranches(string repositoryRoot);

        /// <summary>
        /// Creates a branch named <paramref name="branchName"/> from the current revision. When
        /// <paramref name="checkout"/> is <see langword="true"/> the working tree is switched to the
        /// new branch; otherwise the current branch is left checked out.
        /// </summary>
        LoreCommandResult CreateBranch(string workingDirectory, string branchName, string identity, bool checkout);

        /// <summary>
        /// Switches the working tree at <paramref name="workingDirectory"/> to the existing branch
        /// <paramref name="branchName"/>.
        /// </summary>
        LoreCommandResult SwitchBranch(string workingDirectory, string branchName);

        /// <summary>
        /// Merges the branch <paramref name="sourceBranch"/> into the current branch. Commits
        /// automatically on a clean merge; on conflicts the merge is left in progress and the
        /// conflicted file paths are returned for resolution via <see cref="ResolveMerge"/>
        /// (or <see cref="AbortMerge"/> to back out).
        /// </summary>
        LoreMergeResult MergeBranch(string workingDirectory, string sourceBranch, string identity);

        /// <summary>
        /// Finalizes an in-progress merge: stages the resolved <paramref name="paths"/>, marks them
        /// resolved, and commits the merge revision with <paramref name="message"/>.
        /// </summary>
        LoreCommandResult ResolveMerge(string workingDirectory, string[] paths, string message, string identity);

        /// <summary>Aborts an in-progress merge, restoring the working tree to its pre-merge state.</summary>
        LoreCommandResult AbortMerge(string workingDirectory);

        /// <summary>
        /// Onboards <paramref name="workingDirectory"/> to Lore by creating a repository on
        /// the server identified by <paramref name="repositoryUrl"/>. The existing files in
        /// the directory are preserved and a <c>.lore</c> working tree is laid down alongside.
        /// </summary>
        LoreCommandResult CreateRepository(string workingDirectory, string repositoryUrl, string identity);

        /// <summary>
        /// Clones the remote repository at <paramref name="repositoryUrl"/> into
        /// <paramref name="targetDirectory"/>, creating a working tree bound to that server.
        /// </summary>
        LoreCommandResult CloneRepository(string repositoryUrl, string targetDirectory, string identity);

        /// <summary>
        /// Stages every modified/added/deleted file under <paramref name="workingDirectory"/>
        /// (equivalent to <c>lore stage --scan</c>).
        /// </summary>
        LoreCommandResult StageAll(string workingDirectory);

        /// <summary>Commits the staged revision with <paramref name="message"/>.</summary>
        LoreCommandResult Commit(string workingDirectory, string message, string identity);

        /// <summary>
        /// Stages every changed file and amends the latest revision, replacing its message with
        /// <paramref name="message"/> and folding the staged changes into it.
        /// </summary>
        LoreCommandResult Amend(string workingDirectory, string message, string identity);

        /// <summary>
        /// Commits (or amends, when <paramref name="amend"/> is set) only the supplied
        /// <paramref name="paths"/>. The staging area is reset first so the revision contains exactly
        /// the selected files, enabling partial commits from the Lore Changes window.
        /// </summary>
        LoreCommandResult CommitFiles(string workingDirectory, string[] paths, string message, string identity, bool amend);

        /// <summary>Pushes local commits to the remote (equivalent to <c>lore push</c>).</summary>
        LoreCommandResult Push(string workingDirectory);

        /// <summary>Synchronizes the working tree to the latest remote revision (<c>lore sync</c>).</summary>
        LoreCommandResult Sync(string workingDirectory);

        /// <summary>
        /// Discards working-tree changes for <paramref name="paths"/>, resetting them to the
        /// current revision (equivalent to <c>lore file reset</c>).
        /// </summary>
        LoreCommandResult ResetFiles(string workingDirectory, string[] paths);

        /// <summary>
        /// Writes the content of <paramref name="relativePath"/> as it exists at
        /// <paramref name="revision"/> (empty for the current revision) to <paramref name="outputPath"/>.
        /// Used to materialize the committed version of a file for diffing.
        /// </summary>
        LoreCommandResult WriteFileAtRevision(string workingDirectory, string relativePath, string revision, string outputPath);
    }
}
