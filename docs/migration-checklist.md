# Migration Checklist

This checklist governs future feature migration. Checked Harness foundation items are
present now; unchecked items are implementation work and are not part of this
deliverable.

## Foundation

- [x] Unity editor pinned to `6000.2.7f2 (2b518236b676)`.
- [x] Clean Architecture assemblies and dependency gate.
- [x] UI Toolkit MVVM, R3, UniTask, VContainer, and Addressables compile seams.
- [x] Controlled `ILuaRuntime` seam with optional xLua capability probe.
- [x] Machine-readable 39-message protocol baseline, generated from the Go source by
  `Tools/protocol` and gated against it by byte comparison.
- [x] Typed DTOs for all 39 messages and the five nested view types, asserted against
  the generated fixture by `ProtocolDtoContractTests`.
- [x] Message codec and session routing over `ITransport`, driven end to end by
  deterministic fakes.
- [x] Deterministic transport/content/Lua/time fakes.
- [x] EditMode, PlayMode, static, local aggregate, and CI entry points.
- [ ] Assign real CODEOWNERS teams in `.github/CODEOWNERS`.
- [ ] Protect `master` with required architecture and Unity-test checks. The trunk is
  `master`, not `main`; the workflow's push trigger named `main` from the first commit
  and therefore never fired once. Note the ordering this forces: the architecture check
  can be made required as soon as it goes green, but the **Unity-test check cannot be
  required until a self-hosted runner exists** — that job is manual-only today because
  no runner is registered. See "CI boundary" in `docs/verification-matrix.md`.

## Phase 1 — production infrastructure

- [ ] Introduce protocol version/capability negotiation. **Blocked on a server
  change:** `Server.Start` goes straight from `Accept` to `sess.run()` with no
  handshake, so this cannot be done from the client alone. Scheduled for the
  protocol-evolution iteration, together with a correlation identifier, where the
  read-only constraint on the Go repository is lifted and both sides change together.
- [ ] Add reconnect policy and structured telemetry to `TcpTransport`. Framing,
  cancellation, write serialization, the send budget, and the read-idle watchdog
  landed with the transport; reconnect and metrics did not, so a dropped link today
  faults the session and stays down until something calls StopAsync and StartAsync.
- [x] Serialize writes in the real transport, and make `DisconnectAsync` idempotent.
  `ITransport.SendAsync` now documents the concurrency requirement: the session
  answers a heartbeat from the receive pump, so a caller's send and a `Pong` can be
  in flight together, and a length prefix interleaved with another body
  desynchronizes the stream fatally.
- [x] Wrap `ProtocolSession.StopAsync` in `try`/`finally`. `FailPendingRequests`
  currently sits after the `DisconnectAsync` await, so a throwing disconnect — or an
  already-cancelled token handed to `StopAsync`, a realistic shutdown pattern —
  strands every waiter and leaves `State == Connected` over a dead pump.
- [x] Give `ProtocolSession.Dispose` a defined transport story. It cancels the pump
  but never disconnects and cannot await, so a real socket stays open until
  finalization and the server-side session lingers until its own timeout. Choose
  between a fire-and-forget disconnect and a documented "stop before disposing"
  contract.
- [x] Decide what `StopAsync` from `Faulted` means. It disconnects a second time,
  reaches `Disconnected`, and lets `StartAsync` succeed again, so restart-after-fault
  exists today undesigned and untested.
- [x] Make `ProtocolSession` safe for the second thread a real socket introduces.
  `pendingRequests` is a plain `Dictionary` and `State` an ordinary auto-property,
  both written from the pump's stack and from the `CancelAfter` timer's thread-pool
  thread, where a concurrent resize can misroute a response to subscribers; a caller
  awaiting `RequestAsync` also resumes off the main thread after a timeout. Check the
  deadline on the pump or hop explicitly.
- [x] Close `Dispatch` sitting outside the receive pump's `try`. `State` can read
  `Connected` for a pump that a `Dispatch`-internal exception already killed, and each
  callee is individually responsible for not throwing, so a future branch inherits no
  protection. Either wrap the `Dispatch(message)` call alone in a `try` that publishes
  a fault and continues, or document the invariant on `Dispatch`.
