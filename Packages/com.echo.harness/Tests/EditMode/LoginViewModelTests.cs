using System;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Presentation;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class LoginViewModelTests
    {
        private FakeSessionStatus status;
        private FakeLoginUseCase login;
        private FakeProtocolSession session;
        private RecordingSessionScheduler scheduler;
        private SessionFaultRouter router;
        private LoginViewModel viewModel;

        [SetUp]
        public void SetUp()
        {
            status = new FakeSessionStatus();
            login = new FakeLoginUseCase();
            session = new FakeProtocolSession();
            scheduler = new RecordingSessionScheduler();
            router = new SessionFaultRouter(session, scheduler, new RecordingFaultLog());
            viewModel = new LoginViewModel(status, login, router);
        }

        [TearDown]
        public void TearDown()
        {
            viewModel.Dispose();
            router.Dispose();
        }

        [Test]
        public void SubmitIsRefusedUntilTheSessionIsConnected()
        {
            viewModel.PlayerName = "ada";
            status.State = SessionState.Connecting;
            viewModel.Refresh();

            Assert.That(viewModel.CanSubmit, Is.False);
        }

        [Test]
        public void SubmitIsRefusedWithoutAPlayerName()
        {
            viewModel.PlayerName = "   ";
            status.State = SessionState.Connected;
            viewModel.Refresh();

            Assert.That(viewModel.CanSubmit, Is.False);
        }

        [Test]
        public void SubmitIsAllowedOnceConnectedAndNamed()
        {
            viewModel.PlayerName = "ada";
            status.State = SessionState.Connected;
            viewModel.Refresh();

            Assert.That(viewModel.CanSubmit, Is.True);
        }

        [Test]
        public void RefreshRaisesNothingWhenNothingChanged()
        {
            status.State = SessionState.Connected;
            viewModel.Refresh();

            var raised = 0;
            viewModel.propertyChanged += (_, __) => raised++;
            viewModel.Refresh();

            Assert.That(raised, Is.Zero);
        }

        [Test]
        public void RefreshRaisesWhenTheStateChanged()
        {
            viewModel.Refresh();

            var raised = 0;
            viewModel.propertyChanged += (_, __) => raised++;
            status.State = SessionState.Connected;
            viewModel.Refresh();

            Assert.That(raised, Is.GreaterThan(0));
        }

        [Test]
        public void ASuccessfulLoginShowsThePlayerId()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextOutcome = LoginOutcome.Success("player-7", false);

            viewModel.SubmitAsync().GetAwaiter().GetResult();

            Assert.That(viewModel.ResultText, Does.Contain("player-7"));
            Assert.That(viewModel.IsBusy, Is.False);
        }

        [Test]
        public void ARefusalShowsTheServersReason()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextOutcome = LoginOutcome.Refusal("name already taken");

            viewModel.SubmitAsync().GetAwaiter().GetResult();

            Assert.That(viewModel.ResultText, Does.Contain("name already taken"));
        }

        // LoginUseCase deliberately lets a broken transport escape. Without this
        // catch the exception reaches UniTask's unobserved handler and the button
        // appears to do nothing at all.
        [Test]
        public void AnEscapingFailureBecomesVisibleTextRatherThanSilence()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextFailure = new InvalidOperationException("the stream desynchronized");

            Assert.DoesNotThrow(() => viewModel.SubmitAsync().GetAwaiter().GetResult());

            Assert.That(viewModel.ResultText, Is.Not.Null.And.Not.Empty);
            Assert.That(viewModel.IsBusy, Is.False, "A failed submit must release the button.");
        }

        // A dropped connection must not erase the refusal the user is reading.
        [Test]
        public void AConnectionFaultDoesNotOverwriteTheLoginResult()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextOutcome = LoginOutcome.Refusal("name already taken");
            viewModel.SubmitAsync().GetAwaiter().GetResult();

            session.PublishFault(new SessionFault(
                SessionFaultKind.TransportFailure, MessageId.LoginRequest, "the link died"));

            Assert.That(viewModel.ResultText, Does.Contain("name already taken"));
            Assert.That(viewModel.ConnectionFaultText, Is.Not.Null.And.Not.Empty);
        }
    }
}
