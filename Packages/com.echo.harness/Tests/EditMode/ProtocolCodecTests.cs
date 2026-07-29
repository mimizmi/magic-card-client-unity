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
            // The dispatcher keys on this, so it has to survive the round trip.
            Assert.That(result.MessageId, Is.EqualTo(MessageId.LoginResponse));
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
            Assert.That(result.MessageId, Is.EqualTo(MessageId.Ping));
        }

        [Test]
        public void Decode_ReportsAnUnknownMessageId()
        {
            var result = ProtocolCodec.Decode((MessageId)9999, Array.Empty<byte>());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.UnknownMessageId));
            Assert.That(result.Diagnostic, Does.Contain("9999"));
            // A failure still has to name the message it came from, or the caller
            // cannot say which message it dropped.
            Assert.That(result.MessageId, Is.EqualTo((MessageId)9999));
        }

        [Test]
        public void Decode_ReportsMalformedJsonWithoutThrowing()
        {
            var result = ProtocolCodec.Decode(
                MessageId.LoginResponse, Encoding.UTF8.GetBytes("{not json"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
            Assert.That(result.Diagnostic, Is.Not.Empty);
            Assert.That(result.MessageId, Is.EqualTo(MessageId.LoginResponse));
            // The exception type is part of the diagnostic so that a throw from
            // somewhere other than the reader is not disguised as bad input.
            Assert.That(result.Diagnostic, Does.Contain("Exception"));
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
            Assert.That(result.Diagnostic, Does.Contain("empty"));
        }

        [Test]
        public void Decode_TreatsANullBodyLikeAnEmptyOne()
        {
            // A transport that reads a zero-length frame may hand back null rather
            // than an empty array; that must not be the one input that throws.
            var result = ProtocolCodec.Decode(MessageId.LoginResponse, null);

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
            Assert.That(result.Diagnostic, Does.Contain("empty"));
        }

        [Test]
        public void Decode_DistinguishesALiteralNullBodyFromAnEmptyOne()
        {
            // Both fail, but a body of "null" is a sender that wrote something,
            // not a sender that wrote nothing. Logging them identically would
            // send someone hunting the wrong fault.
            var result = ProtocolCodec.Decode(
                MessageId.LoginResponse, Encoding.UTF8.GetBytes("null"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
            Assert.That(result.Diagnostic, Does.Contain("null"));
            Assert.That(result.Diagnostic, Does.Not.Contain("empty"));
        }

        [Test]
        public void ResponseFor_PairsEveryRequestThatHasAResponse()
        {
            Assert.That(
                ProtocolMessageMap.ResponseFor[MessageId.ClientPingRequest],
                Is.EqualTo(MessageId.ClientPingResponse));
            Assert.That(
                ProtocolMessageMap.ResponseFor[MessageId.LoginRequest],
                Is.EqualTo(MessageId.LoginResponse));
            Assert.That(
                ProtocolMessageMap.ResponseFor[MessageId.JoinQueueRequest],
                Is.EqualTo(MessageId.JoinQueueResponse));
        }

        [Test]
        public void ResponseFor_CoversEveryFixtureMessageOfKindResponse()
        {
            // Driven from the generated fixture so a server-side addition cannot
            // leave the hand-maintained table silently incomplete.
            var fixture = ProtocolContractFixture.Load();
            var responseIds = new System.Collections.Generic.List<MessageId>();
            foreach (var message in fixture.Messages)
            {
                if (message.Kind == "response")
                {
                    responseIds.Add((MessageId)message.Id);
                }
            }

            Assert.That(responseIds, Is.Not.Empty);
            Assert.That(
                ProtocolMessageMap.ResponseFor.Values,
                Is.EquivalentTo(responseIds),
                "Every fixture response must be paired exactly once in ResponseFor.");
        }
    }
}
