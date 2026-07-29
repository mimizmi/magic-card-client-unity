using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class DeterministicFakesTests
    {
        [Test]
        public void FakeTransport_RecordsOutboundFramesAndDequeuesInboundFrames()
        {
            var transport = new FakeTransport();
            var incoming = new TransportMessage(MessageId.Pong, Encoding.UTF8.GetBytes("{}"));
            transport.EnqueueInbound(incoming);

            transport.ConnectAsync(default).GetAwaiter().GetResult();
            transport.SendAsync(
                new TransportMessage(MessageId.Ping, Encoding.UTF8.GetBytes("{}")),
                default).GetAwaiter().GetResult();
            var received = transport.ReceiveAsync(default).GetAwaiter().GetResult();

            Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(received.MessageId, Is.EqualTo(MessageId.Pong));
        }

        [Test]
        public void FakeTransport_ReceiveAwaitsAnEmptyQueueInsteadOfThrowing()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();

            var pending = transport.ReceiveAsync(default).Preserve();
            Assert.That(pending.Status.IsCompleted(), Is.False);

            transport.EnqueueInbound(
                new TransportMessage(MessageId.Pong, System.Array.Empty<byte>()));

            Assert.That(pending.Status.IsCompleted(), Is.True);
            Assert.That(pending.GetAwaiter().GetResult().MessageId, Is.EqualTo(MessageId.Pong));
        }

        [Test]
        public void FakeTransport_FailNextReceiveSurfacesTheInjectedException()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();
            transport.FailNextReceive(new System.IO.IOException("stream desynchronized"));

            var error = Assert.Throws<System.IO.IOException>(
                () => transport.ReceiveAsync(default).GetAwaiter().GetResult());

            Assert.That(error.Message, Is.EqualTo("stream desynchronized"));
        }

        [Test]
        public void FakeTransport_EnqueueInboundResumesAnAwaitingPumpBeforeItReturns()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();

            var received = new List<MessageId>();
            Exception pumpFailure = null;

            // A miniature receive pump: await, record, re-arm — the shape every
            // later session test depends on.
            async UniTaskVoid PumpAsync()
            {
                try
                {
                    while (true)
                    {
                        var message = await transport.ReceiveAsync(default);
                        received.Add(message.MessageId);
                    }
                }
                catch (Exception failure)
                {
                    pumpFailure = failure;
                }
            }

            PumpAsync().Forget();
            Assert.That(received, Is.Empty);

            transport.EnqueueInbound(
                new TransportMessage(MessageId.Pong, Array.Empty<byte>()));

            // Already recorded, on the very next line: the continuation ran
            // inline from EnqueueInbound, with no yield, delay, or poll.
            Assert.That(received, Is.EqualTo(new[] { MessageId.Pong }));

            // The pump re-armed re-entrantly from inside that same call, so a
            // second message completes synchronously too rather than queueing.
            transport.EnqueueInbound(
                new TransportMessage(MessageId.Ping, Array.Empty<byte>()));

            Assert.That(received, Is.EqualTo(new[] { MessageId.Pong, MessageId.Ping }));
            Assert.That(pumpFailure, Is.Null);
        }

        [Test]
        public void FakeTransport_FailNextReceiveFaultsAReceiveThatIsAlreadyAwaiting()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();

            var pending = transport.ReceiveAsync(default).Preserve();
            Assert.That(pending.Status.IsCompleted(), Is.False);

            transport.FailNextReceive(new System.IO.IOException("stream desynchronized"));

            Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Faulted));
            var error = Assert.Throws<System.IO.IOException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(error.Message, Is.EqualTo("stream desynchronized"));
        }

        [Test]
        public void FakeTransport_FailNextReceiveRejectsANullFailure()
        {
            var transport = new FakeTransport();

            Assert.Throws<ArgumentNullException>(() => transport.FailNextReceive(null));
        }

        [Test]
        public void FakeTransport_DisconnectCancelsAReceiveThatIsAlreadyAwaiting()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();

            var pending = transport.ReceiveAsync(default).Preserve();
            Assert.That(pending.Status.IsCompleted(), Is.False);

            transport.DisconnectAsync(default).GetAwaiter().GetResult();

            Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Canceled));
            Assert.Catch<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));
        }

        [Test]
        public void FakeTransport_CancellingTheTokenAloneUnblocksAPendingReceive()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();

            using (var cancellation = new CancellationTokenSource())
            {
                var pending = transport.ReceiveAsync(cancellation.Token).Preserve();
                Assert.That(pending.Status.IsCompleted(), Is.False);

                cancellation.Cancel();

                Assert.That(pending.Status, Is.EqualTo(UniTaskStatus.Canceled));
                Assert.Catch<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
            }

            // The cancelled receive released its slot, so the transport is
            // usable again rather than stuck behind a dead waiter.
            transport.EnqueueInbound(
                new TransportMessage(MessageId.Pong, Array.Empty<byte>()));
            var received = transport.ReceiveAsync(default).GetAwaiter().GetResult();

            Assert.That(received.MessageId, Is.EqualTo(MessageId.Pong));
        }

        [Test]
        public void FakeContentProvider_TracksLoadReleaseSymmetry()
        {
            var content = new FakeContentProvider();
            content.Register("fixture/card", "card-view");

            var value = content.LoadAsync<string>("fixture/card", default).GetAwaiter().GetResult();
            content.Release("fixture/card");

            Assert.That(value, Is.EqualTo("card-view"));
            Assert.That(content.ActiveLeaseCount, Is.Zero);
        }

        [Test]
        public void FakeLuaRuntime_RecordsOnlyLifecycleAndInvocationContracts()
        {
            var lua = new FakeLuaRuntime();

            lua.InitializeAsync(default).GetAwaiter().GetResult();
            lua.ExecuteAsync("ui.bootstrap", "start", default).GetAwaiter().GetResult();
            lua.ShutdownAsync(default).GetAwaiter().GetResult();

            Assert.That(lua.State, Is.EqualTo(LuaRuntimeState.Stopped));
            Assert.That(lua.Invocations, Is.EqualTo(new[] { "ui.bootstrap:start" }));
        }

        [Test]
        public void ManualClock_AdvancesWithoutWallClockTime()
        {
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);

            clock.Advance(TimeSpan.FromSeconds(5));

            Assert.That(clock.UtcNow, Is.EqualTo(DateTimeOffset.UnixEpoch.AddSeconds(5)));
        }
    }
}
