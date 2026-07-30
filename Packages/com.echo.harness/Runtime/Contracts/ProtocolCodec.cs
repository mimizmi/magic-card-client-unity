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

    /// <summary>
    /// Decoding never throws; a malformed body comes back as a failure result. That
    /// is the opposite error model to <see cref="BinaryFrameCodec"/> in this same
    /// directory, which throws on every malformed input, and the split is deliberate
    /// rather than an inconsistency. A malformed frame is stream-fatal because the
    /// byte boundaries are lost and every later read returns garbage; a malformed
    /// body costs exactly one message and must not disconnect a player, which is
    /// what a receive loop needs in order to grade the two differently. Do not infer
    /// either model from the other.
    /// </summary>
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

            // Everything from the byte decode onwards sits inside the try, so the
            // "never throws" guarantee covers the whole conversion and not just
            // the deserializer.
            try
            {
                var json = payload == null ? string.Empty : Encoding.UTF8.GetString(payload);
                if (string.IsNullOrWhiteSpace(json))
                {
                    return ProtocolDecodeResult.Failed(
                        messageId,
                        ProtocolDecodeFailure.MalformedPayload,
                        $"{messageId} expects a {payloadType.Name} body but the payload was empty.");
                }

                var dto = JsonConvert.DeserializeObject(json, payloadType);
                if (dto == null)
                {
                    // Distinct from the empty case: the sender wrote a body, and
                    // that body was the JSON literal null.
                    return ProtocolDecodeResult.Failed(
                        messageId,
                        ProtocolDecodeFailure.MalformedPayload,
                        $"{messageId} expects a {payloadType.Name} body but the payload was the JSON literal null.");
                }

                return ProtocolDecodeResult.Ok(messageId, dto);
            }
            catch (Exception exception)
            {
                // Deliberately broad. A DTO could acquire a custom JsonConverter
                // that throws something other than a JsonException, and Task 4's
                // receive pump is built on this method never throwing. The only
                // thing this can swallow is a bug in the lines above, which the
                // exception type name in the diagnostic makes visible.
                return ProtocolDecodeResult.Failed(
                    messageId,
                    ProtocolDecodeFailure.MalformedPayload,
                    $"{exception.GetType().Name}: {exception.Message}");
            }
        }
    }
}
