namespace Echo.Harness.Application
{
    public enum QueueResult
    {
        Joined,
        Rejected,
        NoAnswer
    }

    /// <summary>
    /// What one attempt to join the matchmaking queue produced.
    ///
    /// <para>Shaped like <see cref="LoginOutcome"/> and for the same reason: "the
    /// server refused" and "the server never answered" lead to different next
    /// actions, and this is the only place both are known. The server's two
    /// refusals are <c>"not logged in"</c> and <c>"already in a game"</c>
    /// (matchmaking.go handleJoinQueue), which a user resolves by logging in and
    /// by finishing or leaving the game respectively - neither of which a retry
    /// fixes, unlike a missing answer.</para>
    ///
    /// <para><b>Joined does not mean matched.</b> It means the server accepted the
    /// player into a FIFO queue and will pair them when a second player arrives.
    /// That may be immediate, may be minutes, and may be never; the pairing shows
    /// up later and out of band as a MatchFoundEvent, which is
    /// <see cref="MatchFoundWatcher"/>'s job rather than this type's.</para>
    /// </summary>
    public readonly struct QueueOutcome
    {
        private QueueOutcome(QueueResult result, string message)
        {
            Result = result;
            Message = message;
        }

        public static QueueOutcome Joined() => new QueueOutcome(QueueResult.Joined, null);

        public static QueueOutcome Refusal(string message) =>
            new QueueOutcome(QueueResult.Rejected, message);

        public static QueueOutcome NoReply(string message) =>
            new QueueOutcome(QueueResult.NoAnswer, message);

        public QueueResult Result { get; }

        /// <summary>The reason, when Rejected or NoAnswer. Null on success.</summary>
        public string Message { get; }
    }
}
