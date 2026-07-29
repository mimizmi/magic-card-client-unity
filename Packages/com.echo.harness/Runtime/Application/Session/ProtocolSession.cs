using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    public sealed class ProtocolSession : IProtocolSession
    {
        public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

        private readonly ITransport transport;
        private readonly IClock clock;
        private readonly List<Action<SessionFault>> faultHandlers = new List<Action<SessionFault>>();

        private CancellationTokenSource pumpCancellation;
        private bool disposed;

        public ProtocolSession(ITransport transport, IClock clock)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public SessionState State { get; private set; } = SessionState.Disconnected;

        public async UniTask StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (State != SessionState.Disconnected)
            {
                throw new InvalidOperationException(
                    $"A session can only be started from Disconnected; it is {State}.");
            }

            State = SessionState.Connecting;
            try
            {
                await transport.ConnectAsync(cancellationToken);
            }
            catch
            {
                State = SessionState.Disconnected;
                throw;
            }

            State = SessionState.Connected;
            pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            RunPumpAsync(pumpCancellation.Token).Forget();
        }

        public async UniTask StopAsync(CancellationToken cancellationToken)
        {
            if (State == SessionState.Disconnected)
            {
                return;
            }

            CancelPump();
            await transport.DisconnectAsync(cancellationToken);
            State = SessionState.Disconnected;
        }

        public UniTask SendAsync(
            MessageId messageId,
            object payload,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler) =>
            throw new NotImplementedException();

        public IDisposable SubscribeToFaults(Action<SessionFault> handler)
        {
            ThrowIfDisposed();
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            faultHandlers.Add(handler);
            return new Subscription(() => faultHandlers.Remove(handler));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CancelPump();
            faultHandlers.Clear();
            State = SessionState.Disconnected;
        }

        private async UniTaskVoid RunPumpAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TransportMessage message;
                try
                {
                    message = await transport.ReceiveAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    await FaultTheStreamAsync(exception);
                    return;
                }

                Dispatch(message);
            }
        }

        private void Dispatch(TransportMessage message)
        {
            var result = ProtocolCodec.Decode(message.MessageId, message.Payload);
            if (!result.Succeeded)
            {
                PublishFault(new SessionFault(
                    result.Failure == ProtocolDecodeFailure.UnknownMessageId
                        ? SessionFaultKind.UnknownMessageId
                        : SessionFaultKind.MalformedPayload,
                    message.MessageId,
                    result.Diagnostic));
            }
        }

        /// <summary>
        /// A receive failure means the byte stream has lost its frame boundaries,
        /// so every later read returns garbage. Disconnecting here is what makes
        /// the problem diagnosable instead of silently endless.
        /// </summary>
        private async UniTask FaultTheStreamAsync(Exception exception)
        {
            State = SessionState.Faulted;

            // The pump returns as soon as this method does, so the token it is
            // running under has no further use. Releasing it here matters
            // because it is linked to the token the caller passed to StartAsync,
            // which outlives the session; leaving it registered would pin this
            // session on an application-lifetime token until disposal.
            CancelPump();

            try
            {
                await transport.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception disconnectFailure)
            {
                PublishFault(new SessionFault(
                    SessionFaultKind.TransportFailure,
                    default,
                    disconnectFailure.Message));
            }

            PublishFault(new SessionFault(
                SessionFaultKind.TransportFailure, default, exception.Message));
        }

        private void PublishFault(SessionFault fault)
        {
            foreach (var handler in faultHandlers.ToArray())
            {
                try
                {
                    handler(fault);
                }
                catch
                {
                    // A fault handler that throws must not stop the others from
                    // being told, and there is nowhere left to report it.
                }
            }
        }

        private void CancelPump()
        {
            if (pumpCancellation == null)
            {
                return;
            }

            pumpCancellation.Cancel();
            pumpCancellation.Dispose();
            pumpCancellation = null;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ProtocolSession));
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Action unsubscribe;

            public Subscription(Action unsubscribe)
            {
                this.unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                var action = unsubscribe;
                unsubscribe = null;
                action?.Invoke();
            }
        }
    }
}
