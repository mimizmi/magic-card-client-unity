using System.Collections.Generic;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// One card as presented to a specific player.
    ///
    /// A null <see cref="Points"/> means the server is hiding the value from
    /// this viewer - under the Diamond Realm field effects, for instance. It
    /// must never be read as zero: that would hand the client a number the
    /// server deliberately withheld.
    /// </summary>
    public sealed class CardViewDto
    {
        [JsonProperty("slot")]
        public int Slot { get; set; }

        [JsonProperty("suit")]
        public string Suit { get; set; } = string.Empty;

        [JsonProperty("card_type")]
        public string CardType { get; set; } = string.Empty;

        /// <summary>Display points including passive bonuses; null when hidden.</summary>
        [JsonProperty("points")]
        public int? Points { get; set; }

        /// <summary>
        /// Unmodified points used for synthesis. Sent only when it differs from
        /// <see cref="Points"/>.
        /// </summary>
        [JsonProperty("raw_points", NullValueHandling = NullValueHandling.Ignore)]
        public int? RawPoints { get; set; }
    }

    /// <summary>An open defense window against a pending attack.</summary>
    public sealed class PendingAttackViewDto
    {
        [JsonProperty("attacker_seat")]
        public int AttackerSeat { get; set; }

        [JsonProperty("attack_points")]
        public int AttackPoints { get; set; }
    }

    /// <summary>The full information a player has about themselves.</summary>
    public sealed class PlayerViewDto
    {
        [JsonProperty("seat")]
        public int Seat { get; set; }

        [JsonProperty("hp")]
        public int Hp { get; set; }

        [JsonProperty("max_hp")]
        public int MaxHp { get; set; }

        [JsonProperty("shield_hp")]
        public int ShieldHp { get; set; }

        [JsonProperty("energy")]
        public int Energy { get; set; }

        [JsonProperty("max_energy")]
        public int MaxEnergy { get; set; }

        /// <summary>You always know your own character, even while it reads "???" to the opponent.</summary>
        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("is_near_death")]
        public bool IsNearDeath { get; set; }

        [JsonProperty("hand")]
        public IReadOnlyList<CardViewDto> Hand { get; set; }

        [JsonProperty("synth_zone")]
        public IReadOnlyList<CardViewDto> SynthZone { get; set; }

        /// <summary>Character-specific extra state. No fixed schema server-side.</summary>
        [JsonProperty("extra_info", NullValueHandling = NullValueHandling.Ignore)]
        public JObject ExtraInfo { get; set; }
    }

    /// <summary>
    /// The restricted information a player has about the opponent.
    ///
    /// There is no Hand property, and that omission is the contract: the server
    /// never sends opponent hand contents, only a count. Adding one here would
    /// invite a consumer to expect data that does not exist.
    /// </summary>
    public sealed class OpponentViewDto
    {
        [JsonProperty("seat")]
        public int Seat { get; set; }

        [JsonProperty("hp")]
        public int Hp { get; set; }

        [JsonProperty("max_hp")]
        public int MaxHp { get; set; }

        [JsonProperty("shield_hp")]
        public int ShieldHp { get; set; }

        [JsonProperty("energy")]
        public int Energy { get; set; }

        [JsonProperty("max_energy")]
        public int MaxEnergy { get; set; }

        /// <summary>"???" until a skill reveals the character.</summary>
        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("is_near_death")]
        public bool IsNearDeath { get; set; }

        /// <summary>Card count only; the contents stay server-side.</summary>
        [JsonProperty("hand_count")]
        public int HandCount { get; set; }

        [JsonProperty("synth_count")]
        public int SynthCount { get; set; }

        /// <summary>Opponent state that is public by design, such as shield stacks.</summary>
        [JsonProperty("public_extra", NullValueHandling = NullValueHandling.Ignore)]
        public JObject PublicExtra { get; set; }
    }

    /// <summary>
    /// Message 3001 - the player-specific authoritative state snapshot. The
    /// server builds one of these per seat so neither client sees the other's
    /// hidden information.
    /// </summary>
    public sealed class GameStateEventDto
    {
        [JsonProperty("round")]
        public int Round { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; } = string.Empty;

        [JsonProperty("active_seat")]
        public int ActiveSeat { get; set; }

        [JsonProperty("field_effect")]
        public string FieldEffect { get; set; } = string.Empty;

        /// <summary>Non-null only while a defense window is open.</summary>
        [JsonProperty("pending_attack", NullValueHandling = NullValueHandling.Ignore)]
        public PendingAttackViewDto PendingAttack { get; set; }

        [JsonProperty("me")]
        public PlayerViewDto Me { get; set; }

        [JsonProperty("opponent")]
        public OpponentViewDto Opponent { get; set; }
    }

    /// <summary>Message 3002 - phase transition notice.</summary>
    public sealed class PhaseChangeEventDto
    {
        [JsonProperty("round")]
        public int Round { get; set; }

        [JsonProperty("phase")]
        public string Phase { get; set; } = string.Empty;

        [JsonProperty("active_seat")]
        public int ActiveSeat { get; set; }

        [JsonProperty("field_effect")]
        public string FieldEffect { get; set; } = string.Empty;
    }
}
