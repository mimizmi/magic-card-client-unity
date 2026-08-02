using System;
using System.Collections.Generic;
using System.IO;
using System.Threading;
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

            // The subscription is part of the scenario, not scaffolding. A frame
            // that decodes cleanly and then has nowhere to go publishes
            // NoDestination, which is a real failure - the caller subscribed too
            // late - rather than a counterexample to the claim below. Giving the
            // message a destination is what keeps this test about decoding.
            LoginResponseDto delivered = null;
            session.Subscribe<LoginResponseDto>(MessageId.LoginResponse, dto => delivered = dto);

            transport.EnqueueInbound(Frame(
                MessageId.LoginResponse,
                "{\"success\":true,\"player_id\":\"p-1\",\"reconnect_token\":\"t-1\"}"));

            Assert.That(delivered, Is.Not.Null);
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

        [Test]
        public void StopAsyncFailsWaitersEvenWhenDisconnectThrows()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            transport.FailNextDisconnect(new IOException("socket already gone"));

            Assert.Throws<IOException>(
                () => session.StopAsync(CancellationToken.None).GetAwaiter().GetResult());

            var failure = Assert.Throws<InvalidOperationException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("stopped before the response"));
        }

        [Test]
        public void StopAsyncFailsWaitersWhenHandedAnAlreadyCancelledToken()
        {
            var session = CreateSession(out _, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => session.StopAsync(cancelled.Token).GetAwaiter().GetResult());
            // The message check is load-bearing, not decoration. A stranded waiter
            // leaves this UniTask incomplete, and GetResult on an incomplete
            // UniTask throws InvalidOperationException("Not yet completed, UniTask
            // only allow to use await.") - so the bare Assert.Throws form is
            // satisfied by the very symptom of the bug and passes against the
            // unfixed StopAsync. Only the message distinguishes a waiter that was
            // failed on purpose from one that was abandoned.
            var failure = Assert.Throws<InvalidOperationException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("stopped before the response"));
        }

        [Test]
        public void DisposeRequestsATransportDisconnect()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            session.Dispose();

            Assert.That(transport.DisconnectCount, Is.EqualTo(1),
                "An undisconnected socket leaves the server holding a ghost session " +
                "until its 35 second pong timeout.");
        }

        [Test]
        public void DisposeSurvivesAThrowingDisconnect()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            transport.FailNextDisconnect(new IOException("socket already gone"));

            Assert.DoesNotThrow(() => session.Dispose());
        }

        [Test]
        public void DisposeOnANeverStartedSessionDoesNotTouchTheTransport()
        {
            var session = CreateSession(out var transport, out _);

            session.Dispose();

            Assert.That(transport.DisconnectCount, Is.EqualTo(0),
                "There is nothing to close, and calling DisconnectAsync on an " +
                "unconnected transport is not universally safe.");
        }

        [Test]
        public void StopAsyncFromFaultedReachesDisconnectedWithoutASecondDisconnect()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            transport.FailNextReceive(new IOException("stream desynchronized"));
            Assert.That(session.State, Is.EqualTo(SessionState.Faulted));
            var disconnectsAfterFault = transport.DisconnectCount;

            session.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(transport.DisconnectCount, Is.EqualTo(disconnectsAfterFault),
                "The fault path already disconnected, and a second close is not " +
                "idempotent on every real transport.");
        }

        [Test]
        public void AFaultedSessionCanBeStoppedAndStartedAgain()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            transport.FailNextReceive(new IOException("stream desynchronized"));
            session.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.DoesNotThrow(
                () => session.StartAsync(CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "This is the seam reconnect will use next iteration.");
        }
    }
}
