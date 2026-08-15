# Verification Matrix

## Local entry point

From the Unity project root:

```powershell
.\Tools\ci\verify.ps1
```

The aggregate command performs no deployment and does not require a live game
server. It verifies the checked-in Harness, runs Unity tests, then runs the existing
Go repository baseline.

| Gate | Entry point | Proves | Output |
|---|---|---|---|
| NuGet presence | `restore-nuget.ps1 -CheckOnly` | all six pinned R3 runtime assemblies exist | console |
| Fixture generator | `go test ./...` in `Tools/protocol` | the generator that produces `protocol.contract.json` is itself correct | console |
| Architecture | `verify-architecture.ps1` | editor revision, package pins, asmdef direction and purity flags, source restrictions, 39 IDs, frame rules | console |
| Protocol drift | `verify-architecture.ps1` → `go run . -check` in `Tools/protocol` | the checked-in `protocol.contract.json` still matches the Go server byte for byte | console |
| Unity tests | `run-unity-tests.ps1` | EditMode and PlayMode suites both have zero failures, no empty run, and no skip outside the one sanctioned class | `Artifacts/unity-test-summary.json` on both paths; XML/logs additionally in batchmode |
| Go baseline | `go test ./...` | existing authoritative server remains green | console |
| Aggregate | `verify.ps1` | all gates above | `Artifacts/verification-summary.md` |

The architecture gate's source-text assertions are narrower than "the whole runtime",
and reasoning about them as if they were uniform has misled reviews. They cover
`Runtime/Domain` and `Runtime/Application` only, and the two banned-word lists differ:
`Cysharp` is forbidden in `Domain` and permitted in `Application`, which is what keeps
the UniTask async boundary legal there. `Runtime/Contracts` has no source-text
assertion at all. What constrains it is its asmdef: the gate pins `references` to
zero, and also pins `noEngineReferences`, `overrideReferences`, and
`precompiledReferences` per assembly. Those three flags, not the reference list, are
what make `using UnityEngine;` and `using R3;` fail to compile there — so flipping
one to make an illegal DTO field build is now a gate failure rather than a silent
loss of the pure-contract layer.

The fixture generator's own `go test ./...` runs from `verify.ps1` before the
architecture gate. It has to, because the drift gate compares the generator against
the generator's own committed output and is therefore blind to a generator regression
by construction; those unit tests are the only thing that can catch one. Like the
drift gate itself, they are **enforced locally, not in CI** — CI invokes
`verify-architecture.ps1` directly and never runs a Go test of either module.

## Test layers

| Layer | Current Harness coverage | Explicitly not covered yet |
|---|---|---|
| Static | package/version pins, dependency direction, forbidden references, fixture shape, fixture-vs-Go drift | API compatibility across upgrades |
| EditMode | frame codec, message IDs, all 39 typed DTOs driven from the fixture, nullable/omitempty behavior, nested view tree, protocol session routing and correlation, fakes, DI, third-party type resolution, optional xLua probe, real TCP framing/cancellation/backpressure against a byte-level loopback, endpoint resolution through both doors, and a live socket against the remote Go server — including one case that resolves the whole session out of the composition root before using it | catalogs, Lua VM, gameplay, reconnect after a dropped link |
| PlayMode | UniTask player-loop yield, R3 disposal, UI Toolkit data source, the main-thread scheduler hop, the scheduler's shutdown latch on both its per-instance and its process-wide arming path, and the session driver's start/stop lifecycle against a fake transport | scenes, final UI, assets, device input; whether the shutdown handlers are actually *subscribed* — see the latch row below; and the bootstrap scene itself, which no automated test loads — see "What the gate does not enforce" |
| Go | existing repository unit/integration baseline | a locally spawned server; the end-to-end tier talks to a remote one and belongs to the EditMode row |
| Performance | framework installed | budgets and measurements |

## Transport and session properties

