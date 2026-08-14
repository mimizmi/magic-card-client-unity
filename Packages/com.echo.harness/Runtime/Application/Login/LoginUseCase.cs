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
    /// finished, badly. A cancellation is not: the only thing that cancels here is
    /// shutdown, and reporting a quit as a failed login would be a lie the user
    /// acts on. Anything else is a real failure and is left to escape, because a
    /// broken transport dressed up as a clean refusal sends whoever debugs it to
    /// the wrong layer.</para>
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
        /// <c>ProtocolSession.RoundTripProbeDeadline</c>, which is the only other
        /// deadline in the repository measured against this same server.
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

            // response.ReconnectToken is deliberately not carried out of this
            // method. Persisting it is its own piece of work with its own storage
            // question, and a token held in memory with no reader would be a
            // speculative store that later reads as an implemented feature.
            // LoginUseCaseTests.TheReconnectTokenNeverLeavesTheUseCase pins it.
            return response.Success
                ? LoginOutcome.Success(response.PlayerId, response.InGame)
                : LoginOutcome.Refusal(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? "The server refused the login without saying why."
                        : response.Error);
        }
    }
}
