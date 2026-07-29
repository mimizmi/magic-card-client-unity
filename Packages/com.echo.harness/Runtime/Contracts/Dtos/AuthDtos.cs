using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// Message 1001 - first login or reconnect. Leave ReconnectToken null on a
    /// first login; send the token the server issued to resume a session.
    /// </summary>
    public sealed class LoginRequestDto
    {
        [JsonProperty("player_name")]
        public string PlayerName { get; set; } = string.Empty;

        [JsonProperty("reconnect_token", NullValueHandling = NullValueHandling.Ignore)]
        public string ReconnectToken { get; set; }
    }

    /// <summary>Message 1002 - login result.</summary>
    public sealed class LoginResponseDto
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("player_id", NullValueHandling = NullValueHandling.Ignore)]
        public string PlayerId { get; set; }

        /// <summary>The client persists this to reconnect after a drop.</summary>
        [JsonProperty("reconnect_token", NullValueHandling = NullValueHandling.Ignore)]
        public string ReconnectToken { get; set; }

        // bool can never be null, so NullValueHandling.Ignore would be a no-op.
        // DefaultValueHandling.Ignore is what reproduces Go's omitempty here.
        [JsonProperty("in_game", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public bool InGame { get; set; }

        [JsonProperty("config_hash", NullValueHandling = NullValueHandling.Ignore)]
        public string ConfigHash { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }
    }
}
