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
        private static (LoginUseCase UseCase, FakeProtocolSession Session, CurrentPlayer Player)
            Build()
        {
            var session = new FakeProtocolSession();
            var player = new CurrentPlayer();
            return (new LoginUseCase(session, player), session, player);
        }

        [Test]
        public void ConstructingWithoutASessionThrows()
        {
            Assert.Throws<ArgumentNullException>(
                () => new LoginUseCase(null, new CurrentPlayer()));
        }

        [Test]
        public void ConstructingWithoutACurrentPlayerThrows()
        {
            Assert.Throws<ArgumentNullException>(
                () => new LoginUseCase(new FakeProtocolSession(), null));
        }

        [Test]
        public void ASuccessfulResponseBecomesSucceededWithThePlayerId()
        {
            var (useCase, session, _) = Build();
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
            var (useCase, session, _) = Build();
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
            var (useCase, session, _) = Build();
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
            var (useCase, session, _) = Build();
            session.NextRequestFailure = new TimeoutException("no LoginResponse");

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.NoAnswer));
        }

        [Test]
        public void ASecondLoginInFlightBecomesNoAnswerRatherThanEscaping()
        {
            var (useCase, session, _) = Build();
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
            var (useCase, session, _) = Build();
            session.NextRequestFailure = new OperationCanceledException();

            Assert.Throws<OperationCanceledException>(() =>
                useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult());
        }

        // A broken transport must not be dressed up as a clean refusal.
        [Test]
        public void AnUnexpectedFailureEscapes()
        {
            var (useCase, session, _) = Build();
            session.NextRequestFailure = new InvalidOperationException("the stream desynchronized");

            Assert.Throws<InvalidOperationException>(() =>
                useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult());
        }

        [Test]
        public void ABlankPlayerNameIsRefusedWithoutTouchingTheSession()
        {
            var (useCase, session, _) = Build();

            var outcome = useCase
                .LoginAsync("   ", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Rejected));
            Assert.That(session.RequestCount, Is.Zero);
        }

        // The player id has two readers now, and they are not the same claim. The
        // outcome's is what the login screen renders once; CurrentPlayer's is what
        // the queue path reads later, when the outcome is long gone.
        [Test]
        public void ASuccessfulLoginRecordsThePlayerForLaterCallers()
        {
            var (useCase, session, player) = Build();
            session.NextResponse = new LoginResponseDto { Success = true, PlayerId = "player-7" };

            useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(player.IsLoggedIn, Is.True);
            Assert.That(player.PlayerId, Is.EqualTo("player-7"));
        }

        [Test]
        public void ARefusedLoginLeavesTheCurrentPlayerUnset()
        {
            var (useCase, session, player) = Build();
            session.NextResponse = new LoginResponseDto
            {
                Success = false,
                Error = "name already taken",
            };

            useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(player.IsLoggedIn, Is.False);
            Assert.That(player.PlayerId, Is.Null);
        }

        // A success carrying no player id is a decode failure wearing a success
        // flag. Recording it would make IsLoggedIn true with nothing to identify,
        // and the queue's wire field would then carry the empty string as though
        // that were the answer.
        [Test]
        public void ASuccessWithNoPlayerIdDoesNotClaimTheClientIsLoggedIn()
        {
            var (useCase, session, player) = Build();
            session.NextResponse = new LoginResponseDto { Success = true, PlayerId = string.Empty };

            var outcome = useCase.LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Succeeded),
                "The outcome still reports what the server said; only the recorded " +
                "identity is withheld.");
            Assert.That(player.IsLoggedIn, Is.False);
        }

        // The reconnect token is never read from the response at all - "dropped"
        // would imply it was looked at first. This is a structural guard rather
        // than a behaviour test: it pins that LoginOutcome carries no property
        // with "Token" in its name, so it never touches LoginUseCase itself and
        // would pass just as well if a static field or a log line held the
        // token somewhere. The failure it actually prevents is a future
        // "helpful" addition of the field to LoginOutcome, which no behavioural
        // test would notice.
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
