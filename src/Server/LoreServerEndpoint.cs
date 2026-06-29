using System;

namespace LoreVS.Server
{
    /// <summary>
    /// Describes the host/port of a Lore server and builds repository URLs from it. Built at
    /// runtime from a user-supplied <c>lore://</c> URL (never a compile-time constant) so the
    /// extension can point at any server. The loopback default matches the zero-config demo server.
    /// </summary>
    internal readonly struct LoreServerEndpoint
    {
        /// <summary>Loopback host used by the zero-config demo server.</summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>gRPC/QUIC port of the zero-config demo server.</summary>
        public const int DefaultGrpcPort = 41337;

        /// <summary>HTTP health-check port of the zero-config demo server.</summary>
        public const int DefaultHttpPort = 41339;

        public LoreServerEndpoint(string host, int grpcPort)
        {
            Host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host;
            GrpcPort = grpcPort;
        }

        public string Host { get; }

        public int GrpcPort { get; }

        /// <summary>
        /// HTTP port used for the server health check. The demo server exposes HTTP two ports
        /// above the QUIC/gRPC port (41337 -> 41339), the only relationship the SDK guarantees.
        /// </summary>
        public int HttpPort => GrpcPort + 2;

        /// <summary>The gRPC/QUIC server URL (e.g. lore://127.0.0.1:41337).</summary>
        public string ServerUrl => $"lore://{Host}:{GrpcPort}";

        /// <summary>The health-check endpoint (e.g. http://127.0.0.1:41339/health_check).</summary>
        public string HealthCheckUrl => $"http://{Host}:{HttpPort}/health_check";

        /// <summary>Builds the repository URL for <paramref name="repositoryName"/> on this server.</summary>
        public string RepositoryUrl(string repositoryName) => $"{ServerUrl}/{repositoryName}";

        /// <summary>The default demo endpoint (127.0.0.1, 41337).</summary>
        public static LoreServerEndpoint Default => new LoreServerEndpoint(DefaultHost, DefaultGrpcPort);

        /// <summary>
        /// Parses a <c>lore://host[:port]</c> URL (any trailing repository path is ignored) into an
        /// endpoint. Returns <see langword="false"/> when the value is not a usable lore:// server URL.
        /// </summary>
        public static bool TryParse(string url, out LoreServerEndpoint endpoint)
        {
            endpoint = default;
            if (string.IsNullOrWhiteSpace(url) ||
                !url.StartsWith("lore://", StringComparison.OrdinalIgnoreCase))
            {
                return false;
            }

            string rest = url.Substring("lore://".Length);
            int slash = rest.IndexOf('/');
            string authority = slash >= 0 ? rest.Substring(0, slash) : rest;
            if (authority.Length == 0)
            {
                return false;
            }

            string host = authority;
            int port = DefaultGrpcPort;
            int colon = authority.IndexOf(':');
            if (colon >= 0)
            {
                host = authority.Substring(0, colon);
                if (!int.TryParse(authority.Substring(colon + 1), out port))
                {
                    return false;
                }
            }

            endpoint = new LoreServerEndpoint(host, port);
            return true;
        }
    }
}