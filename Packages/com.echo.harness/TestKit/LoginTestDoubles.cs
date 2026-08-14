using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    public sealed class FakeSessionStatus : ISessionStatus
    {
        public SessionState State { get; set; } = SessionState.Disconnected;
    }

    /// <summary>
    /// Completes synchronously, so a view-model test may use the
    /// <c>[Test]</c> + <c>GetAwaiter().GetResult()</c> pattern.
    /// </summary>
    public sealed class FakeLoginUseCase : ILoginUseCase
    {
        public LoginOutcome NextOutcome { get; set; } = LoginOutcome.Success("player-1", false);

        /// <summary>One-shot, and it wins over <see cref="NextOutcome"/>.</summary>
        public Exception NextFailure { get; set; }

        public int CallCount { get; private set; }

        public string LastPlayerName { get; private set; }

        public UniTask<LoginOutcome> LoginAsync(
            string playerName,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastPlayerName = playerName;

            if (NextFailure != null)
            {
                var failure = NextFailure;
                NextFailure = null;
                return UniTask.FromException<LoginOutcome>(failure);
            }

            return UniTask.FromResult(NextOutcome);
        }
    }
}
