using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// A TcpListener on the loopback interface that speaks the real frame format
    /// and exposes byte-level control. It exists because fragmentation, coalescing,
    /// an oversized declared length, and a close mid-body cannot be constructed
    /// deterministically any other way - and those are exactly the paths a
    /// streaming frame reader gets wrong.
    ///
    /// Strictly disposable: a leaked listener or reader thread stalls the Unity
    /// editor at domain reload, so every test must dispose this.
    /// </summary>
    public sealed class LoopbackProtocolServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly List<DecodedFrame> received = new List<DecodedFrame>();
        private readonly object receivedGate = new object();
        private TcpClient connection;
        private NetworkStream stream;
        private Thread readerThread;
        private volatile bool disposed;
        private volatile Exception readFailure;

        public LoopbackProtocolServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        /// <summary>
        /// Non-null once the read loop stopped on something other than a normal
        /// close. Interleaved client writes land here as an InvalidDataException.
        /// </summary>
        public Exception ReadFailure => readFailure;

        public IReadOnlyList<DecodedFrame> Received
        {
            get
            {
                lock (receivedGate)
                {
                    return new List<DecodedFrame>(received);
                }
            }
        }

        /// <summary>Accepts one connection and starts reading frames from it.</summary>
        public async UniTask AcceptAsync(TimeSpan timeout)
        {
            var accept = listener.AcceptTcpClientAsync();

            // The UniTask.WhenAny overload taking a UniTask<T> and a plain UniTask
            // reports the winner as a bool rather than an index: true means the
            // left task - the accept - completed first.
            var (accepted, _) = await UniTask.WhenAny(
                accept.AsUniTask(), UniTask.Delay(timeout, DelayType.Realtime));
            if (!accepted)
            {
                throw new TimeoutException($"No client connected within {timeout}.");
            }

            connection = accept.Result;
            connection.NoDelay = true;
            stream = connection.GetStream();

            readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "loopback-reader" };
            readerThread.Start();
        }

        /// <summary>Writes a well-formed frame in one go.</summary>
        public void SendFrame(MessageId messageId, string jsonBody)
        {
            var body = jsonBody == null ? new byte[0] : Encoding.UTF8.GetBytes(jsonBody);
            SendBytes(BinaryFrameCodec.Encode(messageId, body));
        }

        /// <summary>Writes arbitrary bytes, so a test can split or coalesce frames.</summary>
        public void SendBytes(byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>
        /// Writes a six-byte header whose declared length need not match anything
        /// that follows. This is how an oversized frame is constructed; the real
        /// server would never emit one, which is why the loopback double must.
        /// </summary>
        public void SendRawHeader(int declaredLength, MessageId messageId)
        {
            var header = new byte[6];
            header[0] = (byte)(declaredLength >> 24);
            header[1] = (byte)(declaredLength >> 16);
            header[2] = (byte)(declaredLength >> 8);
            header[3] = (byte)declaredLength;
            header[4] = (byte)((ushort)messageId >> 8);
            header[5] = (byte)(ushort)messageId;
            SendBytes(header);
        }

        public void CloseConnection()
        {
            try
            {
                connection?.Close();
            }
            catch (Exception)
            {
                // Closing an already-closed connection is not a test failure.
            }
        }

        /// <summary>
        /// Waits until at least <paramref name="count"/> frames have been read, so
        /// a test can assert without sleeping a fixed amount.
        /// </summary>
        public async UniTask WaitForFramesAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                // Surfaced here, on the caller's thread, because this is the only
                // place a test is waiting. An interleaved write makes the read loop
                // raise InvalidDataException, and reporting it as a timeout instead
                // would name the symptom rather than the cause.
                var failure = readFailure;
                if (failure != null)
                {
                    throw new InvalidDataException(
                        "The loopback server stopped reading: " + failure.Message, failure);
                }

                lock (receivedGate)
                {
                    if (received.Count >= count)
                    {
                        return;
                    }
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(5), DelayType.Realtime);
            }

            int actual;
            lock (receivedGate)
            {
                actual = received.Count;
            }

            throw new TimeoutException(
                $"Expected {count} frames within {timeout}; read {actual}.");
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CloseConnection();

            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            readerThread?.Join(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Reads frames the way the Go server does, header first and then the
        /// declared body. A frame the client interleaved with another makes the
        /// length prefix disagree with the following bytes, and this loop throws -
        /// which is precisely how a write-serialization failure is detected, by
        /// the same mechanism the real server would hit.
        /// </summary>
        private void ReadLoop()
        {
            var header = new byte[6];
            try
            {
                while (!disposed)
                {
                    if (!ReadExactly(header, header.Length))
                    {
                        return;
                    }

                    var declared = (header[0] << 24) | (header[1] << 16) |
                                   (header[2] << 8) | header[3];
                    if (declared < 0 || declared > WireFrameSpec.MaxPayloadBytes)
                    {
                        throw new InvalidDataException(
                            $"Client declared a body length of {declared}, which means " +
                            "the frames it wrote were interleaved.");
                    }

                    var body = new byte[declared];
                    if (declared > 0 && !ReadExactly(body, declared))
                    {
                        return;
                    }

                    var messageId = (MessageId)((header[4] << 8) | header[5]);
                    lock (receivedGate)
                    {
                        received.Add(new DecodedFrame(messageId, body));
                    }
                }
            }
            catch (IOException)
            {
                // The connection closed; that is how this loop is meant to end.
            }
            catch (ObjectDisposedException)
            {
                // Disposed mid-read.
            }
            catch (Exception failure)
            {
                // Recorded, never allowed to escape. An unhandled exception on a
                // background thread is invisible where a test can act on it - it
                // either takes the process down or is swallowed by Unity's handler -
                // and the interleaving detected above is the entire signal the
                // write-serialization test depends on. WaitForFramesAsync surfaces
                // it on the calling thread.
                readFailure = failure;
            }
        }

        private bool ReadExactly(byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk == 0)
                {
                    return false;
                }

                read += chunk;
            }

            return true;
        }
    }
}
