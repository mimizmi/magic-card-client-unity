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

    public interface IClock
    {
        /// <summary>
        /// Must be usable for measuring an interval, not only for stamping a moment.
        /// A round-trip probe returns the difference between two reads of this
        /// property, so an implementation whose value can step backwards reports a
        /// negative latency, and one that can step forwards reports a 30 s round trip
        /// on a healthy link. The only implementation today is monotonic by
        /// construction, so no test can catch a replacement that is not; the
        /// requirement is stated here because this is the only place it can be.
        /// </summary>
        DateTimeOffset UtcNow { get; }
    }
}
