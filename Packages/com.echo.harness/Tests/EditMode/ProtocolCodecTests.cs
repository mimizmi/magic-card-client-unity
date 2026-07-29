using System;
using System.Text;
using Echo.Harness.Contracts;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolCodecTests
    {
        [Test]
        public void EncodePayload_ProducesTheGoJsonNames()
        {
            var bytes = ProtocolCodec.EncodePayload(
                new LoginRequestDto { PlayerName = "echo" });

            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("{\"player_name\":\"echo\"}"));
        }

        [Test]
        public void EncodePayload_TreatsNullAsAnEmptyBody()
        {
            // Ping and Pong are sent as Send(id, nil) on the Go side, which puts
            // zero payload bytes on the wire - not the two bytes of "{}".
            Assert.That(ProtocolCodec.EncodePayload(null), Is.Empty);
        }

        [Test]
        public void Decode_RoundTripsAStructPayload()
        {
            var bytes = ProtocolCodec.EncodePayload(
                new LoginResponseDto { Success = true, PlayerId = "p-1" });

            var result = ProtocolCodec.Decode(MessageId.LoginResponse, bytes);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.None));
            var payload = (LoginResponseDto)result.Payload;
            Assert.That(payload.Success, Is.True);
            Assert.That(payload.PlayerId, Is.EqualTo("p-1"));
        }

        [Test]
        public void Decode_HandlesAnEmptyStructPayload()
        {
            var result = ProtocolCodec.Decode(
                MessageId.EndActionRequest, Encoding.UTF8.GetBytes("{}"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Payload, Is.InstanceOf<EndActionRequestDto>());
        }

        [Test]
        public void Decode_ReturnsANullPayloadForMessagesThatCarryNone()
        {
            // Shape "none" messages are not inspected at all. Ping is one of them,
            // and refusing a Ping would mean never answering with Pong, which the
            // server reads as a dead connection.
            var result = ProtocolCodec.Decode(
                MessageId.Ping, Encoding.UTF8.GetBytes("unexpected junk"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Payload, Is.Null);
        }

        [Test]
        public void Decode_ReportsAnUnknownMessageId()
        {
            var result = ProtocolCodec.Decode((MessageId)9999, Array.Empty<byte>());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.UnknownMessageId));
            Assert.That(result.Diagnostic, Does.Contain("9999"));
        }

        [Test]
        public void Decode_ReportsMalformedJsonWithoutThrowing()
        {
            var result = ProtocolCodec.Decode(
                MessageId.LoginResponse, Encoding.UTF8.GetBytes("{not json"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
            Assert.That(result.Diagnostic, Is.Not.Empty);
        }

        [Test]
        public void Decode_ReportsAnEmptyBodyForAMessageThatRequiresOne()
        {
            // Go always emits at least "{}" for a non-nil struct, so an empty body
            // on a registered type is a real anomaly. Returning null here instead
            // would hand a null to a subscriber whose handler promises a value.
            var result = ProtocolCodec.Decode(MessageId.LoginResponse, Array.Empty<byte>());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
        }
    }
}
