# TCP Transport and Session Concurrency Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Give the protocol session a real TCP transport and make the session correct under the second thread that transport introduces.

**Architecture:** A streaming frame reader and a serialized writer in `Echo.Harness.Infrastructure` implement the existing `ITransport`. All session state stays lock-free; correctness comes from one hop through a new `ISessionScheduler` port placed at a single site in the receive pump. A `LoopbackProtocolServer` in TestKit provides byte-level control for framing tests, and one end-to-end test runs against the real Go server.

**Tech Stack:** Unity 6000.2.7f2, C# against .NET Standard 2.1, UniTask 2.5.11, Newtonsoft.Json 13.0.2, NUnit via Unity Test Framework, PowerShell for gates, Go for the server fixture.

Spec: `docs/superpowers/specs/2026-07-30-tcp-transport-and-session-concurrency-design.md` (committed at `52bd97a`). Branch: `tcp-transport-concurrency`.

## Global Constraints

- **Do not edit `E:\code\_github\magic-card-server-golang`.** Reading it and running `go test ./...` in it is fine. It is the authoritative server.
- **.NET Standard 2.1 only.** `Stream.ReadExactlyAsync`, `Socket.ConnectAsync(host, port, CancellationToken)`, and `TcpClient.ConnectAsync(string, int, CancellationToken)` do **not** exist. `Stream.ReadAsync(Memory<byte>, CancellationToken)`, `Stream.WriteAsync(ReadOnlyMemory<byte>, CancellationToken)`, and `Process.Kill(bool)` do.
- **`Echo.Harness.Application` has `noEngineReferences: true`** and the architecture gate bans the source text `UnityEngine|Addressables|R3|VContainer|XLua` over `Runtime\Application` (`Tools/ci/verify-architecture.ps1:198` — this plan originally cited `:130`, corrected 2026-07-31 after Task 1's review verified the real location). Nothing Unity-specific may enter Application.
- **`Echo.Harness.Domain` stays untouched.** Its ban list is `UnityEngine|Cysharp|R3|VContainer|XLua` (`:192`; originally cited `:124`, corrected with the line above).
- **Tests that drive a real socket use `[UnityTest]` + `IEnumerator` + `UniTask.ToCoroutine`, not `[Test]` + `.GetAwaiter().GetResult()`.** Added 2026-07-31, after Task 5 proved the point by running: five of that task's six tests hit UniTask's `"Not yet completed, UniTask only allow to use await."` before reaching a single assertion, because a real socket is genuinely asynchronous and `GetResult()` cannot block on an incomplete `UniTask`. `[UnityTest]` + `ToCoroutine` is already this repository's idiom for async tests — see `Tests/PlayMode/HarnessPlayerLoopTests.cs:13-24`. The EditMode assembly sets `includePlatforms: ["Editor"]`, so a `[UnityTest]` there stays editor-only and does **not** add to the PlayMode total. Tests driving only the in-memory `FakeTransport` keep `[Test]`, because that fake completes inline and the trap cannot arise. Ruled by the human partner and binding on every remaining task in this plan.
- **Do not add DI registrations.** `HarnessComposition.Configure` registers only `HarnessRuntimeDescriptor`. Session lifetime scopes are the next iteration's work; adding them here is out of scope.
- **Do not change `HarnessPolicy.ContainsGameplayImplementation`.** It stays `false`.
- **Never commit** `Library`, `Temp`, `Artifacts/`, restored `Assets/Packages`, or generated Addressables content.
- **Commit messages are English only.**
- **Max frame body is 1,048,576 bytes**, already `WireFrameSpec.MaxPayloadBytes`.
- **Server constants no environment variable can change:** ping interval 15 s, pong timeout 35 s, rate limit 30 msg/s. `RATE_LIMIT` is dead config. `LISTEN_ADDR` and `LOG_LEVEL` work.
- Run `.\Tools\ci\verify.ps1` before declaring a task done when that task changed runtime code.

---

### Task 1: The ISessionScheduler port

Threads the port through `ProtocolSession` with no behaviour change, so the 97 existing tests prove the plumbing is inert before anything depends on it.

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/HarnessPorts.cs` (append interface after `IClock`, currently ends line 92)
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs:13-28` (field and constructor)
- Create: `Packages/com.echo.harness/TestKit/RecordingSessionScheduler.cs`
- Modify: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs:17` and `:240`
- Modify: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs:15`
- Modify: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs:19`
- Test: `Packages/com.echo.harness/Tests/EditMode/SessionSchedulerTests.cs`

**Interfaces:**
- Produces: `Echo.Harness.Application.ISessionScheduler` with `UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken)`. `Echo.Harness.TestKit.RecordingSessionScheduler` with `IReadOnlyList<int> ObservedThreadIds`, `int SwitchCount`, and a settable `Exception NextFailure`. `ProtocolSession(ITransport, IClock, ISessionScheduler)` — three arguments, no two-argument overload.

- [ ] **Step 1: Write the failing test**

Create `Packages/com.echo.harness/Tests/EditMode/SessionSchedulerTests.cs`:

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SessionSchedulerTests
    {
        [Test]
        public void RecordingSchedulerCompletesSynchronouslyAndRecordsTheThread()
        {
            var scheduler = new RecordingSessionScheduler();

            var task = scheduler.SwitchToSessionContextAsync(CancellationToken.None);

            Assert.That(task.Status.IsCompletedSuccessfully(), Is.True,
                "A synchronous completion is what keeps the existing suite's timing unchanged.");
            Assert.That(scheduler.SwitchCount, Is.EqualTo(1));
            Assert.That(
                scheduler.ObservedThreadIds[0],
                Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
        }

        [Test]
        public void RecordingSchedulerHonoursCancellation()
        {
            var scheduler = new RecordingSessionScheduler();
            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => scheduler.SwitchToSessionContextAsync(cancellation.Token));
            Assert.That(scheduler.SwitchCount, Is.EqualTo(0));
        }

        [Test]
        public void RecordingSchedulerCanBeMadeToFail()
        {
            var scheduler = new RecordingSessionScheduler
            {
                NextFailure = new InvalidOperationException("no context")
            };

            var failure = Assert.Throws<InvalidOperationException>(
                () => scheduler.SwitchToSessionContextAsync(CancellationToken.None)
                    .GetAwaiter().GetResult());
            Assert.That(failure.Message, Is.EqualTo("no context"));
            Assert.That(scheduler.NextFailure, Is.Null, "The failure is one-shot.");
        }
    }
}
```

- [ ] **Step 2: Run the test and confirm it fails to compile**

Run: `.\Tools\ci\run-unity-tests.ps1` (or the Unity Test Runner, EditMode)
Expected: compile error, `RecordingSessionScheduler` and `ISessionScheduler` do not exist.

- [ ] **Step 3: Add the port**

Append to `Packages/com.echo.harness/Runtime/Application/HarnessPorts.cs`, inside the namespace after the `IClock` interface:

```csharp
    /// <summary>
    /// Moves the current continuation onto the session's context. A session's
    /// receive pump resumes on whatever thread the transport's I/O completed on,
    /// and its request timeouts resume on a timer thread; both are hopped through
    /// here so that everything touching session state runs on one context and the
    /// session itself needs no lock.
    ///
    /// The production implementation switches to the Unity main thread, which is
    /// why this is a port rather than a direct call: Application is compiled with
    /// noEngineReferences and cannot name a Unity type. A test implementation
    /// completes synchronously, which is also what keeps EditMode tests
    /// independent of a player loop that EditMode does not run.
    /// </summary>
    public interface ISessionScheduler
    {
        UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken);
    }
```

- [ ] **Step 4: Add the TestKit implementation**

Create `Packages/com.echo.harness/TestKit/RecordingSessionScheduler.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Completes synchronously and records the thread each switch was requested
    /// from. Synchronous completion is deliberate: it leaves the session's
    /// observable timing identical to the pre-hop behaviour, so a test that fails
    /// after the hop is added is reporting a real change rather than a scheduling
    /// artifact.
    /// </summary>
    public sealed class RecordingSessionScheduler : ISessionScheduler
    {
        private readonly List<int> observedThreadIds = new List<int>();

        public IReadOnlyList<int> ObservedThreadIds => observedThreadIds;

        public int SwitchCount => observedThreadIds.Count;

        /// <summary>Makes the next switch fail. One-shot.</summary>
        public Exception NextFailure { get; set; }

        public UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();

            if (NextFailure != null)
            {
                var failure = NextFailure;
                NextFailure = null;
                throw failure;
            }

            observedThreadIds.Add(Thread.CurrentThread.ManagedThreadId);
            return UniTask.CompletedTask;
        }
    }
}
```

- [ ] **Step 5: Take the port in the constructor**

In `ProtocolSession.cs`, add the field next to `clock` (currently line 14) and extend the constructor (currently lines 24-28):

```csharp
        private readonly IClock clock;
        private readonly ISessionScheduler scheduler;
```

```csharp
        public ProtocolSession(ITransport transport, IClock clock, ISessionScheduler scheduler)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }
```

Do **not** add a two-argument overload. An overload that defaulted the scheduler would let a production call site silently skip the hop, which is the one failure this port exists to prevent.

- [ ] **Step 6: Update the four construction sites**

`ProtocolSessionDispatchTests.cs:17`, `ProtocolSessionLifecycleTests.cs:15`, and `ProtocolSessionRequestTests.cs:19` are factory helpers. `ProtocolSessionDispatchTests.cs:240` is inline. Each gains a third argument. Give each helper an `out` parameter for the scheduler so later tasks can assert on it — the shape to apply to all three:

```csharp
        private static ProtocolSession CreateSession(
            out FakeTransport transport,
            out RecordingSessionScheduler scheduler)
        {
            transport = new FakeTransport();
            scheduler = new RecordingSessionScheduler();
            return new ProtocolSession(
                transport, new ManualClock(DateTimeOffset.UnixEpoch), scheduler);
        }
```

Update every caller of each helper to match. `ProtocolSessionRequestTests.cs:19` uses a named `clock` local; keep it and pass a fresh `RecordingSessionScheduler`.

- [ ] **Step 7: Run the whole EditMode suite**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 100/100 passed.` (97 existing plus the 3 new). The count is the check that the port is inert: any existing test whose behaviour changes here means the scheduler is not completing synchronously.

- [ ] **Step 8: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/HarnessPorts.cs \
        Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/TestKit/RecordingSessionScheduler.cs \
        Packages/com.echo.harness/Tests/EditMode/SessionSchedulerTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs
git commit -m "Add the session scheduler port without changing session behaviour"
```

---

### Task 2: Main-thread confinement

The hop and the `try` widening land together because the second makes the first's safety local instead of borrowed. The spec's "borrowed guarantee" section is the reason.

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs:299-320` (`RunPumpAsync`), the comment at `:354-373`, the doc at `:381-390`, the doc at `:406-412`
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/SessionDiagnostics.cs:13-20` (enum)
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionConfinementTests.cs`

**Interfaces:**
- Consumes: `ISessionScheduler`, `RecordingSessionScheduler` from Task 1.
- Produces: `SessionFaultKind.DispatchFailure`, appended to the existing enum so no ordinal shifts.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionConfinementTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionConfinementTests
    {
        private static ProtocolSession CreateStarted(
            out FakeTransport transport,
            out RecordingSessionScheduler scheduler,
            out List<SessionFault> faults)
        {
            transport = new FakeTransport();
            scheduler = new RecordingSessionScheduler();
            var session = new ProtocolSession(
                transport, new ManualClock(DateTimeOffset.UnixEpoch), scheduler);
            faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void EveryDispatchedMessageHopsToTheSessionContextFirst()
        {
            using var session = CreateStarted(out var transport, out var scheduler, out _);

            transport.EnqueueInbound(ProtocolTestFrames.Bodyless(MessageId.Ping));
            transport.EnqueueInbound(ProtocolTestFrames.Bodyless(MessageId.Ping));

            Assert.That(scheduler.SwitchCount, Is.EqualTo(2),
                "One hop per received message, before it is dispatched.");
            Assert.That(
                scheduler.ObservedThreadIds.Distinct().Count(),
                Is.EqualTo(1),
                "All dispatch happens on one context.");
        }

        [Test]
        public void ARequestTimeoutHopsBeforeItsFinallyTouchesTheGate()
        {
            using var session = CreateStarted(out _, out var scheduler, out _);
            var before = scheduler.SwitchCount;

            Assert.Throws<TimeoutException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto { PlayerName = "redacted" },
                    TimeSpan.FromMilliseconds(50),
                    CancellationToken.None).GetAwaiter().GetResult());

            Assert.That(scheduler.SwitchCount, Is.EqualTo(before + 1),
                "A timeout resumes on the CancelAfter timer's thread, and the finally " +
                "below it mutates pendingRequests. The hop is what keeps that " +
                "dictionary single-threaded.");
        }

        [Test]
        public void AnUndecodableMessageDoesNotKillThePump()
        {
            using var session = CreateStarted(out var transport, out _, out var faults);

            transport.EnqueueInbound(new TransportMessage((MessageId)60000, new byte[0]));

            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            var delivered = 0;
            session.Subscribe<PhaseChangeEventDto>(
                MessageId.PhaseChangeEvent, _ => delivered++);
            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}"));

            Assert.That(delivered, Is.EqualTo(1),
                "A pump killed by the previous message would deliver nothing.");
            Assert.That(
                faults.Select(f => f.Kind),
                Does.Contain(SessionFaultKind.UnknownMessageId));
        }

        [Test]
        public void AFailingHopFaultsTheStreamRatherThanDyingUnobserved()
        {
            using var session = CreateStarted(out var transport, out var scheduler, out var faults);

            scheduler.NextFailure = new InvalidOperationException("no player loop");
            transport.EnqueueInbound(ProtocolTestFrames.Bodyless(MessageId.Ping));

            Assert.That(session.State, Is.EqualTo(SessionState.Faulted),
                "A hop that cannot happen means nothing can be dispatched safely.");
            Assert.That(
                faults.Select(f => f.Kind),
                Does.Contain(SessionFaultKind.TransportFailure));
        }
    }
}
```

