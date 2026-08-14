using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class LoginUseCaseTests
    {
        private static (LoginUseCase UseCase, FakeProtocolSession Session) Build()
        {
            var session = new FakeProtocolSession();
            return (new LoginUseCase(session), session);
        }

        [Test]
        public void ASuccessfulResponseBecomesSucceededWithThePlayerId()
        {
            var (useCase, session) = Build();
            session.NextResponse = new LoginResponseDto
            {
                Success = true,
                PlayerId = "player-7",
                InGame = true,
            };

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Succeeded));
            Assert.That(outcome.PlayerId, Is.EqualTo("player-7"));
            Assert.That(outcome.InGame, Is.True);
            Assert.That(session.LastRequestId, Is.EqualTo(MessageId.LoginRequest));
            Assert.That(
                ((LoginRequestDto)session.LastRequestPayload).PlayerName,
                Is.EqualTo("ada"));
        }

        [Test]
        public void AnUnsuccessfulResponseBecomesRejectedCarryingTheServersReason()
        {
            var (useCase, session) = Build();
            session.NextResponse = new LoginResponseDto
            {
                Success = false,
                Error = "name already taken",
            };

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Rejected));
            Assert.That(outcome.Message, Is.EqualTo("name already taken"));
        }

        [Test]
        public void ARefusalWithNoReasonStillSaysSomething()
        {
            var (useCase, session) = Build();
            session.NextResponse = new LoginResponseDto { Success = false, Error = null };

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Rejected));
            Assert.That(outcome.Message, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ATimeoutBecomesNoAnswerRatherThanEscaping()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new TimeoutException("no LoginResponse");

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.NoAnswer));
        }

        [Test]
        public void ASecondLoginInFlightBecomesNoAnswerRatherThanEscaping()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new RequestAlreadyInFlightException(
                MessageId.LoginResponse, "already in flight");

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.NoAnswer));
        }

        // Shutdown cancellation is not a login result. Swallowing it would report
        // quitting the game as a failed login.
        [Test]
        public void ACancellationEscapesRatherThanBecomingAnOutcome()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new OperationCanceledException();

            Assert.Throws<OperationCanceledException>(() =>
                useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult());
        }

        // A broken transport must not be dressed up as a clean refusal.
        [Test]
        public void AnUnexpectedFailureEscapes()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new InvalidOperationException("the stream desynchronized");

            Assert.Throws<InvalidOperationException>(() =>
                useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult());
        }

        [Test]
        public void ABlankPlayerNameIsRefusedWithoutTouchingTheSession()
        {
            var (useCase, session) = Build();

            var outcome = useCase
                .LoginAsync("   ", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Rejected));
            Assert.That(session.RequestCount, Is.Zero);
        }

        // The reconnect token is read from the response and dropped. This is a
        // structural guard rather than a behaviour test: the failure it prevents is
        // a future "helpful" addition of the field, which no behavioural test would
        // notice.
        [Test]
        public void TheReconnectTokenNeverLeavesTheUseCase()
        {
            var names = typeof(LoginOutcome).GetProperties().Select(p => p.Name).ToArray();

            Assert.That(
                names.Any(name => name.IndexOf("Token", StringComparison.OrdinalIgnoreCase) >= 0),
                Is.False,
                "LoginOutcome must not carry the reconnect token. Persisting it is a " +
                "separate, tracked piece of work; see docs/migration-checklist.md.");
        }
    }
}
