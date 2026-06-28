using LoreVS.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreServerEndpoint"/> URL construction. The endpoint is used to build
    /// repository URLs, so its formatting must remain stable.
    /// </summary>
    [TestClass]
    public class LoreServerEndpointTests
    {
        [TestMethod]
        public void Default_UsesLoopbackAndDefaultPort()
        {
            LoreServerEndpoint endpoint = LoreServerEndpoint.Default;

            Assert.AreEqual(LoreServerEndpoint.DefaultHost, endpoint.Host);
            Assert.AreEqual(LoreServerEndpoint.DefaultGrpcPort, endpoint.GrpcPort);
        }

        [TestMethod]
        public void ServerUrl_UsesLoreScheme()
        {
            var endpoint = new LoreServerEndpoint("127.0.0.1", 41337);
            Assert.AreEqual("lore://127.0.0.1:41337", endpoint.ServerUrl);
        }

        [TestMethod]
        public void RepositoryUrl_AppendsRepositoryName()
        {
            var endpoint = new LoreServerEndpoint("127.0.0.1", 41337);
            Assert.AreEqual("lore://127.0.0.1:41337/my-repo", endpoint.RepositoryUrl("my-repo"));
        }

        [TestMethod]
        public void Constructor_BlankHostFallsBackToDefault()
        {
            var endpoint = new LoreServerEndpoint("   ", 41337);
            Assert.AreEqual(LoreServerEndpoint.DefaultHost, endpoint.Host);
        }
    }
}
