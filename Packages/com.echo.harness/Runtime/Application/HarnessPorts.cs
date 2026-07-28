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
        DateTimeOffset UtcNow { get; }
    }
}