- [x] Answer a requester whose response payload fails to decode. The decode-failure
  branch runs before the pending-request check, so a truncated response is dropped
  with its fault on the fault channel while the requester stalls its whole timeout —
  a hardcoded 10 s for `ProbeRoundTripAsync`.
- [x] Rename `ProtocolSession.DefaultRequestTimeout` to what it actually is, the
  round-trip probe's deadline. `RequestAsync` has no overload that defaults a timeout,
  the constant is unreachable from `IProtocolSession`, and no test pins it, so raising
  it for a slow login would silently give every latency probe the longer deadline.
- [x] Add a transport double whose `SendAsync` can park, and pin the two residuals
  that need one: the identity-checked gate removal in `RequestAsync`'s `finally`, and
  a synchronous `try`/`catch` heartbeat reply, which loses a `Pong` when the send
  faults after returning. Cover `FakeTransport.FailNextSend` itself while there — it
  has no fake-level test at all, so its null guard and its one-shot semantics are both
  unpinned; a second `Bodyless(Ping)` in the third heartbeat test, asserting one `Pong`
  and faults still at 1, closes that in one line.
- [x] Give a single-flight gate rejection and a stale echo distinguishable exception
  types. Both throw `InvalidOperationException` (`ProtocolSession.cs:165` and `:224`),
  so a probe loop firing every 5 s against a 10 s deadline cannot tell "a request is
  already in flight" from a genuine correlation mismatch without matching on the
  message text.
- [ ] Re-verify `Dispatch`'s completion-safety comment on any UniTask upgrade, and do
  not treat it as a general guarantee. It quotes the private `RunTask` body of 2.5.11
  verbatim, so a version bump can invalidate the argument silently with the suite
  green, and the argument is narrower than it reads: `AttachExternalCancellation`
  returns no wrapper when the task is already completed, and a throwing caller
  continuation is contained but never reported, because `TrySetException` on an
  already-completed core returns `false`.
- [x] Publish the root-cause receive failure before the disconnect failure in
  `FaultTheStreamAsync` (`ProtocolSession.cs:458-471`). A consumer that reads the first
  `TransportFailure` — the natural thing to do — currently gets the symptom rather than
  the cause.
- [x] Resolve the contradiction over `SessionFault.MessageId` for `TransportFailure`:
  pick one meaning and make the design and the code agree. The design calls the field
  "meaningless for `TransportFailure`" while the heartbeat path populates it with
  `MessageId.Pong` (`ProtocolSession.cs:399-402`) and the stream fault passes `default`
  (`:466`, `:471`). `Kind` is identical on all three, so that field is the only thing
  separating "the heartbeat write failed, the connection is probably still usable" from
  "the stream desynchronized", and a maintainer trusting the design would delete it.
- [x] Settle one assertion convention for `SessionFault` contents and apply it across
  the session. `Kind` is the only field asserted on the faults the session generates
  itself, the unknown-message-id fault's `MessageId` excepted
  (`ProtocolSessionLifecycleTests.cs:65`), so the heartbeat fault's `MessageId` and
  `Diagnostic` are unpinned, and with them the correlation-mismatch diagnostic's
  operand order — the only direction information in that message.
- [ ] Decide whether one `NoDestination` fault per undelivered message is the right
  volume. It replaced a silent drop, which is strictly better, but every event that
  arrives before its UI subscriber now publishes one; the first Phase 2 view will
  show whether that reads as signal or noise.
