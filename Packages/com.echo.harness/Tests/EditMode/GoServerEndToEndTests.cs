using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Bootstrap;
using Echo.Harness.Contracts;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;
using UnityEngine.TestTools;
using VContainer;

namespace Echo.Harness.Tests.EditMode
{
    /// <summary>
    /// One pass over a real socket against the authoritative Go server. Everything
    /// else in this suite runs on fakes or on a loopback double, and both of those
    /// encode our own reading of the protocol; this tier is the only thing in the
    /// repository that can disagree with us.
    ///
    /// <para>The server is remote and always on, so nothing here starts, stops, or
    /// waits for one. The endpoint comes from the HarnessEndpointSettings asset
    /// first and ECHO_SERVER_HOST (plus optional ECHO_SERVER_PORT) second, which is
    /// the same chain the bootstrap scene resolves through; neither is committed,
    /// because it is a developer endpoint and a source with no default is what
    /// keeps it out of the tree. A machine with neither skips this class, which the
    /// test gate tolerates for this class alone - see run-unity-tests.ps1.</para>
    ///
    /// <para>The asset is listed first because on this project it is usually the
    /// only one that works. An editor inherits its environment from whatever
    /// launched it - normally Unity Hub, which is long-running - so a variable set
    /// after the Hub started is invisible to the editor no matter how many times
    /// the editor itself is restarted. Measured: with the variable set and the
    /// asset absent this tier skipped; with the asset present it ran.</para>
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

