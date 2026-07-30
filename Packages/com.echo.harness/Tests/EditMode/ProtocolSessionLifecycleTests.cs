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
        private static ProtocolSession CreateSession(
            out FakeTransport transport,
            out RecordingSessionScheduler scheduler)
        {
            transport = new FakeTransport();
            scheduler = new RecordingSessionScheduler();
            return new ProtocolSession(
                transport, new ManualClock(DateTimeOffset.UnixEpoch), scheduler);
        }

        [Test]
        public void StartAsync_ConnectsTheTransportAndReportsConnected()
        {
            using var session = CreateSession(out var transport, out _);

            session.StartAsync(default).GetAwaiter().GetResult();

            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
            Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
        }

        [Test]
        public void StartAsync_RejectsASecondStart()
        {
            using var session = CreateSession(out _, out _);
            session.StartAsync(default).GetAwaiter().GetResult();

            Assert.Throws<InvalidOperationException>(
                () => session.StartAsync(default).GetAwaiter().GetResult());
        }

        [Test]
        public void StopAsync_FromDisconnectedIsANoOp()
        {
            using var session = CreateSession(out _, out _);

            Assert.DoesNotThrow(() => session.StopAsync(default).GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }

        [Test]
        public void Pump_PublishesAFaultForAnUnknownMessageId()
        {
            using var session = CreateSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));
            transport.EnqueueInbound(Frame((MessageId)9998, "{}"));

            Assert.That(faults, Has.Count.EqualTo(2),
                "The second fault proves the pump kept reading past the first unknown id.");
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.UnknownMessageId));
            Assert.That(faults[0].MessageId, Is.EqualTo((MessageId)9999));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "An unknown id is version drift, not a reason to drop the player.");
        }

        [Test]
        public void Pump_PublishesNoFaultForAWellFormedFrame()
        {
            using var session = CreateSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.EnqueueInbound(Frame(
                MessageId.LoginResponse,
                "{\"success\":true,\"player_id\":\"p-1\",\"reconnect_token\":\"t-1\"}"));

            Assert.That(faults, Is.Empty,
                "A fault means something failed; a message that decodes cleanly is not a failure.");
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
        }

        [Test]
        public void StopAsync_FromConnectedDisconnectsAndStopsThePump()
        {
            using var session = CreateSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            session.StopAsync(default).GetAwaiter().GetResult();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));

            // A frame that would otherwise fault proves the pump is no longer reading.
            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));
            Assert.That(faults, Is.Empty, "A stopped session must not dispatch anything.");
        }

        [Test]
        public void Dispose_RejectsFurtherUseAndSilencesFaultSubscriptions()
        {
            using var session = CreateSession(out var transport, out _);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            session.Dispose();

            Assert.Throws<ObjectDisposedException>(
                () => session.StartAsync(default).GetAwaiter().GetResult());
            Assert.Throws<ObjectDisposedException>(
                () => session.SubscribeToFaults(_ => { }));

            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));
            Assert.That(faults, Is.Empty,
                "Disposal must stop the pump and drop the handlers it would have called.");
        }

        [Test]
        public void Pump_PublishesAFaultForAMalformedPayloadAndKeepsRunning()
        {
            using var session = CreateSession(out var transport, out _);
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
            using var session = CreateSession(out var transport, out _);
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
            using var session = CreateSession(out var transport, out _);
            var faults = new List<SessionFault>();
            var subscription = session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            subscription.Dispose();
            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));

            Assert.That(faults, Is.Empty);
        }
    }
}
