using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Moves the session's work onto the Unity main thread. This lives in
    /// Infrastructure because Application is compiled with noEngineReferences and
    /// the architecture gate bans Unity type names in its source, so the main
    /// thread cannot be named there at all.
    ///
    /// <para>Switching while already on the main thread completes without yielding,
    /// so a session whose transport happens to complete inline pays nothing for the
    /// hop. That is measured, not assumed: see
    /// <c>MainThreadSessionSchedulerTests.SwitchingWhileAlreadyOnTheMainThreadCostsNoFrame</c>,
    /// which pins <c>Time.frameCount</c> across the switch.</para>
    ///
    /// <para><b>Cancellation is not symmetric.</b> On the main thread the awaiter is
    /// already complete, so a cancelled token throws OperationCanceledException
    /// immediately - the same contract RecordingSessionScheduler pins for the
    /// EditMode suite. Off the main thread it is not immediate: the awaiter queues
    /// its continuation on the player loop without consulting the token, and the
    /// token is only checked once that continuation runs. A cancelled caller
    /// therefore still costs a frame before it learns it was cancelled. Do not read
    /// the token argument as a promise of a prompt return.</para>
    /// </summary>
    public sealed class MainThreadSessionScheduler : ISessionScheduler
    {
        public async UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken)
        {
            // SwitchToMainThread returns a SwitchToMainThreadAwaitable rather than
            // a UniTask, hence the async wrapper instead of returning it directly.
            await UniTask.SwitchToMainThread(cancellationToken);
        }
    }
}
