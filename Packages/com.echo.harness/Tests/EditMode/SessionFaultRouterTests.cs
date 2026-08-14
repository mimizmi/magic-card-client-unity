using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SessionFaultRouterTests
    {
        private FakeProtocolSession session;
        private RecordingSessionScheduler scheduler;
        private RecordingFaultLog log;
        private SessionFaultRouter router;

        [SetUp]
        public void SetUp()
        {
            session = new FakeProtocolSession();
            scheduler = new RecordingSessionScheduler();
            log = new RecordingFaultLog();
            router = new SessionFaultRouter(session, scheduler, log);
        }

        [TearDown]
        public void TearDown() => router.Dispose();

        private static SessionFault Fault(SessionFaultKind kind, MessageId id) =>
            new SessionFault(kind, id, $"{kind} on {id}");

        [Test]
        public void ATransportFailureIsLoggedAsAnError()
        {
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(log.Entries.Single().Severity, Is.EqualTo(FaultSeverity.Error));
        }

        [Test]
        public void ANoDestinationIsLoggedAsInformation()
        {
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));

            Assert.That(log.Entries.Single().Severity, Is.EqualTo(FaultSeverity.Info));
        }

        [Test]
        public void AMalformedPayloadIsLoggedAsAWarning()
        {
            session.PublishFault(Fault(SessionFaultKind.MalformedPayload, MessageId.LoginResponse));

            Assert.That(log.Entries.Single().Severity, Is.EqualTo(FaultSeverity.Warning));
        }

        // The session publishes one NoDestination per unrouted message and that
        // contract is not weakened. The volume is handled here instead.
        [Test]
        public void NoDestinationIsLoggedOnlyOncePerMessageId()
        {
            for (var i = 0; i < 5; i++)
            {
                session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));
            }

            Assert.That(log.Entries.Count, Is.EqualTo(1));
        }

        [Test]
        public void ADifferentUnroutedMessageIsStillReportedOnce()
        {
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginRequest));

            Assert.That(log.Entries.Count, Is.EqualTo(2));
        }

        // De-duplication must not leak into the kinds that are never noisy.
        [Test]
        public void ARepeatedTransportFailureIsLoggedEveryTime()
        {
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(log.Entries.Count, Is.EqualTo(2));
        }

        [Test]
        public void OnlyConnectionFaultsReachAConnectionObserver()
        {
            var seen = new List<SessionFaultKind>();
            using var subscription = router.ObserveConnectionFaults(f => seen.Add(f.Kind));

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.DispatchFailure, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));
            session.PublishFault(Fault(SessionFaultKind.MalformedPayload, MessageId.LoginResponse));
            session.PublishFault(Fault(SessionFaultKind.UnknownMessageId, MessageId.LoginResponse));

            Assert.That(seen, Is.EquivalentTo(new[]
            {
                SessionFaultKind.TransportFailure,
                SessionFaultKind.DispatchFailure,
            }));
        }

        // The whole point of the design: the log does not wait for a hop, and the
        // UI does. Deleting the hop makes the second assertion fail.
        [Test]
        public void TheLogTakesNoHopAndTheObserverTakesOne()
        {
            using var subscription = router.ObserveConnectionFaults(_ => { });

            session.PublishFault(Fault(SessionFaultKind.MalformedPayload, MessageId.LoginResponse));
            Assert.That(scheduler.SwitchCount, Is.Zero, "Logging must not hop.");

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));
            Assert.That(scheduler.SwitchCount, Is.EqualTo(1), "UI delivery must hop.");
        }

        [Test]
        public void TheLogIsWrittenOnThePublishingThread()
        {
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(
                log.Entries.Single().ThreadId,
                Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
        }

        [Test]
        public void AThrowingObserverDoesNotStopTheOthers()
        {
            var reached = false;
            using var first = router.ObserveConnectionFaults(_ => throw new InvalidOperationException());
            using var second = router.ObserveConnectionFaults(_ => reached = true);

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(reached, Is.True);
        }

        // ProtocolSession.PublishFault swallows what a handler throws, so a failure
        // in here has nowhere to go unless the router reports it itself.
        [Test]
        public void AFailingHopIsReportedRatherThanLost()
        {
            using var subscription = router.ObserveConnectionFaults(_ => { });
            scheduler.NextFailure = new InvalidOperationException("the loop is gone");

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(
                log.Entries.Any(e => e.Fault.Kind == SessionFaultKind.SubscriberFailure),
                Is.True,
                "A delivery that never reached the UI must still leave a trace.");
        }

        [Test]
        public void DisposeStopsRouting()
        {
            router.Dispose();

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(log.Entries, Is.Empty);
        }
    }
}
