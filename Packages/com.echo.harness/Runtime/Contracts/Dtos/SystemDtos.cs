using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>Message 3 - client-initiated latency probe.</summary>
    public sealed class ClientPingRequestDto
    {
        /// <summary>Unix milliseconds at send time. The client measures the round
        /// trip with a monotonic source rather than by differencing this; the echo
        /// is what correlates a reply with the probe that asked for it.</summary>
        [JsonProperty("ts")]
        public long Ts { get; set; }
    }

    /// <summary>Message 4 - the server echoes the client timestamp back.</summary>
    public sealed class ClientPingResponseDto
    {
        [JsonProperty("ts")]
        public long Ts { get; set; }
    }
}
