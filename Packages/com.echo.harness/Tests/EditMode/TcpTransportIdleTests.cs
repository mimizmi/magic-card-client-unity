using System;
using System.Collections;
using System.IO;
using System.Text;
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
    /// The timeouts here are real wall-clock milliseconds rather than a ManualTime
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
                new ManualTime(DateTimeOffset.UnixEpoch));
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
                Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected),
                    "The deadline closed the socket itself, so a transport still " +
                    "reporting Connected would be lying about a link it had just " +
                    "killed.");
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
        /// A token that is already cancelled when the receive is entered. This is not
        /// a contrived case: a session whose pump is cancelled and then loops once
        /// more calls ReceiveAsync with a token that is already in that state.
        ///
        /// Both assertions are load-bearing and neither is sufficient alone.
        /// CreateLinkedTokenSource over an already-cancelled token returns an
        /// already-cancelled source, and Token.Register on one of those invokes the
        /// callback synchronously on the calling thread - so without an early check
        /// the link is torn down before the first read is ever issued, and the caller
        /// is told the peer closed. Asserting only the exception type would still pass
        /// against a fix that merely relabels the failure while destroying the socket.
        /// </summary>
        [UnityTest]
        public IEnumerator AnAlreadyCancelledReceiveLeavesTheLinkAlone()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(server, TimeSpan.FromSeconds(30));

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                var failure = await CaptureAsync(async () =>
                    await transport.ReceiveAsync(cancellation.Token).Timeout(Patience));

                Assert.That(failure, Is.InstanceOf<OperationCanceledException>(),
                    "A caller that has already cancelled is asking to stop, not being " +
                    "told its peer went away.");
                Assert.That(transport.State, Is.EqualTo(TransportState.Connected),
                    "Nothing was read and nothing timed out, so the link must still " +
                    "be there afterwards - a cancelled receive is not a dead link.");
            });
        }

        /// <summary>
        /// The deadline lands in the gap between the header read and the body read.
        /// That gap is real and wide: UniTask's ValueTask bridge captures Unity's
        /// synchronization context, so a completed header read is posted to the
        /// main-thread queue rather than resumed inline, and the whole time it sits
        /// there undrained the reader is between its two ReadExactlyAsync calls.
        ///
        /// This is the watchdog's own primary path, so it has to produce the
        /// watchdog's own diagnostic. Without the token check in the null-stream
        /// guard, the body call sees the stream the deadline just nulled and reports
        /// "The connection closed while a frame was being read" - the message this
        /// file elsewhere calls indistinguishable from an ordinary peer close, for a
        /// failure whose cause is known exactly.
        ///
        /// The Thread.Sleep is what makes the window deterministic rather than a
        /// race, for the same reason as in
        /// DisposingBetweenHeaderAndBodyFailsTheReceiveCleanly: awaiting here would
        /// yield to the very pump that drains the continuation. The margins are wide
        /// in both directions - the header arrives around 300 ms into a 1000 ms
        /// window, and the sleep outlasts the deadline by hundreds of milliseconds -
        /// so neither an early nor a late tick can turn this into a different test.
        /// </summary>
        [UnityTest]
        public IEnumerator ADeadlineBetweenTheHeaderAndTheBodyStillNamesTheIdleTimeout()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, TimeSpan.FromMilliseconds(1000));

                var receiving = transport.ReceiveAsync(CancellationToken.None);

                // The reader has to be parked inside the header read before the
                // header arrives, so that its completion is posted rather than
                // resumed inline.
                await UniTask.Delay(TimeSpan.FromMilliseconds(250), DelayType.Realtime);

                var frame = BinaryFrameCodec.Encode(
                    MessageId.PhaseChangeEvent,
                    Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));
                var header = new byte[6];
                Array.Copy(frame, header, header.Length);

                // Exactly the header and not one byte of the body, so the next thing
                // the reader wants is a second trip through ReadExactlyAsync.
                server.SendBytes(header);

                // Blocking, not awaiting: the header read completes and queues its
                // continuation while the pump that would drain it is held here, and
                // the deadline fires from its timer thread in the middle of that.
                Thread.Sleep(1200);

                var failure = await CaptureAsync(async () =>
                    await receiving.Timeout(Patience));

                Assert.That(failure, Is.InstanceOf<ReadIdleTimeoutException>(),
                    "A deadline that fires between the header and the body is still " +
                    "the deadline firing, and has to say so.");
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
                new ManualTime(DateTimeOffset.UnixEpoch)));
            Assert.That(zero.Message, Does.Contain("ReadIdleTimeout"),
                "The message has to name the option, which is the whole reason the " +
                "guard is here rather than at the first receive.");

            Assert.Throws<ArgumentOutOfRangeException>(() => new TcpTransport(
                new TcpTransportOptions { ReadIdleTimeout = TimeSpan.FromSeconds(-1) },
                new ManualTime(DateTimeOffset.UnixEpoch)));
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
