using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    /// <summary>
    /// The exception-policy table again, for the queue. It is the same table
    /// <c>LoginUseCaseTests</c> pins and it is asserted separately rather than
    /// shared, because the policy is a decision each use case makes rather than a
    /// base class it inherits - a future one that swallowed a cancellation would
    /// go unnoticed by a shared fixture.
    /// </summary>
    public sealed class QueueUseCaseTests
    {
        private static (QueueUseCase UseCase, FakeProtocolSession Session) Build()
        {
            var session = new FakeProtocolSession();
            return (new QueueUseCase(session), session);
        }

        [Test]
        public void ConstructingWithoutASessionThrows()
        {
            Assert.Throws<ArgumentNullException>(() => new QueueUseCase(null));
        }

        [Test]
        public void ASuccessfulResponseBecomesJoined()
        {
            var (useCase, session) = Build();
            session.NextResponse = new JoinQueueResponseDto { Success = true };

            var outcome = useCase
                .JoinAsync("player-7", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(QueueResult.Joined));
            Assert.That(outcome.Message, Is.Null);
            Assert.That(session.LastRequestId, Is.EqualTo(MessageId.JoinQueueRequest));
        }

        // The server ignores this field entirely today - it identifies the player
        // by the TCP session. It is asserted anyway because the DTO declares it and
        // shipping a blank one when the client knows the value would be a lie on
        // the wire; see QueueUseCase.JoinAsync.
        [Test]
        public void ThePlayerIdIsCarriedOnTheWireEvenThoughTheServerIgnoresIt()
        {
            var (useCase, session) = Build();
            session.NextResponse = new JoinQueueResponseDto { Success = true };

            useCase.JoinAsync("player-7", CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(
                ((JoinQueueRequestDto)session.LastRequestPayload).PlayerId,
                Is.EqualTo("player-7"));
        }

        // Not refused client-side, deliberately: the server does not read the
        // field, so refusing here would invent a requirement the protocol does not
        // have. Contrast LoginUseCase, which does refuse a blank player NAME -
        // there the server genuinely rejects it.
        [Test]
        public void ABlankPlayerIdStillReachesTheServerAsAnEmptyString()
        {
            var (useCase, session) = Build();
            session.NextResponse = new JoinQueueResponseDto { Success = true };

            var outcome = useCase
                .JoinAsync(null, CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(QueueResult.Joined));
            Assert.That(session.RequestCount, Is.EqualTo(1));
            Assert.That(
                ((JoinQueueRequestDto)session.LastRequestPayload).PlayerId,
                Is.Empty,
                "Null must not reach Newtonsoft as a null property; the Go struct " +
                "declares a string, not a pointer.");
        }

        [Test]
        public void AnUnsuccessfulResponseBecomesRejectedCarryingTheServersReason()
        {
            var (useCase, session) = Build();
            session.NextResponse = new JoinQueueResponseDto
            {
                Success = false,
                Error = "already in a game",
            };

            var outcome = useCase
                .JoinAsync("player-7", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(QueueResult.Rejected));
            Assert.That(outcome.Message, Is.EqualTo("already in a game"));
        }

        [Test]
        public void ARefusalWithNoReasonStillSaysSomething()
        {
            var (useCase, session) = Build();
            session.NextResponse = new JoinQueueResponseDto { Success = false, Error = null };

            var outcome = useCase
                .JoinAsync("player-7", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(QueueResult.Rejected));
            Assert.That(outcome.Message, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ATimeoutBecomesNoAnswerRatherThanEscaping()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new TimeoutException("no JoinQueueResponse");

            var outcome = useCase
                .JoinAsync("player-7", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(QueueResult.NoAnswer));
        }

        [Test]
        public void ASecondJoinInFlightBecomesNoAnswerRatherThanEscaping()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new RequestAlreadyInFlightException(
                MessageId.JoinQueueResponse, "already in flight");

            var outcome = useCase
                .JoinAsync("player-7", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(QueueResult.NoAnswer));
        }

        [Test]
        public void ACancellationEscapesRatherThanBecomingAnOutcome()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new OperationCanceledException();

            Assert.Throws<OperationCanceledException>(() =>
                useCase.JoinAsync("player-7", CancellationToken.None).GetAwaiter().GetResult());
        }

        [Test]
        public void AnUnexpectedFailureEscapes()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure =
                new InvalidOperationException("the stream desynchronized");

            Assert.Throws<InvalidOperationException>(() =>
                useCase.JoinAsync("player-7", CancellationToken.None).GetAwaiter().GetResult());
        }

        // ── Leaving ───────────────────────────────────────────────────────────

        /// <summary>
        /// The null payload is the assertion, not an incidental detail. 2003 is
        /// payload-shape "none" and <c>ProtocolSession.SendAsync</c> throws
        /// <c>ArgumentException</c> for a non-null payload on exactly that set, so
        /// a well-meant future edit that attached a DTO would fail before a byte
        /// left - and against the real session, not this fake.
        /// </summary>
        [Test]
        public void LeavingSendsTheBodylessRequest()
        {
            var (useCase, session) = Build();

            useCase.LeaveAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(session.SentMessages.Count, Is.EqualTo(1));
            Assert.That(session.SentMessages[0].MessageId,
                Is.EqualTo(MessageId.LeaveQueueRequest));
            Assert.That(session.SentMessages[0].Payload, Is.Null);
        }

        // Leaving is a send, not a request. If it ever consumed a response slot the
        // single-flight gate would refuse a concurrent join for a reply that is
        // never coming.
        [Test]
        public void LeavingIssuesNoRequestAndSoWaitsForNothing()
        {
            var (useCase, session) = Build();

            useCase.LeaveAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(session.RequestCount, Is.Zero);
        }

        // There is no outcome to convert a failure into, so the policy's remaining
        // half applies: it escapes.
        [Test]
        public void AFailedLeaveSendEscapes()
        {
            var (useCase, session) = Build();
            session.NextSendFailure = new InvalidOperationException("not connected");

            Assert.Throws<InvalidOperationException>(() =>
                useCase.LeaveAsync(CancellationToken.None).GetAwaiter().GetResult());
        }
    }
}