If `PhaseChangeEventDto`'s JSON shape differs from `{"phase":"action"}`, take the correct shape from `ProtocolDtoSerializationTests.cs` rather than guessing; the assertion under test is the delivery count, not the payload.

- [ ] **Step 2: Run and confirm failure**

Expected: `EveryDispatchedMessageHopsToTheSessionContextFirst` fails with `SwitchCount` 0, and `AFailingHopFaultsTheStreamRatherThanDyingUnobserved` fails because the hop does not exist yet.

- [ ] **Step 3: Add the fault kind**

In `SessionDiagnostics.cs`, extend the enum (`:13-20`) with a final member so existing ordinals are unchanged:

```csharp
    public enum SessionFaultKind
    {
        UnknownMessageId,
        MalformedPayload,
        CorrelationMismatch,
        SubscriberFailure,
        TransportFailure,
        DispatchFailure
    }
```

- [ ] **Step 4: Rewrite the pump**

Replace `RunPumpAsync` (`:299-320`) with:

```csharp
        private async UniTaskVoid RunPumpAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TransportMessage message;
                try
                {
                    message = await transport.ReceiveAsync(cancellationToken);

                    // Everything below runs on the session's context. The hop is
                    // here, once, rather than at each call site inside Dispatch,
                    // so that "did this path hop?" has exactly one place to look.
                    await scheduler.SwitchToSessionContextAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    await FaultTheStreamAsync(exception);
                    return;
                }

                // Inside a try, unlike before. Each callee still guards itself, but
                // a future branch that forgets to now costs one message and a fault
                // instead of the pump, and State can no longer read Connected over
                // a pump that a Dispatch exception already killed.
                try
                {
                    Dispatch(message);
                }
                catch (Exception exception)
                {
                    PublishFault(new SessionFault(
                        SessionFaultKind.DispatchFailure,
                        message.MessageId,
                        exception.Message));
                }
            }
        }
```

The hop shares the receive's `try` so a scheduler that cannot deliver a context faults the stream rather than dying unobserved. `Dispatch` gets its own `try` because a dispatch failure costs one message, while a hop failure means no message can be handled safely at all — different grades, different handling.

Then hop the other path that resumes off-context. In `RequestAsync` (`:185-189`), the timeout branch resumes on the `CancelAfter` timer's thread-pool thread, and the `finally` below it mutates `pendingRequests`. Replace that branch with:

```csharp
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    // Hopped before the throw, which means before this frame's
                    // finally removes its gate entry. Without it the timer thread
                    // and the pump can mutate pendingRequests concurrently, and a
                    // Dictionary resized from two threads can misroute a response
                    // to subscribers. The success path needs no hop: TrySetResult
                    // is called from Dispatch, which already ran on the context.
                    await scheduler.SwitchToSessionContextAsync(cancellationToken);
                    throw new TimeoutException(
                        $"{requestId} received no {responseId} within {timeout}.");
                }
```

The `when` clause narrows the window in which the hop could itself throw a cancellation and swallow the timeout.

> **Corrected 2026-07-31, after Task 2's review.** This step originally said the
> `when` clause *guarantees* `cancellationToken` is not cancelled here. It does
> not: the filter evaluates `!cancellationToken.IsCancellationRequested`, then
> the handler body calls `SwitchToSessionContextAsync(cancellationToken)`, which
> throws if cancellation arrives in the gap. That is a TOCTOU, and benign in
> effect (the caller did cancel). The sharper residual, deferred as a Task 2
> minor and carried into Task 8: if the hop throws for a **non**-cancellation
> reason — which is exactly what a real player-loop scheduler does when no
> player loop is running — the `TimeoutException` is replaced, the outer
> `finally` still removes the gate entry while running off-context (the very
> mutation this hop exists to prevent), and no `SessionFault` is published. The
> pump path faults the stream on a hop failure; this path silently degrades to
> pre-hop behaviour. Task 8 must revisit it.

- [ ] **Step 5: Narrow the borrowed-guarantee comment**

