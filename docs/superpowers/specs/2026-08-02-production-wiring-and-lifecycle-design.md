# Production Wiring and Lifecycle — Design

Date: 2026-08-02
Status: approved for planning
Predecessor: `docs/superpowers/specs/2026-07-30-tcp-transport-and-session-concurrency-design.md`

## Problem

Three iterations have produced a typed contract, a session layer, and a real TCP
transport, covered by 155 EditMode and 5 PlayMode tests. None of it can be
constructed in a player build, and none of it has ever run outside a test.

Two facts establish that, both read from the source rather than assumed:

**There is no production `IClock`.** `SystemClock` and `ManualClock` both live in
`Echo.Harness.TestKit`, whose asmdef carries `"defineConstraints":
["UNITY_INCLUDE_TESTS"]`. `TcpTransport`, `SendBudget` and `ProtocolSession` all
require an `IClock` in their constructors, so in a player build none of them can be
built at all.

**Nothing composes the stack.** `HarnessComposition.Configure` registers exactly one
object, a `HarnessRuntimeDescriptor`. The repository contains no scene, no
`LifetimeScope`, and no code that calls `StartAsync`.

The cost of leaving this is not that a feature is missing. It is that a green suite
is being read as evidence about a stack that has never started, connected, or
stopped. Two of the residuals carried out of the previous iteration — the receive
cancellation that destroys the transport, and the main-thread hop that stalls rather
than throws — are reachable only from a real run, so they cannot be closed by adding
more tests to the existing tiers.

This iteration makes the stack constructible, runnable, and cleanly stoppable, and
closes the residuals that a real run forces into the open.

## Acceptance

A bootstrap scene that, with an endpoint configured, connects to the authoritative
Go server on entering play mode, completes a round-trip probe, and shuts down
cleanly on leaving it — no leaked socket, no hang, and no `SessionFault` on the
ordinary path.

"The container can resolve it" is explicitly *not* the acceptance criterion. That is
the standard this codebase has already met while remaining unrunnable.

## Placement

No new assembly. The architecture gate asserts that `Runtime/**` contains exactly
six asmdefs and set-compares each one's references, so every new type lands in an
existing assembly and the gate's tables need no edit. **If the plan finds it must
change those tables, that is a signal the layering went wrong, not a step to
perform.**

| Component | Path | Assembly |
|---|---|---|
| `IClock`, `IElapsedTime` | `Runtime/Application/HarnessPorts.cs` | `Echo.Harness.Application` |
| `SystemClock`, `StopwatchElapsedTime` | `Runtime/Infrastructure/` | `Echo.Harness.Infrastructure` |
| Endpoint resolution (env-var half) | `Runtime/Infrastructure/` | `Echo.Harness.Infrastructure` |
| Scheduler shutdown latch | `Runtime/Infrastructure/MainThreadSessionScheduler.cs` | `Echo.Harness.Infrastructure` |
| `HarnessEndpointSettings` | `Runtime/Bootstrap/` | `Echo.Harness.Bootstrap` |
| `HarnessLifetimeScope`, `HarnessSessionDriver` | `Runtime/Bootstrap/` | `Echo.Harness.Bootstrap` |
| `ManualTime` | `TestKit/` | `Echo.Harness.TestKit` |
| Bootstrap scene | `Assets/Scenes/` | — |

`Echo.Harness.Bootstrap` already carries `noEngineReferences: false` and references
`VContainer`, so the `ScriptableObject` and the `LifetimeScope` subclass need no
reference change. `Stopwatch` is BCL and adds nothing to `Infrastructure`.

## The clock port split

`IClock` today is a bare `DateTimeOffset UtcNow`, carrying a 35-line comment that
names two live defects and then concedes that no test can reliably catch either,
because the requirement sits on the implementation rather than on any call site.

The split removes both defects structurally instead of warning about them.

