using LoreVS.Server;
using Microsoft.VisualStudio.TestTools.UnitTesting;

namespace LoreVS.Tests
{
    /// <summary>
    /// Tests for <see cref="LoreServerEndpoint"/> URL construction and the "is this the demo
    /// server we know how to launch" decision. The endpoint is used to build repository URLs and
    /// to gate auto-start, so its formatting must remain stable across the CLI -> SDK swap.
    /// </summary>
    [TestClass]
    public class LoreServerEndpointTests
    {
        [TestMethod]
        public void Default_UsesLoopbackAndDefaultPorts()
        {
            LoreServerEndpoint endpoint = LoreServerEndpoint.Default;

            Assert.AreEqual(LoreServerEndpoint.DefaultHost, endpoint.Host);
            Assert.AreEqual(LoreServerEndpoint.DefaultGrpcPort, endpoint.GrpcPort);
            Assert.AreEqual(LoreServerEndpoint.DefaultHttpPort, endpoint.HttpPort);
            Assert.IsTrue(endpoint.IsDefaultDemo);
        }

        [TestMethod]
        public void ServerUrl_UsesLoreScheme()
        {
            var endpoint = new LoreServerEndpoint("127.0.0.1", 41337, 41339);
            Assert.AreEqual("lore://127.0.0.1:41337", endpoint.ServerUrl);
        }

        [TestMethod]
        public void HealthUrl_PointsAtHttpPort()
        {
            var endpoint = new LoreServerEndpoint("127.0.0.1", 41337, 41339);
            Assert.AreEqual("http://127.0.0.1:41339/health_check", endpoint.HealthUrl);
        }

        [TestMethod]
        public void RepositoryUrl_AppendsRepositoryName()
        {
            var endpoint = new LoreServerEndpoint("127.0.0.1", 41337, 41339);
            Assert.AreEqual("lore://127.0.0.1:41337/my-repo", endpoint.RepositoryUrl("my-repo"));
        }

        [TestMethod]
        public void Constructor_BlankHostFallsBackToDefault()
        {
            var endpoint = new LoreServerEndpoint("   ", 41337, 41339);
            Assert.AreEqual(LoreServerEndpoint.DefaultHost, endpoint.Host);
        }

        [TestMethod]
        public void IsDefaultDemo_FalseForNonDefaultHost()
        {
            var endpoint = new LoreServerEndpoint("10.0.0.5", 41337, 41339);
            Assert.IsFalse(endpoint.IsDefaultDemo);
        }

        [TestMethod]
        public void IsDefaultDemo_FalseForNonDefaultPort()
        {
            var endpoint = new LoreServerEndpoint("127.0.0.1", 5000, 41339);
            Assert.IsFalse(endpoint.IsDefaultDemo);
        }
    }
}
