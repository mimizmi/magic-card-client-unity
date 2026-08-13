using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;
using static Echo.Harness.Tests.EditMode.ProtocolTestFrames;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionDispatchTests
    {
        private static ProtocolSession StartedSession(
            out FakeTransport transport,
            out RecordingSessionScheduler scheduler)
        {
            transport = new FakeTransport();
            scheduler = new RecordingSessionScheduler();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var session = new ProtocolSession(transport, time, time, scheduler);
            session.StartAsync(default).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void Subscribe_DeliversTheTypedPayload()
        {
            using var session = StartedSession(out var transport, out _);
            GameOverEventDto received = null;
            session.Subscribe<GameOverEventDto>(MessageId.GameOverEvent, dto => received = dto);

            transport.EnqueueInbound(Frame(
                MessageId.GameOverEvent, "{\"winner_seat\":1,\"reason\":\"surrender\"}"));

            // Asserting the field values, not just non-null: a handler wired to
            // the wrong id would still receive a default-constructed instance.
            Assert.That(received, Is.Not.Null);
            Assert.That(received.WinnerSeat, Is.EqualTo(1));
            Assert.That(received.Reason, Is.EqualTo("surrender"));
        }

        [Test]
        public void Subscribe_RejectsATypeThatDoesNotMatchTheContract()
        {
            using var session = StartedSession(out _, out _);

            // Caught at subscription time on purpose. Deferring it to dispatch
            // would surface the mistake when the message first arrives, which for
            // a damage event could be ten minutes into a match.
            Assert.Throws<ArgumentException>(
                () => session.Subscribe<LoginResponseDto>(MessageId.DamageEvent, _ => { }));
        }

        /// <summary>
        /// These four ids carry no body at all, so ProtocolCodec.Decode reports
        /// success with a null payload. Rejecting them at subscription time is
        /// the only thing standing between that null and a typed cast in
        /// DeliverToSubscribers, which is why the assertion below pins the
        /// message: an implementation that happened to throw from the
        /// type-comparison branch instead would satisfy a bare Throws and leave
        /// the guarantee resting on an accident.
        /// </summary>
        [TestCase(MessageId.Ping)]
        [TestCase(MessageId.Pong)]
        [TestCase(MessageId.LeaveQueueRequest)]
        [TestCase(MessageId.RokkaActivateRequest)]
        public void Subscribe_RejectsAMessageThatCarriesNoPayload(MessageId messageId)
        {
            using var session = StartedSession(out _, out _);

            var exception = Assert.Throws<ArgumentException>(
                () => session.Subscribe<GameOverEventDto>(messageId, _ => { }));

            Assert.That(
                exception.Message,
                Does.Contain("carries no payload and cannot be subscribed to"),
                "The no-payload guard must be what rejected this, not the type comparison.");
        }

        [Test]
        public void Subscribe_StopsDeliveringAfterDisposal()
        {
            using var session = StartedSession(out var transport, out _);
            var count = 0;
            var subscription = session.Subscribe<GameOverEventDto>(
                MessageId.GameOverEvent, _ => count++);

            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));
            subscription.Dispose();
            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Dispatch_IsolatesASubscriberThatThrows()
        {
            using var session = StartedSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            var secondSubscriberRan = false;
            session.Subscribe<GameOverEventDto>(
                MessageId.GameOverEvent, _ => throw new InvalidOperationException("boom"));
            session.Subscribe<GameOverEventDto>(
                MessageId.GameOverEvent, _ => secondSubscriberRan = true);

            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));

            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.SubscriberFailure));
            Assert.That(secondSubscriberRan, Is.True,
                "One view model's null reference must not silence the others.");
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
        }

        /// <summary>
        /// Was Dispatch_TreatsAMessageWithNoSubscribersAsNormal, and its assertion
        /// was that no fault was published at all. That is no longer the contract:
        /// an undeliverable message now publishes NoDestination, because dropping
        /// it silently is exactly how a missed MatchFoundEvent used to vanish. The
        /// half of the old intent that survives is pinned here instead - having
        /// nowhere to go is not a stream failure, and the pump reads on - and the
        /// fault list is still counted exactly, so a second unrelated fault would
        /// still fail this.
        /// </summary>
        [Test]
        public void Dispatch_ReportsAMessageWithNoSubscribersWithoutFaultingThePump()
        {
            using var session = StartedSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            transport.EnqueueInbound(Frame(MessageId.TurnTimerEvent, "{}"));

            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.NoDestination));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            // State alone cannot tell a live pump from one that died inside
            // Dispatch, for the reason spelled out in
            // Heartbeat_SurvivesASendFailureWithoutKillingThePump. Only a later
            // message actually arriving can.
            var delivered = false;
            session.Subscribe<TurnTimerEventDto>(MessageId.TurnTimerEvent, _ => delivered = true);
            transport.EnqueueInbound(Frame(MessageId.TurnTimerEvent, "{}"));

            Assert.That(delivered, Is.True,
                "Reporting an undeliverable message must not cost the pump.");
        }

        /// <summary>
        /// A subscriber that stops the session runs while the pump is executing,
        /// not parked on a receive, so there is no pending read for the disconnect
        /// to cancel. The pump's only exit on this path is its loop condition; if
        /// stopping failed to cancel the pump token, the next iteration would call
        /// ReceiveAsync on a disconnected transport, trip its connected guard, and
        /// land in the stream-fault path with a TransportFailure nobody caused.
        /// </summary>
        [Test]
        public void Dispatch_LetsASubscriberStopTheSessionWithoutFaultingThePump()
        {
            using var session = StartedSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.Subscribe<GameOverEventDto>(
                MessageId.GameOverEvent,
                _ => session.StopAsync(default).GetAwaiter().GetResult());

            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));
            Assert.That(faults, Is.Empty,
                "Stopping from inside a subscriber is an orderly shutdown, not a stream failure.");
        }

        [Test]
        public void SendAsync_WritesTheEncodedFrameToTheTransport()
        {
            using var session = StartedSession(out var transport, out _);

            session.SendAsync(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "echo" },
                default).GetAwaiter().GetResult();

            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(transport.Sent[0].MessageId, Is.EqualTo(MessageId.LoginRequest));
            Assert.That(
                Encoding.UTF8.GetString(transport.Sent[0].Payload),
                Is.EqualTo("{\"player_name\":\"echo\"}"));
        }

        [Test]
        public void SendAsync_RejectsAPayloadThatDoesNotMatchTheMessageId()
        {
            using var session = StartedSession(out _, out _);

            Assert.Throws<ArgumentException>(
                () => session.SendAsync(
                    MessageId.PlayCardRequest,
                    new LoginRequestDto(),
                    default).GetAwaiter().GetResult());
        }

        /// <summary>
        /// The other half of the constraint that
        /// SendAsync_AcceptsANullPayloadForAMessageThatCarriesNone covers. Without
        /// this branch a forgotten payload ships as an empty frame and the server
        /// sees a login with no name rather than a client-side error.
        /// </summary>
        [Test]
        public void SendAsync_RejectsANullPayloadForAMessageThatRequiresOne()
        {
            using var session = StartedSession(out var transport, out _);

            var exception = Assert.Throws<ArgumentException>(
                () => session.SendAsync(
                    MessageId.LoginRequest, null, default).GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("requires a LoginRequestDto payload"));
            Assert.That(transport.Sent, Is.Empty, "A rejected send must never reach the wire.");
        }

        [Test]
        public void SendAsync_RejectsAPayloadForAMessageThatCarriesNone()
        {
            using var session = StartedSession(out var transport, out _);

            var exception = Assert.Throws<ArgumentException>(
                () => session.SendAsync(
                    MessageId.Pong,
                    new LoginRequestDto(),
                    default).GetAwaiter().GetResult());

            Assert.That(exception.Message, Does.Contain("Pong carries no payload"));
            Assert.That(transport.Sent, Is.Empty, "A rejected send must never reach the wire.");
        }

        [Test]
        public void SendAsync_AcceptsANullPayloadForAMessageThatCarriesNone()
        {
            using var session = StartedSession(out var transport, out _);

            session.SendAsync(MessageId.Pong, null, default).GetAwaiter().GetResult();

            Assert.That(transport.Sent[0].Payload, Is.Empty);
        }

        [Test]
        public void SendAsync_RequiresAConnectedSession()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            using var session = new ProtocolSession(
                transport, time, time, new RecordingSessionScheduler());

            Assert.Throws<InvalidOperationException>(
                () => session.SendAsync(MessageId.Pong, null, default).GetAwaiter().GetResult());
        }

        [Test]
        public void Heartbeat_AnswersAnInboundPingWithPong()
        {
            using var session = StartedSession(out var transport, out _);

            transport.EnqueueInbound(Bodyless(MessageId.Ping));

            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(transport.Sent[0].MessageId, Is.EqualTo(MessageId.Pong));
            Assert.That(transport.Sent[0].Payload, Is.Empty,
                "The Go server sends heartbeats with a nil body; the reply matches.");
        }

        [Test]
        public void Heartbeat_DoesNotSurfaceToFaultHandlers()
        {
            using var session = StartedSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            transport.EnqueueInbound(Bodyless(MessageId.Ping));

            Assert.That(faults, Is.Empty);
        }

        [Test]
        public void Heartbeat_SurvivesASendFailureWithoutKillingThePump()
        {
            // Both of the previous two tests pass even with a bare
            // SendAsync(...).Forget(), because FakeTransport's send succeeds
            // synchronously. This is the test that actually covers the hazard.
            using var session = StartedSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            transport.FailNextSend(new IOException("socket closed"));

            transport.EnqueueInbound(Bodyless(MessageId.Ping));

            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "A failed heartbeat reply must not kill the pump.");
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.TransportFailure));

            // The pump must still be processing after the failed reply, and that is
            // observed by delivery rather than by State. A pump killed through
            // Dispatch unwinds into the unobserved-exception handler without ever
            // touching State, so State still reads Connected for a dead pump and
            // cannot tell the two apart. Only a later message actually arriving can.
            var delivered = false;
            session.Subscribe<GameOverEventDto>(MessageId.GameOverEvent, _ => delivered = true);
            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));

            Assert.That(delivered, Is.True,
                "The pump must still be delivering messages after a failed heartbeat reply.");
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
        }
    }
}