```csharp
public interface IClock                  // stamps a moment
{
    DateTimeOffset UtcNow { get; }
}

public interface IElapsedTime            // answers "how long", and only that
{
    long GetTimestamp();
    TimeSpan GetElapsedTime(long startingTimestamp);
}
```

Two ports rather than one interface with two faces, because of what the call sites
turn out to need. Read from the source:

- **`SendBudget` uses `clock.UtcNow` for interval arithmetic only** — `lastFill` at
  construction, and `(clock.UtcNow - lastFill).Ticks` in `TryConsume`. It has no
  wall-clock semantics at all.
- **`TcpTransport` holds an `IClock` solely to hand it to `SendBudget`**
  (`TcpTransport.cs:139`). Its own read-idle deadline runs on `CancelAfter` and
  deliberately does not use the injected clock; a comment at `:325-327` says so and
  says not to "fix" it.
- **`ProtocolSession.ProbeRoundTripAsync` is the only genuine dual consumer.**
  `sentAt` becomes the wire `ts` the server echoes, *and* the baseline for the
  returned duration.

So after the split `TcpTransport` stops depending on `IClock` entirely and takes an
`IElapsedTime`, which makes its dependency honest. A single merged interface would
have left it depending on a wall clock it never reads — the exact confusion being
removed.

**Production implementations.** `SystemClock` moves from TestKit to
`Infrastructure` with its body unchanged. It does not stay behind in TestKit: the
tiers that use it, the end-to-end one above all, reach it through TestKit's existing
reference to `Infrastructure`, so one implementation serves both.
`StopwatchElapsedTime` is built on `Stopwatch.GetTimestamp()` and
`Stopwatch.Frequency`; the static `Stopwatch.GetElapsedTime` is .NET 7+ and is not
available here, so the conversion is written out.

**Test implementation.** `ManualClock` becomes `ManualTime`, one type implementing
*both* interfaces and registered as both, so a test advances time once and both
faces move. It replaces `ManualClock` rather than joining it: two manual objects
would force every existing call site to advance two things, which is noise that
obscures what each test is actually about. Existing `clock.Advance(...)` call sites
keep their shape.

**Invariant that must survive the `SendBudget` port.** `lastFill` advances by whole
refill intervals so the fractional remainder carries forward. Setting it to "now"
discards the remainder and makes the effective send rate lower than configured. The
mechanism for preserving this against a `long` timestamp is the plan's to choose;
the invariant is not.

**Two comments become false and must be rewritten in the same change.**
`TcpTransport.cs:50-54` justifies the send gate partly by `SendBudget` writing "an
int and a `DateTimeOffset`, the second wide enough to tear" — `lastFill` becomes a
`long`. And `IClock`'s warning block loses most of its subject matter, because a
non-monotonic wall clock no longer damages anything once nothing measures intervals
with it.

## Composition and configuration

`HarnessComposition.Configure(IContainerBuilder)` stays the single registration
point; `HarnessLifetimeScope` only calls it. That keeps the whole object graph
buildable in EditMode from a bare `ContainerBuilder`, so the scene is the driver and
not a precondition for testing the wiring.

**Lifetime scopes are narrowed deliberately.** The checklist item asks for
app/session/scene scopes. This iteration builds the **app root scope only**. There
is no second scene and no login flow, so a child scope with one child and a lifetime
identical to its parent is ceremony. The checklist item is annotated as partly
satisfied rather than ticked, and the rest is left to Phase 2, where a login flow
gives a session scope something to mean.

**Endpoint resolution: asset → environment variable → not configured.**

The address of the developer's server must not enter the repository. That rule is
already documented on `RemoteServerEndpoint` and is why `ECHO_SERVER_HOST` has no
default — even a commented-out fallback would put the address in git.

The asset half is a `HarnessEndpointSettings : ScriptableObject` at
`Assets/Resources/HarnessEndpointSettings.asset`, added to `.gitignore` along with
its `.meta`. It carries a `host` string, blank meaning "fall through", and a `port`
int defaulting to the server's own 43966.

