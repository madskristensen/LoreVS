namespace LoreVS.Server
{
    /// <summary>
    /// Describes the host/port of a Lore server and builds repository URLs from it. Built at
    /// runtime from user options (never a compile-time constant) so the port can change and so a
    /// user can point the extension at a server on a non-default port. The loopback default
    /// matches the zero-config demo server.
    /// </summary>
    internal readonly struct LoreServerEndpoint
    {
        /// <summary>Loopback host used by the zero-config demo server.</summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>gRPC/QUIC port of the zero-config demo server.</summary>
        public const int DefaultGrpcPort = 41337;

        public LoreServerEndpoint(string host, int grpcPort)
        {
            Host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host;
            GrpcPort = grpcPort;
        }

        public string Host { get; }

        public int GrpcPort { get; }

        /// <summary>The gRPC/QUIC server URL (e.g. lore://127.0.0.1:41337).</summary>
        public string ServerUrl => $"lore://{Host}:{GrpcPort}";

        /// <summary>Builds the repository URL for <paramref name="repositoryName"/> on this server.</summary>
        public string RepositoryUrl(string repositoryName) => $"{ServerUrl}/{repositoryName}";

        /// <summary>The default demo endpoint (127.0.0.1, 41337).</summary>
        public static LoreServerEndpoint Default => new LoreServerEndpoint(DefaultHost, DefaultGrpcPort);
    }
}
