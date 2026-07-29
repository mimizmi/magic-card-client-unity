using System;
using System.Text;
using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    public enum ProtocolDecodeFailure
    {
        None,
        UnknownMessageId,
        MalformedPayload
    }

    /// <summary>
    /// The outcome of decoding one inbound message. Decoding never throws:
    /// a single bad message must not be able to tear down a live connection,
    /// so the failure is data the caller decides what to do with.
    /// </summary>
    public readonly struct ProtocolDecodeResult
    {
        private ProtocolDecodeResult(
            MessageId messageId,
            object payload,
            ProtocolDecodeFailure failure,
            string diagnostic)
        {
            MessageId = messageId;
            Payload = payload;
            Failure = failure;
            Diagnostic = diagnostic;
        }

        public MessageId MessageId { get; }

        /// <summary>The decoded DTO, or null for a message that carries no payload.</summary>
        public object Payload { get; }

        public ProtocolDecodeFailure Failure { get; }

        public string Diagnostic { get; }

        public bool Succeeded => Failure == ProtocolDecodeFailure.None;

        public static ProtocolDecodeResult Ok(MessageId messageId, object payload) =>
            new ProtocolDecodeResult(messageId, payload, ProtocolDecodeFailure.None, string.Empty);

        public static ProtocolDecodeResult Failed(
            MessageId messageId,
            ProtocolDecodeFailure failure,
            string diagnostic) =>
            new ProtocolDecodeResult(messageId, null, failure, diagnostic);
    }

    public static class ProtocolCodec
    {
        public static byte[] EncodePayload(object payload)
        {
            if (payload == null)
            {
                return Array.Empty<byte>();
            }

            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        }

        public static ProtocolDecodeResult Decode(MessageId messageId, byte[] payload)
        {
            if (!Enum.IsDefined(typeof(MessageId), messageId))
            {
                return ProtocolDecodeResult.Failed(
                    messageId,
                    ProtocolDecodeFailure.UnknownMessageId,
                    $"Message id {(ushort)messageId} is not part of the typed contract.");
            }

            if (!ProtocolMessageMap.PayloadTypes.TryGetValue(messageId, out var payloadType))
            {
                // Shape "none". The body is deliberately not inspected.
                return ProtocolDecodeResult.Ok(messageId, null);
            }

            var json = payload == null ? string.Empty : Encoding.UTF8.GetString(payload);
            try
            {
                var dto = JsonConvert.DeserializeObject(json, payloadType);
                if (dto == null)
                {
                    return ProtocolDecodeResult.Failed(
                        messageId,
                        ProtocolDecodeFailure.MalformedPayload,
                        $"{messageId} expects a {payloadType.Name} body but the payload was empty.");
                }

                return ProtocolDecodeResult.Ok(messageId, dto);
            }
            catch (JsonException exception)
            {
                return ProtocolDecodeResult.Failed(
                    messageId,
                    ProtocolDecodeFailure.MalformedPayload,
                    exception.Message);
            }
        }
    }
}