**It is loaded through `Resources.Load`, not a serialized reference from the scene,
and the reason is not convenience.** The scene is committed and the asset is not, so
a serialized reference would ship a dangling GUID in a committed scene, breaking for
every fresh clone. `Resources.Load` returns `null` when the asset is absent, which
is exactly the "not configured" path, with nothing broken to explain.

**The environment-variable half must have exactly one implementation**, shared by
Bootstrap and TestKit, living in `Infrastructure`. `RemoteServerEndpoint` becomes a
delegate to it. Two copies of the port guard — the one that parses with
`NumberStyles.None` so a signed value is a reported typo rather than a silent
fallback — will drift, and the consequence of the drift is running against a
different endpoint than the one asked for and reporting whatever answers as the
truth.

**Registration is identical in both states.** When no endpoint is configured the
full graph is still registered, with the resolution result registered alongside it
as a value carrying `IsConfigured`; the driver decides whether to connect. Omitting
registrations in the unconfigured state would mean the EditMode resolution test
covers a shape that never runs.

## Lifecycle and shutdown

### Measure before wiring

The whole shutdown design rests on one assumption: that some signal arrives *before*
the player loop stops. Which callbacks fire on each of the three paths — player
quit, editor exit-play-mode, domain reload during play mode — and their **order
relative to the loop stopping**, are to be measured, not recalled.

The first task of the plan is a measurement that records the actual callback
sequence and loop state on all three paths. The driver is wired from that result.
The latch's acceptance criterion *is* the measurement's conclusion: **the latch must
demonstrably be set before the loop stops**, or it guarantees nothing.

This is a standing hazard in this project rather than an abstract caution. The
previous iteration's ledger records both a hypothesis published and disproved within
one turn, and a mechanism fabricated and reported to the developer as fact.

### Two paths, deliberately

**Preferred — stop while the loop is alive.** `HarnessSessionDriver`, registered as
VContainer's `IAsyncStartable` and `IDisposable`, starts the session when an
endpoint is configured and stops it through an ordered shutdown hook, so every hop
runs normally. Zero `SessionFault`; session reaches `Disconnected`.

**Backstop — the loop is already gone.** `MainThreadSessionScheduler` gains a
shutdown latch. Once latched, `SwitchToSessionContextAsync` completes as *cancelled*
rather than queueing a continuation onto a loop that will never run again. Candidate
signals are `Application.quitting` plus, under `#if UNITY_EDITOR`,
`playModeStateChanged` (`ExitingPlayMode`) and `beforeAssemblyReload`; which of
these are necessary and which are redundant is the measurement's output.

The backstop rides machinery that already exists and is already tested: a cancelled
hop reaches `StopAsync` as an `OperationCanceledException`, and the previous
iteration wrapped `StopAsync` in `try`/`finally` precisely so `FailPendingRequests`
still runs. It adds no new teardown logic.

Taking both is what makes the ordinary case quiet. With the latch alone, every exit
from play mode would travel the cancellation path, and a normal shutdown would be
indistinguishable in the logs from something genuinely going wrong. **The two paths
must be distinguishable in the session's diagnostics.**

An unconfigured start is not a failure. The driver logs one clear line and stays
`Disconnected`, matching the convention the end-to-end tier already uses when it
skips itself.

### Receive cancellation is a contract, not a defect

`TcpTransport.ReceiveAsync` closes the link on **any** cancellation of its token,
because closing the socket is the only way this runtime can unpark a blocked read.
The ruling is that this is the defined contract: **cancelling a receive means
abandoning the link.**

1. It is stated on `ITransport.ReceiveAsync`, the way `SendAsync` gained its
   concurrency contract in the previous iteration.
2. The session must therefore never cancel an individual receive except as teardown.
   Today it only cancels through `CancelPump()`, which is teardown — but it is
   correct by accident, having had no other reason to cancel. The constraint is
   written down so that a future "pause reading" feature does not reach for the pump
   token.
