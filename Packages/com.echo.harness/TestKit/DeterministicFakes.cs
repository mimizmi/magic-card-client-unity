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
        private Exception nextReceiveFailure;

        public TransportState State { get; private set; } = TransportState.Disconnected;

        public IReadOnlyList<TransportMessage> Sent => sent;

        /// <summary>
        /// Queues an inbound message. When a receive is already awaiting, its
        /// continuation runs synchronously from this call, so a test can assert
        /// on the effects of the message as soon as this method returns.
        /// </summary>
        public void EnqueueInbound(TransportMessage message)
        {
            var waiter = pendingReceive;
            if (waiter != null)
            {
                pendingReceive = null;
                waiter.TrySetResult(message);
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

            var waiter = pendingReceive;
            if (waiter != null)
            {
                pendingReceive = null;
                waiter.TrySetException(failure);
                return;
            }

            nextReceiveFailure = failure;
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

            pendingReceive = new UniTaskCompletionSource<TransportMessage>();
            return pendingReceive.Task;
        }

        public UniTask DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Disconnected;

            var waiter = pendingReceive;
            if (waiter != null)
            {
                pendingReceive = null;
                waiter.TrySetCanceled(cancellationToken);
            }

            return UniTask.CompletedTask;
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

    public sealed class ManualClock : IClock
    {
        public ManualClock(DateTimeOffset initialTime)
        {
            UtcNow = initialTime;
        }

        public DateTimeOffset UtcNow { get; private set; }

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    "Manual time cannot move backwards.");
            }

            UtcNow = UtcNow.Add(duration);
        }
    }
}
