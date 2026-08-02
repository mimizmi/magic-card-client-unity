using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    public sealed class FakeTransport : ITransport
    {
        private readonly Queue<TransportMessage> inbound = new Queue<TransportMessage>();
        private readonly List<TransportMessage> sent = new List<TransportMessage>();
        private UniTaskCompletionSource<TransportMessage> pendingReceive;
        private CancellationTokenRegistration pendingReceiveCancellation;
        private Exception nextReceiveFailure;
        private Exception nextSendFailure;
        private Exception nextDisconnectFailure;

        public int DisconnectCount { get; private set; }

        public TransportState State { get; private set; } = TransportState.Disconnected;

        public IReadOnlyList<TransportMessage> Sent => sent;

        /// <summary>
        /// Queues an inbound message. When a receive is already awaiting, its
        /// continuation runs synchronously from this call, so a test can assert
        /// on the effects of the message as soon as this method returns.
        /// </summary>
        public void EnqueueInbound(TransportMessage message)
        {
            if (pendingReceive != null)
            {
                TakePendingReceive().TrySetResult(message);
                return;
            }

            inbound.Enqueue(message);
        }

        /// <summary>Makes the next receive fail, standing in for a desynchronized stream.</summary>
        public void FailNextReceive(Exception failure)
        {
            if (failure == null)
            {
                throw new ArgumentNullException(nameof(failure));
            }

            if (pendingReceive != null)
            {
                TakePendingReceive().TrySetException(failure);
                return;
            }

            nextReceiveFailure = failure;
        }

        /// <summary>Makes the next send fail, standing in for a closed socket.</summary>
        public void FailNextSend(Exception failure)
        {
            nextSendFailure = failure ?? throw new ArgumentNullException(nameof(failure));
        }

        /// <summary>
        /// Makes the next disconnect fail, standing in for a socket that throws on
        /// close. StopAsync must still fail its waiters when this happens.
        /// </summary>
        public void FailNextDisconnect(Exception failure)
        {
            nextDisconnectFailure = failure ?? throw new ArgumentNullException(nameof(failure));
        }

        public UniTask ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Connected;
            return UniTask.CompletedTask;
        }

        public UniTask SendAsync(
            TransportMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();

            // Synchronously, on purpose: that is the shape a real transport takes
            // when it validates eagerly, and it is the shape a bare .Forget()
            // cannot survive.
            if (nextSendFailure != null)
            {
                var failure = nextSendFailure;
                nextSendFailure = null;
                throw failure;
            }

            sent.Add(message);
            return UniTask.CompletedTask;
        }

        public UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();

            if (nextReceiveFailure != null)
            {
                var failure = nextReceiveFailure;
                nextReceiveFailure = null;
                return UniTask.FromException<TransportMessage>(failure);
            }

            if (inbound.Count > 0)
            {
                return UniTask.FromResult(inbound.Dequeue());
            }

            if (pendingReceive != null)
            {
                throw new InvalidOperationException(
                    "Only one receive may await this transport at a time.");
            }

            var waiter = new UniTaskCompletionSource<TransportMessage>();
            pendingReceive = waiter;

            // Cancelling the token has to unblock the receive on its own. Without
            // this the fake would be less cancellable than the real transport it
            // stands in for, so a pump that stops cleanly in a test could still
            // hang in production.
            var registration = cancellationToken.Register(
                () =>
                {
                    if (ReferenceEquals(pendingReceive, waiter))
                    {
                        TakePendingReceive().TrySetCanceled(cancellationToken);
                    }
                });

            if (ReferenceEquals(pendingReceive, waiter))
            {
                pendingReceiveCancellation = registration;
            }
            else
            {
                // The token was cancelled while registering, so the callback
                // already ran inline and this registration is spent.
                registration.Dispose();
            }

            return waiter.Task;
        }

        public UniTask DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisconnectCount++;
            State = TransportState.Disconnected;

            if (pendingReceive != null)
            {
                TakePendingReceive().TrySetCanceled(cancellationToken);
            }

            // Thrown after the state change and after the pending receive is
            // released, so a failing close still leaves the fake in the state a
            // real closed socket would be in. A transport that threw before
            // releasing the receive would hang the pump instead of failing it.
            if (nextDisconnectFailure != null)
            {
                var failure = nextDisconnectFailure;
                nextDisconnectFailure = null;
                throw failure;
            }

            return UniTask.CompletedTask;
        }

        /// <summary>
        /// Detaches the awaiting receive so the caller can complete it, and
        /// releases its cancellation registration.
        /// </summary>
        private UniTaskCompletionSource<TransportMessage> TakePendingReceive()
        {
            var waiter = pendingReceive;

            // Required, not merely tidy: the field must be cleared BEFORE the
            // returned source is completed. Completing it resumes the awaiting
            // pump inline, on this very stack, and that pump's next loop calls
            // ReceiveAsync re-entrantly. Were the field still set, that call
            // would trip the "only one receive" guard above, and UniTask would
            // swallow the throw into PublishUnobservedTaskException — the pump
            // would silently stop with a message that reads like a concurrency
            // bug rather than the ordering bug it would actually be.
            pendingReceive = null;
            pendingReceiveCancellation.Dispose();
            pendingReceiveCancellation = default;
            return waiter;
        }

        private void EnsureConnected()
        {
            if (State != TransportState.Connected)
            {
                throw new InvalidOperationException("Fake transport is not connected.");
            }
        }
    }

    public sealed class FakeContentProvider : IContentProvider
    {
        private readonly Dictionary<string, object> content =
            new Dictionary<string, object>(StringComparer.Ordinal);
        private readonly Dictionary<string, int> leases =
            new Dictionary<string, int>(StringComparer.Ordinal);

        public int ActiveLeaseCount { get; private set; }

        public void Register<T>(string address, T value)
        {
            ValidateAddress(address);
            content[address] = value;
        }

        public UniTask<T> LoadAsync<T>(string address, CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            ValidateAddress(address);
            if (!content.TryGetValue(address, out var value))
            {
                throw new KeyNotFoundException($"No fixture is registered for '{address}'.");
            }

            if (!(value is T typedValue))
            {
                throw new InvalidCastException(
                    $"Fixture '{address}' is not assignable to {typeof(T).FullName}.");
            }

            leases.TryGetValue(address, out var leaseCount);
            leases[address] = leaseCount + 1;
            ActiveLeaseCount++;
            return UniTask.FromResult(typedValue);
        }

        public void Release(string address)
        {
            ValidateAddress(address);
            if (!leases.TryGetValue(address, out var leaseCount) || leaseCount == 0)
            {
                throw new InvalidOperationException($"Fixture '{address}' has no active lease.");
            }

            if (leaseCount == 1)
            {
                leases.Remove(address);
            }
            else
            {
                leases[address] = leaseCount - 1;
            }

            ActiveLeaseCount--;
        }

        private static void ValidateAddress(string address)
        {
            if (string.IsNullOrWhiteSpace(address))
            {
                throw new ArgumentException("A content address is required.", nameof(address));
            }
        }
    }

    public sealed class FakeLuaRuntime : ILuaRuntime
    {
        private readonly List<string> invocations = new List<string>();

        public LuaRuntimeState State { get; private set; } = LuaRuntimeState.Stopped;

        public IReadOnlyList<string> Invocations => invocations;

        public UniTask InitializeAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = LuaRuntimeState.Running;
            return UniTask.CompletedTask;
        }

        public UniTask ExecuteAsync(
            string module,
            string entryPoint,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            if (State != LuaRuntimeState.Running)
            {
                throw new InvalidOperationException("Fake Lua runtime is not running.");
            }

            if (string.IsNullOrWhiteSpace(module) || string.IsNullOrWhiteSpace(entryPoint))
            {
                throw new ArgumentException("Lua module and entry point are required.");
            }

            invocations.Add($"{module}:{entryPoint}");
            return UniTask.CompletedTask;
        }

        public UniTask ShutdownAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = LuaRuntimeState.Stopped;
            return UniTask.CompletedTask;
        }
    }

    /// <summary>
    /// Controlled time for tests, implementing both time ports so a test advances
    /// once and both faces move. Two separate manual objects would make every test
    /// advance two things, which is noise that hides what the test is about.
    ///
    /// <para>Monotonic by construction: <see cref="Advance"/> rejects a negative
    /// duration.</para>
    /// </summary>
    public sealed class ManualTime : IClock, IElapsedTime
    {
        // Ticks since construction. The unit is deliberately TimeSpan ticks, so a
        // test reasoning about the wall face and the elapsed face gets the same
        // number from both.
        private long ticks;

        public ManualTime(DateTimeOffset initialTime)
        {
            Origin = initialTime;
        }

        private DateTimeOffset Origin { get; }

        public DateTimeOffset UtcNow => Origin.AddTicks(ticks);

        public long GetTimestamp() => ticks;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(ticks - startingTimestamp);

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    "Manual time cannot move backwards.");
            }

            ticks += duration.Ticks;
        }
    }
}
