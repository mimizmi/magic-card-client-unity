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
- [ ] Protect `main` with required architecture and Unity-test checks.

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
  singletons, plus the resolved `EndpointResolution`. What remains is not a
  registration and keeps its own item below: **registering is not resolving.** Every
  registration is lazy, and with no `LifetimeScope` and no scene in the repository,
  `Configure` has no caller outside the EditMode smoke test. Nothing starts a session
  yet, and nothing stops one — see the shutdown note on
  `ProtocolSession.SwitchToSessionContextForTeardownAsync` for what a driver owes the
  session on the way out.
- [ ] Decide what a caller cancelling one receive should mean. `TcpTransport.ReceiveAsync`
  closes the link on **any** cancellation of its token, not only on the idle deadline —
  the socket close is what unparks a read this runtime cannot otherwise interrupt. A
  consequence is that cancelling the token handed to `ProtocolSession.StartAsync`
  destroys the transport as a side effect of asking the session to stop reading. That is
  fine for shutdown and wrong for anything finer; whoever wires the pump needs a
  separate mechanism rather than a tweak to `AbandonTheLink`.
- [ ] Handle the scheduler's real failure mode, which is a stall rather than a throw.
  `UniTask.SwitchToMainThread` queues its continuation on the player loop without
  consulting the token, so when the loop stops — application quit, domain reload, or
  leaving play mode — a pending hop never resumes and never throws. `ProtocolSession`
  treats a failing hop as a fault it can publish; it has no answer for one that simply
  never returns, and `Dispose`/`CancelPump` cannot free it.
- [ ] Revisit the request-timeout hop's non-cancellation failure path. Carried from the
  session-layer iteration and not closed there: if the teardown hop fails for a reason
  other than cancellation, the `finally` still un-registers the gate entry while running
  off-context, and no `SessionFault` is published. The exception the caller receives is
  now correct; the bookkeeping is still unreported.
- [ ] Guard `SendAsync` against a caller with no `SynchronizationContext`.
  `Task.AsUniTask()` calls `TaskScheduler.FromCurrentSynchronizationContext()` eagerly,
  so a send issued from a `Task.Run` or a thread-pool callback throws
  `InvalidOperationException` before the write gate is even taken. Masked today only
  because the Unity main thread always has a context.
- [ ] Add disposable-server golden integration tests. Superseded in part: the end-to-end
  tier now runs against a remote server via `ECHO_SERVER_HOST` rather than a disposable
  local one, so what remains here is whatever a disposable server would cover that a
  shared remote one cannot.
- [ ] Define app/session/scene VContainer lifetime scopes.
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
