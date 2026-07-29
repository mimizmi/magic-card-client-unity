using System;
using System.Text;
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
