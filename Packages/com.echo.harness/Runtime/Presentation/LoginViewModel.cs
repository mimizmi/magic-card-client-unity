using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Unity.Properties;
using UnityEngine.UIElements;

namespace Echo.Harness.Presentation
{
    /// <summary>
    /// The login screen's state, bindable by UI Toolkit's runtime data binding and
    /// free of every visual element type - it names UI Toolkit's binding contract
    /// and no widget, which is what lets the whole of it be tested in EditMode with
    /// no player loop.
    ///
    /// <para><b><see cref="ResultText"/> and <see cref="ConnectionFaultText"/> are
    /// separate on purpose.</b> A login refusal and a dropped link arrive from
    /// unrelated sources at unrelated times; one field would let whichever landed
    /// second erase the other, so a disconnect could silently wipe the very
    /// rejection message the user was reading.</para>
    /// </summary>
    public sealed class LoginViewModel : INotifyBindablePropertyChanged, IDisposable
    {
        private readonly ISessionStatus status;
        private readonly ILoginUseCase login;
        private readonly IDisposable faultSubscription;

        private string playerName = string.Empty;
        private string connectionStatus;
        private string resultText = string.Empty;
        private string connectionFaultText = string.Empty;
        private bool busy;
        private SessionState lastSeenState = (SessionState)(-1);

        public LoginViewModel(ISessionStatus status, ILoginUseCase login, SessionFaultRouter faults)
        {
            this.status = status ?? throw new ArgumentNullException(nameof(status));
            this.login = login ?? throw new ArgumentNullException(nameof(login));

            if (faults == null)
            {
                throw new ArgumentNullException(nameof(faults));
            }

            // Taking the router here is ordinary consumption. It is NOT what forces
            // the router to be constructed - SessionFaultRouterEntryPoint is - so
            // that removing this line degrades the UI without silently switching
            // off fault logging.
            faultSubscription = faults.ObserveConnectionFaults(OnConnectionFault);

            Refresh();
        }

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        [CreateProperty]
        public string PlayerName
        {
            get => playerName;
            set
            {
                if (playerName == value)
                {
                    return;
                }

                playerName = value ?? string.Empty;
                Notify(nameof(PlayerName));
                Notify(nameof(CanSubmit));
            }
        }

        [CreateProperty]
        public string ConnectionStatus => connectionStatus;

        [CreateProperty]
        public bool CanSubmit =>
            status.State == SessionState.Connected
            && !busy
            && !string.IsNullOrWhiteSpace(playerName);

        [CreateProperty]
        public bool IsBusy => busy;

        [CreateProperty]
        public string ResultText => resultText;

        [CreateProperty]
        public string ConnectionFaultText => connectionFaultText;

        /// <summary>
        /// Re-reads the session state. Polling is not a choice:
        /// <see cref="ISessionStatus"/> exposes no change event because
        /// <c>IProtocolSession</c> has none either. Called once a frame by
        /// <c>LoginView.Update</c>, and it raises nothing unless something moved.
        /// </summary>
        public void Refresh()
        {
            var current = status.State;
            if (current == lastSeenState)
            {
                return;
            }

            lastSeenState = current;
            connectionStatus = Describe(current);
            Notify(nameof(ConnectionStatus));
            Notify(nameof(CanSubmit));
        }

        public async UniTask SubmitAsync()
        {
            if (busy)
            {
                return;
            }

            SetBusy(true);
            connectionFaultText = string.Empty;
            Notify(nameof(ConnectionFaultText));

            try
            {
                var outcome = await login.LoginAsync(playerName, CancellationToken.None);
                resultText = Describe(outcome);
            }
            catch (OperationCanceledException)
            {
                // Shutdown. Nothing to show, and the screen is going away.
                resultText = string.Empty;
            }
            catch (Exception failure)
            {
                // LoginUseCase lets a genuinely broken transport escape, and this is
                // where that lands. Without this catch the exception reaches
                // UniTask's unobserved handler and the button appears inert.
                resultText = $"Login failed: {failure.GetType().Name}: {failure.Message}";
            }
            finally
            {
                SetBusy(false);
                Notify(nameof(ResultText));
            }
        }

        public void Dispose() => faultSubscription.Dispose();

        private void OnConnectionFault(SessionFault fault)
        {
            // Delivered on the session context by SessionFaultRouter, so touching
            // bindable state here is safe.
            connectionFaultText = $"Connection problem: {fault.Kind} — {fault.Diagnostic}";
            Notify(nameof(ConnectionFaultText));
        }

        private void SetBusy(bool value)
        {
            if (busy == value)
            {
                return;
            }

            busy = value;
            Notify(nameof(IsBusy));
            Notify(nameof(CanSubmit));
        }

        private void Notify(string property) =>
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));

        private static string Describe(SessionState state)
        {
            switch (state)
            {
                case SessionState.Connected:
                    return "Connected.";
                case SessionState.Connecting:
                    return "Connecting…";
                case SessionState.Faulted:
                    return "The session faulted.";
                default:
                    return "Not connected.";
            }
        }

        private static string Describe(LoginOutcome outcome)
        {
            switch (outcome.Result)
            {
                case LoginResult.Succeeded:
                    return $"Logged in as {outcome.PlayerId}" +
                        (outcome.InGame ? " (a game is already in progress)." : ".");
                case LoginResult.Rejected:
                    return $"The server refused the login: {outcome.Message}";
                default:
                    return $"No answer from the server: {outcome.Message}";
            }
        }
    }
}
