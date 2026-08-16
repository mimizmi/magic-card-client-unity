using System;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Presentation;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class QueueViewModelTests
    {
        private FakeSessionStatus status;
        private CurrentPlayer player;
        private FakeQueueUseCase queue;
        private FakeProtocolSession session;
        private MatchFoundWatcher watcher;
        private QueueViewModel viewModel;

        [SetUp]
        public void SetUp()
        {
            status = new FakeSessionStatus();
            player = new CurrentPlayer();
            queue = new FakeQueueUseCase();
            session = new FakeProtocolSession();
            watcher = new MatchFoundWatcher(session);
            viewModel = new QueueViewModel(status, player, queue, watcher);
        }

        [TearDown]
        public void TearDown()
        {
            viewModel.Dispose();
            watcher.Dispose();
        }

        /// <summary>The ordinary starting point: connected and logged in.</summary>
        private void Ready()
        {
            status.State = SessionState.Connected;
            player.RecordLogin("player-7");
            viewModel.Refresh();
        }

        private void ServerFindsAMatch(
            string gameId = "room-4",
            int seat = 1,
            string opponent = "ada") =>
            session.PublishToSubscribers(MessageId.MatchFoundEvent, new MatchFoundEventDto
            {
                GameId = gameId,
                YourSeat = seat,
                OpponentName = opponent,
            });

        // ── Construction ──────────────────────────────────────────────────────

        [Test]
        public void EveryDependencyIsRequired()
        {
            Assert.Throws<ArgumentNullException>(
                () => new QueueViewModel(null, player, queue, watcher));
            Assert.Throws<ArgumentNullException>(
                () => new QueueViewModel(status, null, queue, watcher));
            Assert.Throws<ArgumentNullException>(
                () => new QueueViewModel(status, player, null, watcher));
            Assert.Throws<ArgumentNullException>(
                () => new QueueViewModel(status, player, queue, null));
        }

        // ── The join gate ─────────────────────────────────────────────────────

        [Test]
        public void JoiningIsRefusedUntilTheSessionIsConnected()
        {
            player.RecordLogin("player-7");
            status.State = SessionState.Connecting;
            viewModel.Refresh();

            Assert.That(viewModel.CanJoin, Is.False);
        }

        // The server answers "not logged in" to a queue request from an
        // unauthenticated session, so the button would be a trap.
        [Test]
        public void JoiningIsRefusedBeforeLogin()
        {
            status.State = SessionState.Connected;
            viewModel.Refresh();

            Assert.That(viewModel.CanJoin, Is.False);
        }

        [Test]
        public void JoiningIsAllowedOnceConnectedAndLoggedIn()
        {
            Ready();

            Assert.That(viewModel.CanJoin, Is.True);
        }

        [Test]
        public void LeavingIsRefusedWhileNotQueued()
        {
            Ready();

            Assert.That(viewModel.CanLeave, Is.False);
        }

        [Test]
        public void RefreshRaisesNothingWhenNothingChanged()
        {
            Ready();

            var raised = 0;
            viewModel.propertyChanged += (_, __) => raised++;
            viewModel.Refresh();

            Assert.That(raised, Is.Zero);
        }

        // ── Joining ───────────────────────────────────────────────────────────

        [Test]
        public void ASuccessfulJoinEntersTheQueueAndSwapsWhichButtonIsLive()
        {
            Ready();
            queue.NextOutcome = QueueOutcome.Joined();

            viewModel.JoinAsync().GetAwaiter().GetResult();

            Assert.That(viewModel.IsQueued, Is.True);
            Assert.That(viewModel.QueueStatusText, Does.Contain("Searching"));
            Assert.That(viewModel.CanJoin, Is.False, "Already queued.");
            Assert.That(viewModel.CanLeave, Is.True);
            Assert.That(viewModel.IsBusy, Is.False);
        }

        // The field the server ignores today but the DTO declares; see
        // QueueUseCase.JoinAsync for why it is populated at all.
        [Test]
        public void TheJoinCarriesTheRecordedPlayerId()
        {
            Ready();

            viewModel.JoinAsync().GetAwaiter().GetResult();

            Assert.That(queue.LastPlayerId, Is.EqualTo("player-7"));
        }

        [Test]
        public void ARefusalShowsTheServersReasonAndLeavesTheQueueUnjoined()
        {
            Ready();
            queue.NextOutcome = QueueOutcome.Refusal("already in a game");

            viewModel.JoinAsync().GetAwaiter().GetResult();

            Assert.That(viewModel.IsQueued, Is.False);
            Assert.That(viewModel.QueueStatusText, Does.Contain("already in a game"));
            Assert.That(viewModel.CanJoin, Is.True, "A refusal must leave a retry possible.");
        }

        [Test]
        public void ANoAnswerShowsWhatHappenedAndLeavesTheQueueUnjoined()
        {
            Ready();
            queue.NextOutcome = QueueOutcome.NoReply("The server did not answer within 10s.");

            viewModel.JoinAsync().GetAwaiter().GetResult();

            Assert.That(viewModel.IsQueued, Is.False);
            Assert.That(viewModel.QueueStatusText, Does.Contain("No answer"));
        }

        // QueueUseCase deliberately lets a broken transport escape. Without the
        // catch-all the exception reaches UniTask's unobserved handler and the
        // button appears to do nothing at all.
        [Test]
        public void AnEscapingFailureBecomesVisibleTextRatherThanSilence()
        {
            Ready();
            queue.NextJoinFailure = new InvalidOperationException("the stream desynchronized");

            Assert.DoesNotThrow(() => viewModel.JoinAsync().GetAwaiter().GetResult());

            Assert.That(viewModel.QueueStatusText, Does.Contain("desynchronized"));
            Assert.That(viewModel.IsBusy, Is.False, "A failed join must release the button.");
            Assert.That(viewModel.IsQueued, Is.False);
        }

        [Test]
        public void ASecondJoinWhileAlreadyQueuedSendsNothing()
        {
            Ready();
            viewModel.JoinAsync().GetAwaiter().GetResult();

            viewModel.JoinAsync().GetAwaiter().GetResult();

            Assert.That(queue.JoinCount, Is.EqualTo(1));
        }

        // Matched is a separate guard from queued, because a match clears the
        // queued flag - without it, the state the server is most certain to refuse
        // would be the one state that could still fire a request.
        [Test]
        public void AJoinAfterAMatchSendsNothing()
        {
            Ready();
            ServerFindsAMatch();

            viewModel.JoinAsync().GetAwaiter().GetResult();

            Assert.That(queue.JoinCount, Is.Zero);
        }

        // ── Leaving ───────────────────────────────────────────────────────────

        /// <summary>
        /// Optimistic by necessity: 2003 has no reply, so this is the only thing
        /// leaving can mean. See QueueViewModel.LeaveAsync.
        /// </summary>
        [Test]
        public void LeavingClearsTheQueuedStateOnTheStrengthOfTheSendAlone()
        {
            Ready();
            viewModel.JoinAsync().GetAwaiter().GetResult();

            viewModel.LeaveAsync().GetAwaiter().GetResult();

            Assert.That(queue.LeaveCount, Is.EqualTo(1));
            Assert.That(viewModel.IsQueued, Is.False);
            Assert.That(viewModel.QueueStatusText, Is.EqualTo("Not in queue."));
            Assert.That(viewModel.CanJoin, Is.True);
        }

        [Test]
        public void LeavingWhenNotQueuedSendsNothing()
        {
            Ready();

            viewModel.LeaveAsync().GetAwaiter().GetResult();

            Assert.That(queue.LeaveCount, Is.Zero);
        }

        // The state is left alone deliberately: the request may or may not have
        // reached the server, so claiming either answer would be a guess.
        [Test]
        public void AFailedLeaveSaysSoAndDoesNotPretendTheQueueWasLeft()
        {
            Ready();
            viewModel.JoinAsync().GetAwaiter().GetResult();
            queue.NextLeaveFailure = new InvalidOperationException("not connected");

            Assert.DoesNotThrow(() => viewModel.LeaveAsync().GetAwaiter().GetResult());

            Assert.That(viewModel.QueueStatusText, Does.Contain("not connected"));
            Assert.That(viewModel.IsQueued, Is.True);
        }

        // ── The match ─────────────────────────────────────────────────────────

        [Test]
        public void AMatchIsReportedWithItsOpponentSeatAndGame()
        {
            Ready();
            viewModel.JoinAsync().GetAwaiter().GetResult();

            ServerFindsAMatch("room-4", 1, "ada");

            Assert.That(viewModel.IsMatched, Is.True);
            Assert.That(viewModel.MatchText, Does.Contain("ada"));
            Assert.That(viewModel.MatchText, Does.Contain("seat 1"));
            Assert.That(viewModel.MatchText, Does.Contain("room-4"));
        }

        [Test]
        public void AMatchEndsTheQueuedStateAndClosesBothButtons()
        {
            Ready();
            viewModel.JoinAsync().GetAwaiter().GetResult();

            ServerFindsAMatch();

            Assert.That(viewModel.IsQueued, Is.False);
            Assert.That(viewModel.CanLeave, Is.False, "There is nothing left to cancel.");
            Assert.That(viewModel.CanJoin, Is.False,
                "The server would refuse: already in a game.");
        }

        /// <summary>
        /// <b>The race this whole slice is shaped around.</b> The server pairs
        /// players under its queue mutex the instant a second one enqueues, so a
        /// MatchFoundEvent can already be on the wire when the player presses
        /// cancel. The match wins. A screen that treated leaving as final would
        /// strand the player in a lobby the server has already moved them out of.
        /// </summary>
        [Test]
        public void AMatchArrivingAfterTheCancelStillWins()
        {
            Ready();
            viewModel.JoinAsync().GetAwaiter().GetResult();
            viewModel.LeaveAsync().GetAwaiter().GetResult();

            ServerFindsAMatch("room-4", 0, "ada");

            Assert.That(viewModel.IsMatched, Is.True);
            Assert.That(viewModel.MatchText, Does.Contain("ada"));
        }

        /// <summary>
        /// The other half of the same race, and the one that needs a parked join to
        /// reach at all: the match lands <i>while the JoinQueueRequest is still in
        /// flight</i>. The server writes the MatchFoundEvent independently of the
        /// response, so this ordering is ordinary rather than exotic. Reporting
        /// "searching for an opponent" when the answer finally arrives would
        /// overwrite the truth with a stale intention.
        ///
        /// <para>Not awaited until the end, deliberately. The join parks, so
        /// blocking on it before the match is published would deadlock the test
        /// rather than exercise the race.</para>
        /// </summary>
        [Test]
        public void AMatchDuringTheJoinIsNotOverwrittenByTheJoinsOwnAnswer()
        {
            Ready();
            queue.ParkNextJoin();
            queue.NextOutcome = QueueOutcome.Joined();

            var join = viewModel.JoinAsync();
            Assert.That(viewModel.IsBusy, Is.True, "The join must genuinely be in flight.");

            ServerFindsAMatch("room-4", 0, "ada");
            queue.CompleteParkedJoin();
            join.GetAwaiter().GetResult();

            Assert.That(viewModel.IsMatched, Is.True);
            Assert.That(viewModel.IsQueued, Is.False,
                "The join answered 'Joined', but the match already superseded it.");
            Assert.That(viewModel.QueueStatusText, Does.Not.Contain("Searching"));
        }

        /// <summary>
        /// The reconnect path, in miniature: the server sends LoginResponse and
        /// MatchFoundEvent back to back, so the match can land before any screen
        /// exists. The watcher's replay is what makes a view-model built afterwards
        /// still learn about it.
        /// </summary>
        [Test]
        public void AViewModelBuiltAfterTheMatchIsAlreadyMatched()
        {
            ServerFindsAMatch("room-9", 1, "carol");

            using var late = new QueueViewModel(status, player, queue, watcher);

            Assert.That(late.IsMatched, Is.True);
            Assert.That(late.MatchText, Does.Contain("room-9"));
        }

        [Test]
        public void DisposingStopsMatchDelivery()
        {
            Ready();
            viewModel.Dispose();

            ServerFindsAMatch();

            Assert.That(viewModel.IsMatched, Is.False);
        }
    }
}
