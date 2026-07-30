using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>Message 2001 - join the matchmaking queue.</summary>
    public sealed class JoinQueueRequestDto
    {
        [JsonProperty("player_id")]
        public string PlayerId { get; set; } = string.Empty;
    }

    /// <summary>Message 2002 - queue join result.</summary>
    public sealed class JoinQueueResponseDto
    {
        [JsonProperty("success")]
        public bool Success { get; set; }

        [JsonProperty("error", NullValueHandling = NullValueHandling.Ignore)]
        public string Error { get; set; }
    }

    /// <summary>Message 2004 - a match was found; character selection begins.</summary>
    public sealed class MatchFoundEventDto
    {
        [JsonProperty("game_id")]
        public string GameId { get; set; } = string.Empty;

        /// <summary>Seat 0 or 1, which decides who acts first.</summary>
        [JsonProperty("your_seat")]
        public int YourSeat { get; set; }

        [JsonProperty("opponent_name")]
        public string OpponentName { get; set; } = string.Empty;
    }

    /// <summary>Message 2005 - select a character face-down.</summary>
    public sealed class SelectCharacterRequestDto
    {
        [JsonProperty("character_id")]
        public string CharacterId { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 2006 - both players have selected. Both seat characters arrive as
    /// "???" and stay hidden until a skill reveals them.
    /// </summary>
    public sealed class GameStartEventDto
    {
        [JsonProperty("game_id")]
        public string GameId { get; set; } = string.Empty;

        [JsonProperty("seat0_char")]
        public string Seat0Char { get; set; } = string.Empty;

        [JsonProperty("seat1_char")]
        public string Seat1Char { get; set; } = string.Empty;
    }

    /// <summary>Message 2007 - create an AI match without queueing.</summary>
    public sealed class CreateAiGameRequestDto
    {
        [JsonProperty("player_char_id")]
        public string PlayerCharId { get; set; } = string.Empty;

        [JsonProperty("ai_char_id")]
        public string AiCharId { get; set; } = string.Empty;
    }
}
