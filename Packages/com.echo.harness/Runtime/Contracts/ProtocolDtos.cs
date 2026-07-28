using System.Collections.Generic;
using System.Linq;
using Newtonsoft.Json;
using Newtonsoft.Json.Linq;

namespace Echo.Harness.Contracts
{
    public sealed class DamageEventDto
    {
        [JsonProperty("attacker_seat")]
        public int AttackerSeat { get; set; }

        [JsonProperty("defender_seat")]
        public int DefenderSeat { get; set; }

        [JsonProperty("raw_damage")]
        public int RawDamage { get; set; }

        [JsonProperty("final_damage")]
        public int FinalDamage { get; set; }

        [JsonProperty("hp_after")]
        public int HpAfter { get; set; }

        [JsonProperty("detail")]
        public string Detail { get; set; } = string.Empty;
    }

    public sealed class LiberationEventDto
    {
        [JsonProperty("player_seat")]
        public int PlayerSeat { get; set; }

        [JsonProperty("character")]
        public string Character { get; set; } = string.Empty;

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    public sealed class FieldEffectEventDto
    {
        [JsonProperty("effect_id")]
        public string EffectId { get; set; } = string.Empty;

        [JsonProperty("effect_name")]
        public string EffectName { get; set; } = string.Empty;

        [JsonProperty("desc")]
        public string Description { get; set; } = string.Empty;
    }

    public static class DamageEventDtoContract
    {
        public static IReadOnlyList<string> SerializePropertyNames(DamageEventDto dto)
        {
            var json = JObject.Parse(JsonConvert.SerializeObject(dto));
            return json.Properties().Select(property => property.Name).ToArray();
        }
    }
}
