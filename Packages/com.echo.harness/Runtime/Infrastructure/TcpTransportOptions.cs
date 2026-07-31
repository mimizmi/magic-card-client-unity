using System;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Every default here is derived from the authoritative Go server, and none of
    /// them is negotiable: its rate limit and heartbeat intervals are compile-time
    /// constants in its session.go, and its RATE_LIMIT environment variable is
    /// loaded, logged, and never consumed.
    /// </summary>
    public sealed class TcpTransportOptions
    {
        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 43966;

        /// <summary>
        /// How long a complete frame may take to arrive before the link is judged
        /// dead. The server sends a Ping every 15 s, so silence is itself a signal
        /// and 45 s means three missed pings. Deliberately not tighter: a tight
        /// value disconnects on a hiccup, and the kernel can take minutes to notice
        /// a half-open connection on its own.
        /// </summary>
        public TimeSpan ReadIdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Messages per second, matching the server's hard-coded 30. Exceeding it
        /// closes the connection server-side with no error frame, which on the wire
        /// is indistinguishable from a pulled cable.
        /// </summary>
        public int SendBudgetPerSecond { get; set; } = 30;
    }
}