Each row is a property the transport or the session now guarantees, and the test
that would fail if it stopped holding. Ten of them are marked **mutation-verified**:
the property was removed from the production code and the test was confirmed to
fail, because each is the kind of claim a test can assert while passing for an
unrelated reason. The count was two until the shutdown latch's process-wide arming
path got a test; that row was the third, and it was added precisely because a review
mutation showed the whole suite staying green without it. Task 8 added the fourth and
fifth, and the fifth is the same story again: the three composition tests the plan
specified all stayed green with the configured endpoint dropped on the floor. The
sixth is the composed-graph end-to-end row, and it was measured the same way — twice,
once by deleting a registration and once by dropping the endpoint, with its three
hand-built siblings green through both. The login slice added four more: the fault
router's UI hop, the fault sink's entry-point registration, the login screen's
`CanSubmit` gate, and the login layout's declared bindings.

| Property | Enforced by | What would otherwise pass unnoticed |
|---|---|---|
| A frame is read whole regardless of how TCP segments it | `TcpTransportFramingTests.AFragmentedFrameIsReadAsOneMessage`, `.TwoFramesInOneSegmentAreReadAsTwoMessages` | A reader assuming one read per frame. The loopback double writes the split deliberately: header alone, then body, and two whole frames in a single write. |
| A declared length over the bound is refused, and an EOF mid-body fails the receive | `TcpTransportFramingTests.ADeclaredLengthOverTheBoundFailsTheReceive`, `.ACloseMidBodyFailsTheReceive` | A four-byte header claiming gigabytes, allocated before it is judged; and a peer that vanishes mid-body read back as a short but valid frame. |
| Concurrent sends arrive as whole frames | `TcpTransportSendTests.ConcurrentSendsArriveAsWholeFrames` | **Mutation-verified.** Judged by the loopback reader's own framing rather than by the test's arithmetic, so an interleaved length prefix desynchronizes the reader instead of merely reordering payloads. The session answers a heartbeat from the receive pump while a caller sends, so this is the real concurrency, not a hypothetical. |
| The send budget refuses the message past the server's limit, and `Pong` is exempt | `TcpTransportSendTests.ExceedingTheBudgetThrowsAndKeepsTheConnection`, `.PongIsExemptFromTheBudget`; `SendBudgetTests` for the token arithmetic | The server's limit is a compile-time 30 per second and exceeding it closes the connection with no error frame — on the wire indistinguishable from a pulled cable. A budget that could swallow a heartbeat reply would cause exactly the disconnect it exists to prevent. |
| Silence beyond the read-idle deadline fails the receive, and every frame resets it | `TcpTransportIdleTests.SilenceBeyondTheIdleTimeoutFailsTheReceive`, `.TheIdleDeadlineResetsForEachFrame`, `.ACallerCancellationIsNotReportedAsAnIdleTimeout` | A half-open link the kernel takes minutes to notice, parking the pump indefinitely. The reset is the other half: without it a healthy connection dies at a fixed age, and a test of the timeout alone cannot tell the two apart. |
| Every dispatched message hops to the session's context before anything reads or writes session state | `ProtocolSessionConfinementTests` (four tests) | **Mutation-verified.** Confinement, not locking, is what makes a plain `Dictionary` of pending requests and a plain `State` property safe once a real socket introduces a second thread — including the request timeout, which fires on a thread-pool thread and must hop before its `finally` touches the single-flight gate. |
| `MainThreadSessionScheduler` really reaches the main thread, and costs no frame when already on it | `MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread`, `.SwitchingWhileAlreadyOnTheMainThreadCostsNoFrame`, `.SwitchingWithACancelledTokenOnTheMainThreadThrows`, `.SwitchingWithACancelledTokenOffTheMainThreadAlsoThrows` (PlayMode) | A scheduler that only appeared to switch, and a hop billed a frame on the common path. Measured rather than asserted. **`SwitchingFromAThreadPoolThreadReachesTheMainThread` is intermittently red** on both paths, mechanism undiagnosed. See "What the gate does not enforce" below for what is and is not known about it; this row is a claim about what the tests are for, not a claim that they are green. |
| A scheduler refuses the hop once the loop is going away, whether it was latched by its own owner or by the process | `MainThreadSessionSchedulerTests.ALatchedSchedulerCancelsInsteadOfQueueingOntoADeadLoop`, `.AnUnlatchedSchedulerStillHops`, `.TheProcessWideSignalLatchesASchedulerThatNeverLatchedItself`, `.ReturningToEditModeClearsTheProcessWideSignal` (PlayMode; the last is `[Test]` and editor-only, the other three are `[UnityTest]`) | **Mutation-verified.** `IsLatched => latched \|\| processIsShuttingDown` rewritten to `=> latched` deletes every process-wide shutdown signal at once, and until these tests existed the whole pipeline stayed green through it — the static, its three handlers and its two installers were all deletable unnoticed. **What no test here covers:** that those handlers are *subscribed*. A PlayMode test cannot stage a real `Application.quitting` or play-mode exit, because the event that would raise it is the one that ends the run; the tests invoke the handlers directly. The subscription's only evidence is a one-off `Editor.log` observation recorded in the Task 7 reports, not something CI repeats. |
| The whole session stack resolves from the composition root, once | `CompositionSmokeTests.HarnessComposition_ResolvesTheWholeSessionStack` (both `[TestCase]`s), `.HarnessComposition_RegistersTheSessionAsASingleton` | This row replaces "the type has process-wide side effects with no consumer", which Task 8 made false. `MainThreadSessionScheduler` now has a production consumer: `HarnessComposition` registers it as `ISessionScheduler`, and deleting that one line fails all three tests. Its two installers still run on **every editor domain load** (`[InitializeOnLoadMethod]`) and **every play-mode entry** (`[RuntimeInitializeOnLoadMethod]`) whether or not an instance exists, so the class was live in production before anything constructed it — that part was and remains true. **The "registering is not resolving" caveat this row used to carry is retired**, and is recorded rather than deleted because the shape of the gap is worth keeping: every registration is lazy, so until something resolved the graph, `Configure` could have registered anything at all and stayed green. `HarnessLifetimeScope` on `Assets/Scenes/Bootstrap.unity` now calls it and registers `HarnessSessionDriver` as an entry point, so a session is started and stopped for real — see the note on `ProtocolSession.SwitchToSessionContextForTeardownAsync` for what the driver owes it on the way out. What remains uncovered is narrower and is listed under "What the gate does not enforce": no automated test loads that scene, and the one test that resolves this graph and then uses it needs an endpoint CI does not have. |
| A configured endpoint reaches the transport, and an unconfigured one leaves it on its defaults | `CompositionSmokeTests.HarnessComposition_PointsTheTransportAtTheConfiguredEndpoint`, `.HarnessComposition_LeavesTheTransportOnItsDefaultsWhenUnconfigured` | **Mutation-verified**, and it is why these two tests exist at all. Nothing in EditMode connects, so resolving `ITransport` proves only that a transport was built, not where it points. With `Configure` mutated to ignore the endpoint entirely, the three tests above stayed green and only `PointsTheTransportAtTheConfiguredEndpoint` failed. The unconfigured half is the mirror: `EndpointResolution.NotConfigured` carries a null `Host` and a `Port` of `0`, so a straight-through assignment builds a transport aimed at nothing rather than at the loopback default, and again only the dedicated test noticed. |
| A port outside 1..65535 is refused at every door, not just the environment one | `EndpointResolutionTests.Resolve_RejectsAnAssetPortOutsideTheUsableRange`, `ServerEndpointTests.Constructor_RejectsAPortOutsideTheUsableRange`, `.TryResolveFromEnvironment_RejectsAnUnusablePort` (`0`, `-1`/`"-1"`, `70000`/`"70000"`, plus `"+43966"` and `"not-a-port"` on the environment door) | **Mutation-verified.** Task 6 guarded `ECHO_SERVER_PORT` only; the asset's `port` was a serialized `int` that reached the transport unexamined, and `ServerEndpoint`'s public constructor assigned it unchecked. `0` is what makes that matter rather than being tidy: it is `default(int)`, so a fresh asset or a reset Inspector field holds it, and `Socket` reads it as "let the OS choose" instead of refusing it — the connect then fails at the socket layer and reads as an unreachable server. The range lives once, in `ServerEndpoint.MinPort`/`MaxPort`/`IsUsablePort`; widening `MinPort` to `0` fails one case in each of the three fixtures at once, which is what proves there are not three copies of it. |
| Our reading of the protocol matches the server's | `GoServerEndToEndTests` (EditMode; three of its four tests) | Field names, framing, and the heartbeat reply, all against the authoritative Go server over a real socket. The reply is counted at the transport after the write returned; see below for why nothing else can see it. |
| The graph the composition root builds is one that actually talks to the server | `GoServerEndToEndTests.TheComposedGraphConnectsAndProbes` (EditMode) | **Mutation-verified, twice.** The other three end-to-end tests construct their transport, session, clock and scheduler by hand, so all three stay green with `HarnessComposition.Configure` broken — which is the whole reason this fourth one exists. Measured: deleting the `ProtocolSession` registration killed only this test, at `Resolve`, with `VContainerException: No such registration of type: IProtocolSession`; and making the registered `TcpTransportOptions` ignore the configured endpoint and keep the loopback defaults killed only this test again, at connect, with a refused-connection `SocketException`. It is the only test in the repository that fails because the *wiring* is wrong rather than because the *protocol* is. What it does not cover is the entry point: `HarnessLifetimeScope` registers `HarnessSessionDriver` on top of `Configure`, and that half belongs to the PlayMode driver tests. |
| A driver starts the session when an endpoint is configured, stays quiet when one is not, and finishes its shutdown without needing a frame | `HarnessSessionDriverTests` (PlayMode, six tests, against a fake transport) | An entry point that started nothing, or one whose shutdown returned a `UniTask` still pending when a quit hook already had no frame left to give it. The last of those is asserted rather than assumed, because a transport with a genuinely asynchronous close is a legitimate future change and must produce a warning rather than a session that is never stopped. |
| A login response becomes something Presentation can act on, and a broken transport is not disguised as a refusal | `LoginUseCaseTests` (9 tests) | The exception policy is the design: a timeout and a duplicate request become `NoAnswer` because the attempt finished badly, while cancellation and everything else escape. `TheReconnectTokenNeverLeavesTheUseCase` is structural rather than behavioural — the failure it prevents is a future helpful addition of the field, which no behavioural test would notice. |
| Faults reach a reader, once, on the right thread | `SessionFaultRouterTests` (12 tests) | **Mutation-verified.** Deleting the hop in the router's UI half fails `TheLogTakesNoHopAndTheObserverTakesOne`. Before this iteration nothing subscribed to `SubscribeToFaults` at all, so all seven kinds were produced and never read. The log deliberately does not hop and the UI delivery does; `NoDestination` is de-duplicated here rather than in `ProtocolSession`, whose contract of publishing every unrouted message is what makes a late subscription visible. |
| The fault sink is constructed rather than merely registered | `verify-architecture.ps1` source assertion on `HarnessLifetimeScope.cs` | **Mutation-verified.** `SessionFaultRouter` subscribes in its constructor and every VContainer registration is lazy, so deleting the `RegisterEntryPoint<SessionFaultRouterEntryPoint>` line leaves a sink that never sees a fault with every test still green. **What this does not prove:** that VContainer runs the registration. It proves the line is present. No test on this runtime can prove the rest, for the same reason `HarnessSessionDriver`'s subscription cannot be asserted. |
| The login screen shows connection state, and a dropped link does not erase a refusal | `LoginViewModelTests` (9 tests), `LoginViewTests` (PlayMode, 1 test) | **Mutation-verified.** Forcing `CanSubmit` to `true` fails two tests. `ResultText` and `ConnectionFaultText` are separate fields so that a fault arriving mid-read cannot overwrite the message the user is looking at. |
| The login layout declares a real binding for every field, not just a path that never binds | `LoginLayoutTests` (4 tests) | **Mutation-verified.** Loads the real `Login.uxml` through `AssetDatabase` and asserts each of the five elements carries an actual `DataBinding`, not just a `data-source-path` attribute — which records where to look and never records what to bind, so an imported layout with none would still pass every other test in this table, because the PlayMode `LoginViewTests` binds a `Label` it builds by hand in C# and never opens the asset. Two of the four tests additionally pin the resolved `dataSourcePath` on the submit button and the status label, so a copy-pasted path is caught too. Deleting the submit button's `<Bindings>` block turns it red. **What it does not prove:** that the running screen displays anything — it proves the layout declares bindings, not that a panel renders them. |

