using System;
using System.Collections;
using System.Collections.Generic;
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
        /// delivers whole frames under real concurrency. It does NOT, on this
        /// platform, discriminate the sendGate: with the gate deleted the run still
        /// passes, and it was pushed to 64 senders of 1,000,000 bytes and to 99
        /// senders of 262,144 bytes without ever interleaving. Windows serializes
        /// the overlapped sends beneath us, so byte-level interleaving cannot be
        /// produced from a test here. The gate is still required - NetworkStream
        /// does not support concurrent writes by contract, other platforms do split
        /// a large write, and the gate is what makes SendBudget.TryConsume's
        /// read-modify-write safe - but do not cite this test as the evidence.
        /// </summary>
        private const int ConcurrentSenders = 8;
        private const int InterleavingBodyBytes = 524_288;

        private static async UniTask<TcpTransport> ConnectAsync(
            LoopbackProtocolServer server,
            ManualClock clock,
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
                    server, new ManualClock(DateTimeOffset.UnixEpoch), budgetPerSecond: 100);

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
            });
        }

        [UnityTest]
        public IEnumerator ExceedingTheBudgetThrowsAndKeepsTheConnection()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, new ManualClock(DateTimeOffset.UnixEpoch));

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
                    server, new ManualClock(DateTimeOffset.UnixEpoch));

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
                for (var i = 0; i < 100; i++)
                {
                    await transport.SendAsync(
                        new TransportMessage(MessageId.Pong, new byte[0]),
                        CancellationToken.None);
                }

                await server.WaitForFramesAsync(130, Patience);
            });
        }

        [UnityTest]
        public IEnumerator APayloadOverTheBoundIsRefusedBeforeTheGate()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(
                    server, new ManualClock(DateTimeOffset.UnixEpoch));

                var tooBig = new byte[WireFrameSpec.MaxPayloadBytes + 1];

                var failure = await CaptureAsync(async () => await transport.SendAsync(
                    new TransportMessage(MessageId.GameStateEvent, tooBig),
                    CancellationToken.None));

                Assert.That(failure, Is.InstanceOf<ArgumentOutOfRangeException>());
                Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
            });
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