> **Corrected 2026-07-31, after Task 2's review.** The text this step originally
> mandated was wrong, and it shipped before the review caught it (fixed in
> `a7da815`). It claimed a throwing requester continuation "is caught there
> [the pump's try], published as a DispatchFailure, and the pump continues."
> It is not. `RequestAsync` awaits through `AttachExternalCancellation`, whose
> runner wraps the inline `TrySetResult`, so the throw unwinds into *that* try
> and `TrySetException` drops it on the already-completed core. The pump's try
> never sees it. The original step also deleted the pre-existing paragraph that
> documented this correctly. Widening the pump's try makes the safety local for
> *future branches of Dispatch*, not for the requester-continuation path.
> The block below is the text that actually shipped.

In `Dispatch`, replace the block at `:354-373` (from "The safety of this line is borrowed" through "returns false and drops it silently.") with:

```csharp
                // TrySetResult resumes the requester's continuation inline, on
                // this stack, so the requester's finally has run - and its gate
                // entry is gone - before this method returns. That is what the
                // paragraph above relies on.
                //
                // What this line does not get is cover from the pump's try, and
                // the difference matters to anyone changing the timeout
                // mechanism. A requester continuation that resumes from here and
                // throws is swallowed by UniTask before it can reach Dispatch's
                // caller. Today that happens in AttachExternalCancellation's
                // runner body,
                //     try { core.TrySetResult(await task); }
                //     catch (Exception ex) { core.TrySetException(ex); }
                // (UniTaskExtensions.cs:314-328), where TrySetException on an
                // already-completed core returns false and drops the exception
                // with no report at all (UniTaskCompletionSource.cs:150-173).
                // Dropping AttachExternalCancellation would not hand this line to
                // the pump's try either: TrySignalCompletion invokes the
                // continuation inside its own catch and routes a throw to
                // UniTaskScheduler.PublishUnobservedTaskException
                // (UniTaskCompletionSource.cs:910-917).
                //
                // So a broken requester continuation is contained but never
                // reported to this session - the pump survives, and no
                // SessionFault is published for it. That is a known diagnostic
                // hole rather than a guarantee. The pump's try is insurance for
                // future branches of this method; it is not what protects this
                // line.
```

- [ ] **Step 6: Update the two docs that cite the old shape**

`ReplyToHeartbeatAsync`'s doc (`:381-390`) opens "Dispatch runs outside the pump's try" and `DeliverToSubscribers`' doc (`:406-412`) says "Dispatch runs on the pump's stack outside its try block". Both are now false. Replace each of those clauses with the shared opening — "Dispatch now runs inside the pump's try, so an escaping exception costs one message rather than the pump" — then give each doc **only** the justification that actually holds there:

- `ReplyToHeartbeatAsync`: the guard is kept because it reports the failure against the right message id. Without it a heartbeat send failure would surface as a `DispatchFailure` against the inbound `Ping` instead of a `TransportFailure` against the `Pong` that actually failed. Also drop ", killing the pump on an open connection" from this doc — it is no longer true once `Dispatch` sits inside the try.
- `DeliverToSubscribers`: the guard is kept for the `SubscriberFailure` grading and per-handler isolation, so one broken subscriber cannot silence the rest.

Leave the remainder of each doc unchanged.

> **Corrected 2026-07-31, after Task 2's review.** This step originally mandated
> one sentence for both docs, ending "...because it reports the failure against
> the right message id and keeps one broken subscriber from silencing the rest."
> Half of it is wrong in each place: there are no subscribers anywhere on the
> heartbeat-reply path, and on the subscriber path the message id is
> `result.MessageId` either way, so the attribution clause is vacuous there.
> Applying it literally also produced ungrammatical prose in one doc. Split as
> above; shipped in `a7da815`.

- [ ] **Step 7: Run the suite**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 104/104 passed.`

- [ ] **Step 8: Mutation-verify the confinement test**

Temporarily delete the `await scheduler.SwitchToSessionContextAsync(...)` line, run `ProtocolSessionConfinementTests`, and confirm `EveryDispatchedMessageHopsToTheSessionContextFirst` fails. Restore the line. A confinement test that passes without the hop is worthless, and this is the only way to know which you have.

- [ ] **Step 9: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Runtime/Application/Session/SessionDiagnostics.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionConfinementTests.cs
git commit -m "Confine session dispatch to one context and contain dispatch failures"
```

---

### Task 3: Session lifecycle repairs

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs:11` (add constant), `:57-77` (`StopAsync`), `:275-297` (`Dispose`)
- Modify: `Packages/com.echo.harness/TestKit/DeterministicFakes.cs:16` (field), `:55-59` (setter), `:142-153` (`DisconnectAsync`)
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs` (append)

**Interfaces:**
- Consumes: `FakeTransport`, `RecordingSessionScheduler`, and the `CreateSession(out FakeTransport, out RecordingSessionScheduler)` helper from Task 1.
- Produces: `FakeTransport.FailNextDisconnect(Exception)`, `FakeTransport.DisconnectCount`, `ProtocolSession.DisposeDisconnectDeadline`.

- [ ] **Step 1: Extend FakeTransport**

In `DeterministicFakes.cs`, add a field beside `nextSendFailure` (`:16`), a counter, and a setter beside `FailNextSend` (`:55-59`):

```csharp
        private Exception nextDisconnectFailure;

        public int DisconnectCount { get; private set; }

        /// <summary>
        /// Makes the next disconnect fail, standing in for a socket that throws on
        /// close. StopAsync must still fail its waiters when this happens.
        /// </summary>
        public void FailNextDisconnect(Exception failure)
        {
            nextDisconnectFailure = failure ?? throw new ArgumentNullException(nameof(failure));
        }
```

Then rewrite `DisconnectAsync` (`:142-153`):

```csharp
        public UniTask DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            DisconnectCount++;
            State = TransportState.Disconnected;

            if (pendingReceive != null)
            {
                TakePendingReceive().TrySetCanceled(cancellationToken);
            }

            // Thrown after the state change and after the pending receive is
            // released, so a failing close still leaves the fake in the state a
            // real closed socket would be in. A transport that threw before
            // releasing the receive would hang the pump instead of failing it.
            if (nextDisconnectFailure != null)
            {
                var failure = nextDisconnectFailure;
                nextDisconnectFailure = null;
                throw failure;
            }

            return UniTask.CompletedTask;
        }
```

- [ ] **Step 2: Write the failing tests**

Append to `ProtocolSessionLifecycleTests.cs`, adding `using System.IO;` and `using System.Collections.Generic;` to its using block if absent:

```csharp
        [Test]
        public void StopAsyncFailsWaitersEvenWhenDisconnectThrows()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            transport.FailNextDisconnect(new IOException("socket already gone"));

            Assert.Throws<IOException>(
                () => session.StopAsync(CancellationToken.None).GetAwaiter().GetResult());

            var failure = Assert.Throws<InvalidOperationException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("stopped before the response"));
        }

        [Test]
        public void StopAsyncFailsWaitersWhenHandedAnAlreadyCancelledToken()
        {
            var session = CreateSession(out _, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(5),
                CancellationToken.None);

            using var cancelled = new CancellationTokenSource();
            cancelled.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => session.StopAsync(cancelled.Token).GetAwaiter().GetResult());
            Assert.Throws<InvalidOperationException>(() => pending.GetAwaiter().GetResult());
        }

        [Test]
        public void DisposeRequestsATransportDisconnect()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            session.Dispose();

            Assert.That(transport.DisconnectCount, Is.EqualTo(1),
                "An undisconnected socket leaves the server holding a ghost session " +
                "until its 35 second pong timeout.");
        }

        [Test]
        public void DisposeSurvivesAThrowingDisconnect()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            transport.FailNextDisconnect(new IOException("socket already gone"));

            Assert.DoesNotThrow(() => session.Dispose());
        }

        [Test]
        public void DisposeOnANeverStartedSessionDoesNotTouchTheTransport()
        {
            var session = CreateSession(out var transport, out _);

            session.Dispose();

            Assert.That(transport.DisconnectCount, Is.EqualTo(0),
                "There is nothing to close, and calling DisconnectAsync on an " +
                "unconnected transport is not universally safe.");
        }

        [Test]
        public void StopAsyncFromFaultedReachesDisconnectedWithoutASecondDisconnect()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            transport.FailNextReceive(new IOException("stream desynchronized"));
            Assert.That(session.State, Is.EqualTo(SessionState.Faulted));
            var disconnectsAfterFault = transport.DisconnectCount;

            session.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(transport.DisconnectCount, Is.EqualTo(disconnectsAfterFault),
                "The fault path already disconnected, and a second close is not " +
                "idempotent on every real transport.");
        }

        [Test]
        public void AFaultedSessionCanBeStoppedAndStartedAgain()
        {
            var session = CreateSession(out var transport, out _);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            transport.FailNextReceive(new IOException("stream desynchronized"));
            session.StopAsync(CancellationToken.None).GetAwaiter().GetResult();

            Assert.DoesNotThrow(
                () => session.StartAsync(CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "This is the seam reconnect will use next iteration.");
        }
```

- [ ] **Step 3: Run and confirm failure**

Expected failures: both `StopAsync` waiter tests (waiters stranded), `DisposeRequestsATransportDisconnect` (no disconnect at all), and `StopAsyncFromFaultedReachesDisconnectedWithoutASecondDisconnect` (a second close happens).

- [ ] **Step 4: Rewrite StopAsync**

Replace `StopAsync` (`:57-77`) with:

```csharp
        public async UniTask StopAsync(CancellationToken cancellationToken)
        {
            if (State == SessionState.Disconnected)
            {
                return;
            }

            // Captured before the state changes. The fault path has already closed
            // the transport, and a second DisconnectAsync is idempotent on the fake
            // and on a well-behaved socket but not on every transport.
            var alreadyDisconnected = State == SessionState.Faulted;

            CancelPump();
            try
            {
                if (!alreadyDisconnected)
                {
                    await transport.DisconnectAsync(cancellationToken);
                }
            }
            finally
            {
                // In the finally, not after the await. A throwing disconnect - or
                // an already-cancelled token, which is a realistic shutdown
                // pattern - would otherwise strand every waiter and leave State
                // reading Connected over a dead pump.
                //
                // The state transition still precedes the failures, for the reason
                // it always did: TrySetException resumes each waiter inline on this
                // stack, so a waiter is free to re-enter the session before this
                // method returns, and reaching Disconnected first means such a call
                // is refused with the truth.
                State = SessionState.Disconnected;
                FailPendingRequests(new InvalidOperationException(
                    "The session was stopped before the response arrived. The request " +
                    "may still have reached the server; stopping does not cancel it."));
            }
        }
```

- [ ] **Step 5: Add the dispose deadline constant**

In `ProtocolSession.cs`, beside the timeout constant at `:11`:

```csharp
        /// <summary>
        /// How long a Dispose-initiated disconnect is given before it is abandoned.
        /// Short on purpose: Dispose has no caller waiting on it, and a close that
        /// has not completed by now will not start helping.
        /// </summary>
        public static readonly TimeSpan DisposeDisconnectDeadline = TimeSpan.FromSeconds(2);
```

- [ ] **Step 6: Rewrite Dispose**

Replace `Dispose` (`:275-297`) with:

```csharp
        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CancelPump();

            // Whether there is a connection to close is decided before State is
            // reset. Disposing a session that was never started must not call
            // DisconnectAsync on a transport that was never connected.
            var closing = State != SessionState.Disconnected;

            faultHandlers.Clear();
            subscribers.Clear();
            State = SessionState.Disconnected;

            // Failing them, not merely dropping them. Clearing the dictionary
            // discards the completion sources without completing them, which
            // leaves every waiter with nothing that can ever tell it anything;
            // it would then wait out its full timeout and report a network
            // failure that never happened. Disposal is synchronous, but so is
            // TrySetException, so there is nothing here that needs awaiting.
            //
            // Before the disconnect is launched, not after: a transport whose
            // close releases a parked receive resumes the pump inline, and a
            // waiter resumed from there would otherwise see a half-cleared gate.
            FailPendingRequests(new ObjectDisposedException(
                nameof(ProtocolSession),
                "The session was disposed before the response arrived."));

            if (closing)
            {
                DisconnectOnDisposeAsync().Forget();
            }
        }

        /// <summary>
        /// The disconnect Dispose cannot await. Bounded fire-and-forget was chosen
        /// over a documented "stop before disposing" contract because leaving the
        /// socket open makes the server hold the session until its 35 second pong
        /// timeout, so a player who quits leaves a ghost behind.
        ///
        /// The try/catch is required rather than tidy: this runs with no caller on
        /// the stack, so an escaping exception would reach the unobserved-exception
        /// handler and be reported as an unrelated crash. There is nowhere to
        /// publish a fault either - Dispose has already cleared the handlers.
        /// </summary>
        private async UniTaskVoid DisconnectOnDisposeAsync()
        {
            try
            {
                using var deadline = new CancellationTokenSource(DisposeDisconnectDeadline);
                await transport.DisconnectAsync(deadline.Token);
            }
            catch
            {
                // Nothing to tell, and no one left to tell it to.
            }
        }
```

- [ ] **Step 7: Run the suite**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 111/111 passed.`

- [ ] **Step 8: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/TestKit/DeterministicFakes.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs
git commit -m "Close the session stop and dispose lifecycle holes"
```

---

### Task 4: Diagnostics the caller can act on

Six repairs sharing one theme: a consumer currently cannot tell two different situations apart.

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/SessionDiagnostics.cs:37` (the `MessageId` doc), enum, and append two exception types
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs:11` (rename), `:163-168`, `:213`, `:215-225`, `:324-334`, `:415-418`, `:441-472`
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/IProtocolSession.cs:30` (`Subscribe` doc)
- Modify: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs:55`, `:406` (comment), `:434`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDiagnosticsTests.cs`

**Interfaces:**
- Produces: `Echo.Harness.Application.RequestAlreadyInFlightException` (with `MessageId ResponseId`) and `Echo.Harness.Application.CorrelationMismatchException` (with `MessageId MessageId`), both deriving from `InvalidOperationException`. `SessionFaultKind.NoDestination`. `ProtocolSession.RoundTripProbeDeadline` replaces `ProtocolSession.DefaultRequestTimeout`.

- [ ] **Step 1: Add the two exception types and the fault kind**

Append `NoDestination` to `SessionFaultKind`, after `DispatchFailure`. Then append to `SessionDiagnostics.cs`, inside the namespace:

```csharp
    /// <summary>
    /// A second request for a response id that already has one in flight. Distinct
    /// from <see cref="CorrelationMismatchException"/> because a probe loop must be
    /// able to tell "mine is still running" from "the server answered wrongly"
    /// without matching on message text.
    /// </summary>
    public sealed class RequestAlreadyInFlightException : InvalidOperationException
    {
        public RequestAlreadyInFlightException(MessageId responseId, string message)
            : base(message)
        {
            ResponseId = responseId;
        }

        public MessageId ResponseId { get; }
    }

    /// <summary>
    /// A reply whose correlatable field does not match what was sent. The protocol
    /// carries no correlation identifier, so this is only detectable where a
    /// payload echoes something back - today, ClientPingResponse.ts.
    /// </summary>
    public sealed class CorrelationMismatchException : InvalidOperationException
    {
        public CorrelationMismatchException(MessageId messageId, string message)
            : base(message)
        {
            MessageId = messageId;
        }

        public MessageId MessageId { get; }
    }
```

- [ ] **Step 2: Fix the SessionFault.MessageId doc**

Replace `SessionDiagnostics.cs:37` (`<summary>Carries no meaning when <see cref="Kind"/> is TransportFailure.</summary>`) with:

```csharp
        /// <summary>
        /// The message this fault concerns, or <c>default</c> when no single
        /// message does. A stream fault passes <c>default</c>; a heartbeat write
        /// failure passes <see cref="Contracts.MessageId.Pong"/>. Kind is identical
        /// for both, so this field is the only thing separating "the heartbeat
        /// write failed and the connection is probably still usable" from "the
        /// stream desynchronized". Do not treat it as meaningless.
        /// </summary>
```

- [ ] **Step 3: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDiagnosticsTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionDiagnosticsTests
    {
        private static ProtocolSession CreateStarted(
            out FakeTransport transport,
            out List<SessionFault> faults)
        {
            transport = new FakeTransport();
            var session = new ProtocolSession(
                transport,
                new ManualClock(DateTimeOffset.UnixEpoch),
                new RecordingSessionScheduler());
            faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void ASecondInFlightRequestThrowsItsOwnType()
        {
            using var session = CreateStarted(out _, out _);
            var payload = new LoginRequestDto { PlayerName = "redacted" };

            session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, payload, TimeSpan.FromSeconds(5),
                CancellationToken.None).Forget();

            var failure = Assert.Throws<RequestAlreadyInFlightException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest, payload, TimeSpan.FromSeconds(5),
                    CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(failure.ResponseId, Is.EqualTo(MessageId.LoginResponse));
        }

        [Test]
        public void AStaleRoundTripEchoThrowsItsOwnType()
        {
            using var session = CreateStarted(out var transport, out var faults);

            var probe = session.ProbeRoundTripAsync(CancellationToken.None);
            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.ClientPingResponse, "{\"ts\":999}"));

            var failure = Assert.Throws<CorrelationMismatchException>(
                () => probe.GetAwaiter().GetResult());
            Assert.That(failure.MessageId, Is.EqualTo(MessageId.ClientPingResponse));
            Assert.That(failure.Message, Does.Contain("999"));
            Assert.That(
                faults.Single(f => f.Kind == SessionFaultKind.CorrelationMismatch).MessageId,
                Is.EqualTo(MessageId.ClientPingResponse));
        }

        [Test]
        public void ADecodeFailureOnAPendingResponseIdAnswersTheWaiter()
        {
            using var session = CreateStarted(out var transport, out var faults);

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "redacted" },
                TimeSpan.FromSeconds(30),
                CancellationToken.None);

            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.LoginResponse, "{not json"));

            var failure = Assert.Throws<InvalidOperationException>(
                () => pending.GetAwaiter().GetResult());
            Assert.That(failure, Is.Not.InstanceOf<TimeoutException>(),
                "Stalling the full timeout for a reply that already arrived and " +
                "failed to parse reports a network problem that did not happen.");
            Assert.That(
                faults.Select(f => f.Kind),
                Does.Contain(SessionFaultKind.MalformedPayload));
        }

        [Test]
        public void AMessageWithNoSubscriberPublishesAFault()
        {
            using var session = CreateStarted(out var transport, out var faults);

            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}"));

            var fault = faults.Single();
            Assert.That(fault.Kind, Is.EqualTo(SessionFaultKind.NoDestination));
            Assert.That(fault.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "One undelivered message must not cost the connection.");
        }

        [Test]
        public void ADisposedSubscriptionLeavesNoSubscriberBehind()
        {
            using var session = CreateStarted(out var transport, out var faults);

            var subscription = session.Subscribe<PhaseChangeEventDto>(
                MessageId.PhaseChangeEvent, _ => { });
            subscription.Dispose();

            transport.EnqueueInbound(ProtocolTestFrames.Frame(
                MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}"));

            Assert.That(
                faults.Single().Kind,
                Is.EqualTo(SessionFaultKind.NoDestination),
                "Subscribe leaves an empty handler list behind, so a key check " +
                "alone would report a destination that is not there.");
        }

        [Test]
        public void TheStreamFaultReportsTheCauseBeforeTheSymptom()
        {
            using var session = CreateStarted(out var transport, out var faults);
            transport.FailNextDisconnect(new IOException("close failed"));

            transport.FailNextReceive(new IOException("stream desynchronized"));

            var transportFaults = faults
                .Where(f => f.Kind == SessionFaultKind.TransportFailure)
                .ToList();
            Assert.That(transportFaults.Count, Is.EqualTo(2));
            Assert.That(transportFaults[0].Diagnostic, Does.Contain("desynchronized"),
                "A consumer that reads the first TransportFailure - the natural " +
                "thing to do - must get the cause, not the close that followed it.");
            Assert.That(transportFaults[1].Diagnostic, Does.Contain("close failed"));
        }

        [Test]
        public void TheRoundTripProbeDeadlineIsPinned()
        {
            Assert.That(
                ProtocolSession.RoundTripProbeDeadline,
                Is.EqualTo(TimeSpan.FromSeconds(10)),
                "Raising this for a slow login would silently give every latency " +
                "probe the longer deadline, which is why it is named for the probe.");
        }
    }
}
```

- [ ] **Step 4: Run and confirm failure**

Expected: compile errors on the two new exception types being thrown nowhere yet is not the failure — they exist from Step 1. The failures are behavioural: the gate and echo tests get a bare `InvalidOperationException`, the decode test times out, both `NoDestination` tests see no fault, the ordering test sees the two faults reversed, and `RoundTripProbeDeadline` does not exist.

- [ ] **Step 5: Rename the constant**

In `ProtocolSession.cs:11`:

```csharp
        /// <summary>
        /// The deadline <see cref="ProbeRoundTripAsync"/> uses. It is not a default
        /// for <see cref="RequestAsync{TResponse}"/>, which has no overload that
        /// omits a timeout, and it is unreachable from <see cref="IProtocolSession"/>.
        /// </summary>
        public static readonly TimeSpan RoundTripProbeDeadline = TimeSpan.FromSeconds(10);
```

Update the single use at `:213`. Then fix the stale comment at `ProtocolSessionRequestTests.cs:406`, which names `DefaultRequestTimeout`.

- [ ] **Step 6: Throw the two new types**

At `:163-168`:

```csharp
            if (pendingRequests.ContainsKey(responseId))
            {
                throw new RequestAlreadyInFlightException(
                    responseId,
                    $"A request awaiting {responseId} is already in flight. The protocol has " +
                    "no correlation id, so a second one could be answered with the first reply.");
            }
```

At `:224`, replace `throw new InvalidOperationException(diagnostic);` with:

```csharp
                throw new CorrelationMismatchException(MessageId.ClientPingResponse, diagnostic);
```

Both derive from `InvalidOperationException`, so existing assertions still pass through inheritance. Tighten `ProtocolSessionRequestTests.cs:55` to `Assert.Throws<RequestAlreadyInFlightException>` and `:434` to `Assert.Throws<CorrelationMismatchException>` in this step, since a test asserting the base type would no longer be pinning what it names.

- [ ] **Step 7: Answer a requester whose response fails to decode**

Replace the decode-failure branch in `Dispatch` (`:324-334`) with:

```csharp
            var result = ProtocolCodec.Decode(message.MessageId, message.Payload);
            if (!result.Succeeded)
            {
                var kind = result.Failure == ProtocolDecodeFailure.UnknownMessageId
                    ? SessionFaultKind.UnknownMessageId
                    : SessionFaultKind.MalformedPayload;
                PublishFault(new SessionFault(kind, message.MessageId, result.Diagnostic));

                // The reply arrived; it just could not be read. Leaving the waiter
                // pending makes it stall its whole timeout and then report a
                // network failure that never happened. Failed after the fault is
                // published so a consumer sees the cause before the effect.
                if (pendingRequests.TryGetValue(message.MessageId, out var stalled))
                {
                    stalled.TrySetException(new InvalidOperationException(
                        $"{message.MessageId} arrived but could not be decoded: " +
                        result.Diagnostic));
                }

                return;
            }
```

- [ ] **Step 8: Fault an undeliverable message**

Replace `DeliverToSubscribers`' early return (`:415-418`) with:

```csharp
            if (!subscribers.TryGetValue(result.MessageId, out var handlers) ||
                handlers.Count == 0)
            {
                // A change from silently dropping it. The server's reconnect path
                // sends LoginResponse and MatchFoundEvent back to back, so a
                // consumer that subscribes after requesting loses the event with no
                // trace. Until subscribe-before-request is enforced, this fault is
                // the only thing that can show it happened.
                //
                // The Count check is not redundant: Subscribe leaves an empty list
                // behind when the last subscription is disposed, so a key check
                // alone would report a destination that is not there.
                PublishFault(new SessionFault(
                    SessionFaultKind.NoDestination,
                    result.MessageId,
                    $"{result.MessageId} decoded but no subscriber was registered."));
                return;
            }
```

- [ ] **Step 9: Publish the cause before the symptom**

Rewrite `FaultTheStreamAsync` (`:441-472`), keeping the existing comments above `FailPendingRequests` and `CancelPump` verbatim:

```csharp
        private async UniTask FaultTheStreamAsync(Exception exception)
        {
            State = SessionState.Faulted;

            // Waiters are failed before anything else: nothing will ever answer
            // them once the pump stops, so leaving them pending would hang the
            // caller until its timeout with no explanation of why. They get the
            // receive failure itself, which is the root cause.
            FailPendingRequests(exception);

            // The pump returns as soon as this method does, so the token it is
            // running under has no further use. Releasing it here matters
            // because it is linked to the token the caller passed to StartAsync,
            // which outlives the session; leaving it registered would pin this
            // session on an application-lifetime token until disposal.
            CancelPump();

            // Published before the disconnect is attempted, so the first
            // TransportFailure a consumer sees is the cause rather than the close
            // that followed it.
            PublishFault(new SessionFault(
                SessionFaultKind.TransportFailure, default, exception.Message));

            try
            {
                await transport.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception disconnectFailure)
            {
                PublishFault(new SessionFault(
                    SessionFaultKind.TransportFailure, default, disconnectFailure.Message));
            }
        }
```

- [ ] **Step 10: Document the subscribe-first rule**

In `IProtocolSession.cs`, above `Subscribe<TPayload>` (`:30`):

```csharp
        /// <summary>
        /// Subscribe before the request that provokes the message. The protocol
        /// pushes events without waiting: the server's reconnect path sends
        /// LoginResponse and MatchFoundEvent back to back, so a subscription
        /// registered after the login returns can miss the event entirely. A
        /// message with no subscriber publishes a NoDestination fault rather than
        /// being dropped silently, which is how that mistake becomes visible.
        /// </summary>
```

- [ ] **Step 11: Run the suite**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 118/118 passed.`

- [ ] **Step 12: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/SessionDiagnostics.cs \
        Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Runtime/Application/Session/IProtocolSession.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDiagnosticsTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs
git commit -m "Make session faults and request failures distinguishable by type"
```

---

### Task 5: The loopback server and the streaming frame reader

These arrive together because a streaming reader cannot be tested without byte-level control of the other end.

**Files:**
- Create: `Packages/com.echo.harness/TestKit/LoopbackProtocolServer.cs`
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs`
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransportOptions.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/TcpTransportFramingTests.cs`

**Interfaces:**
- Produces:
  - `Echo.Harness.Infrastructure.TcpTransportOptions` with `string Host`, `int Port`, `TimeSpan ReadIdleTimeout` (default 45 s), `int SendBudgetPerSecond` (default 30).
  - `Echo.Harness.Infrastructure.TcpTransport : ITransport, IDisposable`, constructor `TcpTransport(TcpTransportOptions options, IClock clock)`.
  - `Echo.Harness.TestKit.LoopbackProtocolServer : IDisposable` with `int Port`, `UniTask AcceptAsync(TimeSpan)`, `void SendFrame(MessageId, string jsonBody)`, `void SendBytes(byte[])`, `void SendRawHeader(int declaredLength, MessageId)`, `UniTask WaitForFramesAsync(int, TimeSpan)`, `IReadOnlyList<DecodedFrame> Received`, `Exception ReadFailure`, `void CloseConnection()`.

- [ ] **Step 1: Write the loopback server**

Create `Packages/com.echo.harness/TestKit/LoopbackProtocolServer.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Net;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// A TcpListener on the loopback interface that speaks the real frame format
    /// and exposes byte-level control. It exists because fragmentation, coalescing,
    /// an oversized declared length, and a close mid-body cannot be constructed
    /// deterministically any other way - and those are exactly the paths a
    /// streaming frame reader gets wrong.
    ///
    /// Strictly disposable: a leaked listener or reader thread stalls the Unity
    /// editor at domain reload, so every test must dispose this.
    /// </summary>
    public sealed class LoopbackProtocolServer : IDisposable
    {
        private readonly TcpListener listener;
        private readonly List<DecodedFrame> received = new List<DecodedFrame>();
        private readonly object receivedGate = new object();
        private TcpClient connection;
        private NetworkStream stream;
        private Thread readerThread;
        private volatile bool disposed;
        private volatile Exception readFailure;

        public LoopbackProtocolServer()
        {
            listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            Port = ((IPEndPoint)listener.LocalEndpoint).Port;
        }

        public int Port { get; }

        /// <summary>
        /// Non-null once the read loop stopped on something other than a normal
        /// close. Interleaved client writes land here as an InvalidDataException.
        /// </summary>
        public Exception ReadFailure => readFailure;

        public IReadOnlyList<DecodedFrame> Received
        {
            get
            {
                lock (receivedGate)
                {
                    return new List<DecodedFrame>(received);
                }
            }
        }

        /// <summary>Accepts one connection and starts reading frames from it.</summary>
        public async UniTask AcceptAsync(TimeSpan timeout)
        {
            var accept = listener.AcceptTcpClientAsync();
            var winner = await UniTask.WhenAny(
                accept.AsUniTask(), UniTask.Delay(timeout, DelayType.Realtime));
            if (winner != 0)
            {
                throw new TimeoutException($"No client connected within {timeout}.");
            }

            connection = accept.Result;
            connection.NoDelay = true;
            stream = connection.GetStream();

            readerThread = new Thread(ReadLoop) { IsBackground = true, Name = "loopback-reader" };
            readerThread.Start();
        }

        /// <summary>Writes a well-formed frame in one go.</summary>
        public void SendFrame(MessageId messageId, string jsonBody)
        {
            var body = jsonBody == null ? new byte[0] : Encoding.UTF8.GetBytes(jsonBody);
            SendBytes(BinaryFrameCodec.Encode(messageId, body));
        }

        /// <summary>Writes arbitrary bytes, so a test can split or coalesce frames.</summary>
        public void SendBytes(byte[] bytes)
        {
            stream.Write(bytes, 0, bytes.Length);
            stream.Flush();
        }

        /// <summary>
        /// Writes a six-byte header whose declared length need not match anything
        /// that follows. This is how an oversized frame is constructed; the real
        /// server would never emit one, which is why the loopback double must.
        /// </summary>
        public void SendRawHeader(int declaredLength, MessageId messageId)
        {
            var header = new byte[6];
            header[0] = (byte)(declaredLength >> 24);
            header[1] = (byte)(declaredLength >> 16);
            header[2] = (byte)(declaredLength >> 8);
            header[3] = (byte)declaredLength;
            header[4] = (byte)((ushort)messageId >> 8);
            header[5] = (byte)(ushort)messageId;
            SendBytes(header);
        }

        public void CloseConnection()
        {
            try
            {
                connection?.Close();
            }
            catch (Exception)
            {
                // Closing an already-closed connection is not a test failure.
            }
        }

        /// <summary>
        /// Waits until at least <paramref name="count"/> frames have been read, so
        /// a test can assert without sleeping a fixed amount.
        /// </summary>
        public async UniTask WaitForFramesAsync(int count, TimeSpan timeout)
        {
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                // Surfaced here, on the caller's thread, because this is the only
                // place a test is waiting. An interleaved write makes the read loop
                // raise InvalidDataException, and reporting it as a timeout instead
                // would name the symptom rather than the cause.
                var failure = readFailure;
                if (failure != null)
                {
                    throw new InvalidDataException(
                        "The loopback server stopped reading: " + failure.Message, failure);
                }

                lock (receivedGate)
                {
                    if (received.Count >= count)
                    {
                        return;
                    }
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(5), DelayType.Realtime);
            }

            int actual;
            lock (receivedGate)
            {
                actual = received.Count;
            }

            throw new TimeoutException(
                $"Expected {count} frames within {timeout}; read {actual}.");
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CloseConnection();

            try
            {
                listener.Stop();
            }
            catch (Exception)
            {
                // Already stopped.
            }

            readerThread?.Join(TimeSpan.FromSeconds(2));
        }

        /// <summary>
        /// Reads frames the way the Go server does, header first and then the
        /// declared body. A frame the client interleaved with another makes the
        /// length prefix disagree with the following bytes, and this loop throws -
        /// which is precisely how a write-serialization failure is detected, by
        /// the same mechanism the real server would hit.
        /// </summary>
        private void ReadLoop()
        {
            var header = new byte[6];
            try
            {
                while (!disposed)
                {
                    if (!ReadExactly(header, header.Length))
                    {
                        return;
                    }

                    var declared = (header[0] << 24) | (header[1] << 16) |
                                   (header[2] << 8) | header[3];
                    if (declared < 0 || declared > WireFrameSpec.MaxPayloadBytes)
                    {
                        throw new InvalidDataException(
                            $"Client declared a body length of {declared}, which means " +
                            "the frames it wrote were interleaved.");
                    }

                    var body = new byte[declared];
                    if (declared > 0 && !ReadExactly(body, declared))
                    {
                        return;
                    }

                    var messageId = (MessageId)((header[4] << 8) | header[5]);
                    lock (receivedGate)
                    {
                        received.Add(new DecodedFrame(messageId, body));
                    }
                }
            }
            catch (IOException)
            {
                // The connection closed; that is how this loop is meant to end.
            }
            catch (ObjectDisposedException)
            {
                // Disposed mid-read.
            }
            catch (Exception failure)
            {
                // Recorded, never allowed to escape. An unhandled exception on a
                // background thread is invisible where a test can act on it - it
                // either takes the process down or is swallowed by Unity's handler -
                // and the interleaving detected above is the entire signal the
                // write-serialization test depends on. WaitForFramesAsync surfaces
                // it on the calling thread.
                readFailure = failure;
            }
        }

        private bool ReadExactly(byte[] buffer, int count)
        {
            var read = 0;
            while (read < count)
            {
                var chunk = stream.Read(buffer, read, count - read);
                if (chunk == 0)
                {
                    return false;
                }

                read += chunk;
            }

            return true;
        }
    }
}
```

`DecodedFrame`'s constructor takes `(MessageId, byte[])` and exposes `Payload` as `ReadOnlyMemory<byte>`; use `.Payload.ToArray()` or `.Payload.Span` when comparing bytes in tests.

- [ ] **Step 2: Write the failing framing tests**

Create `Packages/com.echo.harness/Tests/EditMode/TcpTransportFramingTests.cs`:

```csharp
using System;
using System.IO;
using System.Net.Sockets;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class TcpTransportFramingTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private static TcpTransport Connect(LoopbackProtocolServer server)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions { Host = "127.0.0.1", Port = server.Port },
                new ManualClock(DateTimeOffset.UnixEpoch));
            var connecting = transport.ConnectAsync(CancellationToken.None);
            server.AcceptAsync(Patience).GetAwaiter().GetResult();
            connecting.GetAwaiter().GetResult();
            return transport;
        }

        [Test]
        public void AFragmentedFrameIsReadAsOneMessage()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server);

            var receiving = transport.ReceiveAsync(CancellationToken.None);
            var frame = BinaryFrameCodec.Encode(
                MessageId.PhaseChangeEvent,
                Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));

            // The header one byte at a time, then the body in two chunks. A reader
            // that assumes one Read returns a whole header fails here, and TCP
            // guarantees nothing better than this.
            for (var i = 0; i < 6; i++)
            {
                server.SendBytes(new[] { frame[i] });
            }

            var body = Slice(frame, 6, frame.Length - 6);
            var half = body.Length / 2;
            server.SendBytes(Slice(body, 0, half));
            server.SendBytes(Slice(body, half, body.Length - half));

            var message = receiving.Timeout(Patience).GetAwaiter().GetResult();
            Assert.That(message.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
            Assert.That(
                Encoding.UTF8.GetString(message.Payload),
                Is.EqualTo("{\"phase\":\"action\"}"));
        }

        [Test]
        public void TwoFramesInOneSegmentAreReadAsTwoMessages()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server);

            var first = BinaryFrameCodec.Encode(MessageId.Ping, new byte[0]);
            var second = BinaryFrameCodec.Encode(
                MessageId.PhaseChangeEvent,
                Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));
            var both = new byte[first.Length + second.Length];
            Array.Copy(first, both, first.Length);
            Array.Copy(second, 0, both, first.Length, second.Length);
            server.SendBytes(both);

            var a = transport.ReceiveAsync(CancellationToken.None)
                .Timeout(Patience).GetAwaiter().GetResult();
            var b = transport.ReceiveAsync(CancellationToken.None)
                .Timeout(Patience).GetAwaiter().GetResult();

            Assert.That(a.MessageId, Is.EqualTo(MessageId.Ping));
            Assert.That(a.Payload.Length, Is.EqualTo(0),
                "The server sends Ping with a nil payload, so its body length is 0.");
            Assert.That(b.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
        }

        [Test]
        public void ADeclaredLengthOverTheBoundFailsTheReceive()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server);

            var receiving = transport.ReceiveAsync(CancellationToken.None);
            server.SendRawHeader(2 * 1024 * 1024, MessageId.GameStateEvent);

            var failure = Assert.Throws<InvalidDataException>(
                () => receiving.Timeout(Patience).GetAwaiter().GetResult());
            Assert.That(failure.Message, Does.Contain("2097152"),
                "The length is rejected before a single body byte is allocated.");
        }

        [Test]
        public void ACloseMidBodyFailsTheReceive()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server);

            var receiving = transport.ReceiveAsync(CancellationToken.None);
            var frame = BinaryFrameCodec.Encode(
                MessageId.PhaseChangeEvent,
                Encoding.UTF8.GetBytes("{\"phase\":\"action\"}"));
            server.SendBytes(Slice(frame, 0, 8));
            server.CloseConnection();

            Assert.Throws<EndOfStreamException>(
                () => receiving.Timeout(Patience).GetAwaiter().GetResult());
        }

        [Test]
        public void ConnectingToAClosedPortFails()
        {
            int deadPort;
            using (var probe = new LoopbackProtocolServer())
            {
                deadPort = probe.Port;
            }

            using var transport = new TcpTransport(
                new TcpTransportOptions { Host = "127.0.0.1", Port = deadPort },
                new ManualClock(DateTimeOffset.UnixEpoch));

            Assert.Throws<SocketException>(
                () => transport.ConnectAsync(CancellationToken.None)
                    .Timeout(Patience).GetAwaiter().GetResult());
        }

        [Test]
        public void CancellingAConnectDoesNotLeaveItConnecting()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = new TcpTransport(
                new TcpTransportOptions { Host = "127.0.0.1", Port = server.Port },
                new ManualClock(DateTimeOffset.UnixEpoch));

            using var cancellation = new CancellationTokenSource();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(
                () => transport.ConnectAsync(cancellation.Token)
                    .Timeout(Patience).GetAwaiter().GetResult());
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected),
                "A transport stuck in Connecting can never be retried.");
        }

        private static byte[] Slice(byte[] source, int offset, int count)
        {
            var slice = new byte[count];
            Array.Copy(source, offset, slice, 0, count);
            return slice;
        }
    }
}
```

- [ ] **Step 3: Run and confirm failure**

Expected: compile error, `TcpTransport` and `TcpTransportOptions` do not exist.

- [ ] **Step 4: Write the options**

Create `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransportOptions.cs`:

```csharp
using System;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Every default here is derived from the authoritative Go server, and none of
    /// them is negotiable: its rate limit and heartbeat intervals are compile-time
    /// constants in its session.go, and its RATE_LIMIT environment variable is
    /// loaded, logged, and never consumed.
    /// </summary>
    public sealed class TcpTransportOptions
    {
        public string Host { get; set; } = "127.0.0.1";

        public int Port { get; set; } = 43966;

        /// <summary>
        /// How long a complete frame may take to arrive before the link is judged
        /// dead. The server sends a Ping every 15 s, so silence is itself a signal
        /// and 45 s means three missed pings. Deliberately not tighter: a tight
        /// value disconnects on a hiccup, and the kernel can take minutes to notice
        /// a half-open connection on its own.
        /// </summary>
        public TimeSpan ReadIdleTimeout { get; set; } = TimeSpan.FromSeconds(45);

        /// <summary>
        /// Messages per second, matching the server's hard-coded 30. Exceeding it
        /// closes the connection server-side with no error frame, which on the wire
        /// is indistinguishable from a pulled cable.
        /// </summary>
        public int SendBudgetPerSecond { get; set; } = 30;
    }
}
```

- [ ] **Step 5: Write connect, receive, and disconnect**

Create `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs`. `SendAsync` is a throwing stub here and is implemented in Task 6.

```csharp
using System;
using System.IO;
using System.Net.Sockets;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.Infrastructure
{
    public sealed class TcpTransport : ITransport, IDisposable
    {
        private readonly TcpTransportOptions options;
        private readonly IClock clock;
        private readonly byte[] header =
            new byte[WireFrameSpec.LengthPrefixBytes + WireFrameSpec.MessageIdBytes];

        private TcpClient client;
        private NetworkStream stream;
        private bool disposed;

        public TcpTransport(TcpTransportOptions options, IClock clock)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public TransportState State { get; private set; } = TransportState.Disconnected;

        public async UniTask ConnectAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (State != TransportState.Disconnected)
            {
                throw new InvalidOperationException(
                    $"This transport is {State} and cannot be connected again.");
            }

            State = TransportState.Connecting;
            var connecting = new TcpClient { NoDelay = true };

            // .NET Standard 2.1 has no TcpClient.ConnectAsync overload taking a
            // CancellationToken, so cancellation is implemented by closing the
            // client out from under the pending connect. The exception that
            // produces is translated back into cancellation below.
            using var registration = cancellationToken.Register(() => connecting.Close());
            try
            {
                await connecting.ConnectAsync(options.Host, options.Port).AsUniTask();
            }
            catch (Exception) when (cancellationToken.IsCancellationRequested)
            {
                connecting.Dispose();
                State = TransportState.Disconnected;
                throw new OperationCanceledException(cancellationToken);
            }
            catch (Exception)
            {
                // Reset rather than left in Connecting: a transport stuck there
                // refuses every later ConnectAsync and can never be retried.
                connecting.Dispose();
                State = TransportState.Disconnected;
                throw;
            }

            client = connecting;
            stream = connecting.GetStream();
            State = TransportState.Connected;
        }

        public UniTask SendAsync(TransportMessage message, CancellationToken cancellationToken)
        {
            throw new NotImplementedException("Implemented in Task 6.");
        }

        public async UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureConnected();

            await ReadExactlyAsync(header, header.Length, cancellationToken);

            var declaredLength = (header[0] << 24) | (header[1] << 16) |
                                 (header[2] << 8) | header[3];

            // Checked before a single body byte is allocated. A hostile or
            // desynchronized peer that declares a gigabyte would otherwise get one
            // allocated for it.
            if (declaredLength < 0 || declaredLength > WireFrameSpec.MaxPayloadBytes)
            {
                throw new InvalidDataException(
                    $"Frame declares a body length of {declaredLength}, outside " +
                    $"[0, {WireFrameSpec.MaxPayloadBytes}]. The stream has lost its " +
                    "frame boundaries and nothing later can be trusted.");
            }

            var messageId = (MessageId)((header[4] << 8) | header[5]);
            if (declaredLength == 0)
            {
                // Ping arrives this way: the server sends it with a nil payload.
                return new TransportMessage(messageId, new byte[0]);
            }

            var body = new byte[declaredLength];
            await ReadExactlyAsync(body, declaredLength, cancellationToken);
            return new TransportMessage(messageId, body);
        }

        public UniTask DisconnectAsync(CancellationToken cancellationToken)
        {
            // Idempotent by contract: the session's fault path and StopAsync can
            // both reach here, and Dispose fires one more without awaiting it.
            if (State == TransportState.Disconnected)
            {
                return UniTask.CompletedTask;
            }

            State = TransportState.Disconnected;
            CloseSocket();
            return UniTask.CompletedTask;
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            State = TransportState.Disconnected;
            CloseSocket();
        }

        /// <summary>
        /// Fills <paramref name="count"/> bytes or throws. A TCP read returns what
        /// has arrived, not what was asked for, which is the whole reason a
        /// streaming reader is needed on top of BinaryFrameCodec. .NET Standard 2.1
        /// has no ReadExactlyAsync to borrow.
        /// </summary>
        private async UniTask ReadExactlyAsync(
            byte[] buffer,
            int count,
            CancellationToken cancellationToken)
        {
            var read = 0;
            while (read < count)
            {
                int chunk;
                try
                {
                    chunk = await stream
                        .ReadAsync(new Memory<byte>(buffer, read, count - read), cancellationToken)
                        .AsUniTask();
                }
                catch (ObjectDisposedException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new EndOfStreamException(
                        "The connection closed while a frame was being read.");
                }
                catch (IOException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new EndOfStreamException(
                        "The connection was reset while a frame was being read.");
                }

                if (chunk == 0)
                {
                    throw new EndOfStreamException(
                        $"The peer closed after {read} of {count} expected bytes. " +
                        "A partial frame means the stream is unusable.");
                }

                read += chunk;
            }
        }

        private void CloseSocket()
        {
            try
            {
                stream?.Dispose();
            }
            catch (Exception)
            {
                // A stream that is already broken is exactly what is being closed.
            }

            try
            {
                client?.Close();
            }
            catch (Exception)
            {
                // As above.
            }

            stream = null;
            client = null;
        }

        private void EnsureConnected()
        {
            if (State != TransportState.Connected || stream == null)
            {
                throw new InvalidOperationException(
                    $"This transport is {State} and has no stream to use.");
            }
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(TcpTransport));
            }
        }
    }
}
```

`clock` is unused in this task; Task 6 uses it for the send budget. Leave the field and the constructor parameter in place now so the signature does not change twice.

- [ ] **Step 6: Run the framing tests**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 124/124 passed.`

If a test hangs, a listener leaked. Check that every test has `using` on both the server and the transport before debugging anything else.

- [ ] **Step 7: Commit**

```bash
git add Packages/com.echo.harness/TestKit/LoopbackProtocolServer.cs \
        Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs \
        Packages/com.echo.harness/Runtime/Infrastructure/TcpTransportOptions.cs \
        Packages/com.echo.harness/Tests/EditMode/TcpTransportFramingTests.cs
git commit -m "Add a streaming TCP frame reader and a byte-level loopback server"
```

---

### Task 6: Serialized writes and the send budget

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs` (fields, `ConnectAsync` tail, `SendAsync`, `Dispose`)
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/SendBudget.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/SendBudgetTests.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/TcpTransportSendTests.cs`

**Interfaces:**
- Consumes: `TcpTransport`, `TcpTransportOptions`, `LoopbackProtocolServer` from Task 5.
- Produces: `Echo.Harness.Infrastructure.SendBudget` with `SendBudget(int perSecond, IClock clock)` and `bool TryConsume()`. `Echo.Harness.Infrastructure.SendBudgetExceededException : InvalidOperationException` with `MessageId MessageId`.

- [ ] **Step 1: Write the failing budget tests**

Create `Packages/com.echo.harness/Tests/EditMode/SendBudgetTests.cs`:

```csharp
using System;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SendBudgetTests
    {
        [Test]
        public void ABudgetOfThirtyAllowsThirtyThenRefuses()
        {
            var budget = new SendBudget(30, new ManualClock(DateTimeOffset.UnixEpoch));

            for (var i = 0; i < 30; i++)
            {
                Assert.That(budget.TryConsume(), Is.True, $"Send {i + 1} of 30.");
            }

            Assert.That(budget.TryConsume(), Is.False,
                "The server's limit is 30 per second and it disconnects silently.");
        }

        [Test]
        public void TokensRefillOverTime()
        {
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            var budget = new SendBudget(30, clock);
            for (var i = 0; i < 30; i++)
            {
                budget.TryConsume();
            }

            // One interval is a thirtieth of a second, matching the server's own
            // rateLimitRefillInterval.
            clock.Advance(TimeSpan.FromTicks(TimeSpan.TicksPerSecond / 30));

            Assert.That(budget.TryConsume(), Is.True);
            Assert.That(budget.TryConsume(), Is.False, "Exactly one token refilled.");
        }

        [Test]
        public void RefillNeverExceedsTheMaximum()
        {
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            var budget = new SendBudget(30, clock);
            clock.Advance(TimeSpan.FromMinutes(5));

            for (var i = 0; i < 30; i++)
            {
                Assert.That(budget.TryConsume(), Is.True);
            }

            Assert.That(budget.TryConsume(), Is.False,
                "A long idle period must not build a burst the server will reject.");
        }

        [Test]
        public void ANonPositiveBudgetIsRejected()
        {
            Assert.Throws<ArgumentOutOfRangeException>(
                () => new SendBudget(0, new ManualClock(DateTimeOffset.UnixEpoch)));
        }

        [Test]
        public void ANullClockIsRejected()
        {
            Assert.Throws<ArgumentNullException>(() => new SendBudget(30, null));
        }
    }
}
```

- [ ] **Step 2: Write the failing send tests**

Create `Packages/com.echo.harness/Tests/EditMode/TcpTransportSendTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class TcpTransportSendTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private static TcpTransport Connect(
            LoopbackProtocolServer server,
            ManualClock clock,
            int budgetPerSecond = 30)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions
                {
                    Host = "127.0.0.1",
                    Port = server.Port,
                    SendBudgetPerSecond = budgetPerSecond
                },
                clock);
            var connecting = transport.ConnectAsync(CancellationToken.None);
            server.AcceptAsync(Patience).GetAwaiter().GetResult();
            connecting.GetAwaiter().GetResult();
            return transport;
        }

        [Test]
        public void ConcurrentSendsArriveAsWholeFrames()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(
                server, new ManualClock(DateTimeOffset.UnixEpoch), budgetPerSecond: 100);

            // Twenty callers plus a heartbeat reply, all in flight together. This
            // is the shape a real session produces: it answers Ping from its
            // receive pump while a caller's own send is still going. Without the
            // gate the writes interleave, and the loopback server's frame reader -
            // not an assertion here - is what notices, exactly as the Go server
            // would.
            var body = Encoding.UTF8.GetBytes("{\"phase\":\"action\"}");
            var sends = new List<UniTask>();
            for (var i = 0; i < 20; i++)
            {
                sends.Add(transport.SendAsync(
                    new TransportMessage(MessageId.PhaseChangeEvent, body),
                    CancellationToken.None));
            }

            sends.Add(transport.SendAsync(
                new TransportMessage(MessageId.Pong, new byte[0]),
                CancellationToken.None));

            UniTask.WhenAll(sends).Timeout(Patience).GetAwaiter().GetResult();
            server.WaitForFramesAsync(21, Patience).GetAwaiter().GetResult();

            var received = server.Received;
            Assert.That(received.Count, Is.EqualTo(21));
            var phaseFrames = 0;
            var pongFrames = 0;
            foreach (var frame in received)
            {
                if (frame.MessageId == MessageId.PhaseChangeEvent)
                {
                    phaseFrames++;
                    Assert.That(frame.Payload.Length, Is.EqualTo(body.Length),
                        "A truncated body is what interleaving looks like.");
                }
                else if (frame.MessageId == MessageId.Pong)
                {
                    pongFrames++;
                }
            }

            Assert.That(phaseFrames, Is.EqualTo(20));
            Assert.That(pongFrames, Is.EqualTo(1));
        }

        [Test]
        public void ExceedingTheBudgetThrowsAndKeepsTheConnection()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server, new ManualClock(DateTimeOffset.UnixEpoch));

            var body = Encoding.UTF8.GetBytes("{\"phase\":\"action\"}");
            for (var i = 0; i < 30; i++)
            {
                transport.SendAsync(
                    new TransportMessage(MessageId.PhaseChangeEvent, body),
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            var failure = Assert.Throws<SendBudgetExceededException>(
                () => transport.SendAsync(
                    new TransportMessage(MessageId.PhaseChangeEvent, body),
                    CancellationToken.None).GetAwaiter().GetResult());

            Assert.That(failure.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
            Assert.That(transport.State, Is.EqualTo(TransportState.Connected),
                "One caller's loop bug must not become a global disconnect.");
        }

        [Test]
        public void PongIsExemptFromTheBudget()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server, new ManualClock(DateTimeOffset.UnixEpoch));

            var body = Encoding.UTF8.GetBytes("{\"phase\":\"action\"}");
            for (var i = 0; i < 30; i++)
            {
                transport.SendAsync(
                    new TransportMessage(MessageId.PhaseChangeEvent, body),
                    CancellationToken.None).GetAwaiter().GetResult();
            }

            // The server handles Pong before its own limiter and never counts it.
            // A guard that refused this Pong would cause the 35 second heartbeat
            // disconnect it exists to prevent, and the symptom would appear with
            // no obvious cause.
            for (var i = 0; i < 100; i++)
            {
                Assert.DoesNotThrow(
                    () => transport.SendAsync(
                        new TransportMessage(MessageId.Pong, new byte[0]),
                        CancellationToken.None).GetAwaiter().GetResult());
            }

            server.WaitForFramesAsync(130, Patience).GetAwaiter().GetResult();
        }

        [Test]
        public void APayloadOverTheBoundIsRefusedBeforeTheGate()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server, new ManualClock(DateTimeOffset.UnixEpoch));

            var tooBig = new byte[WireFrameSpec.MaxPayloadBytes + 1];

            Assert.Throws<ArgumentOutOfRangeException>(
                () => transport.SendAsync(
                    new TransportMessage(MessageId.GameStateEvent, tooBig),
                    CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
        }
    }
}
```

- [ ] **Step 3: Run and confirm failure**

Expected: compile errors on `SendBudget` and `SendBudgetExceededException`; `NotImplementedException` from the stub for the send tests.

- [ ] **Step 4: Write SendBudget**

Create `Packages/com.echo.harness/Runtime/Infrastructure/SendBudget.cs`:

```csharp
using System;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// A token bucket shaped like the server's own: a maximum, a refill interval of
    /// one second divided by that maximum, and a cap so an idle period cannot build
    /// a burst. Driven by IClock rather than the wall clock so a test can exhaust
    /// and refill it without sleeping.
    /// </summary>
    public sealed class SendBudget
    {
        private readonly int max;
        private readonly long refillIntervalTicks;
        private readonly IClock clock;
        private int tokens;
        private DateTimeOffset lastFill;

        public SendBudget(int perSecond, IClock clock)
        {
            if (perSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(perSecond), perSecond, "A send budget must be positive.");
            }

            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            max = perSecond;
            tokens = perSecond;
            refillIntervalTicks = TimeSpan.TicksPerSecond / perSecond;
            lastFill = clock.UtcNow;
        }

        public bool TryConsume()
        {
            var elapsedTicks = (clock.UtcNow - lastFill).Ticks;
            if (elapsedTicks >= refillIntervalTicks)
            {
                var refill = elapsedTicks / refillIntervalTicks;
                tokens = (int)Math.Min(max, tokens + refill);

                // Advanced by whole intervals only, so a fractional remainder
                // carries forward instead of being discarded. Setting lastFill to
                // now would lose it and make the effective rate lower than asked.
                lastFill = lastFill.AddTicks(refill * refillIntervalTicks);
            }

            if (tokens <= 0)
            {
                return false;
            }

            tokens--;
            return true;
        }
    }

    /// <summary>
    /// The caller sent faster than the server tolerates. Its own type, and not a
    /// session fault, because this is the one failure that is the caller's defect
    /// rather than the link's: the connection is still fine.
    /// </summary>
    public sealed class SendBudgetExceededException : InvalidOperationException
    {
        public SendBudgetExceededException(MessageId messageId, string message)
            : base(message)
        {
            MessageId = messageId;
        }

        public MessageId MessageId { get; }
    }
}
```

- [ ] **Step 5: Implement SendAsync**

In `TcpTransport.cs`, add two members beside the existing fields:

```csharp
        private readonly SemaphoreSlim sendGate = new SemaphoreSlim(1, 1);
        private SendBudget budget;
