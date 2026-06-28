using System;
using System.IO;
using System.IO.Pipes;
using System.Threading;
using System.Threading.Tasks;
using LoreVcs;
using LoreVcs.Types.Args;
using StreamJsonRpc;
using LoreVS.SourceControl;
namespace LoreVS.Worker
{
    /// <summary>
    /// Entry point for the out-of-process Lore worker. The .NET Framework VS package cannot load
    /// the native <c>lorelib</c>, so it launches this .NET 9 process and talks to it over a named
    /// pipe using JSON-RPC. Usage:
    /// <list type="bullet">
    ///   <item><c>LoreVS.Worker &lt;pipe-name&gt;</c> - serve one JSON-RPC client on the named pipe.</item>
    ///   <item><c>LoreVS.Worker --smoke &lt;dir&gt;</c> - run an offline create/stage/commit/status self-test.</item>
    ///   <item><c>LoreVS.Worker --seed &lt;repoPath&gt;</c> - create a seeded offline repo at the path (for tests).</item>
    /// </list>
    /// </summary>
    internal static class Program
    {
        private static async Task<int> Main(string[] args)
        {
            if (args.Length >= 2 && string.Equals(args[0], "--smoke", StringComparison.OrdinalIgnoreCase))
            {
                return await RunSmokeAsync(args[1]).ConfigureAwait(false);
            }

            if (args.Length >= 2 && string.Equals(args[0], "--seed", StringComparison.OrdinalIgnoreCase))
            {
                return await RunSeedAsync(args[1]).ConfigureAwait(false);
            }

            if (args.Length < 1 || string.IsNullOrWhiteSpace(args[0]))
            {
                Console.Error.WriteLine("LoreVS.Worker: a pipe name (or '--smoke <dir>' / '--seed <repoPath>') is required.");
                return 2;
            }

            return await ServeAsync(args[0]).ConfigureAwait(false);
        }

        /// <summary>Serves a single JSON-RPC client connecting on <paramref name="pipeName"/>.</summary>
        private static async Task<int> ServeAsync(string pipeName)
        {
            try
            {
                using var pipe = new NamedPipeServerStream(
                    pipeName,
                    PipeDirection.InOut,
                    maxNumberOfServerInstances: 1,
                    PipeTransmissionMode.Byte,
                    PipeOptions.Asynchronous);

                await pipe.WaitForConnectionAsync().ConfigureAwait(false);

                var formatter = new SystemTextJsonFormatter();
                var handler = new HeaderDelimitedMessageHandler(pipe, pipe, formatter);
                using var rpc = new JsonRpc(handler);
                rpc.AddLocalRpcTarget<ILoreWorkerContract>(new LoreServiceImpl(), null);
                rpc.StartListening();

                await rpc.Completion.ConfigureAwait(false);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"LoreVS.Worker: fatal error: {ex}");
                return 1;
            }
            finally
            {
                try { Lore.Shutdown(); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Runs a fully offline create -> stage -> commit -> status round trip to prove the native
        /// SDK loads and works in this environment. Prints the resulting status entries.
        /// </summary>
        private static async Task<int> RunSmokeAsync(string directory)
        {
            var service = new LoreServiceImpl();
            try
            {
                Directory.CreateDirectory(directory);
                string repoPath = Path.Combine(directory, "SmokeRepo" + Guid.NewGuid().ToString("N"));
                await SeedOfflineRepoAsync(repoPath).ConfigureAwait(false);
                Console.WriteLine("create/stage/commit/modify: OK");

                LoreStatusEntry[] status = await service.GetRepositoryStatusAsync(repoPath, CancellationToken.None).ConfigureAwait(false);
                Console.WriteLine($"status: {status.Length} entr{(status.Length == 1 ? "y" : "ies")}");
                foreach (LoreStatusEntry entry in status)
                {
                    Console.WriteLine($"  {entry.Status,-12} {entry.Path}");
                }

                Console.WriteLine("smoke: OK");
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"smoke: FAILED: {ex}");
                return 1;
            }
            finally
            {
                try { Lore.Shutdown(); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Creates a seeded offline repository at <paramref name="repoPath"/> (committed file.txt,
        /// then a working-tree modification and an untracked file) and exits. Used by integration
        /// tests that then query the repo through the brokered client over JSON-RPC.
        /// </summary>
        private static async Task<int> RunSeedAsync(string repoPath)
        {
            try
            {
                await SeedOfflineRepoAsync(repoPath).ConfigureAwait(false);
                Console.WriteLine(repoPath);
                return 0;
            }
            catch (Exception ex)
            {
                Console.Error.WriteLine($"seed: FAILED: {ex}");
                return 1;
            }
            finally
            {
                try { Lore.Shutdown(); } catch { /* best effort */ }
            }
        }

        /// <summary>
        /// Offline create -> stage -> commit, then dirties the working tree: modifies file.txt and
        /// adds an untracked file so a subsequent status reports a Modified and an Added entry.
        /// </summary>
        private static async Task SeedOfflineRepoAsync(string repoPath)
        {
            var service = new LoreServiceImpl();
            Directory.CreateDirectory(repoPath);
            string repoName = Path.GetFileName(repoPath.TrimEnd('\\', '/'));

            // The contract's CreateRepository targets a server (Offline=false); seeding is fully
            // offline, so create the local repository directly with the SDK here.
            await Task.Run(() =>
            {
                using var g = new LoreGlobalArgs { RepositoryPath = repoPath, Offline = true, Identity = "seed@lorevs" };
                using var c = new LoreRepositoryCreateArgs { RepositoryUrl = repoName };
                Lore.RepositoryCreate(g, c).Wait();
            }).ConfigureAwait(false);

            File.WriteAllText(Path.Combine(repoPath, "file.txt"), "hello lore");
            await service.StageAllAsync(repoPath, CancellationToken.None).ConfigureAwait(false);
            await service.CommitAsync(repoPath, "Initial commit", "seed@lorevs", CancellationToken.None).ConfigureAwait(false);

            File.WriteAllText(Path.Combine(repoPath, "file.txt"), "hello lore - changed");
            File.WriteAllText(Path.Combine(repoPath, "untracked.txt"), "brand new file");
        }
    }
}
