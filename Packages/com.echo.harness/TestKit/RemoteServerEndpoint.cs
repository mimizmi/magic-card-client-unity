using System;
using System.Globalization;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Where the authoritative Go server is, for the one test tier that talks to it.
    ///
    /// <para>It resolves an endpoint from the environment and does nothing else. It
    /// starts no process, reserves no port, and waits for no readiness, because the
    /// server this points at is remote and continuously running - there is nothing
    /// to build, launch, poll, or kill, and a failed connect is a real failure
    /// rather than a not-yet.</para>
    ///
    /// <para><see cref="HostVariable"/> has no default, and that absence is the
    /// whole mechanism. The address is a developer endpoint and must not appear in
    /// the repository; a fallback here - even a commented-out one - would put it
    /// there. So an unset variable means "not configured", the end-to-end tier
    /// skips itself, and the test gate tolerates that skip for that one class.
    /// <see cref="PortVariable"/> does have a default, because the port is not a
    /// secret: the server's own config defaults to it, its Dockerfile exposes it,
    /// and its deployment publishes it.</para>
    /// </summary>
    public readonly struct RemoteServerEndpoint
    {
        public const string HostVariable = "ECHO_SERVER_HOST";

        public const string PortVariable = "ECHO_SERVER_PORT";

        public const int DefaultPort = 43966;

        public RemoteServerEndpoint(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("A host is required.", nameof(host));
            }

            Host = host;
            Port = port;
        }

        public string Host { get; }

        public int Port { get; }

        /// <summary>
        /// True when an endpoint is configured. False is not a failure - it is the
        /// ordinary state of a machine that has not opted in.
        /// </summary>
        /// <exception cref="ArgumentException">
        /// The port variable is set to something that is not a usable port. A typo
        /// there is a configuration error, and silently falling back to the default
        /// would run the tier against a different endpoint than the one asked for
        /// and report whatever answered as the truth.
        /// </exception>
        public static bool TryResolve(out RemoteServerEndpoint endpoint)
        {
            endpoint = default;

            var host = Environment.GetEnvironmentVariable(HostVariable);
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            endpoint = new RemoteServerEndpoint(host.Trim(), ResolvePort());
            return true;
        }

        private static int ResolvePort()
        {
            var configured = Environment.GetEnvironmentVariable(PortVariable);
            if (string.IsNullOrWhiteSpace(configured))
            {
                return DefaultPort;
            }

            // NumberStyles.None rather than Integer: a leading sign in a port is a
            // mistake, and accepting it would hide the typo this guard exists to
            // surface.
            if (!int.TryParse(
                    configured.Trim(),
                    NumberStyles.None,
                    CultureInfo.InvariantCulture,
                    out var port) ||
                port < 1 ||
                port > 65535)
            {
                throw new ArgumentException(
                    $"{PortVariable} is set to '{configured}', which is not a port " +
                    $"between 1 and 65535. Unset it to use the default of " +
                    $"{DefaultPort}.");
            }

            return port;
        }
    }
}
