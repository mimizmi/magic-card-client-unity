using System.Threading;
using Cysharp.Threading.Tasks;

namespace Echo.Harness.Application
{
    /// <summary>
    /// One login attempt. The port exists so that
    /// <c>Echo.Harness.Presentation</c> depends on an interface rather than on the
    /// concrete use case, and so that a view-model test needs no session at all.
    /// </summary>
    public interface ILoginUseCase
    {
        UniTask<LoginOutcome> LoginAsync(string playerName, CancellationToken cancellationToken);
    }
}