```

Initialise the budget at the end of `ConnectAsync`, immediately before `State = TransportState.Connected;`:

```csharp
            budget = new SendBudget(options.SendBudgetPerSecond, clock);
```

Replace the `SendAsync` stub:

```csharp
        public async UniTask SendAsync(
            TransportMessage message,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureConnected();

            // Encoded before the gate is taken: BinaryFrameCodec.Encode rejects an
            // oversized payload, and there is no reason to make other senders wait
            // behind a frame that will never be written.
            var frame = BinaryFrameCodec.Encode(message.MessageId, message.Payload);

            await sendGate.WaitAsync(cancellationToken).AsUniTask();
            try
            {
                // Inside the gate, not before it. Tokens must correspond to bytes
                // actually placed on the wire, in wire order; checking outside lets
                // two callers both pass and then acquire the gate in the opposite
                // order, so the sequence the server rate-limits is not the sequence
                // that was checked.
                //
                // Pong is exempt because the server handles it ahead of its own
                // limiter and never counts it. Refusing a Pong here would cause the
                // heartbeat disconnect this guard exists to prevent, and the
                // symptom would appear 35 seconds later with no obvious cause.
                if (message.MessageId != MessageId.Pong && !budget.TryConsume())
                {
                    throw new SendBudgetExceededException(
                        message.MessageId,
                        $"Sending {message.MessageId} would exceed " +
                        $"{options.SendBudgetPerSecond} messages per second. The server " +
                        "closes the connection without an error frame when that limit " +
                        "is passed, so this throws instead of queueing: a caller looping " +
                        "faster than the protocol allows is a defect worth surfacing.");
                }

                // One write for the whole frame. BinaryFrameCodec.Encode returns the
                // header and body in a single buffer for exactly this reason, and
                // the server merges them in its EncodeFrame for the same one.
                await stream
                    .WriteAsync(new ReadOnlyMemory<byte>(frame), cancellationToken)
                    .AsUniTask();
                await stream.FlushAsync(cancellationToken).AsUniTask();
            }
            finally
            {
                sendGate.Release();
            }
        }
