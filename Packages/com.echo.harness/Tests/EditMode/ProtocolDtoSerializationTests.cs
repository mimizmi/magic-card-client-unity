using Echo.Harness.Contracts;
using Newtonsoft.Json;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    /// <summary>
    /// Everything in ProtocolDtoContractTests inspects declared contract
    /// metadata. These tests execute a real serialization instead, because the
    /// behavior that matters most - a hidden card value staying null rather
    /// than becoming zero - is invisible to a metadata check.
    /// </summary>
    public sealed class ProtocolDtoSerializationTests
    {
        private const string HeartSuit = "♥";
        private const string AttackCardType = "攻击";

        [Test]
        public void CardView_HiddenPointsDeserializeToNullNotZero()
        {
            var dto = JsonConvert.DeserializeObject<CardViewDto>(
                "{\"slot\":3,\"suit\":\"" + HeartSuit + "\"," +
                "\"card_type\":\"" + AttackCardType + "\",\"points\":null}");

            Assert.That(dto.Slot, Is.EqualTo(3));
            Assert.That(dto.Suit, Is.EqualTo(HeartSuit));
            Assert.That(dto.CardType, Is.EqualTo(AttackCardType));
            Assert.That(dto.Points, Is.Null, "A null points value means hidden and must never become 0.");
            Assert.That(dto.RawPoints, Is.Null, "An absent raw_points must stay null.");
        }

        [Test]
        public void CardView_ZeroPointsStaysDistinctFromHidden()
        {
            var visible = JsonConvert.DeserializeObject<CardViewDto>(
                "{\"slot\":1,\"suit\":\"" + HeartSuit + "\",\"card_type\":\"x\",\"points\":0}");
            var hidden = JsonConvert.DeserializeObject<CardViewDto>(
                "{\"slot\":1,\"suit\":\"" + HeartSuit + "\",\"card_type\":\"x\",\"points\":null}");

            Assert.That(visible.Points, Is.EqualTo(0));
            Assert.That(hidden.Points, Is.Null);
            Assert.That(
                visible.Points, Is.Not.EqualTo(hidden.Points),
                "Zero points and hidden points must remain distinguishable.");
        }

        [Test]
        public void GameStateEvent_DeserializesTheWholeNestedTree()
        {
            const string json =
                "{\"round\":2,\"phase\":\"action\",\"active_seat\":1,\"field_effect\":\"\"," +
                "\"pending_attack\":{\"attacker_seat\":0,\"attack_points\":7}," +
                "\"me\":{\"seat\":1,\"hp\":30,\"max_hp\":50,\"shield_hp\":0,\"energy\":4," +
                "\"max_energy\":10,\"character\":\"???\",\"is_near_death\":false," +
                "\"hand\":[{\"slot\":1,\"suit\":\"" + HeartSuit + "\",\"card_type\":\"x\"," +
                "\"points\":5,\"raw_points\":3}]," +
                "\"synth_zone\":[],\"extra_info\":{\"rift_count\":2}}," +
                "\"opponent\":{\"seat\":0,\"hp\":40,\"max_hp\":50,\"shield_hp\":2,\"energy\":1," +
                "\"max_energy\":10,\"character\":\"???\",\"is_near_death\":false," +
                "\"hand_count\":6,\"synth_count\":1}}";

            var dto = JsonConvert.DeserializeObject<GameStateEventDto>(json);

            Assert.That(dto.PendingAttack, Is.Not.Null);
            Assert.That(dto.PendingAttack.AttackPoints, Is.EqualTo(7));
            Assert.That(dto.Me.Hand, Has.Count.EqualTo(1), "IReadOnlyList<CardViewDto> must deserialize.");
            Assert.That(dto.Me.Hand[0].Points, Is.EqualTo(5));
            Assert.That(dto.Me.Hand[0].RawPoints, Is.EqualTo(3));
            Assert.That(dto.Me.Hand[0].Suit, Is.EqualTo(HeartSuit));
            Assert.That(dto.Me.SynthZone, Is.Empty);
            Assert.That((int)dto.Me.ExtraInfo["rift_count"], Is.EqualTo(2));
            Assert.That(dto.Opponent.HandCount, Is.EqualTo(6));
            Assert.That(dto.Opponent.PublicExtra, Is.Null, "An absent public_extra must stay null.");
        }

        [Test]
        public void GameStateEvent_AbsentPendingAttackMeansNoDefenseWindow()
        {
            var dto = JsonConvert.DeserializeObject<GameStateEventDto>(
                "{\"round\":1,\"phase\":\"draw\",\"active_seat\":0,\"field_effect\":\"\"}");

            Assert.That(dto.PendingAttack, Is.Null);
        }

        [Test]
        public void MoveToSynthesisRequest_OmitsTheDefaultTargetSlot()
        {
            Assert.That(
                JsonConvert.SerializeObject(new MoveToSynthesisRequestDto { HandSlot = 2 }),
                Is.EqualTo("{\"hand_slot\":2}"),
                "target_slot carries omitempty in Go and must vanish at its default.");

            Assert.That(
                JsonConvert.SerializeObject(new MoveToSynthesisRequestDto { HandSlot = 2, TargetSlot = 3 }),
                Is.EqualTo("{\"hand_slot\":2,\"target_slot\":3}"));
        }

        [Test]
        public void MoveToSynthesisRequest_KeepsAZeroHandSlot()
        {
            Assert.That(
                JsonConvert.SerializeObject(new MoveToSynthesisRequestDto()),
                Is.EqualTo("{\"hand_slot\":0}"),
                "hand_slot has no omitempty in Go and must always be sent.");
        }

        [Test]
        public void DefenseRequest_OmitsZoneAndSlotWhenPassing()
        {
            Assert.That(
                JsonConvert.SerializeObject(new DefenseRequestDto { Pass = true }),
                Is.EqualTo("{\"pass\":true}"));
        }

        [Test]
        public void LoginRequest_OmitsANullReconnectToken()
        {
            Assert.That(
                JsonConvert.SerializeObject(new LoginRequestDto { PlayerName = "echo" }),
                Is.EqualTo("{\"player_name\":\"echo\"}"));
        }
    }
}
