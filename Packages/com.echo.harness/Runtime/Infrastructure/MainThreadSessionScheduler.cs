using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Moves the session's work onto the Unity main thread.
    ///
    /// <para><b>Not because Application could not host it.</b> An earlier version of
    /// this comment claimed the main thread cannot be named under
    /// <c>Runtime\Application\</c> at all. That was measured false: a probe calling
    /// <c>UniTask.SwitchToMainThread(default)</c> placed there compiles clean under
    /// <c>noEngineReferences</c> and leaves <c>verify-architecture.ps1</c> green,
    /// because <c>SwitchToMainThreadAwaitable</c>, its nested awaiter and
    /// <c>PlayerLoopTiming</c> are all Cysharp types, and the gate's Application ban
    /// list is <c>UnityEngine|Addressables|R3|VContainer|XLua</c> - Cysharp is banned
    /// only in Domain. A negative control adding <c>UnityEngine.Time.frameCount</c> to
    /// the same file did fail to compile, so the probe was genuinely engine-free.</para>
    ///
    /// <para>The port earns its place for two other reasons. The test double completes
    /// synchronously, which is what keeps the EditMode suite independent of a player
    /// loop EditMode does not run. And it keeps ProtocolSession from hard-coding
    /// <i>which</i> context it confines to, so the confinement contract stays a
    /// property of the session rather than of the Unity player loop.</para>
    ///
    /// <para>Switching while already on the main thread completes without yielding,
    /// so a session whose transport happens to complete inline pays nothing for the
    /// hop. That is measured, not assumed: see
    /// <c>MainThreadSessionSchedulerTests.SwitchingWhileAlreadyOnTheMainThreadCostsNoFrame</c>,
    /// which pins <c>Time.frameCount</c> across the switch and, because
    /// <c>Time.frameCount</c> cannot see a yield that resumes inside the same frame,
    /// also pins that the returned UniTask is already complete when the call
    /// returns.</para>
    ///
    /// <para><b>Cancellation is not symmetric.</b> On the main thread the awaiter is
    /// already complete, so the token is consulted inline and the UniTask this
    /// method returns is already cancelled - the exception surfaces on the caller's
    /// first await, with no frame in between. That is the same shape
    /// RecordingSessionScheduler returns for the EditMode suite. Note what it is
    /// not: nothing throws out of the call itself. This is an <c>async UniTask</c>
    /// method, which structurally cannot, so a caller that never awaits never sees
    /// the cancellation. Off the main thread it is not even prompt: the awaiter queues
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
