using System;
using System.Collections;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
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
    /// </summary>
    public sealed class TcpTransportSendTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        /// <summary>
        /// Sized so every write genuinely goes pending: a body this large cannot be
        /// swallowed whole by the socket's send buffer plus the peer's receive
        /// window, so SendAsync suspends and the next caller starts while the
        /// previous write is still outstanding. That was measured, not assumed - a
        /// probe run at exactly these numbers found all 9 sends, the 8 bodies and
        /// the Pong, in UniTaskStatus.Pending at the same instant.
        ///
        /// Read what this test does and does not prove. It proves the send path
        /// delivers whole frames under real concurrency, and it is a live
        /// regression test for the gate's bookkeeping: a lost Release, a Release on
        /// a path that never acquired, or a deadlock all hang it at Timeout.
        ///
        /// It does NOT discriminate the gate's presence here. With the gate deleted
        /// the run still passes, and it was pushed to 64 senders of 1,000,000 bytes
        /// and to 99 senders of 262,144 bytes without ever interleaving. Something
        /// beneath this code serializes the overlapped sends - the experiment varied
        /// only size and sender count, so it cannot say whether that is Winsock or
        /// Mono's own per-socket async queue, and no conclusion should be drawn
        /// about any other platform or runtime. The gate's justification is on the
        /// sendGate field in TcpTransport, which is where someone about to delete it
        /// will be looking; do not cite this test as the evidence.
        /// </summary>
        private const int ConcurrentSenders = 8;
        private const int InterleavingBodyBytes = 524_288;

        private static async UniTask<TcpTransport> ConnectAsync(
            LoopbackProtocolServer server,
            ManualTime clock,
            int budgetPerSecond = 30)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions
                {
                    Host = "127.0.0.1",
                    Port = server.Port,
                    SendBudgetPerSecond = budgetPerSecond
                },
                clock);
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

        [UnityTest]
        public IEnumerator ConcurrentSendsArriveAsWholeFrames()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, new ManualTime(DateTimeOffset.UnixEpoch), budgetPerSecond: 100);

                // Several callers plus a heartbeat reply, all in flight together.
                // This is the shape a real session produces: it answers Ping from
                // its receive pump while a caller's own send is still going. Were
                // the writes to interleave, the loopback server's frame reader -
                // not an assertion here - is what would notice, exactly as the Go
                // server would; see the note on ConcurrentSenders for why that
                // signal cannot be provoked on this platform.
                var body = BodyOf(InterleavingBodyBytes);
                var sends = new List<UniTask>();
                for (var i = 0; i < ConcurrentSenders; i++)
                {
                    sends.Add(transport.SendAsync(
                        new TransportMessage(MessageId.PhaseChangeEvent, body),
                        CancellationToken.None));
                }

                sends.Add(transport.SendAsync(
                    new TransportMessage(MessageId.Pong, new byte[0]),
                    CancellationToken.None));

                await UniTask.WhenAll(sends).Timeout(Patience);
                await server.WaitForFramesAsync(ConcurrentSenders + 1, Patience);

                var received = server.Received;
                Assert.That(received.Count, Is.EqualTo(ConcurrentSenders + 1));
                var phaseFrames = 0;
                var pongFrames = 0;
                foreach (var frame in received)
                {
                    if (frame.MessageId == MessageId.PhaseChangeEvent)
                    {
                        phaseFrames++;
                        Assert.That(frame.Payload.Length, Is.EqualTo(body.Length),
                            "A truncated body is what interleaving looks like.");
                    }
                    else if (frame.MessageId == MessageId.Pong)
                    {
                        pongFrames++;
                    }
                }

                Assert.That(phaseFrames, Is.EqualTo(ConcurrentSenders));
                Assert.That(pongFrames, Is.EqualTo(1));

                // Asserted rather than assumed. WaitForFramesAsync returns as soon
                // as the count is reached, so a corruption that produced exactly
                // this many decodable frames and then desynchronized the reader
                // would satisfy every assertion above it.
                Assert.That(server.ReadFailure, Is.Null,
                    "The loopback reader stopped, which means the bytes after the "
                    + "frames it did decode were not frame-aligned.");
            });
        }

        [UnityTest]
        public IEnumerator ExceedingTheBudgetThrowsAndKeepsTheConnection()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, new ManualTime(DateTimeOffset.UnixEpoch));

                var body = Encoding.UTF8.GetBytes("{\"phase\":\"action\"}");
                for (var i = 0; i < 30; i++)
                {
                    await transport.SendAsync(
                        new TransportMessage(MessageId.PhaseChangeEvent, body),
                        CancellationToken.None);
                }

                var failure = await CaptureAsync(async () => await transport.SendAsync(
                    new TransportMessage(MessageId.PhaseChangeEvent, body),
                    CancellationToken.None));

                Assert.That(failure, Is.InstanceOf<SendBudgetExceededException>());
                Assert.That(
                    ((SendBudgetExceededException)failure).MessageId,
                    Is.EqualTo(MessageId.PhaseChangeEvent));
                Assert.That(transport.State, Is.EqualTo(TransportState.Connected),
                    "One caller's loop bug must not become a global disconnect.");
            });
        }

        [UnityTest]
        public IEnumerator PongIsExemptFromTheBudget()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, new ManualTime(DateTimeOffset.UnixEpoch));

                var body = Encoding.UTF8.GetBytes("{\"phase\":\"action\"}");
                for (var i = 0; i < 30; i++)
                {
                    await transport.SendAsync(
                        new TransportMessage(MessageId.PhaseChangeEvent, body),
                        CancellationToken.None);
                }

                // The server handles Pong before its own limiter and never counts
                // it. A guard that refused this Pong would cause the 35 second
                // heartbeat disconnect it exists to prevent, and the symptom would
                // appear with no obvious cause. Any throw here fails the test: the
                // awaits below are unguarded, so a SendBudgetExceededException would
                // surface as the test's own failure.
                //
                // Thirty-five, not a hundred. The budget is thirty and the loop
                // above already exhausted it, so the first Pong through settles the
                // question; the rest only guard against a second cap hiding at the
                // same figure, and thirty-five clears that. The count is not free -
                // each await waits for the editor loop to resume it, so a hundred
                // Pongs made this single test twenty seconds long and most of a
                // suite that then overran the test runner's own ceiling.
                for (var i = 0; i < 35; i++)
                {
                    await transport.SendAsync(
                        new TransportMessage(MessageId.Pong, new byte[0]),
                        CancellationToken.None);
                }

                await server.WaitForFramesAsync(65, Patience);
            });
        }

        [UnityTest]
        public IEnumerator APayloadOverTheBoundIsRefused()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, new ManualTime(DateTimeOffset.UnixEpoch));

                var tooBig = new byte[WireFrameSpec.MaxPayloadBytes + 1];

                var failure = await CaptureAsync(async () => await transport.SendAsync(
                    new TransportMessage(MessageId.GameStateEvent, tooBig),
                    CancellationToken.None));

                Assert.That(failure, Is.InstanceOf<ArgumentOutOfRangeException>());
                Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
            });
        }

        /// <summary>
        /// A local disposal while a write is outstanding must read as end of stream,
        /// the same way DisposingMidFrameFailsTheReceiveCleanly pins it for the read
        /// path. Untranslated the failure escapes raw and a session grades an
        /// ordinary shutdown as a transport fault.
        ///
        /// What escapes here was measured, and it is not what the read path sees. A
        /// parked read broken by a local Dispose surfaces as ObjectDisposedException;
        /// a parked write broken by the same Dispose surfaces as a bare IOException
        /// on this runtime, so it is the IOException clause that catches this, not
        /// the ObjectDisposedException one. Both land on EndOfStreamException, which
        /// is the whole point of translating them together.
        /// </summary>
        [UnityTest]
        public IEnumerator DisposingMidSendFailsTheSendCleanly()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var peer = new SilentPeer();
                using var transport = await ConnectAsync(peer);

                var sending = await ParkedSendAsync(transport, CancellationToken.None);

                transport.Dispose();

                var failure = await CaptureAsync(async () => await sending.Timeout(Patience));
                Assert.That(failure, Is.InstanceOf<EndOfStreamException>(),
                    "A disposal mid-write must read as end of stream. A bare " +
                    "IOException is an IOException too, so only the translation " +
                    "satisfies this.");
            });
        }

        /// <summary>
        /// The far end vanishing mid-write, rather than the near end being disposed.
        /// Both arrive as IOException on this runtime and both must land on
        /// EndOfStreamException, but they are separate routes into the send path and
        /// a translation that covered only one of them would still let a session
        /// fault on the other.
        /// </summary>
        [UnityTest]
        public IEnumerator AResetMidSendFailsTheSendCleanly()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var peer = new SilentPeer();
                using var transport = await ConnectAsync(peer);

                var sending = await ParkedSendAsync(transport, CancellationToken.None);

                peer.Reset();

                var failure = await CaptureAsync(async () => await sending.Timeout(Patience));
                Assert.That(failure, Is.InstanceOf<EndOfStreamException>(),
                    "A reset mid-write must read as end of stream. A bare IOException " +
                    "is an IOException too, so this assertion is only satisfied by " +
                    "the translation.");
            });
        }

        /// <summary>
        /// A cancelled send must surface as cancellation, not as the mechanism that
        /// happened to end the write.
        ///
        /// The disposal after the cancel is not incidental. A parked
        /// NetworkStream.WriteAsync observes no token, so cancelling alone leaves it
        /// parked and only closing the socket under it ends the wait - which is the
        /// same CancelPump-then-Disconnect order a session's StopAsync runs. Without
        /// the translation that shutdown surfaces as a bare IOException and the
        /// session faults while it was stopping cleanly.
        /// </summary>
        [UnityTest]
        public IEnumerator CancellingMidSendIsReportedAsCancellation()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var peer = new SilentPeer();
                using var transport = await ConnectAsync(peer);

                using var cancellation = new CancellationTokenSource();
                var sending = await ParkedSendAsync(transport, cancellation.Token);

                cancellation.Cancel();
                transport.Dispose();

                var failure = await CaptureAsync(async () => await sending.Timeout(Patience));
                Assert.That(failure, Is.InstanceOf<OperationCanceledException>(),
                    "Cancelling a send must not be reported as a broken link.");
            });
        }

        /// <summary>
        /// Returns a send that is genuinely still outstanding, by writing
        /// maximum-sized frames one at a time until one fails to complete.
        ///
        /// One frame is not enough, and that was measured rather than assumed: a
        /// single 1 MiB write at a peer that never reads still completed, so this
        /// runtime absorbs at least that much between the sender's buffer and the
        /// receiver's window. Frames are sent one at a time rather than in a batch
        /// because the send gate serializes them - a batch leaves every sender but
        /// one parked in WaitAsync rather than in the write, and a sender that never
        /// reached the stream fails on the null-stream guard instead, which already
        /// throws EndOfStreamException and would satisfy these assertions with no
        /// translation in place at all.
        ///
        /// The assertion at the end is what keeps these tests honest: without a
        /// parked write there is nothing here to translate.
        /// </summary>
        private static async UniTask<UniTask> ParkedSendAsync(
            TcpTransport transport,
            CancellationToken cancellationToken)
        {
            var body = BodyOf(WireFrameSpec.MaxPayloadBytes);
            for (var attempt = 0; attempt < SendsBeforeGivingUpOnParking; attempt++)
            {
                var sending = transport.SendAsync(
                    new TransportMessage(MessageId.PhaseChangeEvent, body),
                    cancellationToken);
                // 300 ms, not 100. A completed write's continuation is posted to the
                // main-thread queue rather than resumed inline, so a status read
                // taken before that queue is drained reports Pending for a write
                // that has already finished - and every one of these tests would
                // then be acting on a send that was never outstanding. A 250 ms
                // settle was measured as enough for the queue to drain in this
                // fixture; this leaves margin on top of it.
                await UniTask.Delay(TimeSpan.FromMilliseconds(300), DelayType.Realtime);
                if (sending.Status == UniTaskStatus.Pending)
                {
                    return sending;
                }

                await sending;
            }

            Assert.Fail(
                $"No write was still outstanding after {SendsBeforeGivingUpOnParking} " +
                "frames of " + WireFrameSpec.MaxPayloadBytes + " bytes at a peer that " +
                "never reads. Without a parked write these tests prove nothing, so " +
                "this is a failure rather than a skip.");
            return UniTask.CompletedTask;
        }

        /// <summary>
        /// Enough frames to overflow whatever this runtime buffers, bounded so a
        /// platform that buffers without limit fails the test instead of writing
        /// until the suite times out. Each frame is 1 MiB, so this is a 32 MiB
        /// ceiling.
        /// </summary>
        private const int SendsBeforeGivingUpOnParking = 32;

        private static async UniTask<TcpTransport> ConnectAsync(SilentPeer peer)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions
                {
                    Host = "127.0.0.1",
                    Port = peer.Port,

                    // High enough that filling the peer's buffers never runs out of
                    // budget: the clock is manual and never advances, so the window
                    // never resets and every frame above counts against this one
                    // allowance.
                    SendBudgetPerSecond = SendsBeforeGivingUpOnParking + 8
                },
                new ManualTime(DateTimeOffset.UnixEpoch));
            try
            {
                var connecting = transport.ConnectAsync(CancellationToken.None);
                await peer.AcceptAsync(Patience);
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
        /// Accepts one connection and never reads a byte from it, so a large write
        /// parks instead of completing. LoopbackProtocolServer cannot stand in here:
        /// its AcceptAsync starts a reader thread that drains everything sent to it,
        /// which is exactly what has to not happen for a write to stay outstanding.
        /// </summary>
        private sealed class SilentPeer : IDisposable
        {
            private readonly TcpListener listener;
            private TcpClient connection;

            public SilentPeer()
            {
                listener = new TcpListener(IPAddress.Loopback, 0);

                // Set on the listening socket rather than the accepted one, because
                // accepted sockets inherit it and the receive window is already
                // negotiated by the time a connection can be touched. A small window
                // is what makes the sender run out of room in a few frames instead of
                // dozens.
                listener.Server.SetSocketOption(
                    SocketOptionLevel.Socket, SocketOptionName.ReceiveBuffer, 1024);
                listener.Start();
                Port = ((IPEndPoint)listener.LocalEndpoint).Port;
            }

            public int Port { get; }

            public async UniTask AcceptAsync(TimeSpan timeout)
            {
                var accept = listener.AcceptTcpClientAsync();

                // The UniTask.WhenAny overload taking a UniTask<T> and a plain
                // UniTask reports the winner as a bool: true means the accept won.
                var (accepted, _) = await UniTask.WhenAny(
                    accept.AsUniTask(), UniTask.Delay(timeout, DelayType.Realtime));
                if (!accepted)
                {
                    throw new TimeoutException($"No client connected within {timeout}.");
                }

                connection = accept.Result;
            }

            /// <summary>
            /// An abortive close, not a graceful one. A zero linger makes the peer
            /// answer with RST at once; a graceful close would only half-close it and
            /// leave the outstanding write sitting in the send buffer.
            /// </summary>
            public void Reset()
            {
                connection.LingerState = new LingerOption(true, 0);
                connection.Close();
            }

            public void Dispose()
            {
                try
                {
                    connection?.Close();
                }
                catch (Exception)
                {
                    // Already closed by Reset.
                }

                try
                {
                    listener.Stop();
                }
                catch (Exception)
                {
                    // Already stopped.
                }
            }
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

        /// <summary>
        /// A non-constant pattern, so a body spliced out of two different writes
        /// cannot happen to look well-formed by landing on a run of identical bytes.
        /// </summary>
        private static byte[] BodyOf(int length)
        {
            var body = new byte[length];
            for (var i = 0; i < length; i++)
            {
                body[i] = (byte)(i % 251);
            }

            return body;
        }
    }
}
