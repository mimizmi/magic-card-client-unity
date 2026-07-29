using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// Owns one receive pump over an <see cref="ITransport"/> and routes each
    /// decoded message to exactly one destination.
    /// </summary>
    public interface IProtocolSession : IDisposable
    {
        SessionState State { get; }

        UniTask StartAsync(CancellationToken cancellationToken);

        UniTask StopAsync(CancellationToken cancellationToken);

        UniTask SendAsync(MessageId messageId, object payload, CancellationToken cancellationToken);

        UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken);

        UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken);

        IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler);

        IDisposable SubscribeToFaults(Action<SessionFault> handler);
    }
}