### The two runners are graded by one function, with one difference

`run-unity-tests.ps1` has two paths — a connected Unity editor and a batchmode
fallback — and CI is configured to take the batch one
(`-PreferConnectedEditor:$false`), though see **CI boundary**: that job has never
actually run. The batch path used to verify nothing but the editor's exit code and
the existence of the results XML, which it never opened. So none of the gate's real
checks existed on the path CI is configured to run: no empty-run guard, no
accounting, no skip count, and no sanctioned-skip check — and a machine with no
endpoint is permanently in the skip state, which is the one state that check exists
to police.

Both paths now call the same `Assert-UnityTestRunPassed`, the batch path feeding it
the NUnit XML reshaped into the connected producer's shape, and both write
`Artifacts/unity-test-summary.json`.

**One check is weaker on the batch path, and it is named here rather than
generalised away.** The connected path receives a summary and a result list from
two different places in the Unity Pipeline package, so comparing them is a real
cross-check — it is what catches `summary.skipped = 1` with the row absent, or a
row labelled `Ignored` instead of `Skipped`, both of which used to pass. The batch
path has only one producer: the summary is *derived* from the `test-case` rows, so
the skipped-row cross-check runs the identical filter on both sides and cannot
catch a miscount. That is deliberate. The alternative is trusting
the `test-run` element's own `total`, whose NUnit 3 semantics for skipped, ignored
and explicit tests differ from this producer's — and measuring it would mean
running batchmode against a project a developer keeps open, which is how a real
editor session was destroyed during this iteration.

