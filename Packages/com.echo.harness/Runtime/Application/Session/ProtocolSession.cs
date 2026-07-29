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
        private readonly Dictionary<MessageId, List<Action<object>>> subscribers =
            new Dictionary<MessageId, List<Action<object>>>();
        private readonly Dictionary<MessageId, UniTaskCompletionSource<object>> pendingRequests =
            new Dictionary<MessageId, UniTaskCompletionSource<object>>();

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

            // Waiters are failed last, after the state transition, and the order
            // is deliberate. TrySetException resumes each waiter inline on this
            // stack, so a waiter is free to re-enter the session before StopAsync
            // returns. Reaching Disconnected first means such a call is refused
            // with the truth; failing them earlier would let a re-entrant request
            // pass the Connected check and then park forever on a cancelled pump.
            FailPendingRequests(new InvalidOperationException(
                "The session was stopped before the response arrived. The request " +
                "may still have reached the server; stopping does not cancel it."));
        }

        public UniTask SendAsync(
            MessageId messageId,
            object payload,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (State != SessionState.Connected)
            {
                throw new InvalidOperationException(
                    $"A message can only be sent from a Connected session; it is {State}.");
            }

            var hasContract = ProtocolMessageMap.PayloadTypes.TryGetValue(
                messageId, out var expectedType);
            if (payload == null)
            {
                if (hasContract)
                {
                    throw new ArgumentException(
                        $"{messageId} requires a {expectedType.Name} payload.", nameof(payload));
                }
            }
            else if (!hasContract)
            {
                throw new ArgumentException(
                    $"{messageId} carries no payload.", nameof(payload));
            }
            else if (payload.GetType() != expectedType)
            {
                throw new ArgumentException(
                    $"{messageId} expects {expectedType.Name}, not {payload.GetType().Name}.",
                    nameof(payload));
            }

            return transport.SendAsync(
                new TransportMessage(messageId, ProtocolCodec.EncodePayload(payload)),
                cancellationToken);
        }

        public async UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!ProtocolMessageMap.ResponseFor.TryGetValue(requestId, out var responseId))
            {
                // Deliberately not "one-way". ResponseFor holds only the pairs
                // whose answer is itself of kind "response"; GameConfigRequest is
                // answered by GameConfigEvent, which is of kind "event" and so is
                // absent from the table without the request being one-way. A
                // caller told the server stays silent stops looking, when what it
                // actually needed was a subscription.
                throw new ArgumentException(
                    $"{requestId} has no paired response message, so there is nothing " +
                    "for a request to await. Send it with SendAsync; if the server " +
                    "answers, it answers with an event, which reaches Subscribe handlers.",
                    nameof(requestId));
            }

            var expectedType = ProtocolMessageMap.PayloadTypes[responseId];
            if (expectedType != typeof(TResponse))
            {
                throw new ArgumentException(
                    $"{responseId} carries {expectedType.Name}, not {typeof(TResponse).Name}.",
                    nameof(TResponse));
            }

            // Validated here rather than at CancelAfter below, which runs after
            // the send: an out-of-range timeout would otherwise be reported as an
            // argument error once the request was already on the wire, and the
            // caller would conclude nothing had happened. Infinite is rejected
            // outright because it installs no timer at all, which would make the
            // waiter genuinely unbounded rather than merely patient.
            if (timeout <= TimeSpan.Zero || timeout.TotalMilliseconds > int.MaxValue)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(timeout),
                    timeout,
                    "A request timeout must be positive and no longer than " +
                    "int.MaxValue milliseconds; an infinite one is not accepted.");
            }

            if (pendingRequests.ContainsKey(responseId))
            {
                throw new InvalidOperationException(
                    $"A request awaiting {responseId} is already in flight. The protocol has " +
                    "no correlation id, so a second one could be answered with the first reply.");
            }

            var completion = new UniTaskCompletionSource<object>();
            pendingRequests[responseId] = completion;
            try
            {
                await SendAsync(requestId, payload, cancellationToken);

                using var timeoutCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCancellation.CancelAfter(timeout);
                try
                {
                    var response = await completion.Task
                        .AttachExternalCancellation(timeoutCancellation.Token);
                    return (TResponse)response;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"{requestId} received no {responseId} within {timeout}.");
                }
            }
            finally
            {
                // By identity, never by key. Once a real transport can park a
                // send, this frame can still be inside SendAsync when a late
                // response completes and clears its entry; another caller then
                // sees an open gate and registers its own completion under the
                // same id. Removing by key alone would delete that caller's
                // entry, and its genuine response would find nothing waiting.
                if (pendingRequests.TryGetValue(responseId, out var registered) &&
                    ReferenceEquals(registered, completion))
                {
                    pendingRequests.Remove(responseId);
                }
            }
        }

        public UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler)
        {
            ThrowIfDisposed();
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!ProtocolMessageMap.PayloadTypes.TryGetValue(messageId, out var expectedType))
            {
                throw new ArgumentException(
                    $"{messageId} carries no payload and cannot be subscribed to.",
                    nameof(messageId));
            }

            if (expectedType != typeof(TPayload))
            {
                throw new ArgumentException(
                    $"{messageId} carries {expectedType.Name}, not {typeof(TPayload).Name}.",
                    nameof(TPayload));
            }

            if (!subscribers.TryGetValue(messageId, out var handlers))
            {
                handlers = new List<Action<object>>();
                subscribers[messageId] = handlers;
            }

            Action<object> boxed = payload => handler((TPayload)payload);
            handlers.Add(boxed);
            return new Subscription(() => handlers.Remove(boxed));
        }

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
            subscribers.Clear();
            State = SessionState.Disconnected;

            // Failing them, not merely dropping them. Clearing the dictionary
            // discards the completion sources without completing them, which
            // leaves every waiter with nothing that can ever tell it anything;
            // it would then wait out its full timeout and report a network
            // failure that never happened. Disposal is synchronous, but so is
            // TrySetException, so there is nothing here that needs awaiting.
            FailPendingRequests(new ObjectDisposedException(
                nameof(ProtocolSession),
                "The session was disposed before the response arrived."));
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
                return;
            }

            if (pendingRequests.TryGetValue(result.MessageId, out var completion))
            {
                // No removal here: the entry has exactly one owner, the
                // RequestAsync frame that created it, and that frame's finally
                // runs inline from this very call. Removing it here as well
                // would open the gate for a request that is still in flight
                // whenever the frame is parked in its send rather than at its
                // await, which is what a real socket makes reachable.
                //
                // The safety of this line is borrowed rather than local, and
                // worth knowing before anyone changes the timeout mechanism.
                // RequestAsync awaits through AttachExternalCancellation, whose
                // runner body is
                //     try { core.TrySetResult(await task); }
                //     catch (Exception ex) { core.TrySetException(ex); }
                // so every frame this call resumes - including the requester's
                // own continuation - runs inside that try. Nothing thrown there
                // can reach this line, which sits outside the pump's try. Drop
                // AttachExternalCancellation and that guarantee leaves with it:
                // a throwing continuation would then kill the pump with the
                // connection still open.
                completion.TrySetResult(result.Payload);
                return;
            }

            DeliverToSubscribers(result);
        }

        /// <summary>
        /// Each handler gets its own try, and deliberately so. Dispatch runs on
        /// the pump's stack outside its try block, so an escaping subscriber
        /// exception would surface as an unobserved task exception and kill the
        /// pump with the connection still open. Catching per handler also keeps
        /// one broken subscriber from silencing the ones queued behind it.
        /// </summary>
        private void DeliverToSubscribers(ProtocolDecodeResult result)
        {
            if (!subscribers.TryGetValue(result.MessageId, out var handlers))
            {
                return;
            }

            foreach (var handler in handlers.ToArray())
            {
                try
                {
                    handler(result.Payload);
                }
                catch (Exception exception)
                {
                    PublishFault(new SessionFault(
                        SessionFaultKind.SubscriberFailure,
                        result.MessageId,
                        exception.Message));
                }
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

            // Waiters are failed before anything else: nothing will ever answer
            // them once the pump stops, so leaving them pending would hang the
            // caller until its timeout with no explanation of why. They get the
            // receive failure itself, which is the root cause.
            FailPendingRequests(exception);

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

        /// <summary>
        /// Completes every waiting request with <paramref name="failure"/> and
        /// clears the gate. All three paths that stop the pump call this - a
        /// stream fault, StopAsync, and Dispose - because a waiter the pump can
        /// no longer answer would otherwise sit until its own timeout and then
        /// report a network failure that never happened.
        ///
        /// The failure must never be an OperationCanceledException: RequestAsync
        /// translates one of those into a TimeoutException, which is precisely
        /// the misleading report this exists to remove.
        ///
        /// Both the copy and the order are load-bearing. TrySetException resumes
        /// each waiter inline, and the resumed frame removes its own key from
        /// this dictionary before the loop advances, so iterating the dictionary
        /// itself would throw from the enumerator and strand every waiter behind
        /// the first. Emptying it up front rather than afterwards means a resumed
        /// waiter that registers a fresh request keeps it, instead of having it
        /// silently discarded by a trailing clear.
        /// </summary>
        private void FailPendingRequests(Exception failure)
        {
            var waiters = new List<KeyValuePair<MessageId, UniTaskCompletionSource<object>>>(
                pendingRequests);
            pendingRequests.Clear();

            foreach (var pair in waiters)
            {
                pair.Value.TrySetException(failure);
            }
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
