using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    public sealed class ProtocolSession : IProtocolSession
    {
        /// <summary>
        /// The deadline <see cref="ProbeRoundTripAsync"/> uses. It is not a default
        /// for <see cref="RequestAsync{TResponse}"/>, which has no overload that
        /// omits a timeout, and it is unreachable from <see cref="IProtocolSession"/>.
        /// </summary>
        public static readonly TimeSpan RoundTripProbeDeadline = TimeSpan.FromSeconds(10);

        /// <summary>
        /// How long a Dispose-initiated disconnect is given before it is abandoned.
        /// Short on purpose: Dispose has no caller waiting on it, and a close that
        /// has not completed by now will not start helping.
        /// </summary>
        public static readonly TimeSpan DisposeDisconnectDeadline = TimeSpan.FromSeconds(2);

        private readonly ITransport transport;
        private readonly IClock clock;
        private readonly IElapsedTime time;
        private readonly ISessionScheduler scheduler;
        private readonly List<Action<SessionFault>> faultHandlers = new List<Action<SessionFault>>();
        private readonly Dictionary<MessageId, List<Action<object>>> subscribers =
            new Dictionary<MessageId, List<Action<object>>>();
        private readonly Dictionary<MessageId, UniTaskCompletionSource<object>> pendingRequests =
            new Dictionary<MessageId, UniTaskCompletionSource<object>>();

        private CancellationTokenSource pumpCancellation;
        private bool disposed;

        public ProtocolSession(
            ITransport transport,
            IClock clock,
            IElapsedTime time,
            ISessionScheduler scheduler)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.time = time ?? throw new ArgumentNullException(nameof(time));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
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

            // Captured before the state changes. The fault path has already closed
            // the transport, and a second DisconnectAsync is idempotent on the fake
            // and on a well-behaved socket but not on every transport.
            var alreadyDisconnected = State == SessionState.Faulted;

            CancelPump();
            try
            {
                if (!alreadyDisconnected)
                {
                    await transport.DisconnectAsync(cancellationToken);
                }
            }
            finally
            {
                // In the finally, not after the await. A throwing disconnect - or
                // an already-cancelled token, which is a realistic shutdown
                // pattern - would otherwise strand every waiter and leave State
                // reading Connected over a dead pump.
                //
                // The state transition still precedes the failures, for the reason
                // it always did: TrySetException resumes each waiter inline on this
                // stack, so a waiter is free to re-enter the session before this
                // method returns, and reaching Disconnected first means such a call
                // is refused with the truth.
                State = SessionState.Disconnected;
                FailPendingRequests(new InvalidOperationException(
                    "The session was stopped before the response arrived. The request " +
                    "may still have reached the server; stopping does not cancel it."));
            }
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
                throw new RequestAlreadyInFlightException(
                    responseId,
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
                // Both failing exits hop, and only the exception each produces
                // differs. Hopping before the throw means hopping before this
                // frame's finally removes its gate entry; without it the resuming
                // thread and the pump can mutate pendingRequests concurrently, and
                // a Dictionary resized from two threads can misroute a response to
                // subscribers. The success path needs no hop: TrySetResult is
                // called from Dispatch, which already ran on the context.
                //
                // Two clauses rather than one widened filter, because the two exits
                // must stay distinguishable to a caller: a deadline that elapsed is
                // a TimeoutException, and a caller that abandoned its own request
                // is an OperationCanceledException. One clause could not produce
                // both without re-deriving which of the two had happened.
                //
                // The negated filter comes first on purpose, and that ordering is
                // what makes the pair exhaustive. Filters run sequentially against
                // the live token: if the first reads the token as not cancelled it
                // matches immediately, and if it reads it as cancelled the token
                // can never go back, so the second is certain to match. Written the
                // other way round, a cancellation landing between the two filters
                // would leave both false and let the raw exception escape past the
                // hop - the shape TcpTransport's read path warns about.
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    await SwitchToSessionContextForTeardownAsync();
                    throw new TimeoutException(
                        $"{requestId} received no {responseId} within {timeout}.");
                }
                catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
                {
                    // The exit that used to skip the hop entirely, leaving the
                    // finally below to mutate pendingRequests on whatever thread
                    // called Cancel. Nothing about a caller cancelling makes that
                    // Dictionary safer to touch off-context than a timeout does.
                    await SwitchToSessionContextForTeardownAsync();
                    throw;
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

        /// <summary>
        /// The hop a failing <see cref="RequestAsync{TResponse}"/> takes on its way
        /// out, so that the <c>finally</c> which un-registers its pending entry runs
        /// on the session's context rather than on the timer or cancelling thread it
        /// happened to resume on.
        ///
        /// <para><b>CancellationToken.None, deliberately.</b> The caller's token is
        /// already cancelled on one of the two paths that come here, and that is
        /// exactly when the hop matters most - handing it in would make the switch
        /// refuse to happen at the moment it is needed. It is not a hypothetical
        /// either way: RecordingSessionScheduler returns an already-cancelled
        /// UniTask without switching at all, so for that one the token decides
        /// whether a switch happens. MainThreadSessionScheduler is not the same
        /// case - off the main thread it queues on the player loop regardless of
        /// the token, and None only stops GetResult throwing once the continuation
        /// has already landed. So None is what makes the first switch at all and
        /// what makes the second's completed switch observable.</para>
        ///
        /// <para><b>A failing hop is swallowed, and that is a trade rather than an
        /// oversight.</b> The exception the caller must be told about is the timeout
        /// or the cancellation - the thing that actually ended its request - not a
        /// bookkeeping failure on the way out; letting the hop's exception win would
        /// replace a report the caller can act on with one it cannot. What is lost
        /// is real and is stated here because there is nowhere else to state it: when
        /// the hop fails, the <c>finally</c> runs off-context after all, unreported.
        /// There is a precedent for the alternative and it is worth naming rather
        /// than arguing past: FaultTheStreamAsync does publish from the very thread
        /// a failing pump hop could not leave, and a test pins it
        /// (AFailingHopFaultsTheStreamRatherThanDyingUnobserved). The reason not to
        /// copy it here is what the two paths owe their caller. The pump has nobody to return an exception
        /// to, so an off-context fault beats silence; this frame does have somebody,
        /// and that caller's report is worth more than a second one that would race
        /// faultHandlers from the thread the hop failed to leave.</para>
        ///
        /// <para><b>The failure a swallow cannot reach.</b> Everything above is
        /// about a hop that <i>throws</i>. A hop that never completes is outside it
        /// entirely: with MainThreadSessionScheduler, a Cancel() from a non-main
        /// thread and a stopped player loop, the await below never resumes.
        /// RequestAsync never returns, the finally never runs, and this request's
        /// entry stays in pendingRequests for good - so every later request for the
        /// same response id throws RequestAlreadyInFlightException, and neither
        /// Dispose nor CancelPump can free it. On the caller-cancel path that
        /// exposure is NEW: before this hop existed, a cancelled caller returned
        /// promptly and took no hop at all. The timeout path already had it, so
        /// CancellationToken.None makes nothing there worse. It is narrow today only
        /// because nothing in production constructs MainThreadSessionScheduler - see
        /// the open item in docs/migration-checklist.md - and it stops being narrow
        /// on the day something does.</para>
        /// </summary>
        private async UniTask SwitchToSessionContextForTeardownAsync()
        {
            try
            {
                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);
            }
            catch (Exception)
            {
                // See above: nothing here may outrank the failure being reported.
                // AFailingTeardownHopDoesNotReplaceTheTimeout and its cancellation
                // sibling are what hold this catch in place.
            }
        }

        public async UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken)
        {
            // Two ports, two jobs. The wall clock stamps the ts the server echoes
            // back, because that value leaves the process and must be a moment. The
            // returned figure is a duration and comes from the monotonic source, so
            // a clock synchronisation landing inside the probe can no longer make
            // this method report a negative latency.
            var sentAt = clock.UtcNow;
            var startedAt = time.GetTimestamp();
            var request = new ClientPingRequestDto { Ts = sentAt.ToUnixTimeMilliseconds() };

            var response = await RequestAsync<ClientPingResponseDto>(
                MessageId.ClientPingRequest, request, RoundTripProbeDeadline, cancellationToken);

            if (response.Ts != request.Ts)
            {
                var diagnostic =
                    $"ClientPingResponse echoed ts {response.Ts} for a request that sent " +
                    $"{request.Ts}.";
                PublishFault(new SessionFault(
                    SessionFaultKind.CorrelationMismatch,
                    MessageId.ClientPingResponse,
                    diagnostic));
                throw new CorrelationMismatchException(MessageId.ClientPingResponse, diagnostic);
            }

            return time.GetElapsedTime(startedAt);
        }

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

            // Whether there is a connection to close is decided before State is
            // reset. Disposing a session that was never started must not call
            // DisconnectAsync on a transport that was never connected.
            var closing = State != SessionState.Disconnected;

            faultHandlers.Clear();
            subscribers.Clear();
            State = SessionState.Disconnected;

            // Failing them, not merely dropping them. Clearing the dictionary
            // discards the completion sources without completing them, which
            // leaves every waiter with nothing that can ever tell it anything;
            // it would then wait out its full timeout and report a network
            // failure that never happened. Disposal is synchronous, but so is
            // TrySetException, so there is nothing here that needs awaiting.
            //
            // Before the disconnect is launched, not after: a transport whose
            // close releases a parked receive resumes the pump inline, and a
            // waiter resumed from there would otherwise see a half-cleared gate.
            FailPendingRequests(new ObjectDisposedException(
                nameof(ProtocolSession),
                "The session was disposed before the response arrived."));

            if (closing)
            {
                DisconnectOnDisposeAsync().Forget();
            }
        }

        /// <summary>
        /// The disconnect Dispose cannot await. Bounded fire-and-forget was chosen
        /// over a documented "stop before disposing" contract because leaving the
        /// socket open makes the server hold the session until its 35 second pong
        /// timeout, so a player who quits leaves a ghost behind.
        ///
        /// The try/catch is required rather than tidy: this runs with no caller on
        /// the stack, so an escaping exception would reach the unobserved-exception
        /// handler and be reported as an unrelated crash. There is nowhere to
        /// publish a fault either - Dispose has already cleared the handlers.
        /// </summary>
        private async UniTaskVoid DisconnectOnDisposeAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(DisposeDisconnectDeadline);
                await transport.DisconnectAsync(deadline.Token);
            }
            catch
            {
                // Nothing to tell, and no one left to tell it to.
            }
        }

        private async UniTaskVoid RunPumpAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TransportMessage message;
                try
                {
                    message = await transport.ReceiveAsync(cancellationToken);

                    // Everything below runs on the session's context. The hop is
                    // here, once, rather than at each call site inside Dispatch,
                    // so that "did this path hop?" has exactly one place to look.
                    await scheduler.SwitchToSessionContextAsync(cancellationToken);
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

                // Inside a try, unlike before. Each callee still guards itself, but
                // a future branch that forgets to now costs one message and a fault
                // instead of the pump, and State can no longer read Connected over
                // a pump that a Dispatch exception already killed.
                try
                {
                    Dispatch(message);
                }
                catch (Exception exception)
                {
                    // The type name, unlike every other fault kind here. The
                    // others name a failure the session already understands, so
                    // the message is the whole story. DispatchFailure is by
                    // definition the one nobody predicted, and
                    // "Object reference not set to an instance of an object."
                    // on its own says neither what threw nor where to look.
                    PublishFault(new SessionFault(
                        SessionFaultKind.DispatchFailure,
                        message.MessageId,
                        $"{exception.GetType().Name}: {exception.Message}"));
                }
            }
        }

        private void Dispatch(TransportMessage message)
        {
            var result = ProtocolCodec.Decode(message.MessageId, message.Payload);
            if (!result.Succeeded)
            {
                var kind = result.Failure == ProtocolDecodeFailure.UnknownMessageId
                    ? SessionFaultKind.UnknownMessageId
                    : SessionFaultKind.MalformedPayload;
                PublishFault(new SessionFault(kind, message.MessageId, result.Diagnostic));

                // The reply arrived; it just could not be read. Leaving the waiter
                // pending makes it stall its whole timeout and then report a
                // network failure that never happened. Failed after the fault is
                // published so a consumer sees the cause before the effect.
                if (pendingRequests.TryGetValue(message.MessageId, out var stalled))
                {
                    stalled.TrySetException(new InvalidOperationException(
                        $"{message.MessageId} arrived but could not be decoded: " +
                        result.Diagnostic));
                }

                return;
            }

            if (result.MessageId == MessageId.Ping)
            {
                // Answered here rather than by a subscriber: missing one Pong makes
                // the server treat the connection as dead, which is too important to
                // depend on someone remembering to subscribe.
                ReplyToHeartbeatAsync().Forget();
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
                // TrySetResult resumes the requester's continuation inline, on
                // this stack, so the requester's finally has run - and its gate
                // entry is gone - before this method returns. That is what the
                // paragraph above relies on.
                //
                // What this line does not get is cover from the pump's try, and
                // the difference matters to anyone changing the timeout
                // mechanism. A requester continuation that resumes from here and
                // throws is swallowed by UniTask before it can reach Dispatch's
                // caller. Today that happens in AttachExternalCancellation's
                // runner body,
                //     try { core.TrySetResult(await task); }
                //     catch (Exception ex) { core.TrySetException(ex); }
                // (UniTaskExtensions.cs:314-328), where TrySetException on an
                // already-completed core returns false and drops the exception
                // with no report at all (UniTaskCompletionSource.cs:150-173).
                // Dropping AttachExternalCancellation would not hand this line to
                // the pump's try either: TrySignalCompletion invokes the
                // continuation inside its own catch and routes a throw to
                // UniTaskScheduler.PublishUnobservedTaskException
                // (UniTaskCompletionSource.cs:910-917).
                //
                // So a broken requester continuation is contained but never
                // reported to this session - the pump survives, and no
                // SessionFault is published for it. That is a known diagnostic
                // hole rather than a guarantee. The pump's try is insurance for
                // future branches of this method; it is not what protects this
                // line.
                completion.TrySetResult(result.Payload);
                return;
            }

            DeliverToSubscribers(result);
        }

        /// <summary>
        /// A bare SendAsync(...).Forget() here would be wrong twice over. Dispatch
        /// now runs inside the pump's try, so an escaping exception costs one
        /// message rather than the pump; this guard is kept because it reports the
        /// failure against the right message id. Without it a heartbeat send
        /// failure would surface as a DispatchFailure against the inbound Ping
        /// instead of a TransportFailure against the Pong that actually failed.
        /// The failure can arrive in either of two shapes, and awaiting inside the
        /// try is what covers both. This session's own SendAsync is not async - it
        /// validates and returns transport.SendAsync(...) directly - so its
        /// validation throws on the pump's stack before any task exists, and a
        /// Forget() would never run. The transport's SendAsync, by contrast, has
        /// been async since it acquired a write gate, so its validation is captured
        /// into the returned UniTask rather than thrown. A Forget() would miss the
        /// first shape and lose the second. And a task that faults later would be
        /// routed to the unobserved-exception handler, which keeps the pump alive
        /// but loses the Pong silently, and one lost Pong is what makes the server
        /// declare the connection dead.
        /// </summary>
        private async UniTaskVoid ReplyToHeartbeatAsync()
        {
            try
            {
                await SendAsync(MessageId.Pong, null, CancellationToken.None);
            }
            catch (Exception exception)
            {
                PublishFault(new SessionFault(
                    SessionFaultKind.TransportFailure,
                    MessageId.Pong,
                    $"Failed to answer a heartbeat: {exception.Message}"));
            }
        }

        /// <summary>
        /// Each handler gets its own try, and deliberately so. Dispatch now runs
        /// inside the pump's try, so an escaping exception costs one message
        /// rather than the pump; this guard is kept because it grades the failure
        /// as a SubscriberFailure rather than a DispatchFailure - the message id
        /// would be the same either way - and because catching per handler keeps
        /// one broken subscriber from silencing the ones queued behind it.
        /// </summary>
        private void DeliverToSubscribers(ProtocolDecodeResult result)
        {
            if (!subscribers.TryGetValue(result.MessageId, out var handlers) ||
                handlers.Count == 0)
            {
                // A change from silently dropping it. The server's reconnect path
                // sends LoginResponse and MatchFoundEvent back to back, so a
                // consumer that subscribes after requesting loses the event with no
                // trace. Until subscribe-before-request is enforced, this fault is
                // the only thing that can show it happened.
                //
                // The Count check is not redundant: Subscribe leaves an empty list
                // behind when the last subscription is disposed, so a key check
                // alone would report a destination that is not there.
                PublishFault(new SessionFault(
                    SessionFaultKind.NoDestination,
                    result.MessageId,
                    $"{result.MessageId} decoded but no subscriber was registered."));
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

            // Published before the disconnect is attempted, so the first
            // TransportFailure a consumer sees is the cause rather than the close
            // that followed it.
            PublishFault(new SessionFault(
                SessionFaultKind.TransportFailure, default, exception.Message));

            try
            {
                await transport.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception disconnectFailure)
            {
                PublishFault(new SessionFault(
                    SessionFaultKind.TransportFailure, default, disconnectFailure.Message));
            }
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
