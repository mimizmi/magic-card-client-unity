using Echo.Harness.Infrastructure;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Where the authoritative Go server is, for the one test tier that talks to
    /// it. The resolution itself lives in <see cref="ServerEndpoint"/> so the test
    /// tier and the bootstrap scene cannot disagree about what ECHO_SERVER_HOST
    /// means; this type is the name the end-to-end tier already uses.
    /// </summary>
    public readonly struct RemoteServerEndpoint
    {
        public const string HostVariable = ServerEndpoint.HostVariable;

        public const string PortVariable = ServerEndpoint.PortVariable;

        public const int DefaultPort = ServerEndpoint.DefaultPort;

        private RemoteServerEndpoint(ServerEndpoint endpoint)
        {
            Host = endpoint.Host;
            Port = endpoint.Port;
        }

        public string Host { get; }

        public int Port { get; }

        public static bool TryResolve(out RemoteServerEndpoint endpoint)
        {
            if (!ServerEndpoint.TryResolveFromEnvironment(out var resolved))
            {
                endpoint = default;
                return false;
            }

            endpoint = new RemoteServerEndpoint(resolved);
            return true;
        }
    }
}