```

Add the gate's disposal to `Dispose`, after `CloseSocket()`:

```csharp
            sendGate.Dispose();
```

- [ ] **Step 6: Run the suite**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 133/133 passed.`

- [ ] **Step 7: Mutation-verify the serialization test**

Temporarily remove `await sendGate.WaitAsync(...)` and the `finally`'s `Release()`, then run `ConcurrentSendsArriveAsWholeFrames`. It must fail — `WaitForFramesAsync` raises the loopback reader's `InvalidDataException` about interleaving, or the frame count and payload lengths disagree. Restore the gate. A serialization test that passes without the gate proves nothing, and this test is the only evidence for the whole write path.

Interleaving is not guaranteed on every run without the gate — two writes can happen to complete in order. If the mutated run passes, raise the concurrent sender count and the body size and try again; a 20-sender run with a ~1 KiB body interleaves reliably. Do not conclude the gate is unnecessary from one lucky pass.

- [ ] **Step 8: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs \
        Packages/com.echo.harness/Runtime/Infrastructure/SendBudget.cs \
        Packages/com.echo.harness/Tests/EditMode/TcpTransportSendTests.cs \
        Packages/com.echo.harness/Tests/EditMode/SendBudgetTests.cs
git commit -m "Serialize transport writes and enforce the server's send budget"
```

---

### Task 7: The read-idle watchdog

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs` (`ReceiveAsync` split, append exception type)
- Test: `Packages/com.echo.harness/Tests/EditMode/TcpTransportIdleTests.cs`

