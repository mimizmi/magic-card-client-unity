using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;
using static Echo.Harness.Tests.EditMode.ProtocolTestFrames;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionRequestTests
    {
        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

        private static ProtocolSession StartedSession(
            ManualClock clock,
            out FakeTransport transport,
            out RecordingSessionScheduler scheduler)
        {
            transport = new FakeTransport();
            scheduler = new RecordingSessionScheduler();
            var session = new ProtocolSession(transport, clock, scheduler);
            session.StartAsync(default).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void RequestAsync_CompletesWithTheTypedResponse()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "echo" },
                Generous,
                default).Preserve();
            transport.EnqueueInbound(Frame(
                MessageId.LoginResponse, "{\"success\":true,\"player_id\":\"p-1\"}"));

            var response = pending.GetAwaiter().GetResult();

            Assert.That(response.Success, Is.True);
            Assert.That(response.PlayerId, Is.EqualTo("p-1"));
        }

        [Test]
        public void RequestAsync_RejectsASecondConcurrentRequestForTheSameResponse()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            var first = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();

            // The protocol has no correlation id, so two in-flight logins would
            // race for one reply and one caller would get the other's answer.
            Assert.Throws<RequestAlreadyInFlightException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest, new LoginRequestDto(), Generous, default)
                    .GetAwaiter().GetResult());

            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));
            Assert.That(first.GetAwaiter().GetResult().Success, Is.True);
        }

        [Test]
        public void RequestAsync_ReleasesTheGateAfterCompleting()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            var first = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));
            first.GetAwaiter().GetResult();

            var second = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":false}"));

            Assert.That(second.GetAwaiter().GetResult().Success, Is.False);
        }

        [Test]
        public void RequestAsync_DoesNotAlsoDeliverTheResponseToSubscribers()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);
            var subscriberRan = false;
            session.Subscribe<LoginResponseDto>(MessageId.LoginResponse, _ => subscriberRan = true);

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));
            pending.GetAwaiter().GetResult();

            Assert.That(subscriberRan, Is.False, "A response belongs to its requester.");
        }

        [Test]
        public void RequestAsync_ReleasesTheGateAfterCancellation()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);
            using var cancellation = new CancellationTokenSource();

            var cancelled = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, cancellation.Token)
                .Preserve();
            cancellation.Cancel();
            Assert.Throws<OperationCanceledException>(() => cancelled.GetAwaiter().GetResult());

            // This is the one path where RequestAsync's own finally is the sole
            // remover: no response ever arrived, so nothing else touched the
            // gate. Without it the response id stays occupied for the lifetime
            // of the session and every later request for it is refused.
            var second = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));

            Assert.That(second.GetAwaiter().GetResult().Success, Is.True);
        }

        [Test]
        public void RequestAsync_RejectsAMessageThatHasNoPairedResponse()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            var failure = Assert.Throws<ArgumentException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.SurrenderRequest, null, Generous, default).GetAwaiter().GetResult());

            Assert.That(failure.Message, Does.Contain("has no paired response message"));
            Assert.That(
                transport.Sent, Is.Empty, "An unawaitable request must not reach the wire.");
        }

        [Test]
        public void RequestAsync_RejectsARequestWhoseAnswerIsAnEvent()
        {
            // GameConfigRequest (4008) IS answered by the server - with
            // GameConfigEvent (5011), which is kind "event" and so has no entry
            // in ResponseFor. The rejection has to send that caller to
            // Subscribe, not tell it the message is one-way and no answer is
            // coming.
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            var failure = Assert.Throws<ArgumentException>(
                () => session.RequestAsync<GameConfigEventDto>(
                    MessageId.GameConfigRequest,
                    new GameConfigRequestDto(),
                    Generous,
                    default).GetAwaiter().GetResult());

            Assert.That(failure.Message, Does.Contain("has no paired response message"));
            Assert.That(failure.Message, Does.Contain("Subscribe"));
            Assert.That(
                failure.Message,
                Does.Not.Contain("one-way"),
                "4008 is answered; only the kind of its answer differs.");
            Assert.That(transport.Sent, Is.Empty);
        }

        [Test]
        public void RequestAsync_RejectsAResponseTypeThePairingDoesNotProduce()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            // Without this guard the login goes out, the real LoginResponse is
            // consumed, and the caller dies on the cast - after the server has
            // already acted on the request.
            var failure = Assert.Throws<ArgumentException>(
                () => session.RequestAsync<JoinQueueResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto(),
                    Generous,
                    default).GetAwaiter().GetResult());

            Assert.That(
                failure.Message,
                Does.Contain("carries LoginResponseDto, not JoinQueueResponseDto"));
            Assert.That(transport.Sent, Is.Empty, "A mistyped request must not reach the wire.");
        }

        [Test]
        public void RequestAsync_RejectsATimeoutThatCouldNeverExpire()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            // Timeout.InfiniteTimeSpan installs no timer at all, so the waiter
            // would be genuinely unbounded rather than merely patient.
            foreach (var rejected in new[]
                     {
                         Timeout.InfiniteTimeSpan,
                         TimeSpan.Zero,
                         TimeSpan.FromSeconds(-1),
                     })
            {
                var failure = Assert.Throws<ArgumentOutOfRangeException>(
                    () => session.RequestAsync<LoginResponseDto>(
                        MessageId.LoginRequest,
                        new LoginRequestDto(),
                        rejected,
                        default).GetAwaiter().GetResult(),
                    $"{rejected} should have been rejected.");

                Assert.That(failure.ParamName, Is.EqualTo("timeout"));
                Assert.That(failure.Message, Does.Contain("request timeout must be positive"));
            }

            // Validating early is the point. CancelAfter runs after the send, so
            // a caller passing an already-elapsed deadline would otherwise be
            // told its argument was bad while the server was already acting on
            // the request it had just been handed.
            Assert.That(transport.Sent, Is.Empty, "A rejected timeout must not reach the wire.");
        }

        [Test]
        public void RequestAsync_PropagatesCallerCancellation()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out _, out _);
            using var cancellation = new CancellationTokenSource();

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, cancellation.Token)
                .Preserve();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
        }

        [Test]
        public void RequestAsync_TimesOutWhenNoResponseArrives()
        {
            // The only test in this plan that waits on real time. The timeout is
            // built on CancellationTokenSource.CancelAfter; removing the real-time
            // dependency would mean introducing a scheduler port for one assertion.
            //
            // AsTask, and only here: UniTask's own awaiter refuses to block ("Not
            // yet completed, UniTask only allow to use await"). Every other test
            // completes inline from EnqueueInbound or Cancel, so the awaiter is
            // already done by the time it is read. This one completes off a timer
            // thread, so the test thread has to actually wait for it.
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out _, out _);

            Assert.Throws<TimeoutException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto(),
                    TimeSpan.FromMilliseconds(50),
                    default).AsTask().GetAwaiter().GetResult());
        }

        [Test]
        public void RequestAsync_FailsPendingRequestsWhenTheStreamDesynchronizes()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out var transport, out _);

            // Two waiters on two different response ids, deliberately. With one
            // the loop is indistinguishable from a loop that stops after the
            // first waiter, and it is also blind to a loop that iterates the
            // dictionary directly: each resumed waiter removes its own key
            // inline, so the second MoveNext throws "collection was modified"
            // and the exception disappears into an unobserved task.
            var login = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            var joinQueue = session.RequestAsync<JoinQueueResponseDto>(
                MessageId.JoinQueueRequest, new JoinQueueRequestDto(), Generous, default)
                .Preserve();
            transport.FailNextReceive(new System.IO.IOException("length prefix out of range"));

            Assert.Throws<System.IO.IOException>(() => login.GetAwaiter().GetResult());
            Assert.Throws<System.IO.IOException>(() => joinQueue.GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Faulted));
        }

        [Test]
        public void RequestAsync_FailsPendingRequestsWhenTheSessionStops()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out _, out _);

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            session.StopAsync(default).GetAwaiter().GetResult();

            // The message matters as much as the type: an abandoned waiter
            // reports a TimeoutException naming the network, which is a false
            // statement about a session the caller stopped itself. The type
            // alone would not catch that, because a still-pending UniTask also
            // throws InvalidOperationException when its result is read.
            var failure = Assert.Throws<InvalidOperationException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("stopped before the response arrived"));
        }

        [Test]
        public void StopAsync_ReachesDisconnectedBeforeItFailsPendingRequests()
        {
            using var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out _, out _);

            // A failed waiter resumes inline on StopAsync's own stack, so it can
            // re-enter the session. Failing waiters after the state transition
            // is what makes that re-entrant call get a truthful answer instead
            // of parking forever on a pump that is already cancelled.
            var observed = StateSeenByAFailedRequest(session).Preserve();
            session.StopAsync(default).GetAwaiter().GetResult();

            Assert.That(
                observed.GetAwaiter().GetResult(), Is.EqualTo(SessionState.Disconnected));
        }

        [Test]
        public void RequestAsync_FailsPendingRequestsWhenTheSessionIsDisposed()
        {
            var session = StartedSession(
                new ManualClock(DateTimeOffset.UnixEpoch), out _, out _);

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            session.Dispose();

            var failure = Assert.Throws<ObjectDisposedException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("disposed before the response arrived"));
        }

        [Test]
        public void ProbeRoundTripAsync_MeasuresTheIntervalTheClockAdvanced()
        {
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            using var session = StartedSession(clock, out var transport, out _);
            var sentAt = DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds();

            var pending = session.ProbeRoundTripAsync(default).Preserve();
            clock.Advance(TimeSpan.FromMilliseconds(120));
            transport.EnqueueInbound(Frame(
                MessageId.ClientPingResponse, "{\"ts\":" + sentAt + "}"));

            Assert.That(
                pending.GetAwaiter().GetResult(),
                Is.EqualTo(TimeSpan.FromMilliseconds(120)));
        }

        [Test]
        public void ProbeRoundTripAsync_SendsTheClockTimestamp()
        {
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            using var session = StartedSession(clock, out var transport, out _);
            var sentAt = DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds();

            var pending = session.ProbeRoundTripAsync(default).Preserve();

            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(transport.Sent[0].MessageId, Is.EqualTo(MessageId.ClientPingRequest));
            Assert.That(
                Encoding.UTF8.GetString(transport.Sent[0].Payload),
                Is.EqualTo("{\"ts\":" + sentAt + "}"));

            transport.EnqueueInbound(Frame(
                MessageId.ClientPingResponse, "{\"ts\":" + sentAt + "}"));
            pending.GetAwaiter().GetResult();
        }

        [Test]
        public void ProbeRoundTripAsync_RejectsAMismatchedEcho()
        {
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            using var session = StartedSession(clock, out var transport, out _);
            var faults = new System.Collections.Generic.List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            var pending = session.ProbeRoundTripAsync(default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.ClientPingResponse, "{\"ts\":999}"));

            // A latency number derived from an unrelated reply looks perfectly
            // plausible, which is exactly why it must not be returned.
            //
            // The exact type, because NUnit's Assert.Throws<T> demands one:
            // unlike a catch clause it does not accept a subclass, so this had
            // to move with the throw rather than riding on the inheritance.
            Assert.Throws<CorrelationMismatchException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.CorrelationMismatch));
        }

        [Test]
        public void ProbeRoundTripAsync_RejectsAnAbandonedProbesEchoAfterARetry()
        {
            // Deliberately not the epoch. At ts 0 a stale echo carries the same
            // value as a ts field that was never deserialized at all, so the
            // assertions below would hold for the wrong reason.
            var clock = new ManualClock(DateTimeOffset.UnixEpoch + TimeSpan.FromSeconds(7));
            using var session = StartedSession(clock, out var transport, out _);
            var faults = new System.Collections.Generic.List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            // The first probe is abandoned by cancellation rather than by waiting
            // out RoundTripProbeDeadline, which is ten seconds and is not a
            // parameter of ProbeRoundTripAsync. Both routes leave the same state
            // behind - no pending entry for ClientPingResponse, and a reply still
            // owed by the server - and only one of them is deterministic.
            var abandonedTs = clock.UtcNow.ToUnixTimeMilliseconds();
            using var abandon = new CancellationTokenSource();
            var abandoned = session.ProbeRoundTripAsync(abandon.Token).Preserve();
            abandon.Cancel();
            Assert.Throws<OperationCanceledException>(() => abandoned.GetAwaiter().GetResult());

            clock.Advance(TimeSpan.FromMilliseconds(500));
            var retriedTs = clock.UtcNow.ToUnixTimeMilliseconds();
            var retried = session.ProbeRoundTripAsync(default).Preserve();
            Assert.That(transport.Sent, Has.Count.EqualTo(2));
            Assert.That(
                Encoding.UTF8.GetString(transport.Sent[1].Payload),
                Is.EqualTo("{\"ts\":" + retriedTs + "}"),
                "A retry must carry its own timestamp, not repeat the abandoned one.");

            // Now the ABANDONED probe's reply lands, with the retry waiting for
            // one of its own. Nothing in the frame distinguishes the two, and the
            // interval the clock has moved since the retry went out - 40ms - is a
            // completely believable latency, so a session without the echo check
            // would report a number that is wrong and looks right.
            clock.Advance(TimeSpan.FromMilliseconds(40));
            transport.EnqueueInbound(Frame(
                MessageId.ClientPingResponse, "{\"ts\":" + abandonedTs + "}"));

            var failure = Assert.Throws<CorrelationMismatchException>(
                () => retried.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain(abandonedTs.ToString()));
            Assert.That(failure.Message, Does.Contain(retriedTs.ToString()));
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.CorrelationMismatch));

            // Rejecting one stale echo must not disable the probe for good. The
            // throw happens after RequestAsync has already returned, so its
            // finally has released the single-flight gate and a further probe is
            // still both sendable and answerable.
            var recovered = session.ProbeRoundTripAsync(default).Preserve();
            clock.Advance(TimeSpan.FromMilliseconds(25));
            transport.EnqueueInbound(Frame(
                MessageId.ClientPingResponse, "{\"ts\":" + (retriedTs + 40) + "}"));

            Assert.That(
                recovered.GetAwaiter().GetResult(),
                Is.EqualTo(TimeSpan.FromMilliseconds(25)));
        }

        private static async UniTask<SessionState> StateSeenByAFailedRequest(
            ProtocolSession session)
        {
            try
            {
                await session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest, new LoginRequestDto(), Generous, default);
            }
            catch (InvalidOperationException)
            {
            }

            return session.State;
        }
    }
}
