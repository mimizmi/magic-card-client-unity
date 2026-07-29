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

        [JsonProperty("types")]
        public Dictionary<string, ProtocolTypeDocument> Types { get; set; } =
            new Dictionary<string, ProtocolTypeDocument>();

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

        [JsonProperty("go_type")]
        public string GoType { get; set; } = string.Empty;

        [JsonProperty("payload")]
        public ProtocolPayloadDocument Payload { get; set; } = new ProtocolPayloadDocument();
    }

    /// <summary>
    /// A message payload. <see cref="Shape"/> is one of "struct" (fields
    /// present), "empty" (an empty Go struct that serializes to <c>{}</c>), or
    /// "none" (no payload at all, as with Ping and Pong).
    /// </summary>
    public sealed class ProtocolPayloadDocument
    {
        [JsonProperty("shape")]
        public string Shape { get; set; } = string.Empty;

        [JsonProperty("fields")]
        public List<ProtocolFieldDocument> Fields { get; set; } =
            new List<ProtocolFieldDocument>();
    }

    /// <summary>A nested view type referenced by one or more payload fields.</summary>
    public sealed class ProtocolTypeDocument
    {
        [JsonProperty("fields")]
        public List<ProtocolFieldDocument> Fields { get; set; } =
            new List<ProtocolFieldDocument>();
    }

    public sealed class ProtocolFieldDocument
    {
        [JsonProperty("json_name")]
        public string JsonName { get; set; } = string.Empty;

        [JsonProperty("go_type")]
        public string GoType { get; set; } = string.Empty;

        /// <summary>
        /// Names an entry in the document's <c>types</c> dictionary, or empty
        /// for a scalar field. The generator omits it when empty.
        /// </summary>
        [JsonProperty("type_ref")]
        public string TypeRef { get; set; } = string.Empty;

        /// <summary>True for a Go slice field. The generator omits it when false.</summary>
        [JsonProperty("repeated")]
        public bool Repeated { get; set; }

        /// <summary>
        /// True when the Go type can marshal to JSON null — pointers, slices,
        /// maps, and interfaces. A field marked here must map to a nullable C#
        /// type; collapsing a null into a default value would, for card point
        /// values, turn "hidden from this player" into a real number.
        /// </summary>
        [JsonProperty("nullable")]
        public bool Nullable { get; set; }

        [JsonProperty("omitempty")]
        public bool OmitEmpty { get; set; }
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
