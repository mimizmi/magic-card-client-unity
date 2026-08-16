namespace Echo.Harness.Application
{
    /// <summary>
    /// Who this client logged in as, if anyone. Read-only, for the reason
    /// <see cref="ISessionStatus"/> gives: a view-model that could write it could
    /// declare itself logged in without a server ever agreeing.
    ///
    /// <para><b><see cref="IsLoggedIn"/> is a claim about this process, not about
    /// the server's current opinion, and the gap is real rather than theoretical.</b>
    /// The server drops a player's session on disconnect (player/manager.go
    /// handleDisconnect) and requires a fresh LoginRequest - with the reconnect
    /// token - before it will answer anything else. Nothing invalidates this type
    /// when that happens, because nothing in the harness reconnects yet: a dropped
    /// link faults the session and stays down until something calls StopAsync and
    /// StartAsync, which is tracked as open work in docs/migration-checklist.md
    /// under "Add reconnect policy". Consumers therefore pair this with
    /// <see cref="ISessionStatus.State"/>, which does go Disconnected, and treat the
    /// server's own <c>"not logged in"</c> refusal as the authority. Whoever lands
    /// reconnect owns clearing this.</para>
    /// </summary>
    public interface ICurrentPlayer
    {
        bool IsLoggedIn { get; }

        /// <summary>The id the server issued, or null before a successful login.</summary>
        string PlayerId { get; }
    }

    /// <summary>
    /// The writable half. <see cref="LoginUseCase"/> is its only writer, and that
    /// is deliberate: a successful LoginResponse is the one place in the process
    /// where "we are now player X" becomes true, so putting the write anywhere else
    /// would mean deriving it from something that had already thrown the fact away.
    /// </summary>
    public sealed class CurrentPlayer : ICurrentPlayer
    {
        public string PlayerId { get; private set; }

        public bool IsLoggedIn => !string.IsNullOrEmpty(PlayerId);

        /// <summary>
        /// A blank id leaves this unchanged rather than clearing it. The server
        /// issues a non-empty id on every success (matchmaking.go handleLogin), so
        /// a blank one means the response decoded wrongly - and treating a decode
        /// failure as a logout would replace a visible bug with a silent one.
        /// </summary>
        public void RecordLogin(string playerId)
        {
            if (string.IsNullOrEmpty(playerId))
            {
                return;
            }

            PlayerId = playerId;
        }
    }
}
