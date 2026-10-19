using System;
using System.Diagnostics;
using System.IO;
using System.Linq;
using System.Net;
using System.Net.NetworkInformation;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using LoreVS.SourceControl;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    public partial class LoreBrokeredClientIntegrationTests
    {
        public TestContext TestContext { get; set; } = null!;

        [TestMethod]
        [DataRow(false)]
        [DataRow(true)]
        [TestCategory("ServerIntegration")]
        [DoNotParallelize]
        public void ServerRoundTrip_CreateClonePushPull_PreservesContentWithoutHttpPreflight(bool httpEnabled)
        {
            string? serverExecutable = Environment.GetEnvironmentVariable("LOREVS_TEST_SERVER_PATH");
            if (string.IsNullOrWhiteSpace(serverExecutable))
            {
                Assert.Inconclusive("Set LOREVS_TEST_SERVER_PATH to loreserver.exe to run the local-server integration tests.");
            }

            Assert.IsTrue(File.Exists(serverExecutable), "LOREVS_TEST_SERVER_PATH must point to an existing loreserver.exe.");
            string worker = RequireWorker();
            using var server = new LocalLoreServer(serverExecutable!, _tempRoot, httpEnabled);
            try
            {
                server.WaitUntilReady();
                string repositoryUrl = "lore://127.0.0.1:" + server.Port + "/smoke";
                string original = Path.Combine(_tempRoot, "original");
                string clone = Path.Combine(_tempRoot, "clone");
                string verification = Path.Combine(_tempRoot, "verification");
                Directory.CreateDirectory(original);

                using var writer = new LoreBrokeredClient(worker);
                AssertServerSuccess(writer.CreateRepository(original, repositoryUrl, "server-test@lorevs"), "Server-backed create");
                File.WriteAllText(Path.Combine(original, "file.txt"), "initial server content");
                AssertServerSuccess(writer.StageAll(original), "Stage initial content");
                AssertServerSuccess(writer.Commit(original, "Initial server revision", "server-test@lorevs"), "Commit initial content");
                AssertServerSuccess(writer.Push(original), "Push initial content");
                string initialHash = writer.GetRevisionHistory(original, string.Empty, 1).Entries.Single().RevisionHash;

                using var reader = new LoreBrokeredClient(worker);
                AssertServerSuccess(reader.CloneRepository(repositoryUrl, clone, "server-test@lorevs"), "Clone");
                Assert.AreEqual("initial server content", File.ReadAllText(Path.Combine(clone, "file.txt")));
                Assert.AreEqual(initialHash, reader.GetRevisionHistory(clone, string.Empty, 1).Entries.Single().RevisionHash);

                File.WriteAllText(Path.Combine(original, "file.txt"), "updated server content");
                File.WriteAllText(Path.Combine(original, "added.txt"), "new remote file");
                AssertServerSuccess(writer.StageAll(original), "Stage remote changes");
                AssertServerSuccess(writer.Commit(original, "Second server revision", "server-test@lorevs"), "Commit remote changes");
                AssertServerSuccess(writer.Push(original), "Push remote changes");
                string latestHash = writer.GetRevisionHistory(original, string.Empty, 1).Entries.Single().RevisionHash;
                Assert.AreNotEqual(initialHash, latestHash);
                Assert.AreEqual("initial server content", File.ReadAllText(Path.Combine(clone, "file.txt")));

                AssertServerSuccess(reader.Sync(clone), "Pull remote changes");
                Assert.AreEqual("updated server content", File.ReadAllText(Path.Combine(clone, "file.txt")));
                Assert.AreEqual("new remote file", File.ReadAllText(Path.Combine(clone, "added.txt")));
                Assert.AreEqual(latestHash, reader.GetRevisionHistory(clone, string.Empty, 1).Entries.Single().RevisionHash);
                Assert.AreEqual(0, reader.GetRepositorySnapshot(clone).Files.Length);

                File.WriteAllText(Path.Combine(clone, "from-clone.txt"), "pushed by the clone");
                AssertServerSuccess(reader.StageAll(clone), "Stage clone changes");
                AssertServerSuccess(reader.Commit(clone, "Revision from clone", "server-test@lorevs"), "Commit clone changes");
                AssertServerSuccess(reader.Push(clone), "Push from clone");
                AssertServerSuccess(writer.Sync(original), "Pull clone changes into original");
                Assert.AreEqual("pushed by the clone", File.ReadAllText(Path.Combine(original, "from-clone.txt")));

                using var verifier = new LoreBrokeredClient(worker);
                AssertServerSuccess(verifier.CloneRepository(repositoryUrl, verification, "server-test@lorevs"), "Fresh verification clone");
                Assert.AreEqual("updated server content", File.ReadAllText(Path.Combine(verification, "file.txt")));
                Assert.AreEqual("new remote file", File.ReadAllText(Path.Combine(verification, "added.txt")));
                Assert.AreEqual("pushed by the clone", File.ReadAllText(Path.Combine(verification, "from-clone.txt")));
                LoreRevisionPage history = verifier.GetRevisionHistory(verification, string.Empty, 10);
                CollectionAssert.AreEqual(
                    new[] { "Revision from clone", "Second server revision", "Initial server revision" },
                    history.Entries.Select(entry => entry.Message).ToArray());
                Assert.IsFalse(history.HasMore);
                LoreRevisionChange[] changes = verifier.GetRevisionChanges(verification, latestHash);
                Assert.AreEqual(2, changes.Length);
                Assert.IsTrue(changes.Any(change => change.Path == Path.Combine(verification, "file.txt") && change.Status == LoreFileStatus.Modified));
                Assert.IsTrue(changes.Any(change => change.Path == Path.Combine(verification, "added.txt") && change.Status == LoreFileStatus.Added));
                string historicalContent = Path.Combine(_tempRoot, "historical.txt");
                AssertServerSuccess(verifier.WriteFileAtRevision(verification, "file.txt", initialHash, historicalContent), "Read historical content");
                Assert.AreEqual("initial server content", File.ReadAllText(historicalContent));
                Assert.AreEqual("updated server content", File.ReadAllText(Path.Combine(verification, "file.txt")));

                server.Stop();
                LoreCommandResult offlinePull = reader.Sync(clone);
                Assert.IsFalse(offlinePull.Success, "Pull against the stopped server must not report remote synchronization.");
                Assert.IsFalse(offlinePull.RequiresAuthentication, offlinePull.CombinedText);
                Assert.AreNotEqual(0, offlinePull.ExitCode);
                StringAssert.Contains(offlinePull.Error, "did not complete within");
                Assert.AreEqual("updated server content", File.ReadAllText(Path.Combine(clone, "file.txt")));
                Assert.AreEqual("pushed by the clone", File.ReadAllText(Path.Combine(clone, "from-clone.txt")));
            }
            finally
            {
                server.Stop();
                TestContext.WriteLine(server.Output);
            }
        }

        private static void AssertServerSuccess(LoreCommandResult result, string operation)
        {
            Assert.IsTrue(result.Success, operation + " failed: " + result.CombinedText);
            Assert.AreEqual(0, result.ExitCode, operation);
            Assert.IsFalse(result.RequiresAuthentication, operation + " unexpectedly requested sign-in.");
            Assert.AreEqual(string.Empty, result.Warning, operation + " unexpectedly fell back to local history.");
        }

        private sealed class LocalLoreServer : IDisposable
        {
            private readonly Process _process;
            private readonly StringBuilder _output = new StringBuilder();

            public int Port { get; }

            public string Output
            {
                get
                {
                    lock (_output)
                    {
                        return _output.ToString();
                    }
                }
            }

            public LocalLoreServer(string executable, string root, bool httpEnabled)
            {
                var loreListener = new TcpListener(IPAddress.Loopback, 0);
                var httpListener = new TcpListener(IPAddress.Loopback, 0);
                int httpPort;
                try
                {
                    loreListener.Start();
                    httpListener.Start();
                    Port = ((IPEndPoint)loreListener.LocalEndpoint).Port;
                    httpPort = ((IPEndPoint)httpListener.LocalEndpoint).Port;
                    using var quicReservation = new UdpClient(new IPEndPoint(IPAddress.Loopback, Port));
                }
                finally
                {
                    loreListener.Stop();
                    httpListener.Stop();
                }

                string configDirectory = Path.Combine(root, "config");
                Directory.CreateDirectory(configDirectory);
                string storePath = Path.Combine(root, "store");
                File.WriteAllText(Path.Combine(configDirectory, "local.toml"),
                    "[server.quic]\nhost = '127.0.0.1'\nport = " + Port + "\n" +
                    "[server.grpc]\nhost = '127.0.0.1'\nport = " + Port + "\n" +
                    "[server.http]\nhost = '127.0.0.1'\nport = " + httpPort + "\nenabled = " + (httpEnabled ? "true" : "false") + "\n" +
                    "[immutable_store.local]\npath = '" + storePath + "'\n" +
                    "[mutable_store.local]\npath = '" + storePath + "'\n");

                var startInfo = new ProcessStartInfo
                {
                    FileName = executable,
                    Arguments = "--config \"" + configDirectory + "\" --env local",
                    WorkingDirectory = root,
                    UseShellExecute = false,
                    CreateNoWindow = true,
                    RedirectStandardOutput = true,
                    RedirectStandardError = true,
                };
                foreach (string key in startInfo.EnvironmentVariables.Keys.Cast<string>().Where(key =>
                    key.StartsWith("LORE__", StringComparison.OrdinalIgnoreCase)).ToArray())
                {
                    startInfo.EnvironmentVariables.Remove(key);
                }

                startInfo.EnvironmentVariables["TEMP"] = root;
                startInfo.EnvironmentVariables["TMP"] = root;
                _process = new Process { StartInfo = startInfo };
                _process.OutputDataReceived += (_, e) => AppendOutput(e.Data);
                _process.ErrorDataReceived += (_, e) => AppendOutput(e.Data);
                Assert.IsTrue(_process.Start(), "Could not start the local Lore server.");
                _process.BeginOutputReadLine();
                _process.BeginErrorReadLine();
            }

            public void WaitUntilReady()
            {
                bool ready = SpinWait.SpinUntil(() => _process.HasExited ||
                    IPGlobalProperties.GetIPGlobalProperties().GetActiveTcpListeners().Any(endpoint =>
                        endpoint.Address.Equals(IPAddress.Loopback) && endpoint.Port == Port), TimeSpan.FromSeconds(30));
                Assert.IsFalse(_process.HasExited, "The local Lore server exited during startup.\n" + Output);
                Assert.IsTrue(ready, "The local Lore server did not listen within 30 seconds.\n" + Output);
            }

            public void Stop()
            {
                if (!_process.HasExited)
                {
                    _process.Kill();
                }

                Assert.IsTrue(_process.WaitForExit(10000), "The local Lore server did not stop.");
                _process.WaitForExit();
            }

            private void AppendOutput(string? line)
            {
                if (line != null)
                {
                    lock (_output)
                    {
                        _output.AppendLine(line);
                    }
                }
            }

            public void Dispose()
            {
                Stop();
                _process.Dispose();
            }
        }
    }
}