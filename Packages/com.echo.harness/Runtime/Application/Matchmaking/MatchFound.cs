namespace Echo.Harness.Application
{
    /// <summary>
    /// A pairing the server has already made. Delivered rather than requested.
    ///
    /// <para>An Application type and not <c>MatchFoundEventDto</c> for the reason
    /// <see cref="LoginOutcome"/> gives: <c>Echo.Harness.Presentation</c> does not
    /// reference <c>Echo.Harness.Contracts</c> and structurally cannot name a wire
    /// DTO.</para>
    ///
    /// <para><see cref="Seat"/> is 0 or 1 and decides who acts first. The server
    /// randomises it (matchmaking.go tryMatch) rather than giving it to whoever
    /// queued first, so a client must never infer its seat from its own
    /// timing.</para>
    /// </summary>
    public readonly struct MatchFound
    {
        public MatchFound(string gameId, int seat, string opponentName)
        {
            GameId = gameId;
            Seat = seat;
            OpponentName = opponentName;
        }

        public string GameId { get; }

        public int Seat { get; }

        public string OpponentName { get; }
    }
}
