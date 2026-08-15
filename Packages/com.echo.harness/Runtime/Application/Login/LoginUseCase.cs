using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// Sends LoginRequest and turns the answer into a <see cref="LoginOutcome"/>.
    ///
    /// <para><b>The exception policy is the design, and it has one rule:</b> this
    /// converts outcomes of trying to log in, and does not convert the system
    /// being broken. A timeout and a duplicate request are outcomes - the attempt
    /// finished, badly. A cancellation is not, and is left to escape rather than
    /// converted - though nothing reaches that path today, since the caller
    /// passes <c>CancellationToken.None</c>. The guard is for the caller
    /// docs/migration-checklist.md's open item "Prove cancellation from view ->
    /// session -> transport" describes: once a live token exists, reporting its
    /// cancellation as a failed login would be a lie the user acts on. Anything
    /// else is a real failure and is left to escape too, because a broken
    /// transport dressed up as a clean refusal sends whoever debugs it to the
    /// wrong layer.</para>
    ///
    /// <para>The cost is real and is paid in <c>LoginViewModel</c>: because two
    /// exception classes escape, the caller needs a catch-all. That is deliberate,
    /// and the alternative - swallowing everything here - would put the same catch
    /// in this class while destroying the information.</para>
    /// </summary>
    public sealed class LoginUseCase : ILoginUseCase
    {
        /// <summary>
        /// How long the server gets to answer. Chosen to match
        /// <c>ProtocolSession.RoundTripProbeDeadline</c>: both time a single
        /// request waiting on one specific reply from this server, which is why
        /// they share a value. Not the only timeout in the repository -
        /// <c>ProtocolSession.DisposeDisconnectDeadline</c> and
        /// <c>TcpTransportOptions.ReadIdleTimeout</c> are two more, timing
        /// different things - but the only other one shaped like this one.
        /// </summary>
        public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        private readonly IProtocolSession session;

        public LoginUseCase(IProtocolSession session) =>
            this.session = session ?? throw new ArgumentNullException(nameof(session));

        public async UniTask<LoginOutcome> LoginAsync(
            string playerName,
            CancellationToken cancellationToken)
        {
            // Refused here as well as by LoginViewModel.CanSubmit. The view-model
            // guard is what a user sees; this one is what holds when some other
            // caller arrives.
            if (string.IsNullOrWhiteSpace(playerName))
            {
                return LoginOutcome.Refusal("A player name is required.");
            }

            LoginResponseDto response;
            try
            {
                response = await session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto { PlayerName = playerName },
                    Deadline,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                return LoginOutcome.NoReply(
                    $"The server did not answer within {Deadline}.");
            }
            catch (RequestAlreadyInFlightException)
            {
                return LoginOutcome.NoReply("A login is already in flight.");
            }

            // response.ReconnectToken is never read here at all - not read and
            // dropped, simply unused. Persisting it is its own piece of work
            // with its own storage question, and a token held in memory with no
            // reader would be a speculative store that later reads as an
            // implemented feature.
            // LoginUseCaseTests.TheReconnectTokenNeverLeavesTheUseCase is a
            // structural check on LoginOutcome's shape, not a behavioural one:
            // it confirms LoginOutcome carries no property with "Token" in its
            // name. A static field or a log line elsewhere would satisfy it
            // just as well - it does not, and cannot, prove this method never
            // reads the DTO field.
            return response.Success
                ? LoginOutcome.Success(response.PlayerId, response.InGame)
                : LoginOutcome.Refusal(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? "The server refused the login without saying why."
                        : response.Error);
        }
    }
}
