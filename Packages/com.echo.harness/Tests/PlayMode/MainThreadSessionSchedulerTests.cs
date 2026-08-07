using System;
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class MainThreadSessionSchedulerTests
    {
        [UnityTest]
        public IEnumerator SwitchingFromAThreadPoolThreadReachesTheMainThread() =>
            UniTask.ToCoroutine(async () =>
            {
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var scheduler = new MainThreadSessionScheduler();

                await UniTask.SwitchToThreadPool();
                Assert.That(
                    Thread.CurrentThread.ManagedThreadId,
                    Is.Not.EqualTo(mainThreadId),
                    "The test has to actually be off the main thread to prove anything.");

                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);

                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThreadId));
            });

        /// <summary>
        /// The scheduler's doc comment claims the already-on-main-thread case costs
        /// nothing. Asserting only "the thread is still the main thread" would hold
        /// whether the switch returned inline, yielded one frame, or yielded a
        /// hundred, so it would measure nothing. The thread id is kept alongside the
        /// rest because that is what would break if the hop were ever swapped for a
        /// thread-pool one.
        ///
        /// <para>Two assertions measure the cost, not one, because the frame count
        /// alone under-measures the claim. Time.frameCount only advances at frame
        /// boundaries, so a hop rewritten as
        /// <c>await UniTask.Yield(PlayerLoopTiming.LastPostLateUpdate)</c> resumes
        /// later within the SAME frame and leaves the count untouched - a real yield
        /// that the frame-count assertion passes. That was verified by mutation: with
        /// the yield in place the frame-count assertion still passed and only the
        /// status assertion below failed. Completing without yielding means the
        /// returned UniTask is already complete when the call returns, which is what
        /// the status check pins directly.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingWhileAlreadyOnTheMainThreadCostsNoFrame() =>
            UniTask.ToCoroutine(async () =>
            {
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var scheduler = new MainThreadSessionScheduler();
                var frameBefore = Time.frameCount;

                var hop = scheduler.SwitchToSessionContextAsync(CancellationToken.None);

                Assert.That(
                    hop.Status.IsCompletedSuccessfully(),
                    Is.True,
                    "Already on the main thread, the hop must be finished before the " +
                    "call returns. A hop that is still pending here yielded, even if " +
                    "it resumes inside this same frame and costs no frame count.");

                await hop;

                Assert.That(
                    Time.frameCount,
                    Is.EqualTo(frameBefore),
                    "Switching while already on the main thread must complete without " +
                    "yielding, or a session whose transport completed inline pays a " +
                    "frame for a hop it did not need.");
                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThreadId));
            });

        /// <summary>
        /// RecordingSessionScheduler surfaces OperationCanceledException on a
        /// cancelled token - as a cancelled UniTask, awaited, not thrown out of the
        /// call - and every EditMode session test is written against that double. If
        /// production disagreed, the EditMode suite would be pinning a contract that
        /// never ships.
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingWithACancelledTokenOnTheMainThreadThrows() =>
            UniTask.ToCoroutine(async () =>
            {
                var scheduler = new MainThreadSessionScheduler();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                var canceled = false;
                try
                {
                    await scheduler.SwitchToSessionContextAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                Assert.That(
                    canceled,
                    Is.True,
                    "A cancelled token has to surface as OperationCanceledException " +
                    "on await, because that is the contract RecordingSessionScheduler " +
                    "pins for the whole EditMode session suite.");
            });

        /// <summary>
        /// The same contract from the other side. Only the exception type is
        /// asserted, deliberately: off the main thread the awaiter registers its
        /// continuation on the player loop before it ever consults the token, so the
        /// throw arrives a frame later rather than immediately. Asserting promptness
        /// here would assert something that is not true.
        /// </summary>
        [UnityTest]
        public IEnumerator SwitchingWithACancelledTokenOffTheMainThreadAlsoThrows() =>
            UniTask.ToCoroutine(async () =>
            {
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var scheduler = new MainThreadSessionScheduler();
                using var cancellation = new CancellationTokenSource();
                cancellation.Cancel();

                await UniTask.SwitchToThreadPool();
                Assert.That(
                    Thread.CurrentThread.ManagedThreadId,
                    Is.Not.EqualTo(mainThreadId),
                    "The test has to actually be off the main thread to prove anything.");

                var canceled = false;
                try
                {
                    await scheduler.SwitchToSessionContextAsync(cancellation.Token);
                }
                catch (OperationCanceledException)
                {
                    canceled = true;
                }

                Assert.That(canceled, Is.True);
                Assert.That(
                    Thread.CurrentThread.ManagedThreadId,
                    Is.EqualTo(mainThreadId),
                    "Even the cancelled switch resumes on the main thread, because the " +
                    "player loop is what runs the continuation that throws.");
            });

        // The failure this closes: UniTask.SwitchToMainThread queues its
        // continuation on the player loop WITHOUT consulting the token, so once the
        // loop stops a pending hop never resumes and never throws. A session can
        // handle a hop that fails; it has no answer for one that never returns.
        [UnityTest]
        public IEnumerator ALatchedSchedulerCancelsInsteadOfQueueingOntoADeadLoop()
        {
            var scheduler = new MainThreadSessionScheduler();
            scheduler.LatchForShutdown();

            // A live witness rather than an afterthought: LatchForShutdown must set
            // per-instance state, not the process-wide flag. If it ever set the static
            // instead, this scheduler would come back latched and
            // AnUnlatchedSchedulerStillHops below would begin failing whenever it
            // happened to run second. That is the one ordering dependency the pair
            // could have, and this pins it shut.
            var bystander = new MainThreadSessionScheduler();

            Exception caught = null;
            var completed = false;

            RunAsync().Forget();

            async UniTaskVoid RunAsync()
            {
                try
                {
                    await scheduler.SwitchToSessionContextAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                finally
                {
                    completed = true;
                }
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while (!completed && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            // Kept as a liveness guard on the deadline loop above, but do not read it
            // as the assertion that catches the defect - measured, it cannot be. A
            // PlayMode test runs on a main thread whose player loop is alive, so an
            // unlatched hop completes here too and this stays true either way. The
            // mutation check confirmed it: with the latch's throw commented out, this
            // line passed and the next one failed with "Expected: instance of
            // <System.OperationCanceledException> But was: null". The parked-hop
            // scenario the latch exists for cannot be staged inside a live loop, so
            // what is pinned here is the refusal, not the parking.
            Assert.That(completed, Is.True, "the hop must settle within the deadline");
            Assert.That(
                caught,
                Is.InstanceOf<OperationCanceledException>(),
                "This is the assertion that kills a broken latch. A latched scheduler " +
                "must refuse the hop outright; without the refusal the hop simply " +
                "succeeds and nothing is thrown.");
            Assert.That(
                scheduler.IsLatched,
                Is.True,
                "the scheduler under test must still report itself latched");
            Assert.That(
                bystander.IsLatched,
                Is.False,
                "LatchForShutdown must latch one scheduler, not the process. A latch " +
                "that leaked into the static would outlive this test and poison every " +
                "scheduler built for the rest of the play session.");
        }

        /// <summary>
        /// <c>IsLatched</c> ORs a process-wide static, so this assertion is only sound
        /// if nothing can set that static during a PlayMode run. Three things write it,
        /// and none of them can fire here: <c>Application.quitting</c> and
        /// <c>ExitingPlayMode</c> arrive when the run is already over, and
        /// <c>beforeAssemblyReload</c> would destroy the run rather than perturb it.
        /// The fourth candidate - <c>ALatchedSchedulerCancelsInsteadOfQueueingOntoADeadLoop</c>
        /// - is ruled out by construction rather than by ordering, because
        /// <c>LatchForShutdown</c> is pinned to per-instance state there. Entering play
        /// mode also clears the static twice over, from the RuntimeInitialize hook and
        /// again on <c>EnteredPlayMode</c>, so a value carried in from a previous play
        /// session cannot reach this line either.
        ///
        /// <para>Two later tests in this fixture <i>do</i> set the static deliberately,
        /// which is the point of them, and the ordering argument above does not cover
        /// that. What covers it is their <c>finally</c>: each restores the static
        /// through <c>InstallRuntimeShutdownSignals</c> on every exit path, including a
        /// failing assertion. If either restore is ever dropped, this assertion is one
        /// of the first things that starts failing - which is the intended alarm, not a
        /// flake.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator AnUnlatchedSchedulerStillHops()
        {
            var scheduler = new MainThreadSessionScheduler();

            Assert.That(scheduler.IsLatched, Is.False);

            var completed = false;
            RunAsync().Forget();

            async UniTaskVoid RunAsync()
            {
                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);
                completed = true;
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while (!completed && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(completed, Is.True);
        }

        /// <summary>
        /// The production arming path, which nothing else in this fixture touches.
        /// Every other test here reaches the latch through <c>LatchForShutdown</c>,
        /// which sets a per-instance field; the process-wide static that
        /// <c>IsLatched</c> also reads was only ever asserted in the negative. That
        /// left the whole shutdown-signal deliverable - the static, its three
        /// handlers and its two installers - deletable with the suite still green.
        /// Rewriting <c>IsLatched</c> to <c>=> latched</c> is the one-token version of
        /// that deletion, and this test is what fails when it is applied.
        ///
        /// <para><b>What this does not cover, plainly.</b> Calling
        /// <c>OnProcessQuitting</c> proves the handler arms the static. It does
        /// <i>not</i> prove the handler is subscribed to anything - a test cannot
        /// stage a real <c>Application.quitting</c> inside a running PlayMode session,
        /// since the event that raises it is the one that ends the session. The
        /// subscription's only evidence is the <c>Editor.log</c> observation in
        /// section 8 of the Task 7 report: a one-off manual run in this project's
        /// editor, not something CI repeats. Do not read a green suite here as
        /// evidence that the signals are wired.</para>
        ///
        /// <para>The static is restored through
        /// <c>InstallRuntimeShutdownSignals</c>, in a <c>finally</c> so that a failing
        /// assertion cannot leave it set. It has to be a <c>finally</c>: the static
        /// outlives this test, and a leaked <c>true</c> would make every later
        /// scheduler in the run report itself latched.</para>
        /// </summary>
        [UnityTest]
        public IEnumerator TheProcessWideSignalLatchesASchedulerThatNeverLatchedItself() =>
            UniTask.ToCoroutine(async () =>
            {
                var scheduler = new MainThreadSessionScheduler();
                Assert.That(
                    scheduler.IsLatched,
                    Is.False,
                    "precondition: a fresh scheduler in a healthy play session is not " +
                    "latched, and nothing before this line has armed anything.");

                try
                {
                    MainThreadSessionScheduler.OnProcessQuitting();

                    Assert.That(
                        scheduler.IsLatched,
                        Is.True,
                        "This scheduler was never handed to LatchForShutdown, so the " +
                        "only channel that can latch it is the process-wide signal. " +
                        "If this fails, IsLatched no longer consults that signal and " +
                        "every shutdown handler in the class is dead code.");

                    var canceled = false;
                    try
                    {
                        await scheduler.SwitchToSessionContextAsync(CancellationToken.None);
                    }
                    catch (OperationCanceledException)
                    {
                        canceled = true;
                    }

                    Assert.That(
                        canceled,
                        Is.True,
                        "The refusal, not merely the flag: the process-wide signal has " +
                        "to reach SwitchToSessionContextAsync the same way an " +
                        "instance latch does, or the property is true and the hop " +
                        "still queues onto a loop that is going away.");

                    Assert.That(
                        new MainThreadSessionScheduler().IsLatched,
                        Is.True,
                        "Built after the signal, and latched all the same. This is " +
                        "why the flag is static: a scheduler constructed once the loop " +
                        "has begun stopping is no more able to hop than one that " +
                        "existed before it.");
                }
                finally
                {
                    MainThreadSessionScheduler.InstallRuntimeShutdownSignals();
                }

                Assert.That(
                    new MainThreadSessionScheduler().IsLatched,
                    Is.False,
                    "the restore in the finally above must actually clear the static, " +
                    "or this test poisons every test that runs after it");
            });

#if UNITY_EDITOR
        /// <summary>
        /// Guarded by <c>UNITY_EDITOR</c> because <c>PlayModeStateChange</c> is a
        /// <c>UnityEditor</c> type, and this assembly does reach one target where that
        /// type does not exist: a standalone <i>player test build</i>, which defines
        /// <c>UNITY_INCLUDE_TESTS</c> and has no <c>UnityEditor</c>.
        ///
        /// <para>Not, as an earlier draft of this comment claimed, because
        /// <c>includePlatforms: []</c> leaves it "compiled for players too". It is not:
        /// this assembly carries <c>defineConstraints: [UNITY_INCLUDE_TESTS]</c>, and
        /// <c>Tools/ci/verify-architecture.ps1:243-247</c> says in as many words that
        /// the constraint "compiles the assembly out entirely" and "is not defined in a
        /// player build" - pinning it for this very assembly at line 294. The empty
        /// platform list alone would permit a player build; the define constraint is
        /// what prevents an ordinary one. The guard is right either way, but the reason
        /// had to be.</para>
        ///
        /// <para>What it pins: nothing reloads the domain when play mode ends in this
        /// project, so the flag armed at <c>ExitingPlayMode</c> would otherwise stay
        /// true for the whole edit-mode session that follows - and a scheduler
        /// resolved from a container during a later EditMode run would refuse every
        /// hop. Both transitions are driven through the real handler rather than a
        /// setter, so the <c>switch</c> itself is under test: a missing
        /// <c>EnteredEditMode</c> case fails the second assertion.</para>
        ///
        /// <para>Same limit as the test above: invoking the handler does not prove
        /// <c>EditorApplication.playModeStateChanged</c> is subscribed to it.</para>
        /// </summary>
        [Test]
        public void ReturningToEditModeClearsTheProcessWideSignal()
        {
            var scheduler = new MainThreadSessionScheduler();

            try
            {
                MainThreadSessionScheduler.OnPlayModeStateChanged(
                    PlayModeStateChange.ExitingPlayMode);
                Assert.That(
                    scheduler.IsLatched,
                    Is.True,
                    "ExitingPlayMode is the only signal left on path A after a domain " +
                    "reload during play, so it has to arm the latch.");

                MainThreadSessionScheduler.OnPlayModeStateChanged(
                    PlayModeStateChange.EnteredEditMode);
                Assert.That(
                    scheduler.IsLatched,
                    Is.False,
                    "and it has to be cleared on the way back, because no domain " +
                    "reload follows play-mode exit to clear it for us.");
            }
            finally
            {
                MainThreadSessionScheduler.InstallRuntimeShutdownSignals();
            }
        }
#endif
    }
}
