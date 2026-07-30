using System;
using System.Buffers.Binary;

namespace Echo.Harness.Contracts
{
    public static class WireFrameSpec
    {
        public const int LengthPrefixBytes = 4;
        public const int MessageIdBytes = 2;
        public const int MaxPayloadBytes = 1_048_576;
    }

    public readonly struct DecodedFrame
    {
        public DecodedFrame(MessageId messageId, byte[] payload)
        {
            MessageId = messageId;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public MessageId MessageId { get; }

        public ReadOnlyMemory<byte> Payload { get; }
    }

    /// <summary>
    /// Every malformed input throws here, which is the opposite error model to
    /// <see cref="ProtocolCodec"/> in this same directory, and the split is
    /// deliberate rather than an inconsistency. A malformed frame means the byte
    /// stream has lost its boundaries, so every later read returns garbage and
    /// there is nothing to salvage; throwing is what lets a receive loop grade it
    /// as fatal and disconnect. A malformed body costs exactly one message and must
    /// not disconnect a player, so <see cref="ProtocolCodec"/> reports it as a
    /// failure result and never throws. Do not infer either model from the other.
    /// </summary>
    public static class BinaryFrameCodec
    {
        public static byte[] Encode(MessageId messageId, ReadOnlySpan<byte> payload)
        {
            if (payload.Length > WireFrameSpec.MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(payload),
                    payload.Length,
                    $"Payload exceeds {WireFrameSpec.MaxPayloadBytes} bytes.");
            }

            var frame = new byte[
                WireFrameSpec.LengthPrefixBytes +
                WireFrameSpec.MessageIdBytes +
                payload.Length];
            BinaryPrimitives.WriteInt32BigEndian(
                frame.AsSpan(0, WireFrameSpec.LengthPrefixBytes),
                payload.Length);
            BinaryPrimitives.WriteUInt16BigEndian(
                frame.AsSpan(WireFrameSpec.LengthPrefixBytes, WireFrameSpec.MessageIdBytes),
                (ushort)messageId);
            payload.CopyTo(frame.AsSpan(
                WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes));
            return frame;
        }

        public static DecodedFrame Decode(ReadOnlySpan<byte> frame)
        {
            var headerBytes = WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes;
            if (frame.Length < headerBytes)
            {
                throw new ArgumentException("Frame is shorter than the six-byte header.", nameof(frame));
            }

            var payloadLength = BinaryPrimitives.ReadInt32BigEndian(
                frame.Slice(0, WireFrameSpec.LengthPrefixBytes));
            if (payloadLength < 0 || payloadLength > WireFrameSpec.MaxPayloadBytes)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(frame),
                    payloadLength,
                    "Frame declares an invalid payload length.");
            }

            if (frame.Length != headerBytes + payloadLength)
            {
                throw new ArgumentException(
                    "Frame size does not match the declared payload length.",
                    nameof(frame));
            }

            var messageId = (MessageId)BinaryPrimitives.ReadUInt16BigEndian(
                frame.Slice(WireFrameSpec.LengthPrefixBytes, WireFrameSpec.MessageIdBytes));
            var payload = frame.Slice(headerBytes, payloadLength).ToArray();
            return new DecodedFrame(messageId, payload);
        }
    }
}
