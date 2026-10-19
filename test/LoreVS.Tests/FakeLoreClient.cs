using System;
using System.Collections.Generic;
using LoreVS.SourceControl;

namespace LoreVS.Tests
{
    /// <summary>
    /// Minimal <see cref="ILoreClient"/> test double for the history view model. Only the members the
    /// history feature uses are given behavior (configurable via delegates); every other member throws
    /// so an unexpected call surfaces in a test rather than silently returning a default.
    /// </summary>
    internal sealed class FakeLoreClient : ILoreClient
    {
        public Func<string, string?>? FindRepositoryRootHandler { get; set; }

        public Func<string, string, int, LoreRevisionPage>? GetRevisionHistoryHandler { get; set; }

        public Func<string, string, LoreRevisionChange[]>? GetRevisionChangesHandler { get; set; }

        public Func<string, string, string, LoreRevisionComparison>? CompareRevisionsHandler { get; set; }

        public Func<string, string, string, string, LoreCommandResult>? WriteFileAtRevisionHandler { get; set; }

        public bool IsAvailable => true;

        public string? FindRepositoryRoot(string path) =>
            FindRepositoryRootHandler != null ? FindRepositoryRootHandler(path) : path;

        public LoreRevisionPage GetRevisionHistory(string repositoryRoot, string startAfterRevision, int pageSize) =>
            GetRevisionHistoryHandler != null
                ? GetRevisionHistoryHandler(repositoryRoot, startAfterRevision, pageSize)
                : new LoreRevisionPage();

        public LoreRevisionChange[] GetRevisionChanges(string repositoryRoot, string revision) =>
            GetRevisionChangesHandler != null
                ? GetRevisionChangesHandler(repositoryRoot, revision)
                : Array.Empty<LoreRevisionChange>();

        public LoreAuthResult Login(string workingDirectory, string remoteUrl) => throw new NotImplementedException();

        public LoreFileStatus GetStatus(string filePath) => throw new NotImplementedException();

        public IReadOnlyDictionary<string, LoreFileStatus> GetRepositoryStatus(string repositoryRoot) => throw new NotImplementedException();

        public LoreRepositorySnapshot GetRepositorySnapshot(string repositoryRoot) => throw new NotImplementedException();

        public LoreRepositoryInfo? GetRepositoryInfo(string repositoryRoot) => throw new NotImplementedException();

        public LoreBranchEntry[] ListBranches(string repositoryRoot) => throw new NotImplementedException();

        public LoreCommandResult CreateBranch(string workingDirectory, string branchName, string identity, bool checkout) => throw new NotImplementedException();

        public LoreCommandResult SwitchBranch(string workingDirectory, string branchName) => throw new NotImplementedException();

        public LoreMergeResult MergeBranch(string workingDirectory, string sourceBranch, string identity) => throw new NotImplementedException();

        public LoreCommandResult ResolveMerge(string workingDirectory, string[] paths, string message, string identity) => throw new NotImplementedException();

        public LoreCommandResult AbortMerge(string workingDirectory) => throw new NotImplementedException();

        public LoreCommandResult CreateRepository(string workingDirectory, string repositoryUrl, string identity) => throw new NotImplementedException();

        public LoreCommandResult CloneRepository(string repositoryUrl, string targetDirectory, string identity) => throw new NotImplementedException();

        public LoreCommandResult StageAll(string workingDirectory) => throw new NotImplementedException();

        public LoreCommandResult RecordMove(string workingDirectory, string oldPath, string newPath) => throw new NotImplementedException();

        public LoreCommandResult Commit(string workingDirectory, string message, string identity) => throw new NotImplementedException();

        public LoreCommandResult Amend(string workingDirectory, string message, string identity) => throw new NotImplementedException();

        public LoreCommandResult CommitFiles(string workingDirectory, string[] paths, string message, string identity, bool amend) => throw new NotImplementedException();

        public LoreCommandResult Push(string workingDirectory) => throw new NotImplementedException();

        public LoreCommandResult Sync(string workingDirectory) => throw new NotImplementedException();

        public LoreCommandResult ResetFiles(string workingDirectory, string[] paths) => throw new NotImplementedException();

        public LoreCommandResult WriteFileAtRevision(string workingDirectory, string relativePath, string revision, string outputPath) =>
            WriteFileAtRevisionHandler != null ? WriteFileAtRevisionHandler(workingDirectory, relativePath, revision, outputPath) : throw new NotImplementedException();

        public LoreRevisionComparison CompareRevisions(string repositoryRoot, string sourceRevision, string targetRevision) =>
            CompareRevisionsHandler != null ? CompareRevisionsHandler(repositoryRoot, sourceRevision, targetRevision) : throw new NotImplementedException();
    }
}