The **accounting check is the exception, and it runs the other way round.** On the
connected path it is dead: `PipelineTestRunner` defines `total` as the sum of the
four counters, so `passed + skipped ≠ total` reduces to `failed + inconclusive ≠ 0`,
which the check above it has already caught. On the batch path `total` is the row
count and the four counters are independent filters over those rows, so a row
carrying a result string none of them recognises is counted by `total` alone.
Measured: a results file holding `<test-case result="Warning"/>` arrives as
`failed=0, inconclusive=0` and is caught only here —
`EditMode did not account for every test: passed=1, skipped=0, total=2.` On the batch
path it is the sole guard against an unrecognised result state.

Everything else — empty run, named failures, the skip count in the log, and the
sanctioned-skip check — grades identically on both paths.

### What the heartbeat test can and cannot see

`TheClientAnswersARealServerHeartbeat` waits 25 s and asserts a `Pong` was composed
and accepted by the socket — the count is taken after `WriteAsync`/`FlushAsync`
return, which is local acceptance rather than proof of delivery to the peer. That
last part is the whole test. Its other three assertions — the session is
still `Connected`, no fault was published, a round trip still completes — cannot
fail for a client that never answered, and this was measured rather than argued:
with `ProtocolSession`'s heartbeat reply removed entirely, all three still passed.

