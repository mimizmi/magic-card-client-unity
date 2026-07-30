using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    /// <summary>Message 3 - client-initiated latency probe.</summary>
    public sealed class ClientPingRequestDto
    {
        /// <summary>Unix milliseconds at send time; the client computes now - ts.</summary>
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
