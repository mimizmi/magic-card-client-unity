using System;
using System.Collections.Generic;
using System.IO;
using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    public sealed class ProtocolContractDocument
    {
        [JsonProperty("version")]
        public string Version { get; set; } = string.Empty;

        [JsonProperty("frame")]
        public ProtocolFrameDocument Frame { get; set; } = new ProtocolFrameDocument();

        [JsonProperty("messages")]
        public List<ProtocolMessageDocument> Messages { get; set; } =
            new List<ProtocolMessageDocument>();
    }

    public sealed class ProtocolFrameDocument
    {
        [JsonProperty("byte_order")]
        public string ByteOrder { get; set; } = string.Empty;

        [JsonProperty("length_prefix_bytes")]
        public int LengthPrefixBytes { get; set; }

        [JsonProperty("message_id_bytes")]
        public int MessageIdBytes { get; set; }

        [JsonProperty("length_includes_message_id")]
        public bool LengthIncludesMessageId { get; set; }

        [JsonProperty("max_payload_bytes")]
        public int MaxPayloadBytes { get; set; }
    }

    public sealed class ProtocolMessageDocument
    {
        [JsonProperty("id")]
        public ushort Id { get; set; }

        [JsonProperty("name")]
        public string Name { get; set; } = string.Empty;

        [JsonProperty("direction")]
        public string Direction { get; set; } = string.Empty;

        [JsonProperty("kind")]
        public string Kind { get; set; } = string.Empty;
    }

    public static class ProtocolContractFixture
    {
        public const string RelativePath =
            "Packages/com.echo.harness/Fixtures/protocol.contract.json";

        public static ProtocolContractDocument Load(string projectRoot = null)
        {
            var root = string.IsNullOrWhiteSpace(projectRoot)
                ? Directory.GetCurrentDirectory()
                : projectRoot;
            var path = Path.GetFullPath(Path.Combine(root, RelativePath));
            if (!File.Exists(path))
            {
                throw new FileNotFoundException("Protocol contract fixture was not found.", path);
            }

            var document = JsonConvert.DeserializeObject<ProtocolContractDocument>(
                File.ReadAllText(path));
            return document ?? throw new InvalidDataException(
                "Protocol contract fixture deserialized to null.");
        }
    }
}
