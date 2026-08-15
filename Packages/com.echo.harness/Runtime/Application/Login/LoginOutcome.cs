namespace Echo.Harness.Application
{
    public enum LoginResult
    {
        Succeeded,
        Rejected,
        NoAnswer
    }

    /// <summary>
    /// What one login attempt produced.
    ///
    /// <para>Three results rather than a boolean, because "the server refused" and
    /// "the server never answered" lead to different next actions - retry the same
    /// name versus check the connection - and collapsing them would throw that
    /// away at the only place it is known.</para>
    ///
    /// <para>This is an Application type and not <c>LoginResponseDto</c> because
    /// <c>Echo.Harness.Presentation</c> does not reference
    /// <c>Echo.Harness.Contracts</c> and structurally cannot name a wire DTO. It
    /// also carries no reconnect token: see <see cref="LoginUseCase"/>.</para>
    /// </summary>
    public readonly struct LoginOutcome
    {
        private LoginOutcome(LoginResult result, string playerId, bool inGame, string message)
        {
            Result = result;
            PlayerId = playerId;
            InGame = inGame;
            Message = message;
        }

        public static LoginOutcome Success(string playerId, bool inGame) =>
            new LoginOutcome(LoginResult.Succeeded, playerId, inGame, null);

        public static LoginOutcome Refusal(string message) =>
            new LoginOutcome(LoginResult.Rejected, null, false, message);

        public static LoginOutcome NoReply(string message) =>
            new LoginOutcome(LoginResult.NoAnswer, null, false, message);

        public LoginResult Result { get; }

        /// <summary>Set only when <see cref="Result"/> is Succeeded.</summary>
        public string PlayerId { get; }

        public bool InGame { get; }

        /// <summary>The reason, when Rejected or NoAnswer. Null on success.</summary>
        public string Message { get; }
    }
}
