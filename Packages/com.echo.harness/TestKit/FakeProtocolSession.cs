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
        private readonly Dictionary<MessageId, List<Action<object>>> subscribers =
            new Dictionary<MessageId, List<Action<object>>>();

        public SessionState State { get; set; } = SessionState.Disconnected;

        /// <summary>
        /// Every message handed to <see cref="SendAsync"/>, in order. Sends are the
        /// only observable effect of a fire-and-forget message such as 2003
        /// LeaveQueueRequest, which the server answers with nothing at all - so
        /// without this, a use case that sent it and one that did nothing would be
        /// indistinguishable.
        /// </summary>
        public List<(MessageId MessageId, object Payload)> SentMessages { get; } =
            new List<(MessageId, object)>();

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

        public UniTask SendAsync(
            MessageId messageId,
            object payload,
            CancellationToken cancellationToken)
        {
            SentMessages.Add((messageId, payload));

            if (NextSendFailure != null)
            {
                var failure = NextSendFailure;
                NextSendFailure = null;
                return UniTask.FromException(failure);
            }

            return UniTask.CompletedTask;
        }

        /// <summary>One-shot. Cleared by the send that consumes it.</summary>
        public Exception NextSendFailure { get; set; }

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

        /// <summary>
        /// Records the handler and returns a live unsubscribe, where this used to
        /// return a handle that unhooked nothing from a list that was never kept.
        /// That no-op was harmless while nothing under test subscribed to anything;
        /// it stopped being harmless with <c>MatchFoundWatcher</c>, whose entire
        /// contract is about WHEN it subscribes and whether disposing it stops
        /// delivery - both unobservable against a fake that discards the handler.
        ///
        /// <para>The two argument checks mirror <c>ProtocolSession.Subscribe</c>
        /// rather than being defensive habit: a payload-shape "none" id and a
        /// mismatched TPayload are the two ways a real subscription throws, and a
        /// fake that accepted them would let a test pin a contract production does
        /// not have.</para>
        /// </summary>
        public IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler)
        {
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
            return new Unsubscribe(() => handlers.Remove(boxed));
        }

        /// <summary>
        /// Delivers a payload to whoever subscribed to <paramref name="messageId"/>,
        /// the way the receive pump would.
        ///
        /// <para>Synchronous and on the caller's thread, matching
        /// <c>ProtocolSession</c>'s dispatch after its single hop to the session
        /// context - which is what lets a subscriber touch UI state without a
        /// scheduler of its own. Unlike <see cref="PublishFault"/>, a handler
        /// exception is NOT swallowed: the session converts one into a
        /// DispatchFailure fault rather than ignoring it, so a fake that ate it
        /// would hide the very escape a subscriber is expected to prevent.</para>
        /// </summary>
        public void PublishToSubscribers(MessageId messageId, object payload)
        {
            if (!subscribers.TryGetValue(messageId, out var handlers))
            {
                return;
            }

            foreach (var handler in handlers.ToArray())
            {
                handler(payload);
            }
        }

        /// <summary>How many handlers are currently subscribed to that id.</summary>
        public int SubscriberCount(MessageId messageId) =>
            subscribers.TryGetValue(messageId, out var handlers) ? handlers.Count : 0;

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
