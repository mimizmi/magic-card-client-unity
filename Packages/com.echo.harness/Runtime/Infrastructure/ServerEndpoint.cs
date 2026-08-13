using System;
using System.Globalization;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Where the authoritative Go server is, resolved from the environment.
    ///
    /// <para>It resolves an endpoint and does nothing else. It starts no process,
    /// reserves no port, and waits for no readiness, because the server this points
    /// at is remote and continuously running - there is nothing to build, launch,
    /// poll, or kill, and a failed connect is a real failure rather than a
    /// not-yet.</para>
    ///
    /// <para>This lives here, in a player-shippable assembly, rather than in the
    /// test tier, because the bootstrap scene and the end-to-end tier must not
    /// disagree about what ECHO_SERVER_HOST means. Two copies of the port guard
    /// would drift, and the drift runs one of them against a different endpoint
    /// than the one asked for while reporting whatever answers as the truth.</para>
    ///
    /// <para><see cref="HostVariable"/> has no default, and that absence is the
    /// whole mechanism. The address is a developer endpoint and must not appear in
    /// the repository; a fallback here - even a commented-out one - would put it
    /// there. So an unset variable means "not configured": the end-to-end tier
    /// skips itself and the test gate tolerates that skip for that one class, and
    /// an application that finds nothing configured has been told nothing rather
    /// than pointed somewhere. That absence is also what makes this type safe to
    /// ship in a player - it carries no address to leak. <see cref="PortVariable"/>
    /// does have a default, because the port is not a secret: the server's own
    /// config defaults to it, its Dockerfile exposes it, and its deployment
    /// publishes it.</para>
    /// </summary>
    public readonly struct ServerEndpoint
    {
        public const string HostVariable = "ECHO_SERVER_HOST";

        public const string PortVariable = "ECHO_SERVER_PORT";

        public const int DefaultPort = 43966;

        /// <summary>
        /// The usable port range, written down once.
        ///
        /// <para><see cref="MinPort"/> is 1 rather than <c>IPEndPoint.MinPort</c>'s 0,
        /// and that one value is why this range needs stating at all. Zero clears
        /// every argument check .NET makes: <c>Socket</c> and <c>TcpClient</c> read it
        /// as "let the OS choose", so a bind succeeds and a connect fails at the
        /// socket layer with WSAEADDRNOTAVAIL - which reads to a developer as an
        /// unreachable server rather than as the typo it is. It is also
        /// <c>default(int)</c>, so it is the value a freshly created asset or a reset
        /// Inspector field lands on.</para>
        ///
        /// <para>Public, with <see cref="IsUsablePort"/>, because a port reaches this
        /// application through a second door that never touches this type - the
        /// settings asset - and this class's own summary says why that must not
        /// become a second copy of the rule: two copies of the port guard would
        /// drift, and the drift runs against a different endpoint than the one asked
        /// for while reporting whatever answers as the truth.</para>
        /// </summary>
        public const int MinPort = 1;

        /// <inheritdoc cref="MinPort"/>
        public const int MaxPort = 65535;

        /// <inheritdoc cref="MinPort"/>
        public static bool IsUsablePort(int port) => port >= MinPort && port <= MaxPort;

        /// <exception cref="ArgumentException">
        /// The host is blank, or the port is outside
        /// <see cref="MinPort"/>..<see cref="MaxPort"/>. The port check is here as
        /// well as at the doors that supply one, because a constructor that took the
        /// host seriously and assigned the port unchecked was lying about what it
        /// accepts.
        /// </exception>
        public ServerEndpoint(string host, int port)
        {
            if (string.IsNullOrWhiteSpace(host))
            {
                throw new ArgumentException("A host is required.", nameof(host));
            }

            if (!IsUsablePort(port))
            {
                throw new ArgumentException(
                    $"A port must be between {MinPort} and {MaxPort}; {port} is not.",
                    nameof(port));
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
        /// would connect to a different endpoint than the one asked for and report
        /// whatever answered as the truth.
        /// </exception>
        public static bool TryResolveFromEnvironment(out ServerEndpoint endpoint)
        {
            endpoint = default;

            var host = Environment.GetEnvironmentVariable(HostVariable);
            if (string.IsNullOrWhiteSpace(host))
            {
                return false;
            }

            endpoint = new ServerEndpoint(host.Trim(), ResolvePort());
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
                !IsUsablePort(port))
            {
                // Kept rather than left to the constructor's guard, which would
                // catch the same values one frame later. This message names the
                // variable and how to unset it; the constructor cannot, because by
                // then the only thing it can see is an int.
                throw new ArgumentException(
                    $"{PortVariable} is set to '{configured}', which is not a port " +
                    $"between {MinPort} and {MaxPort}. Unset it to use the default " +
                    $"of {DefaultPort}.");
            }

            return port;
        }
    }
}
