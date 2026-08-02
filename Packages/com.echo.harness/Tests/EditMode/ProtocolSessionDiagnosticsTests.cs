using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionDiagnosticsTests
    {
        private static ProtocolSession CreateStarted(
            out FakeTransport transport,
            out List<SessionFault> faults)
        {
            transport = new FakeTransport();
            var session = new ProtocolSession(
                transport,
                new ManualTime(DateTimeOffset.UnixEpoch),
                new RecordingSessionScheduler());
            faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void ASecondInFlightRequestThrowsItsOwnType()
        {
            using var session = CreateStarted(out _, out _);
            var payload = new LoginRequestDto { PlayerName = "redacted" };

            // Preserved rather than forgotten. Nothing will ever answer this
            // request, so disposal fails it - and Forget() routes that failure to
            // UniTaskScheduler.PublishUnobservedTaskException, which Unity logs as
            // an unhandled exception and NUnit fails the test on. Holding it lets
            // the disposal be observed below instead.
            var first = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, payload, TimeSpan.FromSeconds(5),
                CancellationToken.None).Preserve();

            var failure = Assert.Throws<RequestAlreadyInFlightException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest, payload, TimeSpan.FromSeconds(5),
                    CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(failure.ResponseId, Is.EqualTo(MessageId.LoginResponse));

            // Cleanup, not the subject - RequestAsync_FailsPendingRequestsWhenThe
            // SessionIsDisposed is what pins that behaviour. Disposing explicitly
            // is what makes the first request observable at all, and Dispose is
            // idempotent, so the using above still runs harmlessly.
            session.Dispose();
            Assert.Throws<ObjectDisposedException>(() => first.GetAwaiter().GetResult());
        }

        [Test]
        public void AStaleRoundTripEchoThrowsItsOwnType()
        {
            using var session = CreateStarted(out var transport, out var faults);

            var probe = session.ProbeRoundTripAsync(CancellationToken.None);
            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.ClientPingResponse, "{\"ts\":999}"));

            var failure = Assert.Throws<CorrelationMismatchException>(
                () => probe.GetAwaiter().GetResult());
            Assert.That(failure.MessageId, Is.EqualTo(MessageId.ClientPingResponse));
            Assert.That(failure.Message, Does.Contain("999"));
            Assert.That(
                faults.Single(f => f.Kind == SessionFaultKind.CorrelationMismatch).MessageId,
                Is.EqualTo(MessageId.ClientPingResponse));
        }

        [Test]
        public void ADecodeFailureOnAPendingResponseIdAnswersTheWaiter()
        {
            using var session = CreateStarted(out var transport, out var faults);

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.LoginResponse, "{not json"));

            var failure = Assert.Throws<InvalidOperationException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure, Is.Not.InstanceOf<TimeoutException>(),
                "Stalling the full timeout for a reply that already arrived and " +
                "failed to parse reports a network problem that did not happen.");

            // The message, not merely the type. A waiter left pending is exactly
            // what this test exists to catch, and GetResult on a pending UniTask
            // throws InvalidOperationException("Not yet completed, UniTask only
            // allow to use await.") - so the type assertion above, and the
            // TimeoutException exclusion with it, are both satisfied by the very
            // symptom of the bug. Only naming the answer proves one was given.
            Assert.That(failure.Message, Does.Contain("arrived but could not be decoded"));
            Assert.That(
                faults.Select(f => f.Kind),
                Does.Contain(SessionFaultKind.MalformedPayload));
        }

        [Test]
        public void AMessageWithNoSubscriberPublishesAFault()
        {
            using var session = CreateStarted(out var transport, out var faults);

            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}"));

            var fault = faults.Single();
            Assert.That(fault.Kind, Is.EqualTo(SessionFaultKind.NoDestination));
            Assert.That(fault.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "One undelivered message must not cost the connection.");
        }

        [Test]
        public void ADisposedSubscriptionLeavesNoSubscriberBehind()
        {
            using var session = CreateStarted(out var transport, out var faults);

            var subscription = session.Subscribe<PhaseChangeEventDto>(
                MessageId.PhaseChangeEvent, _ => { });
            subscription.Dispose();

            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}"));

            Assert.That(
                faults.Single().Kind,
                Is.EqualTo(SessionFaultKind.NoDestination),
                "Subscribe leaves an empty handler list behind, so a key check " +
                "alone would report a destination that is not there.");
        }

        [Test]
        public void TheStreamFaultReportsTheCauseBeforeTheSymptom()
        {
            using var session = CreateStarted(out var transport, out var faults);
            transport.FailNextDisconnect(new IOException("close failed"));

            transport.FailNextReceive(new IOException("stream desynchronized"));

            var transportFaults = faults
                .Where(f => f.Kind == SessionFaultKind.TransportFailure)
                .ToList();
            Assert.That(transportFaults.Count, Is.EqualTo(2));
            Assert.That(transportFaults[0].Diagnostic, Does.Contain("desynchronized"),
                "A consumer that reads the first TransportFailure - the natural " +
                "thing to do - must get the cause, not the close that followed it.");
            Assert.That(transportFaults[1].Diagnostic, Does.Contain("close failed"));
        }

        [Test]
        public void TheRoundTripProbeDeadlineIsPinned()
        {
            Assert.That(
                ProtocolSession.RoundTripProbeDeadline,
                Is.EqualTo(TimeSpan.FromSeconds(10)),
                "Raising this for a slow login would silently give every latency " +
                "probe the longer deadline, which is why it is named for the probe.");
        }
    }
}
