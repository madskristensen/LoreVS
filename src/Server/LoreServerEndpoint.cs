namespace LoreVS.Server
{
    /// <summary>
    /// Describes the host/ports of a Lore server. Built at runtime from user options (never a
    /// compile-time constant) so the endpoint can change and so a user can point the extension at
    /// a server on non-default ports. The loopback default matches the zero-config demo server.
    /// </summary>
    internal readonly struct LoreServerEndpoint
    {
        /// <summary>Loopback host used by the managed demo server.</summary>
        public const string DefaultHost = "127.0.0.1";

        /// <summary>gRPC/QUIC port of the zero-config demo server.</summary>
        public const int DefaultGrpcPort = 41337;

        /// <summary>HTTP port of the zero-config demo server (health check / REST).</summary>
        public const int DefaultHttpPort = 41339;

        public LoreServerEndpoint(string host, int grpcPort, int httpPort)
        {
            Host = string.IsNullOrWhiteSpace(host) ? DefaultHost : host;
            GrpcPort = grpcPort;
            HttpPort = httpPort;
        }

        public string Host { get; }

        public int GrpcPort { get; }

        public int HttpPort { get; }

        /// <summary>The gRPC/QUIC server URL (e.g. lore://127.0.0.1:41337).</summary>
        public string ServerUrl => $"lore://{Host}:{GrpcPort}";

        /// <summary>The HTTP health-check endpoint.</summary>
        public string HealthUrl => $"http://{Host}:{HttpPort}/health_check";

        /// <summary>Builds the repository URL for <paramref name="repositoryName"/> on this server.</summary>
        public string RepositoryUrl(string repositoryName) => $"{ServerUrl}/{repositoryName}";

        /// <summary>
        /// True when this endpoint matches the zero-config demo server the extension knows how to
        /// launch. A non-default endpoint is treated as an external server (reused, never spawned),
        /// because the demo server cannot be reconfigured to other ports without a full config.
        /// </summary>
        public bool IsDefaultDemo =>
            Host == DefaultHost && GrpcPort == DefaultGrpcPort && HttpPort == DefaultHttpPort;

        /// <summary>The default demo endpoint (127.0.0.1, 41337/41339).</summary>
        public static LoreServerEndpoint Default =>
            new LoreServerEndpoint(DefaultHost, DefaultGrpcPort, DefaultHttpPort);
    }
}
