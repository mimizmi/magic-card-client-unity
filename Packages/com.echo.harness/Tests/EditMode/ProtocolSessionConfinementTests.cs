using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using System.Threading.Tasks;
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
                transport, new ManualTime(DateTimeOffset.UnixEpoch), scheduler);
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

        /// <summary>
        /// The sibling of the timeout test above, and the reason it is not
        /// redundant with it is the thread the cancellation comes from. Every
        /// other caller-cancellation test in this suite - three in
        /// ProtocolSessionRequestTests and one in ProtocolSessionLifecycleTests -
        /// calls Cancel() on the NUnit main thread, so the frame it resumes is
        /// already on the session's context and a missing hop is invisible.
        /// Cancelling from the thread pool is what puts RequestAsync's finally,
        /// which mutates pendingRequests, on a thread the pump does not own.
        /// </summary>
        [Test]
        public void ACallerCancellationHopsBeforeItsFinallyTouchesTheGate()
        {
            using var session = CreateStarted(out _, out var scheduler, out _);
            using var cancellation = new CancellationTokenSource();
            var before = scheduler.SwitchCount;

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(30),
                cancellation.Token).Preserve();

            // Joined rather than fired and forgotten, so the id below is written
            // before it is read and the request has already resumed - the
            // cancellation callback completes the waiter inline on this very
            // thread - by the time the assertions run.
            var cancellingThreadId = 0;
            Task.Run(() =>
            {
                cancellingThreadId = Thread.CurrentThread.ManagedThreadId;
                cancellation.Cancel();
            }).GetAwaiter().GetResult();

            Assert.Throws<OperationCanceledException>(
                () => pending.GetAwaiter().GetResult(),
                "Caller cancellation must still surface as cancellation, not as a " +
                "TimeoutException: the request was abandoned, it did not time out.");

            Assert.That(
                cancellingThreadId,
                Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId),
                "This test proves nothing unless the cancellation really came from " +
                "another thread.");
            Assert.That(scheduler.SwitchCount, Is.EqualTo(before + 1),
                "A cancelled request resumes on whichever thread called Cancel, and " +
                "the finally below it removes its entry from pendingRequests. " +
                "Without a hop that removal races the pump's own Dispatch and " +
                "FailPendingRequests over the same Dictionary.");
            Assert.That(
                scheduler.ObservedThreadIds[scheduler.SwitchCount - 1],
                Is.EqualTo(cancellingThreadId),
                "The hop must be requested from the off-context thread - that is " +
                "the frame that needs moving.");
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

        /// <summary>
        /// The teardown hop is allowed to fail, and what it may not do is take the
        /// caller's report with it. Before the swallow existed this path was
        /// unguarded: the hop's exception replaced the TimeoutException, and the
        /// caller was told its bookkeeping had failed rather than that its deadline
        /// had elapsed. Nothing pinned that, so this and its sibling below do.
        /// </summary>
        [Test]
        public void AFailingTeardownHopDoesNotReplaceTheTimeout()
        {
            using var session = CreateStarted(out _, out var scheduler, out _);

            // Nothing is enqueued on the transport in this test, so the pump is
            // parked in ReceiveAsync and cannot consume this one-shot first.
            scheduler.NextFailure = new InvalidOperationException("no player loop");

            Assert.Throws<TimeoutException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto { PlayerName = "redacted" },
                    TimeSpan.FromMilliseconds(50),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult(),
                "A hop that cannot happen is a bookkeeping failure. The caller's " +
                "deadline elapsing is the thing it can act on, and it must be what " +
                "it is told.");
        }

        /// <summary>
        /// The cancellation half of the test above. Both exits hop, so both can
        /// have their exception overwritten by a failing hop, and the two are
        /// distinguishable only by which exception survives.
        /// </summary>
        [Test]
        public void AFailingTeardownHopDoesNotReplaceTheCallerCancellation()
        {
            using var session = CreateStarted(out _, out var scheduler, out _);
            using var cancellation = new CancellationTokenSource();

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(30),
                cancellation.Token).Preserve();

            scheduler.NextFailure = new InvalidOperationException("no player loop");
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => pending.GetAwaiter().GetResult(),
                "A caller that abandoned its request must still be told that is " +
                "what happened, whatever the hop out did.");
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
