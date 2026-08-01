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
        /// One server ping interval (15 s) plus slack. The interval is a
        /// compile-time constant in the server's session.go, so there is no way to
        /// make this test faster, and it is worth its cost: a client that fails to
        /// answer a heartbeat loses the connection 35 seconds later with nothing in
        /// any log to say why.
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
                // what these two using statements do in this order. It matters:
                // ProtocolSession.Dispose launches its own bounded disconnect
                // through the transport, and a transport already disposed would
                // turn that into a swallowed failure rather than a clean close.
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
        /// The suite's one slow test. It is the only place the heartbeat reply is
        /// exercised against the peer that actually enforces it: every other
        /// heartbeat test hands the session a Ping we wrote ourselves.
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

                    Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                        "A missed Pong makes the server close the connection, which " +
                        "the receive pump would surface as a fault and a Faulted state.");
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
        private static async UniTask<(TcpTransport Transport, ProtocolSession Session)>
            StartSessionAsync(RemoteServerEndpoint endpoint)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions { Host = endpoint.Host, Port = endpoint.Port },
                new SystemClock());
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
    }
}