        /// <summary>
        /// Names both doors, in the order they are tried. The asset is listed first
        /// and deliberately: it is the one that works on a machine where the editor
        /// was launched before the variable was set, which is the ordinary case
        /// rather than an edge one.
        /// </summary>
        private static readonly string SkipReason =
            "No server endpoint is configured, so the end-to-end tier did not run. " +
            $"Create an {HarnessEndpointSettings.ResourcePath} asset under a Resources " +
            "folder (Assets > Create > Echo > Harness Endpoint Settings) and set its " +
            $"host, or set {RemoteServerEndpoint.HostVariable} before the editor starts " +
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
        /// The only test in the repository that can fail because the WIRING is wrong
        /// rather than because the protocol is. Every other case in this fixture
        /// constructs its transport, session, clock and scheduler by hand, so all of
        /// them would keep passing with the composition root emptied out; this one
        /// resolves the session from <c>HarnessComposition</c> and then makes it talk
        /// to the real server.
        ///
        /// <para>Measured rather than claimed: with
        /// <c>builder.Register&lt;ProtocolSession&gt;(...).As&lt;IProtocolSession&gt;()</c>
        /// deleted from <c>Configure</c>, this test failed at <c>Resolve</c> and the
        /// three tests above it passed unchanged.</para>
        ///
        /// <para>What it does NOT resolve is the entry point. <c>HarnessLifetimeScope</c>
        /// registers <c>HarnessSessionDriver</c> on top of <c>Configure</c>, and that
        /// half is a <c>LifetimeScope</c>'s and belongs to the PlayMode driver tests.
        /// This is the registration half, exercised against a live socket.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator TheComposedGraphConnectsAndProbes()
        {
            var endpoint = RequireEndpoint();
            return UniTask.ToCoroutine(async () =>
            {
                var builder = new ContainerBuilder();
                HarnessComposition.Configure(
                    builder,
                    EndpointResolution.From(endpoint.Host, endpoint.Port, "the end-to-end tier"));

                using var container = builder.Build();
                var session = container.Resolve<IProtocolSession>();

                await session.StartAsync(CancellationToken.None);
                Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                    "The graph the bootstrap scene builds reached the real server.");

                var latency = await session.ProbeRoundTripAsync(CancellationToken.None);
                Assert.That(latency, Is.GreaterThan(TimeSpan.Zero),
                    "A resolved session that connects but cannot round-trip would " +
                    "mean the graph is wired to something other than the endpoint " +
                    "it was configured with.");

                await session.StopAsync(CancellationToken.None);
                Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            });
        }

        /// <summary>
        /// The queue's refusal path, over a real socket. It needs no second client
        /// and no match: a session that has not logged in is refused by name, and
        /// that string is the contract this asserts.
        ///
        /// <para>Worth having on its own because it is the one queue answer a
        /// single connection can provoke deterministically. It also pins the
        /// direction of <c>QueueUseCase</c>'s conversion: the server says
        /// <c>success:false</c> with a reason, and a client that mapped that to
        /// anything but <c>Rejected</c> would show the player a network error for
        /// what is really "log in first".</para>
        /// </summary>
        [UnityTest]
        public IEnumerator JoiningTheQueueWithoutLoggingInIsRefusedByTheServer()
        {
            var endpoint = RequireEndpoint();
            return UniTask.ToCoroutine(async () =>
            {
                var (transport, session) = await StartSessionAsync(endpoint);
                using (transport)
                using (session)
                {
                    var useCase = new QueueUseCase(session);

                    var outcome = await useCase.JoinAsync(
                        "never-logged-in", CancellationToken.None);

                    Assert.That(outcome.Result, Is.EqualTo(QueueResult.Rejected),
                        "The server refuses a queue request from a session it has no " +
                        "player for; anything else means our reading of JoinQueueResp " +
                        "disagrees with the server's.");
                    Assert.That(outcome.Message, Does.Contain("not logged in"),
                        "matchmaking.go handleJoinQueue writes this reason verbatim.");
                }
            });
        }

        /// <summary>
        /// 2003 LeaveQueueRequest carries no body and gets no reply, so nothing
        /// about it is directly observable. What this proves is the half that can
        /// fail invisibly: the server accepts a frame whose payload length is zero
        /// and the link stays usable afterwards.
        ///
        /// <para>That is not a formality. <c>LeaveQueueRequest</c> is payload-shape
        /// "none", so the client writes a 4-byte length of 0 followed by the message
        /// id and no body. A client that instead sent <c>{}</c>, or a server that
        /// mis-framed an empty payload, would desynchronize the stream - and because
        /// there is no reply to wait on, the damage would only surface on some later
        /// unrelated message. The round trip afterwards is what catches it.</para>
        ///
        /// <para><b>The watcher below is not decoration, and this test failed
        /// without it.</b> Joining a queue on a shared server can be answered by a
        /// match at any moment - another client only has to be waiting - and a
        /// MatchFoundEvent with no subscriber publishes a <c>NoDestination</c>
        /// fault. Measured: the first run of this test was matched by a waiting
        /// player and failed on its own empty-faults assertion. That is the same
        /// rule <c>MatchFoundWatcherEntryPoint</c> enforces in production - a client
        /// that queues must have a 2004 subscriber - and this test was breaking it.
        /// Being matched is therefore tolerated rather than prevented: it changes
        /// nothing about the framing property being measured, and a test that
        /// demanded not to be matched would be asserting something the protocol
        /// does not promise.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator LeavingTheQueueIsAcceptedAndLeavesTheLinkUsable()
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

                    // Before the join, for the reason in the summary and the same
                    // reason production builds one before login.
                    using var watcher = new MatchFoundWatcher(session);

                    var player = await LogInAsync(session, "unity-harness-e2e-leave");
                    var useCase = new QueueUseCase(session);
                    await useCase.JoinAsync(player, CancellationToken.None);

                    await useCase.LeaveAsync(CancellationToken.None);

                    // The assertion the test is named for. A desynchronized stream
                    // fails here rather than at the send, because the send is
                    // fire-and-forget and the server never answers it.
                    var latency = await session.ProbeRoundTripAsync(CancellationToken.None);
                    Assert.That(latency, Is.GreaterThan(TimeSpan.Zero),
                        "A bodyless LeaveQueueRequest must leave the frame stream intact.");
                    Assert.That(session.State, Is.EqualTo(SessionState.Connected));
                    Assert.That(faults, Is.Empty,
                        "Faults: " + string.Join("; ", faults.ConvertAll(f => f.Diagnostic)));
                }
            });
        }

        /// <summary>
        /// The whole slice, end to end: two real connections queue and the server
        /// pairs them with each other.
        ///
        /// <para>Two clients rather than one, because a match is the one thing a
        /// single connection cannot provoke - the server's <c>tryMatch</c> needs two
        /// waiting players (matchmaking.go). This is therefore the only test in the
        /// repository that can disagree with our reading of 2004 MatchFoundEvent;
        /// every other match in the suite is one we published into a fake
        /// ourselves.</para>
        ///
        /// <para><b>It also proves the subscription timing that
        /// <c>MatchFoundWatcher</c> exists for.</b> Both watchers are constructed
        /// before either client logs in, which is the ordering the reconnect path
        /// forces; a watcher built after the join would be racing the server's
        /// pairing, and on a fast link would lose.</para>
        ///
        /// <para><b>Assumption, stated because it is a real flakiness risk:</b> the
        /// server is shared and its queue is global, so a third player queueing at
        /// the same moment could be paired with one of these two instead. The
        /// cross-assertions below fail loudly if that happens rather than passing
        /// vacuously. It has not been observed on this project's development
        /// server - but a green run here is evidence about a quiet server as much
        /// as about the protocol.</para>
        /// </summary>
        [UnityTest]
        [Timeout(60000)]
        public IEnumerator TwoQueueingClientsAreMatchedWithEachOther()
        {
            var endpoint = RequireEndpoint();
            return UniTask.ToCoroutine(async () =>
            {
                var (transportA, sessionA) = await StartSessionAsync(endpoint);
                using (transportA)
                using (sessionA)
                {
                    var (transportB, sessionB) = await StartSessionAsync(endpoint);
                    using (transportB)
                    using (sessionB)
                    {
                        // Before either login, deliberately. See the summary.
                        using var watcherA = new MatchFoundWatcher(sessionA);
                        using var watcherB = new MatchFoundWatcher(sessionB);

                        const string nameA = "unity-harness-e2e-a";
                        const string nameB = "unity-harness-e2e-b";
                        var playerA = await LogInAsync(sessionA, nameA);
                        var playerB = await LogInAsync(sessionB, nameB);

                        var queueA = new QueueUseCase(sessionA);
                        var queueB = new QueueUseCase(sessionB);

                        var joinedA = await queueA.JoinAsync(playerA, CancellationToken.None);
                        Assert.That(joinedA.Result, Is.EqualTo(QueueResult.Joined),
                            joinedA.Message);

                        var joinedB = await queueB.JoinAsync(playerB, CancellationToken.None);
                        Assert.That(joinedB.Result, Is.EqualTo(QueueResult.Joined),
                            joinedB.Message);

                        await WaitUntilAsync(
                            () => watcherA.Latest.HasValue && watcherB.Latest.HasValue,
                            "Both clients queued, so the server's tryMatch had two waiting " +
                            "players and must have paired them. A MatchFoundEvent did not " +
                            "arrive on both connections within the deadline.");

                        var matchA = watcherA.Latest.Value;
                        var matchB = watcherB.Latest.Value;

                        Assert.That(matchA.GameId, Is.Not.Null.And.Not.Empty);
                        Assert.That(matchB.GameId, Is.EqualTo(matchA.GameId),
                            "Both clients must be told about the SAME room. A mismatch " +
                            "means each was paired with someone else - see this test's " +
                            "shared-server assumption.");

                        // The seats are randomised per pairing (matchmaking.go
                        // tryMatch), so which client gets which is not assertable -
                        // only that they differ and cover both.
                        Assert.That(new[] { matchA.Seat, matchB.Seat },
                            Is.EquivalentTo(new[] { 0, 1 }),
                            "One client acts first and the other second; two clients " +
                            "sharing a seat would be a server-side pairing bug.");

                        Assert.That(matchA.OpponentName, Is.EqualTo(nameB));
                        Assert.That(matchB.OpponentName, Is.EqualTo(nameA),
                            "Each client is told the OTHER player's name. Seeing its own " +
                            "would mean the server echoed the recipient back.");
                    }
                }
            });
        }

        /// <summary>
        /// Logs in and returns the server-issued player id, failing the test rather
        /// than returning a useless value when the server refuses.
        /// </summary>
        private static async UniTask<string> LogInAsync(ProtocolSession session, string playerName)
        {
            var response = await session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = playerName },
                Patience,
                CancellationToken.None);

            Assert.That(response.Success, Is.True, response.Error);
            Assert.That(response.PlayerId, Is.Not.Null.And.Not.Empty);
            return response.PlayerId;
        }

        /// <summary>
        /// Polls until the condition holds or <see cref="Patience"/> runs out.
        /// Polling rather than awaiting an event, because the thing being waited for
        /// is a server push with no completion source on this side; the interval is
        /// short enough to add no meaningful latency to a fast pairing.
        /// </summary>
        private static async UniTask WaitUntilAsync(Func<bool> condition, string message)
        {
            var deadline = DateTimeOffset.UtcNow + Patience;
            while (!condition())
            {
                if (DateTimeOffset.UtcNow >= deadline)
                {
                    Assert.Fail($"{message} (waited {Patience})");
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(50), DelayType.Realtime);
            }
        }

        /// <summary>
        /// Resolved before the coroutine is built rather than inside it. Assert.Ignore
        /// throws, and throwing from the test method itself is the shape the Unity
        /// runner handles cleanly; thrown from inside UniTask.ToCoroutine it would
        /// have to survive being captured into a task and rethrown from MoveNext.
        /// </summary>
        /// <para>Resolved through <see cref="HarnessEndpointSettings.ResolveFromResources"/>
        /// rather than <c>RemoteServerEndpoint.TryResolve</c>, which reads the
        /// environment and nothing else. The two doors had drifted: the bootstrap
        /// scene accepted an asset or a variable while this tier accepted only a
        /// variable, and the asset exists precisely so an endpoint can be set
        /// without restarting the editor. That is not a convenience here. An editor
        /// inherits its environment block from whatever launched it - normally Unity
        /// Hub, itself long-running - so a variable set after the Hub started reaches
        /// neither, and this tier stays skipped however many times the editor is
        /// restarted. That was measured rather than supposed.</para>
        private static EndpointResolution RequireEndpoint()
        {
            var endpoint = HarnessEndpointSettings.ResolveFromResources();
            if (!endpoint.IsConfigured)
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
            StartSessionAsync(EndpointResolution endpoint)
        {
            var transport = new CountingTransport(new TcpTransport(
                new TcpTransportOptions { Host = endpoint.Host, Port = endpoint.Port },
                new StopwatchElapsedTime()));
            ProtocolSession session = null;
            try
            {
                // RecordingSessionScheduler, because these are EditMode tests with
                // no player loop to switch to. That is the reason the scheduler port
                // exists at all, and MainThreadSessionScheduler is PlayMode's.
                // Two implementations, not one object passed twice: the wall clock
                // stamps the ping's wire ts and the stopwatch measures the round
                // trip, and a wall clock cannot serve as an elapsed-time source.
                session = new ProtocolSession(
                    transport,
                    new SystemClock(),
                    new StopwatchElapsedTime(),
                    new RecordingSessionScheduler());
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