The arithmetic is the server's. `internal/network/session.go` ticks its heartbeat
loop every 15 s and closes when `time.Since(lastPongAt) > 35s`, with `lastPongAt`
set at accept and refreshed **only** by an inbound `Pong`. The ticks therefore land
at 15/30/45 s and the first one that can close a silent client is **t=45 s** — twenty
seconds after this test has finished. The client's own `ReadIdleTimeout` is 45 s, so
it cannot notice either.

So the property this tier proves is *we reply to a real Ping*, not *the server
accepts our replies*. Making it the second would mean waiting past 45 s and nearly
doubling the slowest test in the suite; the cost was judged higher than the extra
claim is worth. Anything asserting the server's own enforcement belongs in a test
that says so in its name.

### The end-to-end tier skips itself, and what that costs

`GoServerEndToEndTests` needs an endpoint, and there are two doors to one: an
`Assets/Resources/HarnessEndpointSettings.asset` with a host, tried first, and the
`ECHO_SERVER_HOST` variable second. Neither is committed — the asset and its `.meta`
are gitignored, and the variable has no default — because it is a developer address
and a source with no fallback is the whole mechanism keeping it out of the tree. On a
machine with neither, all four tests in the class call `Assert.Ignore`, and the gate
tolerates the skip for that one class alone, naming any other class that skips.