- [x] Give the session stack a production wiring. Two claims this item used to carry
  were already stale by the time Task 8 read it, and they are recorded rather than
  quietly deleted. It said "there is no production `IClock` at all. Both
  implementations live in `Echo.Harness.TestKit`" — that stopped being true when
  `SystemClock` and `StopwatchElapsedTime` landed in
  `Echo.Harness.Infrastructure/SystemTime.cs`, whose asmdef carries
  `"defineConstraints": []` and therefore ships in a player. And it said "nothing
  constructs `MainThreadSessionScheduler`" — `HarnessComposition.Configure` now
  registers it as `ISessionScheduler` alongside `SystemClock`, `StopwatchElapsedTime`,
  `TcpTransportOptions`, `TcpTransport` and `ProtocolSession`, the last two as
  singletons, plus the resolved `EndpointResolution`. **Registering is not resolving**,
  and that half is now closed too, which is what finished this item: `HarnessLifetimeScope`
  in the committed `Assets/Scenes/Bootstrap.unity` calls `Configure` and registers
  `HarnessSessionDriver` as an entry point, so entering play mode resolves the graph,
  starts the session when an endpoint is configured, and stops it on both measured
  shutdown paths — see the note on
  `ProtocolSession.SwitchToSessionContextForTeardownAsync` for what a driver owes the
  session on the way out, and `HarnessSessionDriverTests` for the six properties that
  hold it in place. One end-to-end test now resolves that same graph and drives it
  against the real server; it skips wherever no endpoint is configured, CI included.
- [x] Decide what a caller cancelling one receive should mean. Decided as a contract
  rather than changed as behaviour, and written on `ITransport.ReceiveAsync` where a
  caller will meet it: **cancelling a receive means abandoning the link.** The socket
  close is the only thing that unparks a blocked read on this runtime, so a caller must
  not cancel a receive to pause reading, to apply backpressure, or to impose a
  per-message deadline — all three destroy the transport as a side effect. The session
  honours it because every token that can cancel a receive comes from teardown; what
  there is no path for is cancelling a receive while meaning to keep using the link, and
  the constraint is recorded rather than left for whoever first wants one to rediscover.
- [x] Handle the scheduler's real failure mode, which is a stall rather than a throw.
  Closed by a shutdown latch on `MainThreadSessionScheduler`: once `IsLatched` the hop
  is refused *before* `SwitchToMainThread` is reached, so the caller gets a cancellation
  on its first await instead of a continuation queued onto a loop that has stopped. The
  latch is armed per instance by `LatchForShutdown` and process-wide by
  `Application.quitting`, `beforeAssemblyReload` and `ExitingPlayMode`, and cleared again
  on entry to play mode and to edit mode; the two editor paths were measured rather than
  assumed, and the installers are split between a runtime and an editor hook because a
  domain reload during play does not re-run the runtime one. What is **not** closed is
  the arming in production: nothing outside the tests calls `LatchForShutdown`, so the
  instance latch is exercised only by them — the three process-wide signals are
  self-installed and do reach it in a real editor.
- [ ] Diagnose `MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread`,
  which is intermittently red on both runner paths — once in five batch runs at
  `1d8fc21`, and about one in ten on the connected path. This item exists because the
  failure was previously recorded only as a paragraph in `docs/verification-matrix.md`
  and so was not tracked as open work anywhere. **It is not known whether this is a defect in the
  main-thread hop or an artifact of the test apparatus**, and a spike that ruled out
  the two obvious mechanisms did not identify a third; the surviving lead, and the
  reason a separate probe cannot chase it, are written up in that document under "What
  a spike at `1d8fc21` ruled out, and what it did not". Not a blocker for Phase 2:
  nothing in production subscribes to `SubscribeToFaults` yet, so the hop's failure
  mode has no reader either way.
- [x] Revisit the request-timeout hop's non-cancellation failure path. The hop is still
  swallowed — nothing there may outrank the failure being reported to the caller, and two
  tests hold that half in place — but it is no longer silent: a teardown hop that fails
  for a reason other than cancellation now publishes a `SessionFault` naming the
  exception, so the fact that the `finally` un-registered the gate entry off-context
  leaves a trace. A cancelled hop stays unreported deliberately, because the only thing
  that can cancel it is the scheduler's own shutdown latch and every ordinary quit would
  otherwise publish a fault. **Who reads it is a separate question and the honest answer
  today is nobody** — no production type subscribes to `SubscribeToFaults`, so the
  publish reaches an empty handler list until a fault sink exists. That sink is unbuilt
  and is the real remainder here.
