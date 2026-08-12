using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    public enum TransportState
    {
        Disconnected,
        Connecting,
        Connected
    }

    public readonly struct TransportMessage
    {
        public TransportMessage(MessageId messageId, byte[] payload)
        {
            MessageId = messageId;
            Payload = payload ?? throw new ArgumentNullException(nameof(payload));
        }

        public MessageId MessageId { get; }

        public byte[] Payload { get; }
    }

    public interface ITransport
    {
        TransportState State { get; }

        UniTask ConnectAsync(CancellationToken cancellationToken);

        /// <summary>
        /// Implementations must be safe against a concurrent call from a session's
        /// receive pump. A session answers an inbound heartbeat itself, and that reply
        /// is fire-and-forget, so over a real socket it parks mid-write while a
        /// caller's own send is still in progress. A transport that writes the 4-byte
        /// length prefix and the body as two separate writes interleaves them, the
        /// server reads a corrupt frame, and the byte stream loses its frame
        /// boundaries - the desynchronization a session grades as fatal. Serialize
        /// writes; do not assume one caller at a time.
        /// </summary>
        UniTask SendAsync(TransportMessage message, CancellationToken cancellationToken);

        /// <summary>
        /// <b>Cancelling a receive means abandoning the link.</b> Implementations
        /// close the connection on any cancellation of this token, because closing
        /// the socket is the only way this runtime can unpark a blocked read. That
        /// is the contract rather than an implementation accident: a caller must
        /// not cancel a receive to pause reading, to apply backpressure, or to
        /// impose a per-message deadline, because all three destroy the transport
        /// as a side effect.
        ///
        /// <para>ProtocolSession honours this today, and the reason is worth
        /// stating precisely, because it is not one choke point. The token it
        /// passes comes from a source linked to the one handed to StartAsync, so a
        /// receive is cancelled either by CancelPump or by the caller cancelling
        /// the whole session - and both of those are teardown. What there is no
        /// path for is cancelling a receive while meaning to keep using the link.
        /// That is correct partly by luck: the session has so far had no reason to
        /// want one. The constraint is written here rather than left to be
        /// rediscovered by whoever first has that reason.</para>
        /// </summary>
        UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken);

        UniTask DisconnectAsync(CancellationToken cancellationToken);
    }

    public interface IContentProvider
    {
        UniTask<T> LoadAsync<T>(string address, CancellationToken cancellationToken);

        void Release(string address);
    }

    public enum LuaRuntimeState
    {
        Stopped,
        Starting,
        Running,
        Stopping
    }

    public interface ILuaRuntime
    {
        LuaRuntimeState State { get; }

        UniTask InitializeAsync(CancellationToken cancellationToken);

        UniTask ExecuteAsync(
            string module,
            string entryPoint,
            CancellationToken cancellationToken);

        UniTask ShutdownAsync(CancellationToken cancellationToken);
    }

    /// <summary>
    /// Wall-clock time, for stamping a moment that leaves the process - a wire
    /// timestamp the server echoes, a log line, a display.
    ///
    /// <para><b>Do not measure an interval with this.</b> Wall time can step in
    /// either direction under a clock synchronisation or a manual change, so a
    /// difference between two reads is not a duration. <see cref="IElapsedTime"/>
    /// is the port for that.</para>
    ///
    /// <para>Nothing here does any more, and the two sites that used to are worth
    /// keeping on the page, because what each of them would have cost is the
    /// argument against ever routing a duration back through this port.
    /// SendBudget.TryConsume refills only once the measured interval reaches a
    /// whole refill interval, and a negative interval never reaches one: on a
    /// backwards step the drained bucket would wedge at zero and refuse every
    /// send until the wall clock passed where it had already been. The round-trip
    /// probe returned <c>clock.UtcNow - sentAt</c>, and the end-to-end tier
    /// asserts that latency is greater than zero, so a step landing inside a probe
    /// would fail that assertion with a negative number and nothing to say where
    /// it came from. Both now measure with <see cref="IElapsedTime"/>, which
    /// cannot produce either failure. The one consumer left is that same probe,
    /// and it reads this port for one thing only: stamping the ts the server
    /// echoes back.</para>
    ///
    /// <para>Splitting the port is also what put a wall clock within reach of a
    /// player build, which is why the rule above is a rule rather than a note.
    /// Before the split every wall clock lived in TestKit, behind
    /// defineConstraints <c>["UNITY_INCLUDE_TESTS"]</c>, and could not ship; a
    /// DateTimeOffset.UtcNow-backed <c>SystemClock</c> now sits in Infrastructure
    /// under no define constraint at all. The reach is permanent and the misuse is
    /// not: what keeps it harmless is that a consumer needing a duration asks
    /// <see cref="IElapsedTime"/>, which cannot answer what time it is.</para>
    /// </summary>
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    /// <summary>
    /// Monotonic elapsed time, and only that. It cannot answer "what time is it",
    /// which is the point: a consumer that only needs a duration should be unable
    /// to reach a wall clock by accident.
    ///
    /// <para><see cref="GetTimestamp"/> returns an opaque counter whose unit is an
    /// implementation detail; only <see cref="GetElapsedTime"/> may interpret it.
    /// Implementations must never report a negative interval for a timestamp taken
    /// earlier.</para>
    /// </summary>
    public interface IElapsedTime
    {
        long GetTimestamp();

        TimeSpan GetElapsedTime(long startingTimestamp);
    }

    /// <summary>
    /// Moves the current continuation onto the session's context. A session's
    /// receive pump resumes on whatever thread the transport's I/O completed on,
    /// and its request timeouts resume on a timer thread; both are hopped through
    /// here so that everything touching session state runs on one context and the
    /// session itself needs no lock.
    ///
    /// The production implementation switches to the Unity main thread. That is
    /// not on its own why this is a port. Application is compiled with
    /// noEngineReferences and cannot name a Unity type, but the direct call names
    /// no Unity type either: UniTask's main-thread awaitable is entirely Cysharp,
    /// which this assembly already references, and a probe calling it from here
    /// was measured to compile and to leave the architecture gate green. The port
    /// exists because a test implementation completes synchronously, which is what
    /// keeps EditMode tests independent of a player loop that EditMode does not
    /// run, and because a session should not hard-code which context it confines
    /// to.
    /// </summary>
    public interface ISessionScheduler
    {
        UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken);
    }
}
