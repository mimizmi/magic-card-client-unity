using System;
using System.Collections.Generic;
using System.IO;
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

            // Three, not the two this asserted before the connect continuation was
            // given a hop of its own. The extra one is StartAsync's, taken inside
            // CreateStarted before either message arrives: the transport's connect
            // does not capture a context, so the frame that marks the session
            // Connected and launches the pump has to come back to the context
            // first. Deliberately still an exact count - the number moved because
            // behaviour moved, and a hop that silently stopped happening is exactly
            // what this assertion exists to catch.
            Assert.That(scheduler.SwitchCount, Is.EqualTo(3),
                "One hop for the connect, then one per received message before it " +
                "is dispatched.");
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
            // One, not the zero this asserted before StartAsync took a hop of its
            // own. That one switch is the connect's, and it happened inside
            // CreateStarted before NextFailure was ever armed. The teardown hop
            // that follows is the armed one, and it must still leave the counter
            // where the connect left it. Kept as an exact absolute count rather
            // than rewritten as a delta: the number that must not move is the one
            // the ARMED hop contributes, and pinning the total says both that the
            // connect hopped once and that the teardown hop added nothing.
            Assert.That(switchesAfterFailure, Is.EqualTo(1),
                "SwitchCount records successful switches only, so an armed failure " +
                "must leave it alone. One - StartAsync's connect hop and nothing " +
                "more - is what separates 'the hop ran and threw' from 'the hop " +
                "quietly succeeded and the fault came from elsewhere'.");

            var fromCancel = RunTimedOutRequestWithHopFailure(
                new OperationCanceledException(), out var switchesAfterCancel);

            Assert.That(fromCancel, Is.Empty,
                "A latched scheduler is an orderly quit. Reporting it would make " +
                "every shutdown look like a failure.");
            Assert.That(switchesAfterCancel, Is.EqualTo(1),
                "The cancelled half must reach the hop too, or its silence proves " +
                "nothing. One for the same reason as above: StartAsync's connect " +
                "hop, and no contribution from the cancelled teardown hop.");
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

        /// <summary>
        /// The third failing exit of RequestAsync, and the one that used to leave
        /// with no hop at all. The two exits its siblings above cover both hang off
        /// the INNER try; a throw from the send has not entered that try yet, so it
        /// went straight to the outer finally - the one that removes this request's
        /// entry from pendingRequests - on whatever thread failed the write.
        ///
        /// <para><b>The failure is delivered from the thread pool, and that is the
        /// whole test.</b> FakeTransport cannot stand in here: its SendAsync throws
        /// synchronously, on the caller's own thread, where a missing hop is
        /// invisible for exactly the reason
        /// ACallerCancellationHopsBeforeItsFinallyTouchesTheGate gives about
        /// Cancel(). A real socket parks in the write and resumes the requester
        /// wherever the I/O completed, which is what the local double below
        /// reproduces.</para>
        ///
        /// <para>The occurrence of the hop is what is asserted, not the thread the
        /// finally happened to run on. Task 10 measured an uncontended gate wait
        /// resuming inline on the main thread once in five observations, so a test
        /// that watched threads would pass on the run where the defect was harmless
        /// and prove nothing about the run where it was not.</para>
        /// </summary>
        [Test]
        public void AFailedSendHopsBeforeItsFinallyTouchesTheGate()
        {
            var transport = new DeferredTransport(deferConnect: false);
            var scheduler = new RecordingSessionScheduler();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            using var session = new ProtocolSession(transport, time, time, scheduler);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            var before = scheduler.SwitchCount;

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(30),
                CancellationToken.None).Preserve();

            // Joined rather than fired and forgotten, for the reason its sibling
            // gives: failing the send resumes the requester inline on this very
            // thread, so by the time Run returns the frame under test has already
            // taken its hop and run its finally.
            var failingThreadId = 0;
            Task.Run(() =>
            {
                failingThreadId = Thread.CurrentThread.ManagedThreadId;
                transport.FailPendingSend(new IOException("the socket died mid-write"));
            }).GetAwaiter().GetResult();

            Assert.Throws<IOException>(
                () => pending.GetAwaiter().GetResult(),
                "A send that failed must still surface as the send failure. The hop " +
                "is bookkeeping and may not replace the caller's report.");

            Assert.That(
                failingThreadId,
                Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId),
                "This test proves nothing unless the send really failed on another " +
                "thread.");
            Assert.That(scheduler.SwitchCount, Is.EqualTo(before + 1),
                "A failed send resumes on whichever thread the write died on, and " +
                "the finally below it removes this request's entry from " +
                "pendingRequests. Without a hop that removal races the pump's own " +
                "Dispatch over the same Dictionary.");
            Assert.That(
                scheduler.ObservedThreadIds[scheduler.SwitchCount - 1],
                Is.EqualTo(failingThreadId),
                "The hop must be requested from the off-context thread - that is " +
                "the frame that needs moving.");
        }

        /// <summary>
        /// StartAsync's own hop. The transport's connect does not capture a
        /// synchronization context, so the continuation that marks the session
        /// Connected, installs pumpCancellation and launches the pump resumes on
        /// whatever thread the socket completed on. Those three writes publish the
        /// session as usable, and State is a plain auto-property rather than a
        /// volatile one, so a shutdown running on the session's context at the same
        /// moment can see Connecting, cancel a still-null pump, disconnect, settle
        /// on Disconnected - and then be overwritten by this continuation.
        ///
        /// <para>Asserted as an exact count plus the thread the hop was REQUESTED
        /// from, which is the off-context one. The thread the hop lands on is not
        /// asserted, because RecordingSessionScheduler completes synchronously by
        /// design and would answer that question about itself rather than about the
        /// session.</para>
        ///
        /// <para><b>What the session looked like AT the hop is asserted too, and a
        /// surviving mutation is why.</b> An earlier version of this test checked
        /// only that one hop happened and which thread asked for it. Moving the hop
        /// to sit AFTER the three state writes - so the session still hops exactly
        /// once, from the same thread, but publishes itself as usable first - left
        /// this test green, which is the entire defect R2 is about. Occurrence is
        /// not ordering. The scheduler double below reports the session's state and
        /// the transport's receive count from inside the switch, which is the only
        /// vantage point from which "before" and "after" are distinguishable.</para>
        /// </summary>
        [Test]
        public void AConnectThatCompletesOffContextHopsBeforeTheSessionIsMarkedConnected()
        {
            var transport = new DeferredTransport(deferConnect: true);
            var scheduler = new ObservingSessionScheduler();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            using var session = new ProtocolSession(transport, time, time, scheduler);

            // Faulted and -1 are sentinels no real observation can produce, so a
            // hook that never ran fails these rather than passing them by default.
            var stateAtHop = SessionState.Faulted;
            var receivesAtHop = -1;
            scheduler.OnSwitch = () =>
            {
                stateAtHop = session.State;
                receivesAtHop = transport.ReceiveCount;
            };

            var start = session.StartAsync(CancellationToken.None).Preserve();

            Assert.That(session.State, Is.EqualTo(SessionState.Connecting),
                "The connect has not completed, so nothing may be published yet.");
            Assert.That(scheduler.SwitchCount, Is.EqualTo(0),
                "The hop belongs after the connect, not before it.");

            var connectingThreadId = 0;
            Task.Run(() =>
            {
                connectingThreadId = Thread.CurrentThread.ManagedThreadId;
                transport.CompleteConnect();
            }).GetAwaiter().GetResult();

            start.GetAwaiter().GetResult();

            Assert.That(
                connectingThreadId,
                Is.Not.EqualTo(Thread.CurrentThread.ManagedThreadId),
                "This test proves nothing unless the connect really completed on " +
                "another thread.");
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
            Assert.That(scheduler.SwitchCount, Is.EqualTo(1),
                "One hop, between the connect completing and the session declaring " +
                "itself usable.");
            Assert.That(
                scheduler.ObservedThreadIds[0],
                Is.EqualTo(connectingThreadId),
                "The hop must be requested from the thread the connect resumed on, " +
                "which is the frame that carries the session's state writes.");

            // The ordering, which the three assertions above cannot see.
            Assert.That(stateAtHop, Is.EqualTo(SessionState.Connecting),
                "At the moment the hop is requested the session must still read " +
                "Connecting. A session already reading Connected has published " +
                "itself as usable from the pool thread, which is the race the hop " +
                "exists to close - and it would do so while still hopping exactly " +
                "once, from exactly this thread.");
            Assert.That(receivesAtHop, Is.EqualTo(0),
                "The pump may not have been launched yet either. Starting it before " +
                "the hop puts a receive - and every Dispatch behind it - on the " +
                "wrong side of the context switch.");
        }

        /// <summary>
        /// The other half of StartAsync's hop: what happens when it cannot be taken.
        /// A session reaches single-threaded dispatch, a single-threaded gate and
        /// single-threaded subscriber lists only by having a context to confine them
        /// to, so one that cannot reach its context must not advertise itself as
        /// Connected. The link is closed on the way out because the connect
        /// SUCCEEDED - abandoning it leaves the server holding the session until its
        /// own pong timeout.
        /// </summary>
        [Test]
        public void AStartWhoseHopFailsDoesNotDeclareItselfConnectedAndCedesTheLink()
        {
            var transport = new FakeTransport();
            var scheduler = new RecordingSessionScheduler
            {
                NextFailure = new InvalidOperationException("no player loop"),
            };
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            using var session = new ProtocolSession(transport, time, time, scheduler);

            Assert.Throws<InvalidOperationException>(
                () => session.StartAsync(CancellationToken.None).GetAwaiter().GetResult(),
                "A start that could not reach the session's context is a failed " +
                "start, and the caller is owed the reason.");

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected),
                "Connected behind an unreachable context is a promise the session " +
                "cannot keep.");
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));
            Assert.That(transport.DisconnectCount, Is.EqualTo(1),
                "The connect succeeded, so there is a real socket here. Unlike a " +
                "failed connect, this path has a link to give back.");
        }

        /// <summary>
        /// A transport whose connect and whose send can each be left genuinely in
        /// flight and completed from another thread. Neither
        /// <see cref="FakeTransport"/> nor the real TcpTransport can serve: the fake
        /// completes both synchronously on the caller's thread, which is precisely
        /// the property a test of off-context resumption may not borrow, and the real
        /// one needs a socket.
        /// </summary>
        private sealed class DeferredTransport : ITransport
        {
            private readonly UniTaskCompletionSource connect = new UniTaskCompletionSource();
            private readonly bool deferConnect;
            private UniTaskCompletionSource pendingSend;

            public DeferredTransport(bool deferConnect)
            {
                this.deferConnect = deferConnect;
            }

            public TransportState State { get; private set; } = TransportState.Disconnected;

            /// <summary>
            /// How many times the pump has asked for a message. It is the cheapest
            /// observable proof that RunPumpAsync has been launched, which is one of
            /// the three things StartAsync may not do before its hop.
            /// </summary>
            public int ReceiveCount { get; private set; }

            public UniTask ConnectAsync(CancellationToken cancellationToken)
            {
                if (!deferConnect)
                {
                    State = TransportState.Connected;
                    return UniTask.CompletedTask;
                }

                State = TransportState.Connecting;
                return ParkUntilConnectedAsync();
            }

            public UniTask SendAsync(
                TransportMessage message,
                CancellationToken cancellationToken)
            {
                pendingSend = new UniTaskCompletionSource();
                return pendingSend.Task;
            }

            // Parks and honours the token, so CancelPump can still unblock the pump.
            // A double less cancellable than the real transport would hang these
            // tests rather than fail them.
            public UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
            {
                ReceiveCount++;
                var waiter = new UniTaskCompletionSource<TransportMessage>();
                cancellationToken.Register(() => waiter.TrySetCanceled(cancellationToken));
                return waiter.Task;
            }

            public UniTask DisconnectAsync(CancellationToken cancellationToken)
            {
                State = TransportState.Disconnected;
                return UniTask.CompletedTask;
            }

            public void CompleteConnect() => connect.TrySetResult();

            public void FailPendingSend(Exception failure) =>
                pendingSend.TrySetException(failure);

            // The state change follows the await, so the session's own continuation
            // cannot observe a Connected transport before this frame has finished
            // becoming one.
            private async UniTask ParkUntilConnectedAsync()
            {
                await connect.Task;
                State = TransportState.Connected;
            }
        }

        /// <summary>
        /// <see cref="RecordingSessionScheduler"/> plus one seam: a callback invoked
        /// from inside the switch, before the switch itself is recorded.
        ///
        /// <para>It wraps the TestKit scheduler rather than reimplementing it, so the
        /// completion shape a session sees here stays the one every other test in
        /// this suite sees - synchronous on success, an already-faulted UniTask on
        /// failure. Reimplementing it would let this test pin a contract the shared
        /// double does not have.</para>
        ///
        /// <para>Local to this fixture rather than added to TestKit, and the reason
        /// is the same one <c>StallingCloseTransport</c> gives: the seam exists for
        /// one question - what had the session already done when it asked to hop -
        /// and a shared double that answered it would invite tests to assert against
        /// a hook instead of against behaviour.</para>
        /// </summary>
        private sealed class ObservingSessionScheduler : ISessionScheduler
        {
            private readonly RecordingSessionScheduler inner = new RecordingSessionScheduler();

            public Action OnSwitch { get; set; }

            public int SwitchCount => inner.SwitchCount;

            public IReadOnlyList<int> ObservedThreadIds => inner.ObservedThreadIds;

            // Before the inner call, so what the callback reads is the state the
            // session was in when it ASKED to hop. After it, the switch has already
            // been recorded and, on the synchronous success path, already happened.
            public UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken)
            {
                OnSwitch?.Invoke();
                return inner.SwitchToSessionContextAsync(cancellationToken);
            }
        }
    }
}
