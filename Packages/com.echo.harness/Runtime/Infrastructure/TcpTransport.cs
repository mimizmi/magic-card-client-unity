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

        // volatile because these are read and written from different threads
        // without a lock: socket I/O completes on the thread pool while Dispose or
        // DisconnectAsync can run on the session's context. The capture in
        // ReadExactlyAsync depends on seeing a current value rather than one the
        // jitter cached in a register across the loop.
        private volatile TcpClient client;
        private volatile NetworkStream stream;
        private volatile bool disposed;

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

                // Inside the try, not after it. Cancellation can fire between a
                // successful connect and the registration being disposed, and the
                // callback closes the client - so GetStream() here can throw. Left
                // outside, that would escape with State still Connecting: exactly
                // the unretryable stuck state this method exists to prevent.
                client = connecting;
                stream = connecting.GetStream();
                State = TransportState.Connected;
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                AbandonConnect(connecting);
                throw new OperationCanceledException(cancellationToken);
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

        public UniTask SendAsync(TransportMessage message, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("Implemented in Task 6.");
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
            // Captured once, deliberately. This loop suspends at every await, and
            // CloseSocket nulls the stream field, so re-reading that field after a
            // resume dereferences null whenever a Dispose or DisconnectAsync lands
            // mid-frame - which needs no second thread, the suspension point alone
            // opens the window. A NullReferenceException would match neither filter
            // below and escape raw, and a session grades it as a transport fault
            // reading "Object reference not set to an instance of an object." for
            // what is an ordinary shutdown. Reading through the captured reference
            // instead throws ObjectDisposedException, which becomes the
            // EndOfStreamException a caller can actually act on.
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
                catch (Exception) when (cancellationToken.IsCancellationRequested)
                {
                    // Nothing else in this path raises OperationCanceledException.
                    // NetworkStream.ReadAsync observes its token only before the
                    // read is issued; once parked, the only thing that unblocks it
                    // is the socket being closed under it. Without this translation
                    // a clean StopAsync - CancelPump, then DisconnectAsync - ends
                    // with the parked read faulting on ObjectDisposedException, and
                    // a session's receive pump grades that as a transport fault
                    // instead of the cancellation it actually is.
                    throw new OperationCanceledException(cancellationToken);
                }
                catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new EndOfStreamException(
                        "The connection closed while a frame was being read.");
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
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
