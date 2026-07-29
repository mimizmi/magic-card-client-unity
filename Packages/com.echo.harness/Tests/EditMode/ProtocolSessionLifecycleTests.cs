using System;
using System.Collections.Generic;
using System.IO;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;
using static Echo.Harness.Tests.EditMode.ProtocolTestFrames;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionLifecycleTests
    {
        private static ProtocolSession NewSession(FakeTransport transport) =>
            new ProtocolSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

        [Test]
        public void StartAsync_ConnectsTheTransportAndReportsConnected()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);

            session.StartAsync(default).GetAwaiter().GetResult();

            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
            Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
        }

        [Test]
        public void StartAsync_RejectsASecondStart()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            session.StartAsync(default).GetAwaiter().GetResult();

            Assert.Throws<InvalidOperationException>(
                () => session.StartAsync(default).GetAwaiter().GetResult());
        }

        [Test]
        public void StopAsync_FromDisconnectedIsANoOp()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);

            Assert.DoesNotThrow(() => session.StopAsync(default).GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }

        [Test]
        public void Pump_PublishesAFaultForAnUnknownMessageId()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));

            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.UnknownMessageId));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "An unknown id is version drift, not a reason to drop the player.");
        }

        [Test]
        public void Pump_PublishesAFaultForAMalformedPayloadAndKeepsRunning()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{not json"));
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{not json either"));

            Assert.That(faults, Has.Count.EqualTo(2));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.MalformedPayload));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
        }

        [Test]
        public void Pump_TreatsAReceiveFailureAsStreamDesynchronization()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.FailNextReceive(new IOException("length prefix out of range"));

            Assert.That(session.State, Is.EqualTo(SessionState.Faulted));
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.TransportFailure));
        }

        [Test]
        public void FaultSubscription_StopsDeliveringAfterDisposal()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            var subscription = session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            subscription.Dispose();
            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));

            Assert.That(faults, Is.Empty);
        }
    }
}
