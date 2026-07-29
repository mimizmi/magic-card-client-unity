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
        public void RequestAsync_RejectsAMessageThatHasNoPairedResponse()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            Assert.Throws<ArgumentException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.SurrenderRequest, null, Generous, default).GetAwaiter().GetResult());
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

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.FailNextReceive(new System.IO.IOException("length prefix out of range"));

            Assert.Throws<System.IO.IOException>(() => pending.GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Faulted));
        }
    }
}