The cost is worth stating plainly: **on such a machine the suite goes green having
never run the one test that can disagree with our own understanding of the
protocol.** Everything else in the repository — the fakes, the byte-level loopback
double, even the generated fixture — encodes our reading of the server rather than
checking it. CI is such a machine, permanently. So the skip count in the runner's
summary line is not cosmetic: `188/192 passed, 4 skipped` and `192/192 passed` are
different claims about how much is known, and only the second one has been
contradicted by a real server. Both of those figures are measured on this tree, one
run each way, with the gate exiting 0 both times.

**The order of the two doors is not arbitrary, and the reason is a measurement.**
Whichever process runs the tests is the one that reads the variable, and on the
connected-editor path that process is the **Unity editor** — not the shell invoking
`verify.ps1`. An editor inherits its environment block from whatever launched it,
normally Unity Hub, which is long-running, so a variable set after the Hub started is
invisible to the editor *however many times the editor alone is restarted*. That was
measured, not supposed: with the variable set at user scope and a genuinely restarted
editor, the tier still skipped. The asset is read through `Resources.Load` at test
time, so it needs no restart at all, and it is the door that works on a machine a
developer is already using. To exercise the unconfigured half deliberately, rename
the asset rather than clearing the variable — and rename it back, because `git status`
reports the renamed file as untracked but says nothing at all about the ignored name
being gone.

### What the gate does not enforce, and one test that is not reliable

Two checks on the production wiring exist and **neither is gate-enforced**, for the
same reason: both need an endpoint CI does not have.

1. `GoServerEndToEndTests.TheComposedGraphConnectsAndProbes` is the only automated
   test that resolves the session from the composition root and then uses it. On any
   machine without an endpoint — CI, permanently — it skips with the other three, so
   the graph is proved to *build* by the EditMode composition tests and proved to
   *work* by nothing.
2. The manual acceptance check is a human opening `Assets/Scenes/Bootstrap.unity` and
   pressing Play: the console names the endpoint's source, the session reaches
   `Connected`, leaving play mode logs no error and no `SessionFault`, and the editor
   does not hang on exit. No automated test loads that scene. Nothing in the gate runs
   it, nothing in the gate notices if it is never run again, and it needs a configured
   endpoint too.

The manual acceptance check now has a second half, and it has the same standing as
the first: **nothing in the gate runs it, and nothing notices if it is never run
again.** With an endpoint configured, open `Assets/Scenes/Bootstrap.unity` and press
Play; the status label must reach "Connected.", the login button must be unusable
before that, a submitted name must return a real `player_id` from the Go server, and
leaving play mode must log no error and no unexplained `SessionFault`. No automated
test loads that scene, and the end-to-end tier that could check the wire half skips
itself on any machine without an endpoint. `LoginLayoutTests` already covers whether
`Login.uxml` declares a binding for each field, so that is not what this check is
for — what remains genuinely manual is the rest: that a panel actually renders on
screen, that the states sequence correctly against a real connection, and that the
console stays clean on the way out.

So the scene, and the graph as a running thing rather than a resolvable one, are
covered by a local run and a person — not by `verify.ps1` and not by CI.

**`MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread`
is intermittently red on both paths, and it is still red as of this document.** It
asserts that a continuation resumes on the main thread after
`await UniTask.SwitchToMainThread(cancellationToken)`; when it fails it reports
`Expected: 1 But was: <a thread-pool thread id>`.

- On the **connected** path it fails roughly **1 in 10**: one red in seven runs when
  it was first seen, and one red among eleven PlayMode attempts during the
  production-wiring iteration — eight of those green and two aborted before they
  produced a verdict — with long green streaks either side (four consecutive
  pipelines green at one commit, two more while this document was first written).
- On the **batch path** it failed **once in five runs** at `1d8fc21`. Read that as
  "not every run", not as a rate: five is a small sample, and some of those runs
  carried throwaway instrumentation alongside the test that has since been reverted.
  The batch path is the one CI is configured to take — but those runs were local, and
  CI has never reached the Unity suite at all; see **CI boundary**.

**This paragraph used to say the batch path failed *deterministically*, three runs for
three, and that is no longer reproducible.** Five runs at `1d8fc21` produced one red.
Neither `Runtime/Infrastructure/MainThreadSessionScheduler.cs` nor its test has
changed since `2bc34e9` and `c98d56f`, both dated 2026-08-07 and both ancestors of
`f7fa10a`, where the earlier failures were recorded — so the code under test was
byte-identical across every one of these measurements, and whatever moved was
environmental. What moved is not known.

