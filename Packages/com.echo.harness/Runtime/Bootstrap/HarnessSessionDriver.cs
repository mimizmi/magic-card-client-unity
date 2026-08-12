using System;
using System.Runtime.CompilerServices;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Infrastructure;
using UnityEngine;
using VContainer.Unity;
#if UNITY_EDITOR
using UnityEditor;
#endif

// Assembly-level, and parked in this file for the reason the same attribute is
// parked on MainThreadSessionScheduler: this class is the only thing in
// Echo.Harness.Bootstrap with internals worth exposing. It opens exactly one
// member, and the measurement that earned it is recorded on that member.
[assembly: InternalsVisibleTo("Echo.Harness.Tests.PlayMode")]

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Starts the session when an endpoint is configured, and stops it before the
    /// player loop goes away.
    ///
    /// <para><b>The shutdown is synchronous, and that is the whole design.</b> The
    /// plan for this task specified
    /// <c>ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult()</c> from
    /// <c>Application.quitting</c> and described it as awaiting the quiet path. It
    /// does not await anything. In the resolved UniTask package the awaiter's
    /// <c>GetResult()</c> is <c>if (task.source == null) return;
    /// task.source.GetResult(task.token);</c> (<c>UniTask.cs:313-317</c>) - no
    /// blocking wait - and on an incomplete promise that call throws
    /// <c>InvalidOperationException("Not yet completed, UniTask only allow to use
    /// await.")</c> (<c>UniTaskCompletionSource.cs:227-232</c>) and then, in its
    /// <c>finally</c>, returns the promise to the pool underneath the continuation
    /// still using it (<c>StateMachineRunner.cs:214-226</c>). A pending shutdown
    /// driven from a quit hook is not merely unawaited; it is corrupted.</para>
    ///
    /// <para>There is no frame to wait for either.
    /// <c>docs/findings/2026-08-02-unity-shutdown-callback-order.md</c> measures
    /// <c>Application.quitting</c> firing in the same frame as <c>wantsToQuit</c> on
    /// path A, with no loop tick after it. Nothing hopped onto the player loop from
    /// here would ever resume.</para>
    ///
    /// <para>What makes the quiet path real anyway is a measured property of the
    /// session rather than of the hook: <c>ProtocolSession.StopAsync</c> takes no
    /// scheduler hop. Its only await is <c>ITransport.DisconnectAsync</c>, and both
    /// <c>TcpTransport</c> (<c>TcpTransport.cs:474-486</c>) and the test fake return
    /// <c>UniTask.CompletedTask</c>. So the teardown runs to completion inside this
    /// call and the quit hook has nothing left to wait for.
    /// <c>HarnessSessionDriverTests.ShutdownIsAlreadyCompleteWhenItReturnsSoAQuitHookNeedsNoFrame</c>
    /// is what stops that property being lost silently, and
    /// <see cref="RunShutdownWithoutAFrameToSpare"/> reports it at runtime rather
    /// than assuming it - a transport with a genuinely asynchronous close is a
    /// legitimate future change, and it must produce a warning naming this
    /// paragraph rather than a session that is never stopped.</para>
    ///
    /// <para><b>Two signals, because the two measured shutdown paths share none.</b>
    /// <c>UnityEngine.Application.quitting</c> is path A - leaving play mode, and
    /// the only one of the two that also exists in a player.
    /// <c>AssemblyReloadEvents.beforeAssemblyReload</c> is path B, a domain reload
    /// during play, on which <c>Application.quitting</c> is measured never to fire
    /// at all. Path B is in scope here for one narrow reason: it is the only path on
    /// which nothing else closes the socket. The scheduler latch is already armed on
    /// both paths by <c>MainThreadSessionScheduler</c>, so nothing parks either way;
    /// what path B would otherwise leave behind is a live TCP connection the server
    /// holds until its own timeout, because the reload destroys the container
    /// without disposing it. Editor-only, and stated as such: path C, a built
    /// player, was never measured, and a player has no domain reloads to need this
    /// for.</para>
    /// </summary>
    public sealed class HarnessSessionDriver : IAsyncStartable, IDisposable
    {
        private readonly IProtocolSession session;
        private readonly EndpointResolution endpoint;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private bool hooksInstalled;
        private bool shutdownStarted;
        private bool disposed;

        /// <summary>
        /// Whether the two shutdown signals are currently subscribed.
        ///
        /// <para><c>internal</c> rather than <c>private</c> because without it the
        /// whole of this class's shutdown wiring is untested. Measured: deleting the
        /// <see cref="InstallHooks"/> call from <see cref="StartAsync"/> - so the
        /// driver subscribes to nothing and an ordinary quit tears nothing down -
        /// left all fourteen PlayMode tests green. The only thing that noticed was a
        /// human watching a real quit.</para>
        ///
        /// <para><b>What it does not prove, stated rather than implied.</b> It
        /// proves the installation path ran and that the matching removal ran. It
        /// does <i>not</i> prove the delegate reached
        /// <c>UnityEngine.Application.quitting</c>: a C# event's invocation list
        /// cannot be read from outside the type that declares it, so no test on this
        /// runtime can assert that last step. The acceptance run recorded in the
        /// task report is the evidence for it - a probe subscribed to
        /// <c>Application.quitting</c> after this driver's handler observed the
        /// session already reading <c>Disconnected</c>.</para>
        /// </summary>
        internal bool ShutdownSignalsInstalled => hooksInstalled;

        public HarnessSessionDriver(IProtocolSession session, EndpointResolution endpoint)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.endpoint = endpoint;
        }

        /// <summary>
        /// The token handed to the session is a SHUTDOWN token and nothing else, and
        /// it is this driver's own rather than the one the container supplies.
        /// Cancelling it destroys the transport, because TcpTransport's read path
        /// closes the link on any cancellation - closing the socket is the only way
        /// this runtime can unpark a blocked read. Anything finer than "stop for
        /// good" needs a different mechanism.
        ///
        /// <para>Not the <paramref name="cancellation"/> parameter, deliberately.
        /// The container owns that token and cancels it from its own disposal, which
        /// is route 2 of the list on <c>ProtocolSession.RunPumpAsync</c>'s
        /// cancellation catch: it cancels the pump without passing through StopAsync
        /// or Dispose, so nothing fails the waiters and a request in flight is told
        /// it timed out - a fabricated network failure for an orderly quit.</para>
        /// </summary>
        public async UniTask StartAsync(CancellationToken cancellation)
        {
            if (!endpoint.IsConfigured)
            {
                // Not a failure. It is the ordinary state of a machine that has not
                // opted in, and it matches how the end-to-end tier skips itself.
                Debug.Log(
                    $"[Harness] No server endpoint configured ({endpoint.Source}). " +
                    "The session stays disconnected. Set a host in the " +
                    $"{HarnessEndpointSettings.ResourcePath} asset or in " +
                    $"{ServerEndpoint.HostVariable}.");
                return;
            }

            InstallHooks();

            Debug.Log($"[Harness] Connecting to the server, endpoint from {endpoint.Source}.");
            await session.StartAsync(shutdown.Token);
        }

        public async UniTask ShutdownAsync(CancellationToken cancellationToken)
        {
            if (shutdownStarted)
            {
                return;
            }

            shutdownStarted = true;
            RemoveHooks();

            try
            {
                await session.StopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // Kept, but not for the reason the plan gave. The plan called this
                // "the backstop path: the scheduler was already latched, so the
                // teardown hop cancelled". There is no teardown hop - StopAsync
                // takes none - so a latched scheduler cannot reach here, and no test
                // in this repository exercises this arm. What can reach it is a
                // caller-supplied token already cancelled together with a transport
                // whose DisconnectAsync honours it; TcpTransport's does not, so
                // today it is unreachable through the shipped stack. It stays
                // because StopAsync's own try/finally has already run
                // FailPendingRequests by the time such an exception escapes, which
                // makes it orderly rather than a failure - and because letting it
                // out of a quit hook would turn an orderly stop into a console
                // error.
                Debug.Log(
                    "[Harness] Shut down through the cancelled path; the stop was " +
                    "cancelled before the transport finished closing.");
            }
        }

        /// <summary>
        /// Runs <see cref="ShutdownAsync"/> from a callback that has no frame after
        /// it, and says so out loud when it could not finish.
        ///
        /// <para><c>Status</c> is read before <c>GetResult</c> rather than after,
        /// because <c>GetResult</c> on a pending promise throws and recycles it. A
        /// pending shutdown is handed to <c>Forget</c> instead - which at least
        /// routes any eventual exception to UniTask's unobserved handler rather than
        /// dropping it - and reported as a warning, because on this runtime it will
        /// not complete: the loop that would resume it is the thing going away.</para>
        /// </summary>
        private void RunShutdownWithoutAFrameToSpare(string signal)
        {
            try
            {
                var teardown = ShutdownAsync(CancellationToken.None);
                if (!teardown.Status.IsCompleted())
                {
                    teardown.Forget();
                    Debug.LogWarning(
                        $"[Harness] The shutdown started from {signal} did not " +
                        "complete synchronously, and there is no further player " +
                        "loop tick in which it could. The session may be left open " +
                        "until the server's own timeout. See the class summary on " +
                        "HarnessSessionDriver: something on the StopAsync path now " +
                        "yields.");
                    return;
                }

                teardown.GetAwaiter().GetResult();
            }
            catch (Exception failure)
            {
                // Reported here rather than allowed to escape. This runs on Unity's
                // own event dispatch, where an escaping exception is attributed to
                // the event rather than to the harness and can stop the remaining
                // subscribers being called at all.
                Debug.LogError(
                    $"[Harness] The shutdown started from {signal} failed: " +
                    $"{failure.GetType().Name}: {failure.Message}");
            }
        }

        private void OnApplicationQuitting() =>
            RunShutdownWithoutAFrameToSpare("UnityEngine.Application.quitting");

