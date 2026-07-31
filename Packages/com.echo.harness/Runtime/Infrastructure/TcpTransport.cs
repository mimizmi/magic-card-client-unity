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
        private readonly IClock clock;
        private readonly byte[] header =
            new byte[WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes];

        // volatile for visibility, not for atomicity. These are written by Dispose
        // and DisconnectAsync and read by the receive path, with no lock between
        // them, so a plain field leaves a reader free to observe a stale value
        // indefinitely. ReadExactlyAsync's capture must see the null a concurrent
        // CloseSocket wrote, and ThrowIfDisposed must see a concurrent Dispose.
        //
        // State belongs to this same set - written by ConnectAsync, DisconnectAsync
        // and Dispose, read by EnsureConnected next to stream - and is not volatile
        // only because it is an auto-property and the modifier is not legal on one.
        // Converting it to a backing field would buy the same visibility; it is left
        // alone because nothing today races on it in a way the stream null check
        // does not already cover, not because it is in a different category.
        private volatile TcpClient client;
        private volatile NetworkStream stream;
        private volatile bool disposed;

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
        // ConnectAsync and read by a SendAsync that may resume on another thread,
        // with no lock between them.
        private volatile SendBudget budget;

        public TcpTransport(TcpTransportOptions options, IClock clock)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public TransportState State { get; private set; } = TransportState.Disconnected;

        public async UniTask ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (State != TransportState.Disconnected)
            {
                throw new InvalidOperationException(
                    $"This transport is {State} and cannot be connected again.");
            }

            State = TransportState.Connecting;
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
                budget = new SendBudget(options.SendBudgetPerSecond, clock);

                // Inside the try, not after it. Cancellation can fire between a
                // successful connect and the registration being disposed, and the
                // callback closes the client - so GetStream() here can throw. Left
                // outside, that would escape with State still Connecting: exactly
                // the unretryable stuck state this method exists to prevent.
                client = connecting;
                stream = connecting.GetStream();
                State = TransportState.Connected;
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
            State = TransportState.Disconnected;

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

                // One write for the whole frame. BinaryFrameCodec.Encode returns the
                // header and body in a single buffer for exactly this reason, and
                // the server merges them in its EncodeFrame for the same one.
                await active
                    .WriteAsync(new ReadOnlyMemory<byte>(frame), cancellationToken)
                    .AsUniTask();
                await active.FlushAsync(cancellationToken).AsUniTask();
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

            State = TransportState.Disconnected;
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
            State = TransportState.Disconnected;
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
}
