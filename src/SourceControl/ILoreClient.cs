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
        string FindRepositoryRoot(string path);

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
        /// Onboards <paramref name="workingDirectory"/> to Lore by creating a repository on
        /// the server identified by <paramref name="repositoryUrl"/>. The existing files in
        /// the directory are preserved and a <c>.lore</c> working tree is laid down alongside.
        /// </summary>
        LoreCommandResult CreateRepository(string workingDirectory, string repositoryUrl, string identity);

        /// <summary>
        /// Stages every modified/added/deleted file under <paramref name="workingDirectory"/>
        /// (equivalent to <c>lore stage --scan</c>).
        /// </summary>
        LoreCommandResult StageAll(string workingDirectory);

        /// <summary>Commits the staged revision with <paramref name="message"/>.</summary>
        LoreCommandResult Commit(string workingDirectory, string message, string identity);

        /// <summary>Pushes local commits to the remote (equivalent to <c>lore push</c>).</summary>
        LoreCommandResult Push(string workingDirectory);

        /// <summary>Synchronizes the working tree to the latest remote revision (<c>lore sync</c>).</summary>
        LoreCommandResult Sync(string workingDirectory);
    }
}
