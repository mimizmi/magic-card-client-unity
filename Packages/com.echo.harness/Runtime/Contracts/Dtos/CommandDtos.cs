using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// References a card in the hand or synthesis zone. Zone is "hand" or
    /// "synth"; Slot is 1-indexed.
    /// </summary>
    public sealed class CardRefDto
    {
        [JsonProperty("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonProperty("slot")]
        public int Slot { get; set; }
    }

    /// <summary>
    /// Message 4001 - play a card. Zone is "hand" or "synth"; Slot is 1-8 in the
    /// hand zone and 1-4 in the synthesis zone.
    /// </summary>
    public sealed class PlayCardRequestDto
    {
        [JsonProperty("zone")]
        public string Zone { get; set; } = string.Empty;

        [JsonProperty("slot")]
        public int Slot { get; set; }
    }

    /// <summary>Message 4002 - move a hand card into the synthesis zone.</summary>
    public sealed class MoveToSynthesisRequestDto
    {
        [JsonProperty("hand_slot")]
        public int HandSlot { get; set; }

        // Go: `target_slot,omitempty` with 0 meaning auto. int is a value type,
        // so DefaultValueHandling.Ignore is what reproduces omitempty here.
        [JsonProperty("target_slot", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int TargetSlot { get; set; }
    }

    /// <summary>Message 4003 - synthesize two cards.</summary>
    public sealed class SynthesizeRequestDto
    {
        [JsonProperty("slot1")]
        public int Slot1 { get; set; }

        [JsonProperty("zone1")]
        public string Zone1 { get; set; } = string.Empty;

        [JsonProperty("slot2")]
        public int Slot2 { get; set; }

        [JsonProperty("zone2")]
        public string Zone2 { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 4004 - use an active skill. The skill card's point value decides
    /// whether the first- or second-tier effect fires.
    /// </summary>
    public sealed class UseSkillRequestDto
    {
        [JsonProperty("skill_card_slot")]
        public int SkillCardSlot { get; set; }
    }

    /// <summary>Message 4005 - manually trigger liberation. Empty payload.</summary>
    public sealed class TriggerLiberationRequestDto
    {
    }

    /// <summary>Message 4006 - end the action phase. Empty payload.</summary>
    public sealed class EndActionRequestDto
    {
    }

    /// <summary>
    /// Message 4007 - respond to an incoming attack. Pass true to take the full
    /// damage; otherwise name the card that absorbs it.
    /// </summary>
    public sealed class DefenseRequestDto
    {
        [JsonProperty("pass")]
        public bool Pass { get; set; }

        [JsonProperty("zone", NullValueHandling = NullValueHandling.Ignore)]
        public string Zone { get; set; }

        // Go: `slot,omitempty` on an int. See MoveToSynthesisRequestDto.
        [JsonProperty("slot", DefaultValueHandling = DefaultValueHandling.Ignore)]
        public int Slot { get; set; }
    }

    /// <summary>Message 4008 - request the full game config. Empty payload.</summary>
    public sealed class GameConfigRequestDto
    {
    }

    /// <summary>Message 4009 - surrender, already confirmed client-side. Empty payload.</summary>
    public sealed class SurrenderRequestDto
    {
    }

    /// <summary>Message 4010 - Suou revival: submit two cards to spend.</summary>
    public sealed class ReviveRequestDto
    {
        [JsonProperty("card1")]
        public CardRefDto Card1 { get; set; }

        [JsonProperty("card2")]
        public CardRefDto Card2 { get; set; }
    }
}
