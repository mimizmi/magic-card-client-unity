using System;
using System.Collections;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;
using UnityEngine.TestTools;

namespace Echo.Harness.Tests.EditMode
{
    /// <summary>
    /// [UnityTest] rather than [Test] for the reason spelled out at the head of
    /// TcpTransportFramingTests: these drive a real socket, so their work completes
    /// on the thread pool and UniTask resumes on the editor loop. Blocking the main
    /// thread with .GetAwaiter().GetResult() on a pending UniTask throws "Not yet
    /// completed, UniTask only allow to use await." before any assertion is reached,
    /// and stops the very loop that would let the operation finish.
    ///
    /// The timeouts here are real wall-clock milliseconds rather than a ManualClock
    /// tick, because the idle deadline is not driven by the injected IClock and
    /// cannot be: see the comment on the deadline in TcpTransport.ReceiveAsync.
    /// </summary>
    public sealed class TcpTransportIdleTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private static async UniTask<TcpTransport> ConnectAsync(
            LoopbackProtocolServer server,
            TimeSpan idle)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions
                {
                    Host = "127.0.0.1",
                    Port = server.Port,
                    ReadIdleTimeout = idle
                },
                new ManualClock(DateTimeOffset.UnixEpoch));
            try
            {
                var connecting = transport.ConnectAsync(CancellationToken.None);
                await server.AcceptAsync(Patience);
                await connecting;
            }
            catch (Exception)
            {
                // A half-built transport still owns a socket, and a leaked socket
                // stalls the editor at domain reload.
                transport.Dispose();
                throw;
            }

            return transport;
        }

        /// <summary>
        /// The connection is accepted and then nothing is ever written to it. That
        /// is the exact shape of a half-open link the kernel has not yet noticed:
        /// the socket is alive, the read is parked, and no byte is ever coming.
        /// </summary>
        [UnityTest]
        public IEnumerator SilenceBeyondTheIdleTimeoutFailsTheReceive()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, TimeSpan.FromMilliseconds(300));

                var failure = await CaptureAsync(async () =>
                    await transport.ReceiveAsync(CancellationToken.None).Timeout(Patience));

                Assert.That(failure, Is.InstanceOf<ReadIdleTimeoutException>(),
                    "Silence past the deadline has to surface as the idle timeout " +
                    "itself, not as whatever the receive happened to be doing.");
                Assert.That(
                    ((ReadIdleTimeoutException)failure).Idle,
                    Is.EqualTo(TimeSpan.FromMilliseconds(300)));
                Assert.That(failure, Is.InstanceOf<IOException>(),
                    "The session grades an IOException from the receive as fatal, " +
                    "which is the treatment a dead link deserves.");
            });
        }

        /// <summary>
        /// Three frames, each arriving well inside the window but the three together
        /// exceeding it by a wide margin. A deadline covering the connection rather
        /// than a frame would kill this healthy link.
        ///
        /// The gap is deliberately larger than the plan's 250 ms and the window
        /// larger than its 600 ms. Every await in an unfocused editor waits for the
        /// next loop tick - roughly 150 ms - and a receive spends two of them, so a
        /// 600 ms window has only about 300 ms of real headroom. These numbers keep
        /// both margins wide: each frame uses about a third of its window, and the
        /// three gaps together are more than twice the window.
        /// </summary>
        [UnityTest]
        public IEnumerator TheIdleDeadlineResetsForEachFrame()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, TimeSpan.FromMilliseconds(1000));

                for (var i = 0; i < 3; i++)
                {
                    server.SendFrame(MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}");
                    var message = await transport
                        .ReceiveAsync(CancellationToken.None)
                        .Timeout(Patience);
                    Assert.That(message.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
                    await UniTask.Delay(TimeSpan.FromMilliseconds(700), DelayType.Realtime);
                }
            });
        }

        /// <summary>
        /// The idle window here is thirty seconds, so nothing the deadline does can
        /// explain the failure: only the caller's own token can.
        ///
        /// The delay before the cancel is load-bearing. Without it the cancel could
        /// land before the read was ever issued, where ThrowIfCancellationRequested
        /// catches it and nothing about the parked case is exercised.
        /// </summary>
        [UnityTest]
        public IEnumerator ACallerCancellationIsNotReportedAsAnIdleTimeout()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(server, TimeSpan.FromSeconds(30));

                using var cancellation = new CancellationTokenSource();
                var receiving = transport.ReceiveAsync(cancellation.Token);
                await UniTask.Delay(TimeSpan.FromMilliseconds(250), DelayType.Realtime);
                cancellation.Cancel();

                var failure = await CaptureAsync(async () =>
                    await receiving.Timeout(Patience));

                Assert.That(failure, Is.InstanceOf<OperationCanceledException>());
                Assert.That(failure, Is.Not.InstanceOf<ReadIdleTimeoutException>(),
                    "Reporting an orderly shutdown as a dead link would send a " +
                    "session into Faulted while it was stopping cleanly.");
            });
        }

        /// <summary>
        /// Rejected where the option can still be named. CancelAfter throws
        /// ArgumentOutOfRangeException on a negative TimeSpan and treats zero as
        /// "now", so an unguarded value surfaces from inside a receive as either a
        /// bare argument fault or a link that dies on its first frame - and neither
        /// message mentions ReadIdleTimeout.
        /// </summary>
        [Test]
        public void ANonPositiveReadIdleTimeoutIsRejected()
        {
            var zero = Assert.Throws<ArgumentOutOfRangeException>(() => new TcpTransport(
                new TcpTransportOptions { ReadIdleTimeout = TimeSpan.Zero },
                new ManualClock(DateTimeOffset.UnixEpoch)));
            Assert.That(zero.Message, Does.Contain("ReadIdleTimeout"),
                "The message has to name the option, which is the whole reason the " +
                "guard is here rather than at the first receive.");

            Assert.Throws<ArgumentOutOfRangeException>(() => new TcpTransport(
                new TcpTransportOptions { ReadIdleTimeout = TimeSpan.FromSeconds(-1) },
                new ManualClock(DateTimeOffset.UnixEpoch)));
        }

        /// <summary>
        /// NUnit's Assert.ThrowsAsync takes a Task-returning delegate and UniTask is
        /// not a Task, so an awaited failure is captured here and asserted on by the
        /// caller. Returning the exception rather than asserting inside keeps each
        /// test's own expectation visible in the test that owns it.
        /// </summary>
        private static async UniTask<Exception> CaptureAsync(Func<UniTask> operation)
        {
            try
            {
                await operation();
            }
            catch (Exception failure)
            {
                return failure;
            }

            Assert.Fail("The operation was expected to fail, and completed instead.");
            return null;
        }
    }
}
