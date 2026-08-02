using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.Infrastructure
{
    public sealed class TcpTransport : ITransport, IDisposable
    {
        private readonly TcpTransportOptions options;
        private readonly IElapsedTime time;
        private readonly byte[] header =
            new byte[WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes];

        // volatile for visibility, not for atomicity. These are written by Dispose
        // and DisconnectAsync and read by the receive path, with no lock between
        // them, so a plain field leaves a reader free to observe a stale value
        // indefinitely. ReadExactlyAsync's capture must see the null a concurrent
        // CloseSocket wrote, and ThrowIfDisposed must see a concurrent Dispose.
        //
        // State belongs to this same set - written by ConnectAsync, DisconnectAsync
        // and Dispose, read by EnsureConnected next to stream. It used to be an
        // auto-property, where the modifier is not legal, and the note here said it
        // was left alone because nothing raced on it. That is no longer true: the
        // read-idle deadline's registration writes it from a timer thread, so it is
        // now a backing field for the same reason as the three above and the
        // property is a read-only view onto it.
        private volatile TcpClient client;
        private volatile NetworkStream stream;
        private volatile bool disposed;
        private volatile TransportState state = TransportState.Disconnected;

        // WHY THIS EXISTS - read before deleting it. The one test written to catch
        // interleaving, ConcurrentSendsArriveAsWholeFrames, still passes with this
        // gate removed; that was measured deliberately, and it is not evidence the
        // gate is unnecessary. Byte-level interleaving could not be provoked on
        // this platform and runtime at any size tried, up to 64 concurrent senders
        // of 1,000,000 bytes, because something beneath us serializes the
        // overlapped sends. Three reasons stand independently of that:
        //
        //   1. NetworkStream does not support concurrent writes by contract. What
        //      the platform does today is not what the API promises.
        //   2. A runtime that splits a large write into several syscalls - which
        //      any of the mobile targets may - interleaves two callers' frames
        //      and desynchronizes the stream. The server then reads a garbage
        //      length prefix and closes without an error frame.
        //   3. SendBudget.TryConsume is a read-modify-write over an int and two
        //      longs, and is not thread-safe on its own. C# guarantees atomic
        //      reads and writes only up to 32 bits, and `long` is excluded, so
        //      `lastFillTimestamp` and `carryTicks` can tear on a 32-bit runtime
        //      - the IL2CPP ARM32 target named in clause 2 among them. Replacing
        //      the DateTimeOffset narrowed that hazard rather than removing it: a
        //      torn long now corrupts an interval, not a wall-clock moment.
        //      Either that or a lost update to `tokens` silently raises the
        //      effective send rate above the server's hard limit, and exceeding it
        //      closes the connection with no error frame. This gate is the whole
        //      of what makes it safe, and it is what keeps tokens in wire order.
        //
        // Deliberately never disposed. SemaphoreSlim.Dispose does not release
        // waiters, so a sender parked in WaitAsync when Dispose ran would never be
        // signalled and would hang forever; and an in-flight sender's
        // finally { sendGate.Release(); } would throw ObjectDisposedException,
        // replacing the real write failure with a bookkeeping one. Left alive, the
        // gate holder's write fails fast against the closed socket, its finally
        // releases, and the parked caller then fails cleanly on the null-stream
        // guard in SendAsync. A SemaphoreSlim only owns an OS handle once
        // AvailableWaitHandle is touched, and nothing here touches it.
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);

        // volatile for the same reason as client and stream above: written by
        // ConnectAsync, read by SendAsync, with no lock between them.
        //
        // Today every caller reaches SendAsync on the main thread and UniTask
        // binds each resume back to it, so no cross-thread read is demonstrable
        // and this modifier cannot be shown to change the generated behaviour.
        // It is kept because it costs nothing and because the confinement it
        // would otherwise rely on is not this class's to enforce or even to
        // observe: it falls out of Task.AsUniTask binding continuations through
        // TaskScheduler.FromCurrentSynchronizationContext, which is a property of
        // a third-party library. One await - the gate wait - separates the write
        // in ConnectAsync from the read in SendAsync. Do not read the modifier as
        // proof a race exists.
        private volatile SendBudget budget;

        // Snapshotted rather than read live, and that is what makes the constructor
        // guard below mean anything. TcpTransportOptions.ReadIdleTimeout has a public
        // setter, so an options object mutated after construction would otherwise
        // reach CancelAfter unchecked on the very next receive and reproduce exactly
        // the two failures the guard exists to prevent. Every other option is already
        // effectively pinned - SendBudgetPerSecond is captured into `budget` at
        // connect, Host and Port are used once - so this was the one live read left.
        private readonly TimeSpan readIdleTimeout;

        public TcpTransport(TcpTransportOptions options, IElapsedTime time)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.time = time ?? throw new ArgumentNullException(nameof(time));

            // Rejected here, where the option can still be named. The deadline is
            // built on CancelAfter, which throws ArgumentOutOfRangeException on a
            // negative TimeSpan and treats zero as "now" - so an unguarded value
            // surfaces from deep inside a receive as either a bare argument fault
            // naming nothing that led to it, or a link that dies on its first frame
            // for no stated reason.
            if (this.options.ReadIdleTimeout <= TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(options),
                    this.options.ReadIdleTimeout,
                    "ReadIdleTimeout must be positive.");
            }

            readIdleTimeout = this.options.ReadIdleTimeout;
        }

        public TransportState State => state;

        public async UniTask ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (State != TransportState.Disconnected)
            {
                throw new InvalidOperationException(
                    $"This transport is {State} and cannot be connected again.");
            }

            state = TransportState.Connecting;
            var connecting = new TcpClient { NoDelay = true };

            // .NET Standard 2.1 has no TcpClient.ConnectAsync overload taking a
            // CancellationToken, so cancellation is implemented by closing the
            // client out from under the pending connect. The exception that
            // produces is translated back into cancellation below.
            using var registration = cancellationToken.Register(() => connecting.Close());
            try
            {
                await connecting.ConnectAsync(options.Host, options.Port).AsUniTask();

                // Published ahead of the fields that advertise a usable transport,
                // so a send can never find a Connected transport with no budget
                // behind it.
                budget = new SendBudget(options.SendBudgetPerSecond, time);

                // Inside the try, not after it. Cancellation can fire between a
                // successful connect and the registration being disposed, and the
                // callback closes the client - so GetStream() here can throw. Left
                // outside, that would escape with State still Connecting: exactly
                // the unretryable stuck state this method exists to prevent.
                client = connecting;
                stream = connecting.GetStream();
                state = TransportState.Connected;
            }
            catch (Exception failure) when (cancellationToken.IsCancellationRequested)
            {
                AbandonConnect(connecting);

                // The original is kept as the inner exception for the same reason as
                // in the receive path: it names what the cancellation callback's
                // Close() actually broke, which is the only clue to whether the
                // connect had already succeeded.
                throw new OperationCanceledException(
                    "The connect was cancelled.", failure, cancellationToken);
            }
            catch (Exception)
            {
                // Reset rather than left in Connecting: a transport stuck there
                // refuses every later ConnectAsync and can never be retried.
                AbandonConnect(connecting);
                throw;
            }
        }

        /// <summary>
        /// Undoes a partial connect. The fields are cleared as well as the socket
        /// closed because the assignments now sit inside the try: a failure in
        /// GetStream leaves client assigned and stream still null, and a transport
        /// that reports Disconnected must not keep a live handle behind it.
        /// </summary>
        private void AbandonConnect(TcpClient connecting)
        {
            client = null;
            stream = null;
            budget = null;
            state = TransportState.Disconnected;

            try
            {
                connecting.Dispose();
            }
            catch (Exception)
            {
                // Already closed by the cancellation callback.
            }
        }

        public async UniTask SendAsync(
            TransportMessage message,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureConnected();

            // Encoded before the gate is taken: BinaryFrameCodec.Encode rejects an
            // oversized payload, and there is no reason to make other senders wait
            // behind a frame that will never be written.
            var frame = BinaryFrameCodec.Encode(message.MessageId, message.Payload);

            // Acquired outside the try, so a cancelled or failed WaitAsync cannot
            // reach a finally that releases a gate it never took.
            await sendGate.WaitAsync(cancellationToken).AsUniTask();
            try
            {
                // Captured once for the reason spelled out at ReadExactlyAsync's
                // capture: the field can be nulled by a Dispose or DisconnectAsync
                // that runs while this continuation sits in the main-thread queue,
                // and re-reading it after an await dereferences null. Taken after
                // the gate rather than before it because the wait is itself an await
                // and the window covers it.
                var active = stream;
                if (active == null)
                {
                    throw new EndOfStreamException(
                        "The connection closed before the frame could be sent.");
                }

                // Inside the gate, not before it. Tokens must correspond to bytes
                // actually placed on the wire, in wire order; checking outside lets
                // two callers both pass and then acquire the gate in the opposite
                // order, so the sequence the server rate-limits is not the sequence
                // that was checked. TryConsume is not thread-safe on its own either
                // - it is a read-modify-write over a plain int - and being inside
                // the gate is the whole of what makes it safe.
                //
                // Pong is exempt because the server handles it ahead of its own
                // limiter and never counts it. Refusing a Pong here would cause the
                // heartbeat disconnect this guard exists to prevent, and the
                // symptom would appear 35 seconds later with no obvious cause.
                if (message.MessageId != MessageId.Pong && !budget.TryConsume())
                {
                    throw new SendBudgetExceededException(
                        message.MessageId,
                        $"Sending {message.MessageId} would exceed " +
                        $"{options.SendBudgetPerSecond} messages per second. The server " +
                        "closes the connection without an error frame when that limit " +
                        "is passed, so this throws instead of queueing: a caller looping " +
                        "faster than the protocol allows is a defect worth surfacing.");
                }

                // The two awaits are wrapped, not the whole gate body, and that
                // boundary is load-bearing in both directions. The null-stream guard
                // above throws EndOfStreamException, which derives from IOException,
                // so a catch spanning the body would re-wrap the guard's own
                // exception; and SendBudgetExceededException is an
                // InvalidOperationException that has to escape untouched, because a
                // caller sending faster than the protocol allows is a defect rather
                // than a link failure.
                try
                {
                    // One write for the whole frame. BinaryFrameCodec.Encode returns
                    // the header and body in a single buffer for exactly this
                    // reason, and the server merges them in its EncodeFrame for the
                    // same one.
                    await active
                        .WriteAsync(new ReadOnlyMemory<byte>(frame), cancellationToken)
                        .AsUniTask();
                    await active.FlushAsync(cancellationToken).AsUniTask();
                }
                // The same partition as ReadExactlyAsync's, and bare on the same two
                // trailing clauses for the reason spelled out there. Do not make them
                // symmetrical.
                //
                // Untranslated, a disposal or a reset during a write escapes raw and
                // a session grades an ordinary shutdown as a transport fault - the
                // same defect the receive path already fixed.
                catch (Exception failure) when (cancellationToken.IsCancellationRequested)
                {
                    throw new OperationCanceledException(
                        "The send was cancelled while a frame was being written.",
                        failure,
                        cancellationToken);
                }
                // Reachable, and Task 7 is what made it so. `active` is captured
                // above and then synchronous budget work runs before either await;
                // the read-idle deadline's AbandonTheLink disposes that very stream
                // from a timer thread, and a write issued against an already-disposed
                // NetworkStream throws ObjectDisposedException synchronously rather
                // than as an IOException. The capture window is the whole of that
                // gap. Not covered by a test - the window is too narrow to open
                // deliberately - so this is kept on the mechanism, not on evidence.
                catch (ObjectDisposedException)
                {
                    throw new EndOfStreamException(
                        "The connection closed while a frame was being written.");
                }
                catch (IOException)
                {
                    throw new EndOfStreamException(
                        "The connection was reset while a frame was being written.");
                }
            }
            finally
            {
                sendGate.Release();
            }
        }

        public async UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureConnected();

            // Before the linked source exists, and this ordering is the whole point.
            // CreateLinkedTokenSource over an already-cancelled token returns an
            // already-cancelled source, and Token.Register on one of those invokes the
            // callback synchronously on this thread - so AbandonTheLink would close the
            // socket and set Disconnected before ReceiveFrameAsync was ever entered,
            // and the read would then fail on the null-stream guard as
            // EndOfStreamException. A caller who cancelled before asking would be told
            // its peer closed, and the link it was merely declining to read from would
            // be gone. Checking here costs one branch and leaves both facts true.
            cancellationToken.ThrowIfCancellationRequested();

            // The deadline covers one whole frame and restarts with the next, so a
            // healthy but quiet link is not killed by the sum of its gaps. The
            // server sends a Ping every 15 s, which is what makes silence
            // measurable at all.
            //
            // Real time, not the injected IElapsedTime. CancelAfter runs off a
            // timer and there is nothing to drive a virtual clock against a real
            // socket, so this deliberately does not use `time`. Do not "fix" that.
            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);

            // Declared after the source so the two using declarations dispose in
            // reverse and this registration goes first. A registration outliving its
            // source is an ordering bug waiting to bite.
            //
            // KNOWN, MEASURED, ACCEPTED - do not "fix" this without reading the trade.
            // The registration is only disposed once ReceiveFrameAsync has returned,
            // so the timer can still fire in the gap between a frame being read
            // successfully and this using going out of scope. The frame is delivered
            // and the transport is left Disconnected, and the caller's next receive
            // then fails with EnsureConnected's message instead of the idle
            // diagnostic. The window was measured at roughly 2-4 ms on an actively
            // ticking editor loop - not the ~150 ms an unfocused editor's tick would
            // suggest - against a 45 s production deadline, and the outcome is nearly
            // right anyway: the link genuinely was idle right up to the deadline.
            //
            // What a fix would cost, stated correctly this time. An earlier version
            // of this note justified leaving it by saying the fix "puts shared
            // mutable state into a class that has deliberately avoided any". That is
            // simply false about this class: it has five volatile fields and a
            // SemaphoreSlim, and the thirteen-line header comment standing over
            // four of them spends its whole length documenting cross-thread races
            // on exactly those fields - the fifth, budget, carries its own. The fix is also cheaper than that made it sound - a
            // per-call int local, captured by turning the registration below into a
            // closure and claimed with Interlocked.CompareExchange, adding no field
            // to the class at all. What it costs is an allocation per receive and
            // the method-group registration's readability.
            //
            // The reason to leave it is the size of what it buys. A flag set once
            // ReceiveFrameAsync has returned still leaves a few instructions in
            // which the timer can win, so it narrows this window rather than closing
            // it - and the window it narrows is milliseconds against a 45 second
            // deadline whose outcome is very nearly correct already. That trade, and
            // not an invented property of the class, is why this stays.
            using var closing = deadline.Token.Register(AbandonTheLink);

            // Registered before the timer is armed, so a very short timeout cannot
            // fire into a token that has no callback on it yet.
            deadline.CancelAfter(readIdleTimeout);

            try
            {
                return await ReceiveFrameAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Only the deadline fired. A caller's own cancellation passes
                // through untouched, because reporting it as a dead link would
                // send a session into Faulted during an orderly shutdown.
                throw new ReadIdleTimeoutException(
                    readIdleTimeout,
                    $"No complete frame arrived within {readIdleTimeout}. The " +
                    "server sends a Ping every 15 seconds, so this much silence means " +
                    "the link is gone even though the socket has not said so.");
            }
        }

        /// <summary>
        /// Ends the link when the idle deadline fires, or when the caller cancels a
        /// receive that is already parked.
        ///
        /// Closing the socket is the load-bearing half, and cancelling alone is not
        /// enough. That was measured on this runtime rather than reasoned about:
        /// with CancelAfter and no close, the deadline fired against a silent peer
        /// and the parked read never returned - the receive hung until the test's
        /// own ceiling. It confirms the note at ReadExactlyAsync's cancellation
        /// clause that a parked NetworkStream.ReadAsync observes no token and only
        /// the socket closing under it ends the wait.
        ///
        /// The cancellation is still what routes the exception. Closing without
        /// cancelling would make ReadExactlyAsync produce EndOfStreamException,
        /// indistinguishable from an ordinary peer close; under the cancelled
        /// deadline token its first catch filter matches instead and it produces the
        /// OperationCanceledException that ReceiveAsync translates.
        ///
        /// The thread this runs on differs between the two paths, and only one of
        /// them races anything. Measured rather than assumed: on the deadline path
        /// CancelAfter fires from a timer, so this runs on a thread-pool thread and
        /// does race the main-thread reader - deliberately, because it is the same
        /// race Dispose already runs and CloseSocket already tolerates it. On the
        /// caller-cancellation path a token is cancelled by someone calling Cancel,
        /// and the callback runs inline on that caller's thread, which for every
        /// caller in this codebase is the main thread. There is no race there at all.
        /// Do not read the thread-pool half as a claim about both.
        ///
        /// State goes to Disconnected rather than being left at Connected with a
        /// null stream. Both leave every later call throwing - EnsureConnected
        /// covers the null stream - but a transport that has just closed its own
        /// socket and still reports Connected is lying to whoever asks, and the
        /// session's next step either way is to tear the connection down.
        ///
        /// Closing on a caller's cancellation too, not only on the deadline, because
        /// the token is linked and a cancelled receive is in exactly the same
        /// position: its read is parked and nothing else will ever end it.
        /// Cancelling the pump and then disconnecting is already what a session's
        /// StopAsync does; this only makes the disconnect immediate. That reasoning
        /// is about a receive already in flight; a caller that cancels before asking
        /// never reaches here, because ReceiveAsync now rejects it up front.
        /// </summary>
        private void AbandonTheLink()
        {
            state = TransportState.Disconnected;
            CloseSocket();
        }

        private async UniTask<TransportMessage> ReceiveFrameAsync(
            CancellationToken cancellationToken)
        {
            await ReadExactlyAsync(header, header.Length, cancellationToken);

            var declaredLength = (header[0] << 24) | (header[1] << 16) |
                                 (header[2] << 8) | header[3];

            // Checked before a single body byte is allocated. A hostile or
            // desynchronized peer that declares a gigabyte would otherwise get one
            // allocated for it.
            if (declaredLength < 0 || declaredLength > WireFrameSpec.MaxPayloadBytes)
            {
                throw new InvalidDataException(
                    $"Frame declares a body length of {declaredLength}, outside " +
                    $"[0, {WireFrameSpec.MaxPayloadBytes}]. The stream has lost its " +
                    "frame boundaries and nothing later can be trusted.");
            }

            var messageId = (MessageId)((header[4] << 8) | header[5]);
            if (declaredLength == 0)
            {
                // Ping arrives this way: the server sends it with a nil payload.
                return new TransportMessage(messageId, new byte[0]);
            }

            var body = new byte[declaredLength];
            await ReadExactlyAsync(body, declaredLength, cancellationToken);
            return new TransportMessage(messageId, body);
        }

        public UniTask DisconnectAsync(CancellationToken cancellationToken)
        {
            // Idempotent by contract: the session's fault path and StopAsync can
            // both reach here, and Dispose fires one more without awaiting it.
            if (State == TransportState.Disconnected)
            {
                return UniTask.CompletedTask;
            }

            state = TransportState.Disconnected;
            CloseSocket();
            return UniTask.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            state = TransportState.Disconnected;
            CloseSocket();
        }

        /// <summary>
        /// Fills <paramref name="count"/> bytes or throws. A TCP read returns what
        /// has arrived, not what was asked for, which is the whole reason a
        /// streaming reader is needed on top of BinaryFrameCodec. .NET Standard 2.1
        /// has no ReadExactlyAsync to borrow.
        /// </summary>
        private async UniTask ReadExactlyAsync(
            byte[] buffer,
            int count,
            CancellationToken cancellationToken)
        {
            // Captured once, deliberately, and the window this closes is wide rather
            // than narrow. UniTask's ValueTask bridge is `async UniTask<T>
            // AsUniTask<T>(this ValueTask<T> task) => await task;` - a bare await,
            // which captures SynchronizationContext.Current. On Unity's main thread
            // that is UnitySynchronizationContext, so a completed read's
            // continuation is *posted to the main-thread queue* rather than resumed
            // inline. Everything that happens on the main thread before that queue
            // is next drained is inside the window: a main-thread Dispose or
            // DisconnectAsync nulls the stream field there, and a session's StopAsync
            // is exactly that shape.
            //
            // Re-reading the field after such a resume dereferences null. The
            // resulting NullReferenceException matches none of the clauses below, so
            // it escapes raw and a session grades it as a transport fault reading
            // "Object reference not set to an instance of an object." for what is an
            // ordinary shutdown. Reading through the captured reference instead
            // throws ObjectDisposedException, which becomes the EndOfStreamException
            // a caller can act on; and a disposal landing between this method's two
            // calls (header, then body) is caught by the null check below.
            //
            // Do NOT "fix" this by dropping the nulls in CloseSocket. Those nulls
            // are one of the two mechanisms that make a Dispose after a
            // DisconnectAsync a no-op rather than a second close on an already
            // disposed TcpClient.
            var active = stream;
            if (active == null)
            {
                // The token is consulted first, and this guard was the one place in
                // the read path that was cancellation-blind. A null stream here does
                // not say who nulled it: a peer close and the idle deadline's own
                // AbandonTheLink land identically. The deadline landing between this
                // method's two calls - header read completed, its continuation still
                // queued on the main thread, body read not yet issued - is not a
                // corner: it is the watchdog's own primary path, and reporting it as
                // "the connection closed" gives back the message this file elsewhere
                // calls indistinguishable from an ordinary peer close, for a failure
                // whose cause is known exactly. Under a cancelled token the throw
                // below would be a worse answer than the one the token already has.
                //
                // Unconditional, for the same reason the trailing catch clauses below
                // are bare: a check that cannot be skipped cannot miss.
                cancellationToken.ThrowIfCancellationRequested();

                throw new EndOfStreamException(
                    "The connection closed while a frame was being read.");
            }

            var read = 0;
            while (read < count)
            {
                cancellationToken.ThrowIfCancellationRequested();

                int chunk;
                try
                {
                    chunk = await active
                        .ReadAsync(new Memory<byte>(buffer, read, count - read), cancellationToken)
                        .AsUniTask();
                }
                // The partition below is: cancelled -> OperationCanceledException,
                // otherwise -> EndOfStreamException. Only the first clause is
                // filtered, and the two after it are deliberately bare.
                //
                // A matching `when (!cancellationToken.IsCancellationRequested)` on
                // them would look symmetrical and would be a bug. Exception filters
                // run sequentially as ordinary code against the live token, with no
                // snapshot: if the first filter reads false and cancellation is then
                // requested before the second runs, every filter is false and the
                // raw ObjectDisposedException escapes untranslated - a spurious
                // transport fault, which is the exact defect the first clause exists
                // to prevent. Bare clauses cannot miss.
                catch (Exception failure) when (cancellationToken.IsCancellationRequested)
                {
                    // Nothing else in this path raises OperationCanceledException.
                    // NetworkStream.ReadAsync observes its token only before the
                    // read is issued; once parked, the only thing that unblocks it
                    // is the socket being closed under it. Without this translation
                    // a clean StopAsync - CancelPump, then DisconnectAsync - ends
                    // with the parked read faulting on ObjectDisposedException, and
                    // a session's receive pump grades that as a transport fault
                    // instead of the cancellation it actually is.
                    throw new OperationCanceledException(
                        "The receive was cancelled while a frame was being read.",
                        failure,
                        cancellationToken);
                }
                catch (ObjectDisposedException)
                {
                    throw new EndOfStreamException(
                        "The connection closed while a frame was being read.");
                }
                catch (IOException)
                {
                    throw new EndOfStreamException(
                        "The connection was reset while a frame was being read.");
                }

                if (chunk == 0)
                {
                    throw new EndOfStreamException(
                        $"The peer closed after {read} of {count} expected bytes. " +
                        "A partial frame means the stream is unusable.");
                }

                read += chunk;
            }
        }

        private void CloseSocket()
        {
            try
            {
                stream?.Dispose();
            }
            catch (Exception)
            {
                // A stream that is already broken is exactly what is being closed.
            }

            try
            {
                client?.Close();
            }
            catch (Exception)
            {
                // As above.
            }

            stream = null;
            client = null;
        }

        private void EnsureConnected()
        {
            if (State != TransportState.Connected || stream == null)
            {
                throw new InvalidOperationException(
                    $"This transport is {State} and has no stream to use.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(TcpTransport));
            }
        }
    }

    /// <summary>
    /// No complete frame arrived within the idle window.
    ///
    /// The load-bearing property is that this is NOT an OperationCanceledException.
    /// ProtocolSession.RunPumpAsync catches that one and returns quietly, and faults
    /// the stream on everything else; it does not switch on exception type anywhere,
    /// so "everything else" is the whole of what makes a dead link fatal. Being an
    /// IOException buys nothing behavioural today - it is chosen because a link the
    /// kernel has not yet noticed is dead belongs in the same family as a
    /// desynchronized stream, and because it keeps a caller's `catch (IOException)`
    /// around a receive working. Changing the base class would not change how a
    /// session grades this; deriving it from OperationCanceledException would, and
    /// would silently turn a dead link into a clean shutdown.
    /// </summary>
    public sealed class ReadIdleTimeoutException : IOException
    {
        public ReadIdleTimeoutException(TimeSpan idle, string message)
            : base(message)
        {
            Idle = idle;
        }

        public TimeSpan Idle { get; }
    }
}
