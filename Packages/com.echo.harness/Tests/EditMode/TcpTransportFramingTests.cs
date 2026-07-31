using System;
using System.Collections;
using System.IO;
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
    /// These are [UnityTest] rather than [Test] because they are the first tests in
    /// this suite whose work is genuinely asynchronous. Every other EditMode fixture
    /// drives an in-memory fake that completes synchronously, so
    /// .GetAwaiter().GetResult() returns a value there; on a pending UniTask the
    /// same call throws "Not yet completed, UniTask only allow to use await."
    /// instead of ever reaching the assertion. A real socket completes on the thread
    /// pool and UniTask resumes on the editor loop, so the loop has to keep ticking
    /// while a test waits - which is exactly what yielding a coroutine does and what
    /// blocking the main thread would prevent.
    /// </summary>
    public sealed class TcpTransportFramingTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private static async UniTask<TcpTransport> ConnectAsync(LoopbackProtocolServer server)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions { Host = "127.0.0.1", Port = server.Port },
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

        [UnityTest]
        public IEnumerator AFragmentedFrameIsReadAsOneMessage()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(server);

                var receiving = transport.ReceiveAsync(CancellationToken.None);
                var frame = BinaryFrameCodec.Encode(
                    MessageId.PhaseChangeEvent,
                    Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));

                // The header one byte at a time, then the body in two chunks. A reader
                // that assumes one Read returns a whole header fails here, and TCP
                // guarantees nothing better than this.
                for (var i = 0; i < 6; i++)
                {
                    server.SendBytes(new[] { frame[i] });
                }

                var body = Slice(frame, 6, frame.Length - 6);
                var half = body.Length / 2;
                server.SendBytes(Slice(body, 0, half));
                server.SendBytes(Slice(body, half, body.Length - half));

                var message = await receiving.Timeout(Patience);
                Assert.That(message.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
                Assert.That(
                    Encoding.UTF8.GetString(message.Payload),
                    Is.EqualTo("{\"phase\":\"action\"}"));
            });
        }

        [UnityTest]
        public IEnumerator TwoFramesInOneSegmentAreReadAsTwoMessages()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(server);

                var first = BinaryFrameCodec.Encode(MessageId.Ping, new byte[0]);
                var second = BinaryFrameCodec.Encode(
                    MessageId.PhaseChangeEvent,
                    Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));
                var both = new byte[first.Length + second.Length];
                Array.Copy(first, both, first.Length);
                Array.Copy(second, 0, both, first.Length, second.Length);
                server.SendBytes(both);

                var a = await transport.ReceiveAsync(CancellationToken.None)
                    .Timeout(Patience);
                var b = await transport.ReceiveAsync(CancellationToken.None)
                    .Timeout(Patience);

                Assert.That(a.MessageId, Is.EqualTo(MessageId.Ping));
                Assert.That(a.Payload.Length, Is.EqualTo(0),
                    "The server sends Ping with a nil payload, so its body length is 0.");
                Assert.That(b.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
            });
        }

        [UnityTest]
        public IEnumerator ADeclaredLengthOverTheBoundFailsTheReceive()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(server);

                var receiving = transport.ReceiveAsync(CancellationToken.None);
                server.SendRawHeader(2 * 1024 * 1024, MessageId.GameStateEvent);

                var failure = await CaptureAsync(async () => await receiving.Timeout(Patience));
                Assert.That(failure, Is.InstanceOf<InvalidDataException>());
                Assert.That(failure.Message, Does.Contain("2097152"),
                    "The length is rejected before a single body byte is allocated.");
            });
        }

        [UnityTest]
        public IEnumerator ACloseMidBodyFailsTheReceive()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(server);

                var receiving = transport.ReceiveAsync(CancellationToken.None);
                var frame = BinaryFrameCodec.Encode(
                    MessageId.PhaseChangeEvent,
                    Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));
                server.SendBytes(Slice(frame, 0, 8));
                server.CloseConnection();

                var failure = await CaptureAsync(async () => await receiving.Timeout(Patience));
                Assert.That(failure, Is.InstanceOf<EndOfStreamException>());
            });
        }

        /// <summary>
        /// Disposing the transport while a frame is half-read must surface as
        /// EndOfStreamException. This is the local-dispose path, distinct from
        /// ACloseMidBodyFailsTheReceive above, which closes the far end instead.
        ///
        /// Read the scope of this test honestly: it does NOT pin I1, the
        /// capture-once fix in ReadExactlyAsync. It was written for that and does
        /// not achieve it - verified by reverting the fix, against which this test
        /// still passes. By the time Dispose runs here the reader is already parked
        /// inside ReadAsync, so the stream field was dereferenced before it was
        /// nulled and the disposal arrives as ObjectDisposedException either way.
        /// I1's window is the instant between an I/O completion and the next
        /// dereference, both of which run inside the async continuation on the
        /// thread pool, and a test driving the socket from the main thread cannot
        /// deterministically land a Dispose inside it. A racing attempt would be
        /// flaky, which is worse than absent.
        /// </summary>
        [UnityTest]
        public IEnumerator DisposingMidFrameFailsTheReceiveCleanly()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = await ConnectAsync(server);

                var receiving = transport.ReceiveAsync(CancellationToken.None);
                var frame = BinaryFrameCodec.Encode(
                    MessageId.PhaseChangeEvent,
                    Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));

                // A whole header plus part of the body, so the reader consumes the
                // header, takes a short first chunk, and loops for the remainder.
                server.SendBytes(Slice(frame, 0, 6));
                server.SendBytes(Slice(frame, 6, 4));
                await UniTask.Delay(TimeSpan.FromMilliseconds(250), DelayType.Realtime);

                transport.Dispose();

                var failure = await CaptureAsync(async () => await receiving.Timeout(Patience));
                Assert.That(failure, Is.InstanceOf<EndOfStreamException>(),
                    "A disposal mid-frame must read as end of stream.");
            });
        }

        [UnityTest]
        public IEnumerator ConnectingToAClosedPortFails()
        {
            return UniTask.ToCoroutine(async () =>
            {
                int deadPort;
                using (var probe = new LoopbackProtocolServer())
                {
                    deadPort = probe.Port;
                }

                using var transport = new TcpTransport(
                    new TcpTransportOptions { Host = "127.0.0.1", Port = deadPort },
                    new ManualClock(DateTimeOffset.UnixEpoch));

                var failure = await CaptureAsync(async () =>
                    await transport.ConnectAsync(CancellationToken.None).Timeout(Patience));
                Assert.That(failure, Is.InstanceOf<SocketException>());
            });
        }

        [UnityTest]
        public IEnumerator CancellingAConnectDoesNotLeaveItConnecting()
        {
            return UniTask.ToCoroutine(async () =>
            {
                using var server = new LoopbackProtocolServer();
                using var transport = new TcpTransport(
                    new TcpTransportOptions { Host = "127.0.0.1", Port = server.Port },
                    new ManualClock(DateTimeOffset.UnixEpoch));

                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                var failure = await CaptureAsync(async () =>
                    await transport.ConnectAsync(cancellation.Token).Timeout(Patience));

                // The exact type, not Is.InstanceOf: TaskCanceledException derives
                // from OperationCanceledException, so InstanceOf would accept one
                // leaking out of the framework and stop distinguishing it from the
                // deliberate translation in ConnectAsync - which is the whole point
                // of this test.
                Assert.That(failure, Is.TypeOf<OperationCanceledException>());
                Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected),
                    "A transport stuck in Connecting can never be retried.");
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

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Array.Copy(source, offset, slice, 0, count);
            return slice;
        }
    }
}
