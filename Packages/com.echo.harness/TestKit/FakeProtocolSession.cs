using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// A session that answers synchronously, so EditMode tests may use the
    /// <c>[Test]</c> + <c>GetAwaiter().GetResult()</c> pattern the rest of the
    /// suite uses. Nothing here yields.
    ///
    /// <para><see cref="PublishFault"/> reproduces
    /// <c>ProtocolSession.PublishFault</c> exactly - synchronous, on the caller's
    /// thread, swallowing handler exceptions - because those three properties are
    /// what <c>SessionFaultRouter</c> is designed around, and a double that
    /// dispatched asynchronously would let the router's tests pin a contract
    /// production does not have.</para>
    /// </summary>
    public sealed class FakeProtocolSession : IProtocolSession
    {
        private readonly List<Action<SessionFault>> faultHandlers =
            new List<Action<SessionFault>>();

        public SessionState State { get; set; } = SessionState.Disconnected;

        /// <summary>What the next RequestAsync returns. Cast to TResponse.</summary>
        public object NextResponse { get; set; }

        /// <summary>One-shot. Cleared by the request that consumes it.</summary>
        public Exception NextRequestFailure { get; set; }

        public int RequestCount { get; private set; }

        public MessageId LastRequestId { get; private set; }

        public object LastRequestPayload { get; private set; }

        public bool Disposed { get; private set; }

        public UniTask StartAsync(CancellationToken cancellationToken) => UniTask.CompletedTask;

        public UniTask StopAsync(CancellationToken cancellationToken) => UniTask.CompletedTask;

        public UniTask SendAsync(MessageId messageId, object payload, CancellationToken cancellationToken) =>
            UniTask.CompletedTask;

        public UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestId = requestId;
            LastRequestPayload = payload;

            if (cancellationToken.IsCancellationRequested)
            {
                return UniTask.FromCanceled<TResponse>(cancellationToken);
            }

            if (NextRequestFailure != null)
            {
                var failure = NextRequestFailure;
                NextRequestFailure = null;
                return UniTask.FromException<TResponse>(failure);
            }

            return UniTask.FromResult((TResponse)NextResponse);
        }

        public UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken) =>
            UniTask.FromResult(TimeSpan.Zero);

        public IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler) =>
            new Unsubscribe(() => { });

        public IDisposable SubscribeToFaults(Action<SessionFault> handler)
        {
            faultHandlers.Add(handler);
            return new Unsubscribe(() => faultHandlers.Remove(handler));
        }

        public void PublishFault(SessionFault fault)
        {
            foreach (var handler in faultHandlers.ToArray())
            {
                try
                {
                    handler(fault);
                }
                catch
                {
                    // Matches ProtocolSession.PublishFault:978-982 exactly.
                }
            }
        }

        public void Dispose() => Disposed = true;

        private sealed class Unsubscribe : IDisposable
        {
            private readonly Action action;

            public Unsubscribe(Action action) => this.action = action;

            public void Dispose() => action();
        }
    }
}
