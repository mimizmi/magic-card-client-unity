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

        private static ProtocolSession StartedSession(FakeTransport transport, ManualClock clock)
        {
            var session = new ProtocolSession(transport, clock);
            session.StartAsync(default).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void RequestAsync_CompletesWithTheTypedResponse()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            var first = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();

            // The protocol has no correlation id, so two in-flight logins would
            // race for one reply and one caller would get the other's answer.
            Assert.Throws<InvalidOperationException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest, new LoginRequestDto(), Generous, default)
                    .GetAwaiter().GetResult());

            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));
            Assert.That(first.GetAwaiter().GetResult().Success, Is.True);
        }

        [Test]
        public void RequestAsync_ReleasesTheGateAfterCompleting()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));
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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));
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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));
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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

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
            var transport = new FakeTransport();
            var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            session.Dispose();

            var failure = Assert.Throws<ObjectDisposedException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("disposed before the response arrived"));
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
