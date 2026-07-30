using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Echo.Harness.Contracts
{
    /// <summary>
    /// Message 5001 - damage settlement detail, sent to both players. The Go
    /// names are authoritative; the legacy Godot client read seat, amount, and
    /// damage_type, none of which the server ever sent.
    /// </summary>
    public sealed class DamageEventDto
    {
        [JsonProperty("attacker_seat")]
        public int AttackerSeat { get; set; }

        [JsonProperty("defender_seat")]
        public int DefenderSeat { get; set; }

        [JsonProperty("raw_damage")]
        public int RawDamage { get; set; }

        /// <summary>Actual HP lost, after reflection, absorption, and reduction.</summary>
        [JsonProperty("final_damage")]
        public int FinalDamage { get; set; }

        [JsonProperty("hp_after")]
        public int HpAfter { get; set; }

        [JsonProperty("detail")]
        public string Detail { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 5002 - a skill was used. This also reveals the character, so it
    /// doubles as the disclosure event.
    /// </summary>
    public sealed class SkillUsedEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("skill_level")]
        public int SkillLevel { get; set; }

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 5003 - liberation triggered. Go names the seat player_seat; the
    /// legacy client expected seat.
    /// </summary>
    public sealed class LiberationEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 5004 - field effect applied. Go sends three fields; the legacy
    /// client expected a single field_effect string.
    /// </summary>
    public sealed class FieldEffectEventDto
    {
        [JsonProperty("effect_id")]
        public string EffectId { get; set; } = string.Empty;

        [JsonProperty("effect_name")]
        public string EffectName { get; set; } = string.Empty;

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    /// <summary>Message 5005 - incremental HP and energy update.</summary>
    public sealed class PlayerStatusEventDto
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
    }

    /// <summary>Message 5006 - the game ended.</summary>
    public sealed class GameOverEventDto
    {
        [JsonProperty("winner_seat")]
        public int WinnerSeat { get; set; }

        [JsonProperty("reason")]
        public string Reason { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 5007 - a non-fatal operation error. The connection stays open;
    /// the client is expected to correct the input and retry.
    /// </summary>
    public sealed class ErrorEventDto
    {
        [JsonProperty("code")]
        public int Code { get; set; }

        [JsonProperty("message")]
        public string Message { get; set; } = string.Empty;
    }

    /// <summary>Message 5008 - blessing triggered below 40 HP; a second character is granted.</summary>
    public sealed class BlessingEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("second_char_id")]
        public string SecondCharId { get; set; } = string.Empty;

        [JsonProperty("second_char_name")]
        public string SecondCharName { get; set; } = string.Empty;
    }

    /// <summary>Message 5009 - an attack is incoming and the defense window is open.</summary>
    public sealed class IncomingAttackEventDto
    {
        [JsonProperty("attacker_seat")]
        public int AttackerSeat { get; set; }

        [JsonProperty("attack_points")]
        public int AttackPoints { get; set; }
    }

    /// <summary>Message 5010 - action countdown, pushed once per second.</summary>
    public sealed class TurnTimerEventDto
    {
        [JsonProperty("active_seat")]
        public int ActiveSeat { get; set; }

        [JsonProperty("seconds_left")]
        public int SecondsLeft { get; set; }
    }

    /// <summary>
    /// Message 5011 - character and field data. The server side is
    /// []map[string]any with no fixed schema, so these stay opaque rather than
    /// inventing a shape the server does not actually guarantee.
    /// </summary>
    public sealed class GameConfigEventDto
    {
        [JsonProperty("characters")]
        public IReadOnlyList<JObject> Characters { get; set; }

        [JsonProperty("fields")]
        public IReadOnlyList<JObject> Fields { get; set; }

        [JsonProperty("config_hash")]
        public string ConfigHash { get; set; } = string.Empty;
    }

    /// <summary>
    /// Message 5012 - a card was played, visible to both players. Points is
    /// nullable: null means the point value is hidden from the viewer and must
    /// never be read as a zero.
    /// </summary>
    public sealed class CardPlayedEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("card_type")]
        public string CardType { get; set; } = string.Empty;

        /// <summary>One of the Unicode suit symbols.</summary>
        [JsonProperty("suit")]
        public string Suit { get; set; } = string.Empty;

        [JsonProperty("points")]
        public int? Points { get; set; }
    }

    /// <summary>Message 5013 - Suou hit zero HP and entered the 15s revival dialog.</summary>
    public sealed class DeathDialogEventDto
    {
        [JsonProperty("seat")]
        public int Seat { get; set; }

        /// <summary>Unix milliseconds at which the dialog times out.</summary>
        [JsonProperty("deadline_ms")]
        public long DeadlineMs { get; set; }

        [JsonProperty("duration_sec")]
        public int DurationSec { get; set; }
    }

    /// <summary>Reports the wire names a damage event actually serializes.</summary>
    public static class DamageEventDtoContract
    {
        public static IReadOnlyList<string> SerializePropertyNames(DamageEventDto dto)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(dto));
            return json.Properties().Select(property => property.Name).ToArray();
        }
    }
}
