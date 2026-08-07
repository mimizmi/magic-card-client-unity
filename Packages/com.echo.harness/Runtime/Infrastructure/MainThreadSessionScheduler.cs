using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Assembly-level, and parked in this file rather than in a new AssemblyInfo.cs because
// this class is the only thing in Echo.Harness.Infrastructure with internals worth
// exposing. What it buys is the only seam that reaches the process-wide shutdown
// signal from a test: the members it opens are the ones production subscribes, so a
// test invoking them exercises the real arming path rather than a setter invented for
// the test. Reflection would have needed no production surface at all, but it would
// also have gone on passing after any of those members was renamed or deleted, which is
// precisely the failure this seam exists to make impossible.
[assembly: InternalsVisibleTo("Echo.Harness.Tests.PlayMode")]

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
    ///
    /// <para><b>The latched path is the one exception to that, and it is the whole
    /// reason the latch exists.</b> Once <see cref="IsLatched"/> is true the hop is
    /// refused before <c>SwitchToMainThread</c> is ever reached, so the returned
    /// UniTask is already cancelled when the call returns and the caller learns on its
    /// first await - on either thread, with no frame in between. Note that this makes
    /// the latched path prompt in the same sense the already-on-main-thread path is,
    /// and for the same reason: no awaiter is ever constructed. The failure it closes
    /// is worse than a slow cancellation. <c>SwitchToMainThread</c> queues its
    /// continuation onto the player loop <i>without</i> consulting the token, so once
    /// that loop stops a pending hop neither resumes nor throws. A session can handle
    /// a hop that fails; it has no answer for one that never returns.</para>
    /// </summary>
    public sealed class MainThreadSessionScheduler : ISessionScheduler
    {
        // Static because the signal is a property of the process, not of one
        // scheduler: a scheduler constructed after the loop has begun stopping is
        // just as unable to hop as one constructed before it.
        private static volatile bool processIsShuttingDown;

        private volatile bool latched;

        public bool IsLatched => latched || processIsShuttingDown;

        /// <summary>
        /// Declares that the player loop is going away, after which
        /// <see cref="SwitchToSessionContextAsync"/> cancels rather than queueing a
        /// continuation that will never run.
        ///
        /// <para><b>One-way, and for this instance only.</b> It sets the per-scheduler
        /// field and never the process-wide one, so no caller can latch a scheduler it
        /// does not own.</para>
        ///
        /// <para>An earlier draft justified the one-way rule with "there is no path on
        /// which a loop that has begun stopping starts again within the same process
        /// lifetime". That is false, and the counterexample is measured rather than
        /// argued: on path B of
        /// <c>docs/findings/2026-08-02-unity-shutdown-callback-order.md</c>, a domain
        /// reload during play mode raises <c>beforeAssemblyReload</c> and play mode
        /// then <i>continues</i>.</para>
        ///
        /// <para><b>The replacement was false too, and this is the third attempt.</b>
        /// It read: "a latch cannot outlive the shutdown it was armed for, because the
        /// reload that follows destroys this static and every scheduler instance along
        /// with it". True on path B. False on path A, because <i>no reload follows</i>
        /// leaving play mode in this project. The finding's "Subscription lifetime
        /// across repeated play sessions" section measured handlers registered inside a
        /// play session going on to fire afterwards - they deliver
        /// <c>EnteredEditMode</c>, then the <i>next</i> entry's <c>ExitingEditMode</c>
        /// and <c>beforeAssemblyReload</c>. If the handlers survive play-mode exit then
        /// so does the domain, and so does this static. The reload that eventually
        /// frees them belongs to the next play-mode <i>entry</i>, not to the exit.</para>
        ///
        /// <para>What is true is narrower, and it is two statements rather than one
        /// because the two flags are bounded by different things. The instance flag
        /// this method sets is bounded by the instance: nothing else can reach it and
        /// no scheduler outlives the session that owns it. The static is bounded by an
        /// explicit clear on each of the two paths that arm it. On path B,
        /// <c>beforeAssemblyReload</c>'s arming is undone by the reload immediately
        /// after it, which the finding's path B fact 3 records as returning every
        /// static field in the reloaded domain to its default. On path A,
        /// <c>ExitingPlayMode</c>'s arming is undone by the <c>EnteredEditMode</c> case
        /// in <c>OnPlayModeStateChanged</c> - the finding logs that callback in both
        /// path A runs, after <c>Application.quitting</c>, so it gets the last word
        /// there. Neither bound is a property of a reload alone, which is what the
        /// second draft got wrong.</para>
        ///
        /// <para>No unlatch is offered, because an unlatch would invite a caller to
        /// clear the flag during teardown - which is exactly when it is right.</para>
        /// </summary>
        public void LatchForShutdown() => latched = true;

        // internal, not private, so MainThreadSessionSchedulerTests can arm the
        // process-wide signal through the same method Application.quitting calls. See
        // the InternalsVisibleTo note at the top of this file for why that seam is a
        // handler rather than a setter.
        internal static void OnProcessQuitting() => processIsShuttingDown = true;

        /// <summary>
        /// Installs the one shutdown signal that also exists outside the editor.
        ///
        /// <para>Deliberately <i>not</i> where the editor signals are wired. A domain
        /// reload during play mode does not re-run
        /// <c>RuntimeInitializeOnLoadMethod</c> - measured, the finding's path B fact 3
        /// - so anything installed only from here is silently dead for the remainder of
        /// that play session. <c>InstallEditorShutdownSignals</c> below does re-run,
        /// and carries the editor signals for that reason. A built player has no domain
        /// reloads, so this hook needs no re-arm there; that last clause is reasoning
        /// from the absence of reloads in a player, not a measurement, since path C was
        /// never exercised.</para>
        ///
        /// <para><c>internal</c> rather than <c>private</c>: this is the one
        /// unconditional path in the class that <i>clears</i> the static, and it is
        /// idempotent - the reset is a plain assignment and the subscription removes
        /// before it adds - so the test fixture calls it to restore the process-wide
        /// signal after deliberately arming it. That puts the restore on a real
        /// production path instead of a setter invented for the test.</para>
        /// </summary>
        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        internal static void InstallRuntimeShutdownSignals()
        {
            // Reset first - but the reason given here is narrower than the one this
            // method was handed. That reason was: "With Enter Play Mode Options and
            // domain reload disabled, this method re-runs while the static still
            // carries the previous play session's value." Whether
            // RuntimeInitializeOnLoadMethod re-runs in that configuration was NOT
            // established for this project. EditorSettings has
            // m_EnterPlayModeOptions: 0, so entering play mode always performs a full
            // domain reload here, and the configuration the claim describes cannot be
            // observed without changing a project setting.
            //
            // What holds either way: with the reload, this line is a no-op on an
            // already-zeroed static; without one, it is what stops a new session
            // inheriting the old session's flag. It is cheap and it cannot be wrong, so
            // it stays - only the justification has been cut back to what is known. The
            // re-arm that does NOT depend on this open question is the EnteredPlayMode
            // reset in the editor installer.
            processIsShuttingDown = false;

            // Fully qualified deliberately. This file is in Echo.Harness.Infrastructure,
            // whose enclosing namespace Echo.Harness contains a namespace named
            // Application; enclosing-namespace lookup beats a using directive, so a bare
            // "Application" here binds to Echo.Harness.Application and does not compile.
            //
            // Removed before added, with a method group rather than a lambda so that the
            // removal can match anything. If this ever re-runs without the statics
            // having been wiped, that idiom is what keeps one subscription from
            // silently becoming two.
            UnityEngine.Application.quitting -= OnProcessQuitting;
            UnityEngine.Application.quitting += OnProcessQuitting;
        }

#if UNITY_EDITOR
        private static void OnBeforeAssemblyReload() => processIsShuttingDown = true;

        // internal for the same reason as OnProcessQuitting: it is the handler the
        // editor installer subscribes, so a test that calls it directly is exercising
        // the real arming and clearing paths, switch included.
        internal static void OnPlayModeStateChanged(PlayModeStateChange state)
        {
            switch (state)
            {
                case PlayModeStateChange.EnteredPlayMode:
                    // A loop that has just started is not a loop that is stopping. This
                    // is the re-arm that depends on no claim about
                    // RuntimeInitializeOnLoadMethod: EnteredPlayMode is measured to fire
                    // on every play-mode entry in the finding (frame=1 in all four
                    // runs), and measured to arrive after the RuntimeInitialize hook,
                    // whose "installed" line is frame=0 - so it gets the last word.
                    processIsShuttingDown = false;
                    break;
                case PlayModeStateChange.ExitingPlayMode:
                    // Load-bearing, not mere corroboration. The finding ranks this
                    // behind Application.quitting as a "usable earlier warning", and on
                    // a fresh domain that is all it is - it fires one frame sooner
                    // (5015 before 5016, 661 before 662). After a domain reload during
                    // play, though, Application.quitting has no subscriber at all,
                    // because RuntimeInitializeOnLoadMethod did not re-run. From that
                    // point on this is the only signal left on path A.
                    processIsShuttingDown = true;
                    break;
                case PlayModeStateChange.EnteredEditMode:
                    // Not symmetry for its own sake. Nothing reloads the domain on
                    // leaving play mode here - see LatchForShutdown's doc comment for
                    // the measurement - so without this line the flag armed at
                    // ExitingPlayMode above stays true for the whole of the edit-mode
                    // session that follows. Harmless while nothing outside the PlayMode
                    // fixture constructs this type; the moment a container registers it,
                    // an EditMode run in an editor process that has already played once
                    // would resolve a scheduler that reports IsLatched and refuses every
                    // hop. The finding logs this callback on both path A runs, arriving
                    // after Application.quitting, so it is the last word on that path.
                    //
                    // Measured, not inferred, and in both directions. With this handler
                    // temporarily logging a freshly constructed scheduler's IsLatched,
                    // a play session was entered and exited: the EnteredEditMode entry
                    // read True - the flag armed at ExitingPlayMode arriving intact in
                    // edit mode - and an EditorApplication.delayCall queued from the
                    // same handler, i.e. running on an ordinary edit-mode tick after
                    // the transition, read False. With this assignment commented out
                    // and nothing else changed, that same delayCall read True. The
                    // stale flag is real and this line is what clears it.
                    processIsShuttingDown = false;
                    break;
            }
        }

        /// <summary>
        /// Re-arms the editor-side signals after every domain reload.
        ///
        /// <para><c>InitializeOnLoadMethod</c> rather than a second
        /// <c>RuntimeInitializeOnLoadMethod</c>, because a reload during play mode
        /// destroys every static - the delegate fields above included - and does not
        /// re-run the runtime hook. Without this, the latch would be dead for the rest
        /// of any play session in which a script was edited, which is the ordinary case
        /// while developing.</para>
        ///
        /// <para><b>Measured, not assumed.</b> With these three installers temporarily
        /// logging, a play session was entered, a recompile forced mid-play, and play
        /// mode then exited. <c>Editor.log</c>, in order: <c>OnBeforeAssemblyReload</c>
        /// (the latch arming on path B), then <c>InstallEditorShutdownSignals</c> again
        /// in the fresh domain - <b>no</b> <c>InstallRuntimeShutdownSignals</c> line
        /// beside it, reproducing the finding's path B fact 3 - and finally, on exiting
        /// play mode, <c>ExitingPlayMode</c> arming the latch. That last line is the
        /// whole point: it was delivered by a subscription this method re-made after
        /// the reload, at a moment when <c>Application.quitting</c> had no subscriber
        /// at all. The dictated design, which installed every signal from the runtime
        /// hook alone, would have been silently dead from the recompile onwards.</para>
        ///
        /// <para>The finding warns that "any editor-side subscription made from runtime
        /// code has this tail and should unsubscribe rather than assume play-mode exit
        /// ended it". That warning is answered by relocating the subscription rather
        /// than by unsubscribing: outliving play mode is precisely what is wanted of a
        /// re-arm, and an installer that unsubscribed on play-mode exit would restore
        /// the hole it exists to close. The tail's real hazard is duplication, and
        /// <c>-=</c> before <c>+=</c> on a method group closes that instead.</para>
        ///
        /// <para><c>Application.quitting</c> is deliberately not re-subscribed here. In
        /// the editor it is raised by <c>Internal_ApplicationQuit</c> on leaving play
        /// mode - the same event <c>ExitingPlayMode</c> already reports, one frame
        /// earlier - so adding it would buy nothing that case does not already
        /// cover.</para>
        /// </summary>
        [InitializeOnLoadMethod]
        private static void InstallEditorShutdownSignals()
        {
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;

            EditorApplication.playModeStateChanged -= OnPlayModeStateChanged;
            EditorApplication.playModeStateChanged += OnPlayModeStateChanged;
        }
#endif

        public async UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken)
        {
            // Checked before the hop, not after. After is too late by construction:
            // the await is the thing that never returns.
            //
            // Thrown without a message, and that is not an oversight. This is an
            // `async UniTask` method that has not yet awaited, so the throw travels via
            // AsyncUniTaskMethodBuilder.Task into UniTask.FromException, which reads
            // `if (ex is OperationCanceledException oce) return
            // FromCanceled(oce.CancellationToken)` and drops the exception object
            // entirely; the caller's await is then served by CanceledResultSource,
            // which throws a brand new OperationCanceledException of its own. Any text
            // put here would reach nobody. (Read out of the vendored UniTask source,
            // Runtime/UniTask.Factory.cs lines 30-37 and 408-420, rather than observed
            // at runtime.) So the explanation lives here, where it can be read: the
            // session context is gone, a continuation queued onto the stopping player
            // loop would never run, and this is an orderly shutdown signal rather than
            // a transport failure.
            if (IsLatched)
            {
                throw new OperationCanceledException();
            }

            // SwitchToMainThread returns a SwitchToMainThreadAwaitable rather than
            // a UniTask, hence the async wrapper instead of returning it directly.
            await UniTask.SwitchToMainThread(cancellationToken);
        }
    }
}