- [x] Guard `SendAsync` against a caller with no `SynchronizationContext`. Closed by
  passing `useCurrentSynchronizationContext: false` at every `Task`-to-`UniTask` boundary
  in `TcpTransport`: the write gate's `WaitAsync`, the stream's `FlushAsync`, and — found
  while pinning it — `ConnectAsync`, which had the same defect one step earlier and would
  have refused the connect before a send could be attempted at all. `WriteAsync` returns
  a `ValueTask`, whose `AsUniTask` has no such overload and needs none: it is a bare
  `await`, which captures the current context rather than demanding a `TaskScheduler`
  from it, so a null context resumes on the pool instead of throwing. Two tests pin it,
  both driving a real socket from a
  thread-pool thread with the premise (`SynchronizationContext.Current` is null)
  asserted rather than assumed.
- [ ] Add disposable-server golden integration tests. Superseded in part: the end-to-end
  tier now runs against a remote server — configured through the endpoint asset or
  `ECHO_SERVER_HOST` — rather than a disposable local one, so what remains here is
  whatever a disposable server would cover that a shared remote one cannot.
- [ ] Define app/session/scene VContainer lifetime scopes. **Partly satisfied, and
  deliberately left open rather than ticked.** The app root scope exists —
  `HarnessLifetimeScope` on `Assets/Scenes/Bootstrap.unity`, which calls
  `HarnessComposition.Configure` and registers the session driver as its entry point.
  Session and scene scopes are deferred to Phase 2: with one scene and no login flow, a
  child scope with one child and a lifetime identical to its parent is ceremony, and a
  login flow is what would give a session scope something to mean. The deferral is not
  free and the cost is recorded where it bites: `CompositionSmokeTests` pins the session
  as a `Singleton` and says in its own comment that the moment a child scope exists,
  `Scoped` would give every scope its own `ProtocolSession` over its own `TcpTransport`.
  Whoever adds the second scope must decide that, not inherit it.
- [ ] Implement Addressables catalog environments, build profiles, release
  ownership, CDN credentials, rollback, and cache budgets.
- [ ] Select, audit, pin, and import xLua only if hot-update requirements justify it.
- [ ] Sign Lua manifests and enforce client/server/build compatibility, sandbox,
  rollback, AOT/IL2CPP preservation, and Android 16 KiB page-size checks.

## Phase 2 — vertical slice

- [ ] Implement typed login DTOs and one login use case.
- [ ] Build one UI Toolkit view/view-model pair without infrastructure access.
- [ ] Implement queue and match-found flow.
- [ ] Start a local authoritative Go room and render one player-specific state.
- [ ] Prove cancellation from view → session → transport.
- [ ] Capture memory, allocation, startup, and package-size baselines.

## Phase 3 — battle parity

- [x] Migrate every message as a typed contract before consuming it.
- [ ] Preserve server authority and hidden-information boundaries. The contract layer
  now encodes the two structural rules — `CardView` points stay `int?` and
  `OpponentView` exposes no hand — but no use case consumes them yet.
- [ ] Resolve all documented Godot/Go JSON drift explicitly. The three JSON naming
  rows in `docs/protocol-contract.md` are resolved in favor of the Go names; the
  disconnect lifecycle result is still undefined.
- [ ] Verify all 18 server-defined characters; do not copy the old count.
- [ ] Add deterministic replay fixtures for phase, card, damage, defense,
  liberation, revival, surrender, timeout, and game-over paths.
- [ ] Add reconnect/resume, duplicate-command, and out-of-order event tests.

## Release gates

- [ ] Zero Unity compile errors and warnings.
- [ ] Static, EditMode, PlayMode, integration, performance, and Go baselines green.
- [ ] Addressables and Lua artifacts are signed, traceable, and reversible.
- [ ] IL2CPP builds verified on every target platform.
- [ ] Observability, crash reporting, privacy, localization, accessibility, and
  incident rollback runbooks approved.
- [ ] A staged migration comparison shows parity with the verified Godot build.
