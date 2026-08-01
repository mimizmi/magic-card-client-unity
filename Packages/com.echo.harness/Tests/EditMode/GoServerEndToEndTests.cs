using System;
using System.Collections;
using System.Collections.Generic;
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
    /// One pass over a real socket against the authoritative Go server. Everything
    /// else in this suite runs on fakes or on a loopback double, and both of those
    /// encode our own reading of the protocol; this tier is the only thing in the
    /// repository that can disagree with us.
    ///
    /// <para>The server is remote and always on, so nothing here starts, stops, or
    /// waits for one. The endpoint comes from ECHO_SERVER_HOST and, optionally,
    /// ECHO_SERVER_PORT, and it is deliberately not committed anywhere: it is a
    /// developer endpoint, and a variable with no default is what keeps it out of
    /// the tree. A machine that has not set it skips this class, which the test
    /// gate tolerates for this class alone - see run-unity-tests.ps1.</para>
    ///
    /// <para>[UnityTest] rather than [Test], for the reason spelled out at
    /// TcpTransportFramingTests: a real socket completes on the thread pool and
    /// UniTask posts the continuation to the editor loop, so blocking the main
    /// thread with .GetAwaiter().GetResult() prevents the very pump that would
    /// complete the task and throws "Not yet completed, UniTask only allow to use
    /// await." instead.</para>
    /// </summary>
    public sealed class GoServerEndToEndTests
    {
        /// <summary>
        /// Generous rather than tight: this is the only tier whose latency is a
        /// real network's rather than loopback's, and a deadline that fits a fast
        /// link would turn a slow one into a failure that names the wrong cause.
        /// </summary>
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(15);

        /// <summary>
        /// One server ping interval (15 s) plus slack, which is long enough to be
        /// certain a Ping has arrived and no longer. The interval is a compile-time
        /// constant in the server's session.go, so there is no way to make this
        /// test faster, and it is worth its cost: a client that fails to answer a
        /// heartbeat loses the connection with nothing in any log to say why.
        ///
        /// <para>Deliberately NOT long enough for the server to act on a missing
        /// Pong, and this is the arithmetic that says so. The server's heartbeat
        /// loop ticks every 15 s and evaluates
        /// <c>time.Since(lastPongAt) &gt; pongTimeout</c> (35 s) on each tick, with
        /// <c>lastPongAt</c> set at accept and refreshed only by an inbound Pong.
        /// So the ticks land at 15/30/45 s and the FIRST one that can close a
        /// silent client is t=45 s. Waiting for that would nearly double the
        /// slowest test in the suite; asserting the reply was written instead pins
        /// the same property at t=25 s.</para>
        /// </summary>
        private static readonly TimeSpan OneHeartbeatInterval = TimeSpan.FromSeconds(25);

        private static readonly string SkipReason =
            "No server endpoint is configured, so the end-to-end tier did not run. " +
            $"Set {RemoteServerEndpoint.HostVariable} to enable it " +
            $"({RemoteServerEndpoint.PortVariable} is optional and defaults to " +
            $"{RemoteServerEndpoint.DefaultPort}).";

        [UnityTest]
        public IEnumerator LoginOverARealSocketReturnsATypedResponse()
        {
            var endpoint = RequireEndpoint();
            return UniTask.ToCoroutine(async () =>
            {
                var (transport, session) = await StartSessionAsync(endpoint);

                // The session is disposed first and the transport second, which is
                // what these two using statements do in this order. Keep it, but
                // not for the reason this comment used to give: it claimed the
                // reverse order would turn the session's bounded disconnect into a
                // swallowed failure. Measured, it would not fail at all.
                // TcpTransport.DisconnectAsync has no disposed guard and returns
                // immediately when State is already Disconnected, which Dispose
                // sets - so a transport disposed first makes
                // ProtocolSession.Dispose's disconnect a silent no-op.
                //
                // A no-op is still the wrong thing to arrange deliberately. This
                // order is what makes the session's close the one that actually
                // closes the socket, on the path production takes, which is the
                // only reason this tier can be said to have exercised it.
                using (transport)
                using (session)
                {
                    var response = await session.RequestAsync<LoginResponseDto>(
                        MessageId.LoginRequest,
                        new LoginRequestDto { PlayerName = "unity-harness-e2e" },
                        Patience,
                        CancellationToken.None);

                    Assert.That(response, Is.Not.Null);

                    // Asserted on the contents, not merely on arrival. A response
                    // that decoded into an all-default DTO would satisfy a null
                    // check while proving that our field names do not match the
                    // server's - which is precisely what this tier exists to catch.
                    Assert.That(response.Success, Is.True, response.Error);
                    Assert.That(response.PlayerId, Is.Not.Null.And.Not.Empty,
                        "The server issues a player id on a first login.");
                    Assert.That(response.ReconnectToken, Is.Not.Null.And.Not.Empty,
                        "The reconnect token is what a client must persist to resume.");
                    Assert.That(session.State, Is.EqualTo(SessionState.Connected));
                }
            });
        }

        [UnityTest]
        public IEnumerator TheRoundTripProbeMeasuresARealLatency()
        {
            var endpoint = RequireEndpoint();
            return UniTask.ToCoroutine(async () =>
            {
                var (transport, session) = await StartSessionAsync(endpoint);
                using (transport)
                using (session)
                {
                    var latency = await session.ProbeRoundTripAsync(CancellationToken.None);

                    Assert.That(latency, Is.GreaterThan(TimeSpan.Zero),
                        "The server echoes ClientPingRequest verbatim, so a payload " +
                        "we encoded differently would have thrown " +
                        "CorrelationMismatchException before reaching here.");
                    Assert.That(latency, Is.LessThan(TimeSpan.FromSeconds(5)));
                }
            });
        }

        /// <summary>
        /// The suite's one slow test. It is the only place a Ping the server itself
        /// composed and sent is answered: every other heartbeat test hands the
        /// session a Ping we wrote ourselves.
        ///
        /// <para>What it proves and what it does not. It proves the client wrote a
        /// Pong in reply to a real server Ping - the reply is counted at the
        /// transport, after the write returned, so a session that merely intended to
        /// answer does not satisfy it. It does NOT exercise the server's own
        /// enforcement: the run ends at t=25 s and the first heartbeat tick that can
        /// close a silent client is t=45 s, so the server has not yet had the chance
        /// to judge us. See OneHeartbeatInterval for that arithmetic.</para>
        /// </summary>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator TheClientAnswersARealServerHeartbeat()
        {
            var endpoint = RequireEndpoint();
            return UniTask.ToCoroutine(async () =>
            {
                var (transport, session) = await StartSessionAsync(endpoint);
                using (transport)
                using (session)
                {
                    var faults = new List<SessionFault>();
                    session.SubscribeToFaults(faults.Add);

                    await UniTask.Delay(OneHeartbeatInterval, DelayType.Realtime);

                    // The assertion the name of this test is about, and the only one
                    // here that a silent client fails. Everything below it is
                    // necessary but not sufficient: at t=25 s a client that answered
                    // and one that said nothing at all are still indistinguishable
                    // to the server, so a Connected state, an empty fault list and a
                    // working round trip hold for both.
                    Assert.That(transport.PongsSent, Is.GreaterThan(0),
                        "The server sends a Ping every 15 seconds and this test waits " +
                        "past one of them, so the session must have composed at least " +
                        "one Pong and handed it to a socket that accepted it. The " +
                        "count is taken after WriteAsync and FlushAsync return, which " +
                        "proves the write was accepted locally rather than that the " +
                        "bytes reached the peer - the round trip below is what shows " +
                        "the link was still carrying traffic. Counted rather than " +
                        "bounded: connect latency decides whether the second interval " +
                        "also lands inside the window.");

                    Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                        "The reply above was written; this says the link survived it.");
                    Assert.That(faults, Is.Empty,
                        "Faults: " + string.Join("; ", faults.ConvertAll(f => f.Diagnostic)));

                    // Still usable, which a half-dead connection would not be. The
                    // state check above cannot see a link the kernel has not yet
                    // noticed is gone; a completed round trip can.
                    var latency = await session.ProbeRoundTripAsync(CancellationToken.None);
                    Assert.That(latency, Is.GreaterThan(TimeSpan.Zero));
                }
            });
        }

        /// <summary>
        /// Resolved before the coroutine is built rather than inside it. Assert.Ignore
        /// throws, and throwing from the test method itself is the shape the Unity
        /// runner handles cleanly; thrown from inside UniTask.ToCoroutine it would
        /// have to survive being captured into a task and rethrown from MoveNext.
        /// </summary>
        private static RemoteServerEndpoint RequireEndpoint()
        {
            if (!RemoteServerEndpoint.TryResolve(out var endpoint))
            {
                Assert.Ignore(SkipReason);
            }

            return endpoint;
        }

        /// <summary>
        /// A connected session, or nothing left behind. There is no readiness wait
        /// and no retry: the server is already up, so a failed connect is a real
        /// failure rather than a not-yet.
        /// </summary>
        private static async UniTask<(CountingTransport Transport, ProtocolSession Session)>
            StartSessionAsync(RemoteServerEndpoint endpoint)
        {
            var transport = new CountingTransport(new TcpTransport(
                new TcpTransportOptions { Host = endpoint.Host, Port = endpoint.Port },
                new SystemClock()));
            ProtocolSession session = null;
            try
            {
                // RecordingSessionScheduler, because these are EditMode tests with
                // no player loop to switch to. That is the reason the scheduler port
                // exists at all, and MainThreadSessionScheduler is PlayMode's.
                session = new ProtocolSession(
                    transport, new SystemClock(), new RecordingSessionScheduler());
                await session.StartAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // A half-built session still owns a socket, and a leaked socket
                // stalls the editor at domain reload.
                session?.Dispose();
                transport.Dispose();
                throw;
            }

            return (transport, session);
        }

        /// <summary>
        /// A pass-through <see cref="ITransport"/> that counts the heartbeat replies
        /// the session writes. It exists because the property this tier is named for
        /// - the client answers a real Ping - is otherwise unobservable from the
        /// outside: the reply is fire-and-forget from the receive pump, it produces
        /// no state change, no fault and no return value, and the peer that would
        /// react to its absence does not react for another twenty seconds.
        ///
        /// <para>Counted AFTER the inner send returns, deliberately. A session that
        /// tried to answer and failed at the socket is exactly the failure worth
        /// catching, and incrementing first would count it as a success.</para>
        ///
        /// <para>Local to this fixture rather than in TestKit. It is scaffolding for
        /// one assertion, and TestKit is an assembly the architecture gate now pins
        /// specifically because things that touch sockets keep landing in it.</para>
        /// </summary>
        private sealed class CountingTransport : ITransport, IDisposable
        {
            private readonly TcpTransport inner;
            private int pongsSent;

            public CountingTransport(TcpTransport inner)
            {
                this.inner = inner ?? throw new ArgumentNullException(nameof(inner));
            }

            /// <summary>
            /// Interlocked on both ends: the reply is written from the receive pump,
            /// which over a real socket resumes on whichever thread the read
            /// completed on, and the assertion reads from the test's coroutine.
            /// </summary>
            public int PongsSent => Interlocked.CompareExchange(ref pongsSent, 0, 0);

            public TransportState State => inner.State;

            public UniTask ConnectAsync(CancellationToken cancellationToken) =>
                inner.ConnectAsync(cancellationToken);

            public async UniTask SendAsync(
                TransportMessage message,
                CancellationToken cancellationToken)
            {
                await inner.SendAsync(message, cancellationToken);
                if (message.MessageId == MessageId.Pong)
                {
                    Interlocked.Increment(ref pongsSent);
                }
            }

            public UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken) =>
                inner.ReceiveAsync(cancellationToken);

            public UniTask DisconnectAsync(CancellationToken cancellationToken) =>
                inner.DisconnectAsync(cancellationToken);

            public void Dispose() => inner.Dispose();
        }
    }
}