**Interfaces:**
- Consumes: `TcpTransport`, `TcpTransportOptions`, `LoopbackProtocolServer`.
- Produces: `Echo.Harness.Infrastructure.ReadIdleTimeoutException : IOException` with `TimeSpan Idle`.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/TcpTransportIdleTests.cs`:

```csharp
using System;
using System.IO;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class TcpTransportIdleTests
    {
        private static readonly TimeSpan Patience = TimeSpan.FromSeconds(5);

        private static TcpTransport Connect(LoopbackProtocolServer server, TimeSpan idle)
        {
            var transport = new TcpTransport(
                new TcpTransportOptions
                {
                    Host = "127.0.0.1",
                    Port = server.Port,
                    ReadIdleTimeout = idle
                },
                new ManualClock(DateTimeOffset.UnixEpoch));
            var connecting = transport.ConnectAsync(CancellationToken.None);
            server.AcceptAsync(Patience).GetAwaiter().GetResult();
            connecting.GetAwaiter().GetResult();
            return transport;
        }

        [Test]
        public void SilenceBeyondTheIdleTimeoutFailsTheReceive()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server, TimeSpan.FromMilliseconds(300));

            var failure = Assert.Throws<ReadIdleTimeoutException>(
                () => transport.ReceiveAsync(CancellationToken.None)
                    .Timeout(Patience).GetAwaiter().GetResult());

            Assert.That(failure.Idle, Is.EqualTo(TimeSpan.FromMilliseconds(300)));
            Assert.That(failure, Is.InstanceOf<IOException>(),
                "The session grades an IOException from the receive as fatal, which " +
                "is the treatment a dead link deserves.");
        }

        [Test]
        public void TheIdleDeadlineResetsForEachFrame()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server, TimeSpan.FromMilliseconds(600));

            // Three frames, each arriving well inside the window but the three
            // together exceeding it. A deadline covering the connection rather than
            // a frame would kill this healthy link.
            for (var i = 0; i < 3; i++)
            {
                server.SendFrame(MessageId.PhaseChangeEvent, "{\"phase\":\"action\"}");
                var message = transport.ReceiveAsync(CancellationToken.None)
                    .Timeout(Patience).GetAwaiter().GetResult();
                Assert.That(message.MessageId, Is.EqualTo(MessageId.PhaseChangeEvent));
                UniTask.Delay(TimeSpan.FromMilliseconds(250), DelayType.Realtime)
                    .GetAwaiter().GetResult();
            }
        }

        [Test]
        public void ACallerCancellationIsNotReportedAsAnIdleTimeout()
        {
            using var server = new LoopbackProtocolServer();
            using var transport = Connect(server, TimeSpan.FromSeconds(30));

            using var cancellation = new CancellationTokenSource();
            var receiving = transport.ReceiveAsync(cancellation.Token);
            cancellation.Cancel();

            var failure = Assert.Throws<OperationCanceledException>(
                () => receiving.Timeout(Patience).GetAwaiter().GetResult());
            Assert.That(failure, Is.Not.InstanceOf<ReadIdleTimeoutException>(),
                "Reporting an orderly shutdown as a dead link would send a session " +
                "into Faulted while it was stopping cleanly.");
        }
    }
}
```

- [ ] **Step 2: Run and confirm failure**

Expected: compile error, `ReadIdleTimeoutException` does not exist.

- [ ] **Step 3: Add the exception type**

Append to `TcpTransport.cs`, after the `TcpTransport` class and inside the namespace:

```csharp
    /// <summary>
    /// No complete frame arrived within the idle window. Derived from IOException
    /// because that is what the session already grades as a desynchronized stream,
    /// and a link the kernel has not yet noticed is dead deserves the same
    /// treatment.
    /// </summary>
    public sealed class ReadIdleTimeoutException : IOException
    {
        public ReadIdleTimeoutException(TimeSpan idle, string message)
            : base(message)
        {
            Idle = idle;
        }

        public TimeSpan Idle { get; }
    }