The consequence has to be said rather than left to be inferred: **every "PlayMode
15/15" recorded for the production-wiring iteration is a connected-path number from a
suite that contains an intermittently failing test.** The figure is true of the run it
describes and is not a claim that the suite is reliable.

The advice this section used to give — "start from the batch path, where it reproduces
every time" — is withdrawn along with the determinism claim. There is no path on which
this reproduces on demand, and that is the main reason it is still undiagnosed. What
remains true is that **"do not run the batch path" is not a mitigation**: it is the
configuration that hides the failure more often, not one that removes it.

#### What a spike at `1d8fc21` ruled out, and what it did not

Throwaway instrumentation, since reverted, was run against the failing test and
against a probe that exercised the hop directly. It established three things:

- **UniTask had not lost track of the main thread.** At the moment of failure
  `PlayerLoopHelper.MainThreadId` read `1` — correct — while the continuation resumed
  on pool thread `11`. So `SwitchToMainThreadAwaitable.IsCompleted` was correctly
  false and the continuation genuinely went through `PlayerLoopHelper.AddContinuation`.
- **The continuation was not run inline by a torn-down loop.** Neither
  `AddContinuation` nor `ContinuationQueue.Enqueue` has any inline-execution path:
  `Enqueue` only appends to an array, and `AddContinuation` throws rather than
  executing when the queue is null.
- **A direct probe never caught it in the act.** Three runs, each exercising both the
  bare `await UniTask.SwitchToMainThread(...)` and the same hop through
  `MainThreadSessionScheduler.SwitchToSessionContextAsync(...)`, reached the main
  thread every time.

It did **not** establish the mechanism, and so neither candidate verdict — a
production defect in the hop, or an artifact of the test apparatus — is supported.
The three findings above do not sit together comfortably: the continuation was
enqueued, nothing on the enqueue path can run it inline, and yet it resumed off the
main thread. That means the model of UniTask's resumption used to reason about this is
incomplete, and the surface nobody has examined is **who invokes the queued
continuation, and on which thread** — `ContinuationQueue.Run()` and its callers. That
is where the next attempt should start, instrumenting the failing test itself rather
than a separate probe, because a separate probe does not fail when the real one does.

### A PlayMode run straight after a failed PlayMode run is not fully trustworthy

Measured during the main-thread scheduler work: a PlayMode run immediately
following a failed one in the same editor process reported
`Expected: 1 But was: 170` on
`MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread`
with the source confirmed restored. Two consecutive re-runs and an independent run
were all green. The plausible cause is continuations stranded on the player loop
contaminating UniTask's `PlayerLoopHelper` across PlayMode sessions.

This is recorded as a property of the apparatus, not a bug anyone is chasing.
**Re-run before investigating a strange PlayMode failure that follows another
failure**, and if a fresh editor process reproduces it, then it is real.

### Two more apparatus notes, moved here from a working ledger that is gone

Both were measured during the transport iteration and had no home in the repository
until now; the notes they came from were never tracked, so this paragraph is the only
copy.

**A transient CLI exit fails the whole gate on the connected path.** The polling loop
in `run-unity-tests.ps1` throws on *any* non-zero exit from the `test_status` command,
including a dispatch timeout that a second poll would have survived — while its
sibling `Wait-ForUnityCompile` deliberately tolerates an unreachable editor for
precisely that reason. The two loops grade the same class of event differently. It was
seen once, as exit code 6 raced against a domain reload, and it has been left alone:
it fails closed and loudly, which is the safe direction. Note the consequence for
reading a run, because it is the opposite of the batch path's: **on the connected path
a non-zero exit means the run produced no verdict at all, so re-run it** rather than
hunting for a pass/fail line that was never printed. The benign leaked exit code
belongs to the batch path's connection probe only.

