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
- [x] Deterministic transport/content/Lua/time fakes.
- [x] EditMode, PlayMode, static, local aggregate, and CI entry points.
- [ ] Assign real CODEOWNERS teams in `.github/CODEOWNERS`.
- [ ] Protect `main` with required architecture and Unity-test checks.

## Phase 1 — production infrastructure

- [ ] Introduce protocol version/capability negotiation with the Go server.
- [ ] Implement a cancellable TCP transport with partial-read framing,
  backpressure, reconnect policy, and structured telemetry.
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