```

- [ ] **Step 4: Split ReceiveAsync**

Rename the current `ReceiveAsync` body to `ReceiveFrameAsync`, dropping its `ThrowIfDisposed` and `EnsureConnected` calls, and add the deadline wrapper:

```csharp
        public async UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            EnsureConnected();

            // The deadline covers one whole frame and restarts with the next, so a
            // healthy but quiet link is not killed by the sum of its gaps. The
            // server sends a Ping every 15 s, which is what makes silence
            // measurable at all.
            using var deadline =
                CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            deadline.CancelAfter(options.ReadIdleTimeout);

            try
            {
                return await ReceiveFrameAsync(deadline.Token);
            }
            catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
            {
                // Only the deadline fired. A caller's own cancellation passes
                // through untouched, because reporting it as a dead link would
                // send a session into Faulted during an orderly shutdown.
                throw new ReadIdleTimeoutException(
                    options.ReadIdleTimeout,
                    $"No complete frame arrived within {options.ReadIdleTimeout}. The " +
                    "server sends a Ping every 15 seconds, so this much silence means " +
                    "the link is gone even though the socket has not said so.");
            }
        }

        private async UniTask<TransportMessage> ReceiveFrameAsync(
            CancellationToken cancellationToken)
        {
            await ReadExactlyAsync(header, header.Length, cancellationToken);

            var declaredLength = (header[0] << 24) | (header[1] << 16) |
                                 (header[2] << 8) | header[3];

            // Checked before a single body byte is allocated. A hostile or
            // desynchronized peer that declares a gigabyte would otherwise get one
            // allocated for it.
            if (declaredLength < 0 || declaredLength > WireFrameSpec.MaxPayloadBytes)
            {
                throw new InvalidDataException(
                    $"Frame declares a body length of {declaredLength}, outside " +
                    $"[0, {WireFrameSpec.MaxPayloadBytes}]. The stream has lost its " +
                    "frame boundaries and nothing later can be trusted.");
            }

            var messageId = (MessageId)((header[4] << 8) | header[5]);
            if (declaredLength == 0)
            {
                // Ping arrives this way: the server sends it with a nil payload.
                return new TransportMessage(messageId, new byte[0]);
            }

            var body = new byte[declaredLength];
            await ReadExactlyAsync(body, declaredLength, cancellationToken);
            return new TransportMessage(messageId, body);
        }
```

A large frame arriving slowly but steadily can still trip this, because the deadline covers the whole frame rather than each read. At a 1 MiB cap over any usable link that is not reachable, and a per-read deadline would fail to notice a peer that dribbles one byte per interval forever.

- [ ] **Step 5: Run the suite**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 136/136 passed.`

- [ ] **Step 6: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs \
        Packages/com.echo.harness/Tests/EditMode/TcpTransportIdleTests.cs
git commit -m "Detect a dead link with a per-frame read idle deadline"
```

---

### Task 8: The production scheduler

Small, and deliberately not wired into DI. `HarnessComposition.Configure` registers only `HarnessRuntimeDescriptor`; session lifetime scopes are the next iteration's work, and adding them here would pull in scope decisions this spec defers.

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/MainThreadSessionScheduler.cs`
- Test: `Packages/com.echo.harness/Tests/PlayMode/MainThreadSessionSchedulerTests.cs`

**Interfaces:**
- Consumes: `ISessionScheduler` from Task 1.
- Produces: `Echo.Harness.Infrastructure.MainThreadSessionScheduler : ISessionScheduler`.

- [ ] **Step 1: Write the failing PlayMode test**

PlayMode, not EditMode: this is the one piece that needs a running player loop, which is exactly what EditMode does not have.

Create `Packages/com.echo.harness/Tests/PlayMode/MainThreadSessionSchedulerTests.cs`:

```csharp
using System.Collections;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using UnityEngine.TestTools;

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

        [UnityTest]
        public IEnumerator SwitchingWhileAlreadyOnTheMainThreadStaysThere() =>
            UniTask.ToCoroutine(async () =>
            {
                var mainThreadId = Thread.CurrentThread.ManagedThreadId;
                var scheduler = new MainThreadSessionScheduler();

                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);

                Assert.That(Thread.CurrentThread.ManagedThreadId, Is.EqualTo(mainThreadId));
            });
    }
}
```

- [ ] **Step 2: Run and confirm failure**

Expected: compile error, `MainThreadSessionScheduler` does not exist.

- [ ] **Step 3: Write the scheduler**

Create `Packages/com.echo.harness/Runtime/Infrastructure/MainThreadSessionScheduler.cs`:

```csharp
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
    /// Switching while already on the main thread completes without yielding, so a
    /// session whose transport happens to complete inline pays nothing for the hop.
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
```

- [ ] **Step 4: Run the suite**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 136/136 passed.` and `PlayMode: 3/3 passed.`

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/MainThreadSessionScheduler.cs \
        Packages/com.echo.harness/Tests/PlayMode/MainThreadSessionSchedulerTests.cs
git commit -m "Implement the session scheduler against the Unity main thread"
```

---

### Task 9: The end-to-end test, the gate, and the docs

**Files:**
- Create: `Packages/com.echo.harness/TestKit/GoServerFixture.cs`
- Create: `Packages/com.echo.harness/Tests/EditMode/GoServerEndToEndTests.cs`
- Modify: `Packages/com.echo.harness/TestKit/DeterministicFakes.cs` (append `SystemClock`)
- Modify: `Packages/com.echo.harness/Tests/EditMode/DeterministicFakesTests.cs` (append)
- Modify: `Tools/ci/verify.ps1`
- Modify: `docs/migration-checklist.md`
- Modify: `docs/verification-matrix.md`

**Interfaces:**
- Consumes: everything from Tasks 1-8.
- Produces: `Echo.Harness.TestKit.GoServerFixture : IDisposable` with `static bool TryLocate(out string serverRoot)`, `static GoServerFixture Create(string serverRoot)`, `int Port`, `UniTask StartAsync(TimeSpan)`. `Echo.Harness.TestKit.SystemClock : IClock`.

- [ ] **Step 1: Close the FakeTransport coverage gap**

`FakeTransport.FailNextSend` has no test at all, so neither its null guard nor its one-shot semantics is pinned. Append to `DeterministicFakesTests.cs`, adding `using System.IO;` if absent:

```csharp
        [Test]
        public void FailNextSendRejectsNull()
        {
            var transport = new FakeTransport();
            Assert.Throws<ArgumentNullException>(() => transport.FailNextSend(null));
        }

        [Test]
        public void FailNextSendAppliesToExactlyOneSend()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            transport.FailNextSend(new IOException("socket closed"));

            Assert.Throws<IOException>(
                () => transport.SendAsync(
                    new TransportMessage(MessageId.Pong, new byte[0]),
                    CancellationToken.None).GetAwaiter().GetResult());

            Assert.DoesNotThrow(
                () => transport.SendAsync(
                    new TransportMessage(MessageId.Pong, new byte[0]),
                    CancellationToken.None).GetAwaiter().GetResult());
            Assert.That(transport.Sent.Count, Is.EqualTo(1),
                "The failed send must not have been recorded as sent.");
        }

        [Test]
        public void FailNextDisconnectRejectsNull()
        {
            var transport = new FakeTransport();
            Assert.Throws<ArgumentNullException>(() => transport.FailNextDisconnect(null));
        }

        [Test]
        public void FailNextDisconnectAppliesToExactlyOneDisconnect()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(CancellationToken.None).GetAwaiter().GetResult();
            transport.FailNextDisconnect(new IOException("close failed"));

            Assert.Throws<IOException>(
                () => transport.DisconnectAsync(CancellationToken.None)
                    .GetAwaiter().GetResult());
            Assert.DoesNotThrow(
                () => transport.DisconnectAsync(CancellationToken.None)
                    .GetAwaiter().GetResult());
            Assert.That(transport.DisconnectCount, Is.EqualTo(2));
        }
```

- [ ] **Step 2: Add SystemClock**

Append to `DeterministicFakes.cs`, inside the namespace:

```csharp
    /// <summary>
    /// Wall-clock time, for the few tests that need real elapsed time. Monotonic
    /// enough in practice for the interval measurement IClock documents, and the
    /// only IClock that can measure a real round trip.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }
```

- [ ] **Step 3: Write the Go server fixture**

Create `Packages/com.echo.harness/TestKit/GoServerFixture.cs`:

