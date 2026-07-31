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

        private TcpClient client;
        private NetworkStream stream;
        private bool disposed;

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
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                connecting.Dispose();
                State = TransportState.Disconnected;
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception)
            {
                // Reset rather than left in Connecting: a transport stuck there
                // refuses every later ConnectAsync and can never be retried.
                connecting.Dispose();
                State = TransportState.Disconnected;
                throw;
            }

            client = connecting;
            stream = connecting.GetStream();
            State = TransportState.Connected;
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
            var read = 0;
            while (read < count)
            {
                int chunk;
                try
                {
                    chunk = await stream
                        .ReadAsync(new Memory<byte>(buffer, read, count - read), cancellationToken)
                        .AsUniTask();
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