3. The application-level token handed to `StartAsync` is a **shutdown token**, owned
   by the driver and not reused for anything finer.

### Two adjacent residuals, stated as requirements

**A send from a thread with no `SynchronizationContext` must reach the write gate**
and then either succeed or fail for a transport reason. It must not throw
`InvalidOperationException` before the gate is taken. Today `Task.AsUniTask()` calls
`TaskScheduler.FromCurrentSynchronizationContext()` eagerly, and this is masked only
because Unity's main thread always has a context. How to avoid the eager capture is
the plan's choice.

**A teardown hop that fails for a non-cancellation reason must publish a
`SessionFault`** naming the bookkeeping that was completed off-context. The previous
iteration corrected the exception the caller receives; the `finally` still
un-registers the gate entry while running off-context and reports nothing.

## Testing

**EditMode.** `StopwatchElapsedTime`'s monotonicity contract; `ManualTime` advancing
both faces together; `SendBudget` ported to `IElapsedTime` with its remainder-carry
invariant pinned; the full graph resolving from `HarnessComposition.Configure`, such
that deleting any registration fails the test; the endpoint chain across asset
present/absent × variable set/unset/invalid, with "not configured" as an ordinary
return rather than an exception; and a send from a thread with no
`SynchronizationContext`, constructible here via `Task.Run`.

**PlayMode.** The latch: once set, `SwitchToSessionContextAsync` completes cancelled
instead of parking — removing the latch must make this test hang rather than merely
report a wrong value. The driver's preferred path over a fake transport: zero faults,
reaching `Disconnected`. The driver's backstop path: latch first, then `StopAsync`
returns within a bounded time and still fails pending requests.

**End-to-end tier.** The real graph against the real server — connect, one probe,
clean stop — skipped when unconfigured, following the existing convention.

**Manual.** Press Play with an endpoint configured; leave play mode. This is the
acceptance criterion and it is performed by a human.

### One honest limitation

Two things in this design are removed structurally and cannot be covered by a test,
and neither will be dressed up as though they were.

The first is `SendBudget` wedging at zero after a backwards wall-clock step. The
split removes it by making a wall clock unreachable from the type, and no test can
demonstrate that something is unrepresentable.

The second is the acceptance criterion itself. CI has no configured endpoint, by the
same rule that keeps the address out of the repository, so both the end-to-end tier
and the manual check are local-only. `docs/verification-matrix.md` already carries a
local-only enforcement paragraph; this iteration widens it rather than implying the
gate covers a run it cannot reach.

## Documentation, as a deliverable

- `IClock`'s warning block shrinks to match what is still true; `TcpTransport.cs:50-54`
  is rewritten for a `long` `lastFill`.
- `docs/verification-matrix.md` gains rows for the new tiers and an accurate account
  of what remains local-only.
- `docs/migration-checklist.md` ticks the residuals discharged here and marks the
  lifetime-scope item partly satisfied, with the reason.
- The SDD ledger directory is gitignored. Anything worth keeping lands in tracked
  documentation as it is found, not in a sweep at the end. The stale
  `.superpowers/sdd/2026-07-30-tcp-transport-and-session-concurrency/` from the
  previous iteration is deleted; its content already reached
  `docs/migration-checklist.md`.

## Out of scope

- **Reconnect policy and transport telemetry.** A dropped link still faults the
  session and stays down until something calls `StopAsync` then `StartAsync`.
- **Production `IContentProvider` and `ILuaRuntime`.** Neither is required by the
  session stack; including them would pull Addressables and xLua into this iteration.
- **Protocol version and capability negotiation.** Blocked on a server change and
  scheduled for the protocol-evolution iteration.
- **Session and scene lifetime scopes.** Deferred to Phase 2 with a reason, above.
- **Any UI.** A connection-state view is Phase 2's view/view-model work.