```csharp
using System;
using System.Diagnostics;
using System.IO;
using System.Net;
using System.Net.Sockets;
using Cysharp.Threading.Tasks;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Runs the authoritative Go server for one test. It never modifies it.
    ///
    /// Two details are not optional. The working directory must be the server root,
    /// because its main.go loads ./data/characters.json and ./data/fields.json
    /// relative to it and exits if they are missing. And the port must be chosen
    /// here rather than by passing :0, because the server logs the address it was
    /// configured with rather than the one it bound, so a kernel-assigned port
    /// would be undiscoverable.
    /// </summary>
    public sealed class GoServerFixture : IDisposable
    {
        private readonly string serverRoot;
        private Process process;

        private GoServerFixture(string serverRoot, int port)
        {
            this.serverRoot = serverRoot;
            Port = port;
        }

        public int Port { get; }

        /// <summary>
        /// Finds the server checkout, or reports that it is absent so a test can
        /// skip rather than fail. The environment variable wins, so a machine with
        /// a different layout needs no code change.
        /// </summary>
        public static bool TryLocate(out string serverRoot)
        {
            var candidates = new[]
            {
                Environment.GetEnvironmentVariable("ECHO_GO_SERVER_ROOT"),
                @"E:\code\_github\magic-card-server-golang"
            };

            foreach (var candidate in candidates)
            {
                if (!string.IsNullOrWhiteSpace(candidate) &&
                    File.Exists(Path.Combine(candidate, "main.go")) &&
                    File.Exists(Path.Combine(candidate, "data", "characters.json")))
                {
                    serverRoot = candidate;
                    return true;
                }
            }

            serverRoot = null;
            return false;
        }

        public static GoServerFixture Create(string serverRoot)
        {
            if (string.IsNullOrWhiteSpace(serverRoot))
            {
                throw new ArgumentException("A server root is required.", nameof(serverRoot));
            }

            return new GoServerFixture(serverRoot, ReserveFreePort());
        }

        public async UniTask StartAsync(TimeSpan timeout)
        {
            var startInfo = new ProcessStartInfo
            {
                FileName = "go",
                Arguments = "run .",
                WorkingDirectory = serverRoot,
                UseShellExecute = false,
                RedirectStandardOutput = true,
                RedirectStandardError = true,
                CreateNoWindow = true
            };
            startInfo.EnvironmentVariables["LISTEN_ADDR"] = $"127.0.0.1:{Port}";
            startInfo.EnvironmentVariables["LOG_LEVEL"] = "warn";
            startInfo.EnvironmentVariables["GOTELEMETRY"] = "off";

            process = Process.Start(startInfo);
            if (process == null)
            {
                throw new InvalidOperationException(
                    "Could not start 'go run .'. Is the Go toolchain on PATH?");
            }

            // Drained so a full pipe buffer cannot block the server mid-test.
            process.OutputDataReceived += (_, __) => { };
            process.ErrorDataReceived += (_, __) => { };
            process.BeginOutputReadLine();
            process.BeginErrorReadLine();

            // Polled rather than parsed out of the log: `go run` compiles first, so
            // an accepting port is the only reliable readiness signal.
            var deadline = DateTime.UtcNow + timeout;
            while (DateTime.UtcNow < deadline)
            {
                if (process.HasExited)
                {
                    throw new InvalidOperationException(
                        $"The Go server exited with code {process.ExitCode} before " +
                        "accepting connections.");
                }

                if (CanConnect())
                {
                    return;
                }

                await UniTask.Delay(TimeSpan.FromMilliseconds(100), DelayType.Realtime);
            }

            throw new TimeoutException(
                $"The Go server did not accept connections on {Port} within {timeout}.");
        }

        public void Dispose()
        {
            if (process == null)
            {
                return;
            }

            try
            {
                if (!process.HasExited)
                {
                    // `go run` spawns the built binary as a child, so the whole tree
                    // has to go; killing only `go` orphans the server and leaves the
                    // port bound for the next test.
                    process.Kill(entireProcessTree: true);
                    process.WaitForExit(5000);
                }
            }
            catch (Exception)
            {
                // The process is gone, which is the goal.
            }
            finally
            {
                process.Dispose();
                process = null;
            }
        }

        private bool CanConnect()
        {
            try
            {
                using var probe = new TcpClient();
                probe.Connect(IPAddress.Loopback, Port);
                return true;
            }
            catch (SocketException)
            {
                return false;
            }
        }

        private static int ReserveFreePort()
        {
            var listener = new TcpListener(IPAddress.Loopback, 0);
            listener.Start();
            var port = ((IPEndPoint)listener.LocalEndpoint).Port;
            listener.Stop();
            return port;
        }
    }
}
```

- [ ] **Step 4: Write the end-to-end test**

Create `Packages/com.echo.harness/Tests/EditMode/GoServerEndToEndTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    /// <summary>
    /// One end-to-end pass over a real socket against the real server. Everything
    /// else in the suite runs on fakes or a loopback double, both of which encode
    /// our own understanding of the protocol; this is the only test that can
    /// disagree with us.
    /// </summary>
    public sealed class GoServerEndToEndTests
    {
        private static readonly TimeSpan StartupPatience = TimeSpan.FromSeconds(90);

        private const string SkipReason =
            "The Go server checkout was not found. Set ECHO_GO_SERVER_ROOT to run this.";

        private static bool TrySetUp(
            out GoServerFixture server,
            out TcpTransport transport,
            out ProtocolSession session)
        {
            server = null;
            transport = null;
            session = null;

            if (!GoServerFixture.TryLocate(out var serverRoot))
            {
                return false;
            }

            server = GoServerFixture.Create(serverRoot);
            try
            {
                server.StartAsync(StartupPatience).GetAwaiter().GetResult();

                transport = new TcpTransport(
                    new TcpTransportOptions { Host = "127.0.0.1", Port = server.Port },
                    new SystemClock());
                session = new ProtocolSession(
                    transport, new SystemClock(), new RecordingSessionScheduler());
                session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();
                return true;
            }
            catch
            {
                // The server started but the client could not attach; without this
                // the process would outlive the test and hold its port.
                transport?.Dispose();
                server.Dispose();
                throw;
            }
        }

        [Test]
        public void LoginOverARealSocketReturnsATypedResponse()
        {
            if (!TrySetUp(out var server, out var transport, out var session))
            {
                Assert.Ignore(SkipReason);
                return;
            }

            using (server)
            using (transport)
            using (session)
            {
                var response = session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto { PlayerName = "e2e-probe" },
                    TimeSpan.FromSeconds(10),
                    CancellationToken.None).GetAwaiter().GetResult();

                Assert.That(response, Is.Not.Null);
                Assert.That(session.State, Is.EqualTo(SessionState.Connected));
            }
        }

        [Test]
        public void TheRoundTripProbeMeasuresARealLatency()
        {
            if (!TrySetUp(out var server, out var transport, out var session))
            {
                Assert.Ignore(SkipReason);
                return;
            }

            using (server)
            using (transport)
            using (session)
            {
                var latency = session.ProbeRoundTripAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();

                Assert.That(latency, Is.GreaterThan(TimeSpan.Zero),
                    "The server echoes ClientPingRequest verbatim, so a mismatch " +
                    "would have thrown CorrelationMismatchException instead.");
                Assert.That(latency, Is.LessThan(TimeSpan.FromSeconds(5)));
            }
        }

        /// <summary>
        /// The suite's one slow test, and worth its cost. The server's ping interval
        /// is a compile-time constant, so there is no way to make this faster; and a
        /// client that fails to answer a heartbeat loses the connection 35 seconds
        /// later with nothing in any log to say why.
        /// </summary>
        [Test]
        [Timeout(60000)]
        public void TheClientAnswersARealServerHeartbeat()
        {
            if (!TrySetUp(out var server, out var transport, out var session))
            {
                Assert.Ignore(SkipReason);
                return;
            }

            using (server)
            using (transport)
            using (session)
            {
                var faults = new List<SessionFault>();
                session.SubscribeToFaults(faults.Add);

                // The server pings every 15 s; 25 gives one interval plus slack.
                UniTask.Delay(TimeSpan.FromSeconds(25), DelayType.Realtime)
                    .GetAwaiter().GetResult();

                Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                    "A missed Pong makes the server close the connection.");
                Assert.That(faults, Is.Empty,
                    "Faults: " + string.Join("; ", faults.ConvertAll(f => f.Diagnostic)));

                // Still usable, which a half-dead connection would not be.
                var latency = session.ProbeRoundTripAsync(CancellationToken.None)
                    .GetAwaiter().GetResult();
                Assert.That(latency, Is.GreaterThan(TimeSpan.Zero));
            }
        }
    }
}
```

If `LoginRequestDto`'s property is not `PlayerName`, take the real name from `Packages/com.echo.harness/Runtime/Contracts/Dtos/AuthDtos.cs` rather than guessing.

- [ ] **Step 5: Run the end-to-end tests**

Run: `.\Tools\ci\verify.ps1`
Expected: `EditMode: 143/143 passed.` The three end-to-end tests take roughly 30-40 s together, dominated by the heartbeat one and by `go run`'s first compile.

If they report `Ignore`, `TryLocate` did not find the checkout. Confirm `E:\code\_github\magic-card-server-golang\main.go` and `data\characters.json` exist, or set `ECHO_GO_SERVER_ROOT`.

- [ ] **Step 6: Note the end-to-end tier in verify.ps1**

The EditMode suite already runs from `verify.ps1`, so the new tests execute without a change to its ordering. Add one line immediately before it invokes `run-unity-tests.ps1`, so a slow run is not mistaken for a hang:

```powershell
Write-Host "Unity tests include a live Go server end-to-end tier (~40s); it skips itself if the server checkout is absent." -ForegroundColor DarkGray
```

Do not add another `go test` invocation. `Tools/protocol` and the server baseline are already covered.

- [ ] **Step 7: Update the checklist**

In `docs/migration-checklist.md`, under Phase 1, mark `[x]` on these, keeping each item's existing text: write serialization and idempotent `DisconnectAsync`; `StopAsync` `try`/`finally`; the `Dispose` transport story; `StopAsync` from `Faulted`; session thread safety; `Dispatch` outside the pump's `try`; answering a requester whose response fails to decode; the `DefaultRequestTimeout` rename; the parking transport double and `FailNextSend` coverage; distinguishable exception types; root-cause ordering in `FaultTheStreamAsync`; the `SessionFault.MessageId` contradiction; the fault assertion convention.

Replace the TCP transport item with what is left of it:

```markdown
- [ ] Add reconnect policy and structured telemetry to `TcpTransport`. Framing,
  cancellation, write serialization, the send budget, and the read-idle watchdog
  landed with the transport; reconnect and metrics did not, so a dropped link today
  faults the session and stays down until something calls StopAsync and StartAsync.
```

Replace the version-negotiation item so it records that it is blocked rather than merely pending:

```markdown
- [ ] Introduce protocol version/capability negotiation. **Blocked on a server
  change:** `Server.Start` goes straight from `Accept` to `sess.run()` with no
  handshake, so this cannot be done from the client alone. Scheduled for the
  protocol-evolution iteration, together with a correlation identifier, where the
  read-only constraint on the Go repository is lifted and both sides change together.
```

Add the two residuals this iteration creates:

```markdown
- [ ] Decide whether one `NoDestination` fault per undelivered message is the right
  volume. It replaced a silent drop, which is strictly better, but every event that
  arrives before its UI subscriber now publishes one; the first Phase 2 view will
  show whether that reads as signal or noise.
- [ ] Give `MainThreadSessionScheduler` a DI registration. It is implemented and
  PlayMode-tested but nothing constructs it, because `HarnessComposition` registers
  only `HarnessRuntimeDescriptor` and session lifetime scopes are still undefined.
```

- [ ] **Step 8: Update the verification matrix**

In `docs/verification-matrix.md`, following the file's existing column layout, add a row per newly enforced property naming the test that enforces it: the streaming frame reader (`TcpTransportFramingTests` fragmentation and coalescing); the oversize and mid-body-EOF rejections; write serialization (`TcpTransportSendTests.ConcurrentSendsArriveAsWholeFrames`, judged by the loopback reader's own frame check, mutation-verified); the send budget and its `Pong` exemption; the read-idle deadline and its per-frame reset; main-thread confinement (`ProtocolSessionConfinementTests`, mutation-verified); and the live Go end-to-end tier with its skip condition and its cost.

- [ ] **Step 9: Run the full gate**

Run: `.\Tools\ci\verify.ps1`
Expected: exit 0, with the NuGet restore check, `ok echo/protocolcontract`, the fixture matching the Go source at 39 messages, the architecture gate passing, `EditMode: 143/143 passed.`, `PlayMode: 3/3 passed.`, and the Go server baseline clean.

- [ ] **Step 10: Commit**

```bash
git add Packages/com.echo.harness/TestKit/GoServerFixture.cs \
        Packages/com.echo.harness/TestKit/DeterministicFakes.cs \
        Packages/com.echo.harness/Tests/EditMode/GoServerEndToEndTests.cs \
        Packages/com.echo.harness/Tests/EditMode/DeterministicFakesTests.cs \
        Tools/ci/verify.ps1 \
        docs/migration-checklist.md \
        docs/verification-matrix.md
git commit -m "Prove the transport against the real server and record what is left"
```

---

## Notes for the implementer

**The test counts in the expected output are cumulative and approximate.** They assume every prior task landed and that no test was added beyond what is written here. A difference matching the number of tests you actually added is fine; a difference you cannot explain is not — stop and find out why before continuing.

**The mutation-verification steps are not optional.** Two of this iteration's central claims — that dispatch is confined to one context, and that writes are serialized — are exactly the kind that can be asserted by a test which would have passed anyway. Steps 2.8 and 6.7 are the only things standing between those tests and false confidence.

**If a socket test hangs, suspect a leaked listener first.** Unity stalls at domain reload on a live background thread, and the symptom looks like a frozen editor rather than a failing test. Every `LoopbackProtocolServer`, `TcpTransport`, and `GoServerFixture` in a test needs `using`.

**Do not widen the architecture gate.** Its source-text bans cover `Runtime\Domain` and `Runtime\Application` only, and everything socket-related in this plan lands in `Runtime\Infrastructure`. If the gate fails, something went into the wrong assembly — fix the placement, not the gate.

**Do not edit the Go server.** If a task seems to require it, that task is mis-scoped; stop and say so. The one iteration where that constraint lifts is the protocol-evolution one, and it is not this one.