**The heartbeat mutation above is attested, not archived.** The claim that removing
`ProtocolSession`'s heartbeat reply left the other three assertions passing was
measured against the live remote server — twice, once with the `PongsSent` assertion
present (failed, `Expected: greater than 0 But was: 0`) and once with it suppressed
and the same mutation still in place (passed). No artifact in this repository records
either run; the evidence is a report made in-session and confirmed by the reviewer who
demanded it. It cannot be re-derived without a ~25 s run against a server CI never
reaches. Anyone reopening that question should re-run it rather than trust the sentence.

## Prerequisites

- PowerShell 7.
- Unity `6000.2.7f2`; local default is
  `E:\code\_Unity\editor\6000.2.7f2\Editor\Unity.exe`, or set
  `UNITY_EDITOR_PATH`.
- Go available on `PATH`.
- When `Assets/Packages` is absent, install the pinned CLI and restore:

```powershell
dotnet tool install --global NuGetForUnity.Cli --version 4.5.0
.\Tools\ci\restore-nuget.ps1
```

If this project is already open, the test script uses the connected Unity Pipeline
instance. Otherwise it uses hidden batchmode and writes NUnit XML.

## CI boundary

**CI runs the architecture gate only. The Unity suite is a local gate, not a CI
gate.** That is stated first because the version of this section that stood until
now described an arrangement which had never once worked.

`.github/workflows/unity-tests.yml` declares two jobs:

- **`architecture`** runs `verify-architecture.ps1` on GitHub's hosted
  `windows-latest`. It needs no runner registration, no Unity licence and no
  secrets, and it is the only check a pull request receives.
- **`unity-tests`** runs EditMode and PlayMode on the batch path
  (`-PreferConnectedEditor:$false`) and requires a runner labelled
  `[self-hosted, Windows, unity-6000.2.7f2]`, so that the exact licensed editor
  revision is controlled. **No such runner is registered on this repository.** The
  job is therefore gated behind `workflow_dispatch` and does not run on pull
  requests. To promote it back into the gate, register a runner carrying those three
  labels and delete the `if:`.

Before production, pin third-party GitHub Actions to reviewed commit SHAs.

### What was measured, and why this section was rewritten

Every pull request this repository has ever opened — three, across three iterations —
failed `architecture` in 12 to 16 seconds, and `unity-tests` was skipped behind
`needs: architecture` every time. **The Unity suite has never executed in CI.** Pull
request #3 was merged with that check red.

The cause was one line, and it is worth naming precisely because the *intent* was
correct throughout. `verify-architecture.ps1` composed the Go source path with
`Join-Path`, which resolves the drive qualifier and throws `Cannot find drive` under
`$ErrorActionPreference = 'Stop'` when handed the developer default on a runner that
has no `E:`. That threw **before** the `Test-Path` skip described below could be
reached — so the graceful skip this section documents was unreachable in exactly the
environment it was written for. The path is composed with
`[System.IO.Path]::Combine` now, which performs no drive resolution. Both directions
were re-measured after the change: a non-existent drive warns and exits 0, and a real
Go checkout still runs the byte comparison over all 39 messages.

Two further consequences are recorded because they outlived their cause:

- The `push` trigger named `main` while the default branch is `master`, so no push to
  the trunk ever triggered the workflow either. Only `pull_request` ran anything.
- **Every figure quoted in this document, and every figure in the iteration ledgers,
  comes from a local run** — a connected editor for most of them. None was produced by
  CI. Where this document says CI "takes" the batch path, read that as *is configured
  to take*, not *has run*.

The protocol drift gate inherits that boundary. `verify-architecture.ps1` looks for
`internal/protocol` under `-GoServerRoot` (default
`E:\code\_github\magic-card-server-golang`); when the directory is absent it emits a
warning and skips rather than failing, because a missing sibling checkout is the
normal CI condition and is not evidence of drift. A missing `go` on `PATH` skips the
same way and for the same reason: without that second guard the gate threw
`CommandNotFoundException`, which fails the architecture job with an error naming
neither the gate nor its optional dependency. When the source and the toolchain are
both present the gate is strict and real drift fails the build. The consequence is
that **the gate
is enforced locally, not in CI** — regenerate and commit the fixture in the same
change as any Go protocol edit. Point `-GoServerRoot` elsewhere if the server lives
at a different path. The comparison is byte-exact and safe on Windows because
`.gitattributes` pins `*.json` to LF in the working tree.
