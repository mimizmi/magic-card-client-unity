using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionConfinementTests
    {
        private static ProtocolSession CreateStarted(
            out FakeTransport transport,
            out RecordingSessionScheduler scheduler,
            out List<SessionFault> faults)
        {
            transport = new FakeTransport();
            scheduler = new RecordingSessionScheduler();
            var session = new ProtocolSession(
                transport, new ManualClock(DateTimeOffset.UnixEpoch), scheduler);
            faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void EveryDispatchedMessageHopsToTheSessionContextFirst()
        {
            using var session = CreateStarted(out var transport, out var scheduler, out _);

            transport.EnqueueInbound(ProtocolTestFrames.Bodyless(MessageId.Ping));
            transport.EnqueueInbound(ProtocolTestFrames.Bodyless(MessageId.Ping));

            Assert.That(scheduler.SwitchCount, Is.EqualTo(2),
                "One hop per received message, before it is dispatched.");
            Assert.That(
                scheduler.ObservedThreadIds.Distinct().Count(),
                Is.EqualTo(1),
                "All dispatch happens on one context.");
        }

        [Test]
        public void ARequestTimeoutHopsBeforeItsFinallyTouchesTheGate()
        {
            using var session = CreateStarted(out _, out var scheduler, out _);
            var before = scheduler.SwitchCount;

            // AsTask, as in RequestAsync_TimesOutWhenNoResponseArrives: UniTask's
            // own awaiter refuses to block, and this is the one path here that
            // completes off a timer thread rather than inline.
            Assert.Throws<TimeoutException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto { PlayerName = "redacted" },
                    TimeSpan.FromMilliseconds(50),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult());

            Assert.That(scheduler.SwitchCount, Is.EqualTo(before + 1),
                "A timeout resumes on the CancelAfter timer's thread, and the finally " +
                "below it mutates pendingRequests. The hop is what keeps that " +
                "dictionary single-threaded.");
        }

        [Test]
        public void AnUndecodableMessageDoesNotKillThePump()
        {
            using var session = CreateStarted(out var transport, out _, out var faults);

            transport.EnqueueInbound(new TransportMessage((MessageId)60000, new byte[0]));

            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            var delivered = 0;
            session.Subscribe<PhaseChangeEventDto>(
                MessageId.PhaseChangeEvent, _ => delivered++);
            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}"));

            Assert.That(delivered, Is.EqualTo(1),
                "A pump killed by the previous message would deliver nothing.");
            Assert.That(
                faults.Select(f => f.Kind),
                Does.Contain(SessionFaultKind.UnknownMessageId));
        }

        [Test]
        public void AFailingHopFaultsTheStreamRatherThanDyingUnobserved()
        {
            using var session = CreateStarted(out var transport, out var scheduler, out var faults);

            scheduler.NextFailure = new InvalidOperationException("no player loop");
            transport.EnqueueInbound(ProtocolTestFrames.Bodyless(MessageId.Ping));

            Assert.That(session.State, Is.EqualTo(SessionState.Faulted),
                "A hop that cannot happen means nothing can be dispatched safely.");
            Assert.That(
                faults.Select(f => f.Kind),
                Does.Contain(SessionFaultKind.TransportFailure));
        }
    }
}
