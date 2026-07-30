using System;
using System.Linq;
using System.Text;
using Echo.Harness.Contracts;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolContractTests
    {
        [Test]
        public void MessageIds_AreUniqueAndContainKnownServerEndpoints()
        {
            var values = Enum.GetValues(typeof(MessageId)).Cast<ushort>().ToArray();

            Assert.That(values, Has.Length.EqualTo(39));
            Assert.That(values.Distinct().Count(), Is.EqualTo(values.Length));
            Assert.That((ushort)MessageId.LoginRequest, Is.EqualTo(1001));
            Assert.That((ushort)MessageId.CreateAiGameRequest, Is.EqualTo(2007));
            Assert.That((ushort)MessageId.GameStateEvent, Is.EqualTo(3001));
            Assert.That((ushort)MessageId.DeathDialogEvent, Is.EqualTo(5013));
        }

        [Test]
        public void BinaryFrame_UsesBigEndianLengthThenMessageId()
        {
            var payload = Encoding.UTF8.GetBytes("{\"probe\":true}");

            var encoded = BinaryFrameCodec.Encode(MessageId.Ping, payload);
            var decoded = BinaryFrameCodec.Decode(encoded);

            Assert.That(encoded, Has.Length.EqualTo(
                WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes + payload.Length));
            Assert.That(encoded.Take(4).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 14 }));
            Assert.That(encoded.Skip(4).Take(2).ToArray(), Is.EqualTo(new byte[] { 0, 1 }));
            Assert.That(decoded.MessageId, Is.EqualTo(MessageId.Ping));
            Assert.That(decoded.Payload.ToArray(), Is.EqualTo(payload));
        }

        [Test]
        public void BinaryFrame_AcceptsAZeroLengthPayload()
        {
            // The server sends heartbeats as Send(MsgIDPing, nil), so a frame
            // with no body at all has to be legal in both directions.
            var encoded = BinaryFrameCodec.Encode(MessageId.Ping, Array.Empty<byte>());

            Assert.That(encoded, Has.Length.EqualTo(
                WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes));
            Assert.That(encoded.Take(4).ToArray(), Is.EqualTo(new byte[] { 0, 0, 0, 0 }));

            var decoded = BinaryFrameCodec.Decode(encoded);

            Assert.That(decoded.MessageId, Is.EqualTo(MessageId.Ping));
            Assert.That(decoded.Payload.Length, Is.EqualTo(0));
        }

        [Test]
        public void BinaryFrame_RoundTripsUnicodeSuitSymbols()
        {
            // Card suits are the Unicode symbols themselves, not ASCII codes,
            // so the frame has to carry multi-byte UTF-8 through intact.
            const string body = "{\"suit\":\"♥\",\"suits\":[\"♦\",\"♣\",\"♠\"]}";
            var payload = Encoding.UTF8.GetBytes(body);

            var decoded = BinaryFrameCodec.Decode(
                BinaryFrameCodec.Encode(MessageId.CardPlayedEvent, payload));

            Assert.That(Encoding.UTF8.GetString(decoded.Payload.ToArray()), Is.EqualTo(body));
        }

        [Test]
        public void DamageEvent_UsesAuthoritativeGoJsonNames()
        {
            var dto = new DamageEventDto
            {
                AttackerSeat = 0,
                DefenderSeat = 1,
                RawDamage = 5,
                FinalDamage = 3,
                HpAfter = 7,
                Detail = "fixture"
            };

            var propertyNames = DamageEventDtoContract.SerializePropertyNames(dto);

            Assert.That(propertyNames, Is.EquivalentTo(new[]
            {
                "attacker_seat",
                "defender_seat",
                "raw_damage",
                "final_damage",
                "hp_after",
                "detail"
            }));
            Assert.That(propertyNames, Does.Not.Contain("seat"));
            Assert.That(propertyNames, Does.Not.Contain("amount"));
        }

        [Test]
        public void ContractFixture_MatchesTheTypedMessageIdSet()
        {
            var fixture = ProtocolContractFixture.Load();
            var enumIds = Enum.GetValues(typeof(MessageId)).Cast<ushort>().OrderBy(value => value);
            var fixtureIds = fixture.Messages.Select(message => message.Id).OrderBy(value => value);

            Assert.That(fixture.Version, Is.EqualTo("legacy-v1"));
            Assert.That(fixture.Frame.MaxPayloadBytes, Is.EqualTo(1_048_576));
            Assert.That(fixtureIds, Is.EqualTo(enumIds));
        }
    }
}