#if UNITY_EDITOR
        private void OnBeforeAssemblyReload() =>
            RunShutdownWithoutAFrameToSpare("AssemblyReloadEvents.beforeAssemblyReload");
#endif

        // Removed before added, with method groups rather than lambdas so the
        // removal can match. StartAsync is reached once per driver today, but a
        // subscription that silently became two would run the teardown twice.
        private void InstallHooks()
        {
            UnityEngine.Application.quitting -= OnApplicationQuitting;
            UnityEngine.Application.quitting += OnApplicationQuitting;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
            AssemblyReloadEvents.beforeAssemblyReload += OnBeforeAssemblyReload;
#endif
            hooksInstalled = true;
        }

        /// <summary>
        /// The finding records that an editor-side subscription made from runtime
        /// code outlives play mode and goes on delivering into the next session.
        /// This driver answers that by unsubscribing rather than by relocating the
        /// subscription, which is the opposite of what
        /// <c>MainThreadSessionScheduler</c> does and for the opposite reason: that
        /// type's latch must survive a domain reload, and this one's session cannot.
        /// </summary>
        private void RemoveHooks()
        {
            if (!hooksInstalled)
            {
                return;
            }

            UnityEngine.Application.quitting -= OnApplicationQuitting;
#if UNITY_EDITOR
            AssemblyReloadEvents.beforeAssemblyReload -= OnBeforeAssemblyReload;
#endif
            hooksInstalled = false;
        }

        /// <summary>
        /// The teardown runs here too, not only from the hooks, so a scope disposed
        /// without any quit - a scene unload, a container built and thrown away -
        /// still stops the session rather than cancelling its pump out from under
        /// it. It is idempotent: whichever of the two arrives first sets
        /// <c>shutdownStarted</c> and the other returns immediately.
        ///
        /// <para>The cancel comes after the stop for the reason
        /// <c>ProtocolSession.RunPumpAsync</c> spells out. Cancelling this token
        /// first is route 2 - it cancels the pump without failing any waiter - and
        /// StopAsync is what fails them truthfully. Reversed, an orderly disposal
        /// would report a fabricated timeout.</para>
        /// </summary>
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            RunShutdownWithoutAFrameToSpare("IDisposable.Dispose");
            RemoveHooks();
            shutdown.Cancel();
            shutdown.Dispose();
        }
    }
}
