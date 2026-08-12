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
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var session = new ProtocolSession(transport, time, time, scheduler);
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

        /// <summary>
        /// The third of the teardown-hop family, and the one that asks what the
        /// session RECORDED rather than what the caller received. Its two halves
        /// differ only in the exception the hop comes back with, because that is
        /// the entire distinction the catch pair draws: a cancelled hop is the
        /// shutdown path and must stay silent, while any other failure means the
        /// finally below it un-registered a gate entry off the session's context
        /// with nothing anywhere recording that it happened.
        ///
        /// <para><b>NextFailure rather than a scheduler double of its own, and the
        /// cancelled half is the reason.</b> The hop passes CancellationToken.None,
        /// so no token this test holds can cancel it. The one production shape that
        /// does is MainThreadSessionScheduler's latch, which throws a bare
        /// OperationCanceledException out of an <c>async UniTask</c> method that has
        /// not yet awaited - so it reaches the awaiter through
        /// UniTask.FromException, which turns an OperationCanceledException into
        /// FromCanceled. RecordingSessionScheduler hands an armed failure to
        /// UniTask.FromException too, so arming it with an
        /// OperationCanceledException reproduces that route exactly rather than
        /// approximating it. A double that stored the exception and threw it out of
        /// the call would not.</para>
        ///
        /// <para>Nothing is enqueued on the transport, so the pump is parked in
        /// ReceiveAsync and cannot consume the one-shot before the teardown hop
        /// reaches it - the condition NextFailure's own summary sets out. The
        /// helper asserts the one-shot was cleared rather than assuming it.</para>
        /// </summary>
        [Test]
        public void AFailingTeardownHopIsReportedButACancelledOneIsNot()
        {
            var fromFailure = RunTimedOutRequestWithHopFailure(
                new InvalidOperationException("no player loop"), out var switchesAfterFailure);

            Assert.That(fromFailure.Count, Is.EqualTo(1),
                "A hop that failed for a reason other than shutdown is the one thing " +
                "on this path that nothing else records.");
            Assert.That(fromFailure[0].Kind, Is.EqualTo(SessionFaultKind.DispatchFailure));
            Assert.That(fromFailure[0].MessageId, Is.EqualTo(default(MessageId)),
                "The failure belongs to no single message, which is also what tells " +
                "it apart from the per-message DispatchFailure the pump publishes.");
            Assert.That(fromFailure[0].Diagnostic, Does.Contain("off the session context"));

            // The type name is asserted separately because dropping it survived a
            // mutation that this test was already supposed to cover. "no player
            // loop" on its own names neither what threw nor where to look, which is
            // the reason the pump's DispatchFailure carries the type too.
            Assert.That(fromFailure[0].Diagnostic, Does.Contain("InvalidOperationException"));
            Assert.That(switchesAfterFailure, Is.EqualTo(0),
                "SwitchCount records successful switches only, so an armed failure " +
                "must leave it alone. Zero is what separates 'the hop ran and threw' " +
                "from 'the hop quietly succeeded and the fault came from elsewhere'.");

            var fromCancel = RunTimedOutRequestWithHopFailure(
                new OperationCanceledException(), out var switchesAfterCancel);

            Assert.That(fromCancel, Is.Empty,
                "A latched scheduler is an orderly quit. Reporting it would make " +
                "every shutdown look like a failure.");
            Assert.That(switchesAfterCancel, Is.EqualTo(0),
                "The cancelled half must reach the hop too, or its silence proves " +
                "nothing.");
        }

        /// <summary>
        /// Drives one request to its deadline with the teardown hop armed to fail,
        /// and returns what the session published. The caller's TimeoutException is
        /// asserted here rather than returned because it is evidence rather than
        /// subject matter: that catch clause is the only place this hop is taken
        /// from, so a caller that received the timeout is a caller whose request
        /// entered the frame the hop lives in.
        /// </summary>
        private static List<SessionFault> RunTimedOutRequestWithHopFailure(
            Exception hopFailure, out int switchCountAfter)
        {
            using var session = CreateStarted(out _, out var scheduler, out var faults);

            scheduler.NextFailure = hopFailure;

            Assert.Throws<TimeoutException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto { PlayerName = "redacted" },
                    TimeSpan.FromMilliseconds(50),
                    CancellationToken.None).AsTask().GetAwaiter().GetResult(),
                "The caller is still owed its deadline, whatever the hop out did.");

            Assert.That(scheduler.NextFailure, Is.Null,
                "The one-shot must have been consumed. Still armed would mean the " +
                "hop never reached it, and every assertion above would be about a " +
                "session that took no failing hop at all.");

            switchCountAfter = scheduler.SwitchCount;
            return faults;
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
