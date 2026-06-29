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

        /// <summary>Stages every changed file under <paramref name="workingDirectory"/> (scan).</summary>
        Task<LoreCommandResult> StageAllAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>Commits the staged revision with <paramref name="message"/>.</summary>
        Task<LoreCommandResult> CommitAsync(string workingDirectory, string message, string identity, CancellationToken cancellationToken);

        /// <summary>
        /// Stages every changed file and amends the latest revision, replacing its message with
        /// <paramref name="message"/> and folding the staged changes into it.
        /// </summary>
        Task<LoreCommandResult> AmendAsync(string workingDirectory, string message, string identity, CancellationToken cancellationToken);

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
