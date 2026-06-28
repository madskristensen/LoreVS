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

        /// <summary>Creates a repository on the server identified by <paramref name="repositoryUrl"/>.</summary>
        Task<LoreCommandResult> CreateRepositoryAsync(string workingDirectory, string repositoryUrl, string identity, CancellationToken cancellationToken);

        /// <summary>Stages every changed file under <paramref name="workingDirectory"/> (scan).</summary>
        Task<LoreCommandResult> StageAllAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>Commits the staged revision with <paramref name="message"/>.</summary>
        Task<LoreCommandResult> CommitAsync(string workingDirectory, string message, string identity, CancellationToken cancellationToken);

        /// <summary>Pushes local commits to the remote.</summary>
        Task<LoreCommandResult> PushAsync(string workingDirectory, CancellationToken cancellationToken);

        /// <summary>Synchronizes the working tree to the latest remote revision.</summary>
        Task<LoreCommandResult> SyncAsync(string workingDirectory, CancellationToken cancellationToken);
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
