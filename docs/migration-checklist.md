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

- [ ] Introduce protocol version/capability negotiation with the Go server.
- [ ] Implement a cancellable TCP transport with partial-read framing,
  backpressure, reconnect policy, and structured telemetry.
- [ ] Serialize writes in the real transport, and make `DisconnectAsync` idempotent.
  `ITransport.SendAsync` now documents the concurrency requirement: the session
  answers a heartbeat from the receive pump, so a caller's send and a `Pong` can be
  in flight together, and a length prefix interleaved with another body
  desynchronizes the stream fatally.
- [ ] Wrap `ProtocolSession.StopAsync` in `try`/`finally`. `FailPendingRequests`
  currently sits after the `DisconnectAsync` await, so a throwing disconnect — or an
  already-cancelled token handed to `StopAsync`, a realistic shutdown pattern —
  strands every waiter and leaves `State == Connected` over a dead pump.
- [ ] Give `ProtocolSession.Dispose` a defined transport story. It cancels the pump
  but never disconnects and cannot await, so a real socket stays open until
  finalization and the server-side session lingers until its own timeout. Choose
  between a fire-and-forget disconnect and a documented "stop before disposing"
  contract.
- [ ] Decide what `StopAsync` from `Faulted` means. It disconnects a second time,
  reaches `Disconnected`, and lets `StartAsync` succeed again, so restart-after-fault
  exists today undesigned and untested.
- [ ] Make `ProtocolSession` safe for the second thread a real socket introduces.
  `pendingRequests` is a plain `Dictionary` and `State` an ordinary auto-property,
  both written from the pump's stack and from the `CancelAfter` timer's thread-pool
  thread, where a concurrent resize can misroute a response to subscribers; a caller
  awaiting `RequestAsync` also resumes off the main thread after a timeout. Check the
  deadline on the pump or hop explicitly.
- [ ] Close `Dispatch` sitting outside the receive pump's `try`. `State` can read
  `Connected` for a pump that a `Dispatch`-internal exception already killed, and each
  callee is individually responsible for not throwing, so a future branch inherits no
  protection. Either wrap the `Dispatch(message)` call alone in a `try` that publishes
  a fault and continues, or document the invariant on `Dispatch`.
- [ ] Answer a requester whose response payload fails to decode. The decode-failure
  branch runs before the pending-request check, so a truncated response is dropped
  with its fault on the fault channel while the requester stalls its whole timeout —
  a hardcoded 10 s for `ProbeRoundTripAsync`.
- [ ] Rename `ProtocolSession.DefaultRequestTimeout` to what it actually is, the
  round-trip probe's deadline. `RequestAsync` has no overload that defaults a timeout,
  the constant is unreachable from `IProtocolSession`, and no test pins it, so raising
  it for a slow login would silently give every latency probe the longer deadline.
- [ ] Add a transport double whose `SendAsync` can park, and pin the two residuals
  that need one: the identity-checked gate removal in `RequestAsync`'s `finally`, and
  a synchronous `try`/`catch` heartbeat reply, which loses a `Pong` when the send
  faults after returning. Cover `FakeTransport.FailNextSend` itself while there — it
  has no fake-level test at all, so its null guard and its one-shot semantics are both
  unpinned; a second `Bodyless(Ping)` in the third heartbeat test, asserting one `Pong`
  and faults still at 1, closes that in one line.
- [ ] Give a single-flight gate rejection and a stale echo distinguishable exception
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
- [ ] Publish the root-cause receive failure before the disconnect failure in
  `FaultTheStreamAsync` (`ProtocolSession.cs:458-471`). A consumer that reads the first
  `TransportFailure` — the natural thing to do — currently gets the symptom rather than
  the cause.
- [ ] Resolve the contradiction over `SessionFault.MessageId` for `TransportFailure`:
  pick one meaning and make the design and the code agree. The design calls the field
  "meaningless for `TransportFailure`" while the heartbeat path populates it with
  `MessageId.Pong` (`ProtocolSession.cs:399-402`) and the stream fault passes `default`
  (`:466`, `:471`). `Kind` is identical on all three, so that field is the only thing
  separating "the heartbeat write failed, the connection is probably still usable" from
  "the stream desynchronized", and a maintainer trusting the design would delete it.
- [ ] Settle one assertion convention for `SessionFault` contents and apply it across
  the session. `Kind` is the only field asserted on the faults the session generates
  itself, the unknown-message-id fault's `MessageId` excepted
  (`ProtocolSessionLifecycleTests.cs:65`), so the heartbeat fault's `MessageId` and
  `Diagnostic` are unpinned, and with them the correlation-mismatch diagnostic's
  operand order — the only direction information in that message.
- [ ] Add disposable-server golden integration tests.
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
