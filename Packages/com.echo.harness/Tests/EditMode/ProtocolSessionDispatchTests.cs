using System;
using System.Collections.Generic;
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
        private static ProtocolSession StartedSession(FakeTransport transport)
        {
            var session = new ProtocolSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));
            session.StartAsync(default).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void Subscribe_DeliversTheTypedPayload()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
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
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            // Caught at subscription time on purpose. Deferring it to dispatch
            // would surface the mistake when the message first arrives, which for
            // a damage event could be ten minutes into a match.
            Assert.Throws<ArgumentException>(
                () => session.Subscribe<LoginResponseDto>(MessageId.DamageEvent, _ => { }));
        }

        [Test]
        public void Subscribe_StopsDeliveringAfterDisposal()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
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
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
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

        [Test]
        public void Dispatch_TreatsAMessageWithNoSubscribersAsNormal()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            transport.EnqueueInbound(Frame(MessageId.TurnTimerEvent, "{}"));

            Assert.That(faults, Is.Empty);
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
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
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
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            Assert.Throws<ArgumentException>(
                () => session.SendAsync(
                    MessageId.PlayCardRequest,
                    new LoginRequestDto(),
                    default).GetAwaiter().GetResult());
        }

        [Test]
        public void SendAsync_AcceptsANullPayloadForAMessageThatCarriesNone()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            session.SendAsync(MessageId.Pong, null, default).GetAwaiter().GetResult();

            Assert.That(transport.Sent[0].Payload, Is.Empty);
        }

        [Test]
        public void SendAsync_RequiresAConnectedSession()
        {
            var transport = new FakeTransport();
            using var session = new ProtocolSession(
                transport, new ManualClock(DateTimeOffset.UnixEpoch));

            Assert.Throws<InvalidOperationException>(
                () => session.SendAsync(MessageId.Pong, null, default).GetAwaiter().GetResult());
        }
    }
}
