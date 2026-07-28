# Protocol Contract Typing — Design

## Status

Accepted 2026-07-29. First migration iteration on top of the Echo Unity Harness.

## Goal

Give all 39 server message IDs a typed C# contract, and make Go-side JSON drift
mechanically detectable instead of relying on human review.

## Why this first

`docs/migration-checklist.md` Phase 3 opens with "migrate every message as a typed
contract before consuming it". Every Phase 2 and Phase 3 item depends on it.

The Harness currently ships a 39-value `MessageId` enum but only **three** DTOs
(`DamageEventDto`, `LiberationEventDto`, `FieldEffectEventDto`) — the three drift
cases someone found by hand. The remaining 36 messages have no typed contract, and
nothing in the repository compares the fixture against the authoritative Go source.
Scaling hand-maintenance to 39 messages would scale the existing weakness with it.

## Authority

`E:\code\_github\magic-card-server-golang\internal\protocol` is authoritative:

- `msgid.go` — 39 ID constants (verified identical to the Harness `MessageId` enum)
- `messages.go` — request/response/event payload structs
- `view.go` — `GameStateView` and its nested view types

## Contract facts established from the Go source

These drive the design and must survive into tests:

| Fact | Consequence |
|---|---|
| `session.Send(MsgIDPing, nil)` — Ping/Pong carry a **nil** payload, not JSON | zero-length payload must be a legal frame |
| `GameConfigReq`, `TriggerLibrateReq`, `EndActionReq`, `SurrenderReq` are empty structs | serialize to `{}`, distinct from nil |
| `MsgLeaveQueueReq` (2003) and `MsgRokkaActivateReq` (4011) have **no Go struct** | handlers ignore the payload entirely |
| `CardView.Points *int` / `RawPoints *int` — nil means "points hidden" | must map to `int?`; `null` and `0` must never collapse |
| `ExtraInfo`, `PublicExtra`, `GameConfigEv.Characters/Fields` are `map[string]any` | no fixed schema exists server-side |
| Suit values are Unicode `♥ ♦ ♣ ♠` | UTF-8 round-trip must be asserted |
| Many fields carry `omitempty` | affects serialization assertions |

The `Points` nullability is the correctness core of this iteration: it implements
server-side information hiding. Mapping it to a non-nullable `int` would silently
turn "hidden" into "zero" and become a cheating vector.

## Approach

Three options were considered:

- **A — hand-written DTOs and a hand-maintained fixture.** Drift is only detected
  between C# and the fixture. Nothing checks the fixture against Go. Rejected: it
  reproduces the current weakness at 13x the surface area.
- **B — a Go AST extractor generates the fixture, and a gate byte-compares the
  regenerated output against the committed file.** Selected.
- **C — golden payload capture from a live Go server process.** Most faithful, but
  covering all 39 messages requires driving cold paths (game-over, blessing, Suou
  revival), and process orchestration is Phase 1's "disposable-server golden
  integration tests". Deferred, not rejected — it complements B rather than
  replacing it.

Approach B produces a three-stage closed loop:

```text
Go source --(generated, byte-compared by gate)--> fixture --(asserted by tests)--> C# DTOs
```

Any Go JSON tag change fails the next `verify.ps1` run. This is exactly the order
`CONTRIBUTING.md` rule 2 mandates: update contract fixtures and tests before client
consumers.

## Components

### 1. Extractor — `Tools/protocol/`

A standalone Go module parsing the three authoritative files with `go/parser` and
`go/ast`.

Derivation rules:

- **id** — from the `msgid.go` const block
- **direction** — from the `S→C` / `C→S` arrow in each constant's line comment
  (all 39 constants carry one)
- **kind** — from the `Req` / `Resp` / `Ev` suffix; `MsgPing` / `MsgPong` have no
  suffix and map to `system`
- **go_type** — `Msg` prefix stripped from the constant name
- **fields** — per struct field: JSON tag name, Go type, pointer implies `nullable`,
  `omitempty` presence

Four exceptions live in an explicit table in the extractor source:

| Constant | Handling |
|---|---|
| `MsgGameStateEv` | payload type is `GameStateView`, not `GameStateEv` |
| `MsgLeaveQueueReq` | no struct — `payload.shape = "none"` |
| `MsgRokkaActivateReq` | no struct — `payload.shape = "none"` |
| `MsgPing` / `MsgPong` | nil payload — `payload.shape = "none"` |

The C#-facing name (`DamageEvent`) cannot be derived from Go, so the extractor also
holds a 39-row id to C# name table. This is hand-maintained, but it is **one table
rather than 39 classes**, and an EditMode test cross-asserts it against the
`MessageId` enum.

Modes: `-out <path>` writes the fixture; `-check <path>` regenerates, compares, and
exits non-zero on any difference.

Output is deterministic: messages sorted by ascending id, fields in Go declaration
order, stable indentation.

The extractor deliberately does **not** record the Go repository commit hash.
Recording it would produce diff noise and false gate failures whenever the Go repo
advances without a protocol change.

### 2. Fixture format

Extends the existing document. `version` stays `legacy-v1`; `frame` is unchanged.

```json
{ "id": 5001, "name": "DamageEvent", "go_type": "DamageEv",
  "direction": "server_to_client", "kind": "event",
  "payload": { "shape": "struct", "fields": [
    { "json_name": "attacker_seat", "go_type": "int",
      "nullable": false, "omitempty": false }
  ]}}
```

`payload.shape` is three-state:

- `struct` — a Go struct with fields
- `empty` — an empty Go struct, serializing to `{}`
- `none` — no payload at all (Ping, Pong, LeaveQueue, RokkaActivate)

Nested view types (`PlayerView`, `OpponentView`, `CardView`, `PendingAttackView`,
`CardRef`) are emitted once into a top-level `types` dictionary and referenced by
name, rather than expanded at each use site. Each `types` entry has the same
`{ "fields": [...] }` shape as a struct payload.

A field whose Go type is one of those named types carries a `type_ref` alongside
its `go_type`, and `repeated` marks slice fields:

```json
{ "json_name": "hand", "go_type": "[]CardView",
  "type_ref": "CardView", "repeated": true,
  "nullable": false, "omitempty": false }
```

`type_ref` and `repeated` are omitted for scalar fields. Resolution is one level of
indirection only — `types` entries may themselves contain `type_ref` fields, and
tests walk the graph rather than assuming a flat structure.

Every existing architecture-gate assertion (`version`, `frame.*`, exactly 39
messages, no duplicate ids) continues to hold.

### 3. C# DTO layer

All DTOs go into the existing `Echo.Harness.Contracts` assembly. **No new assembly
is introduced** — `verify-architecture.ps1` hard-codes the runtime assembly count
and each assembly's reference set, so adding one would force that gate to change.
Keeping the change inside one assembly keeps this iteration's blast radius small.

Layout: `Runtime/Contracts/Dtos/` split by family into `SystemDtos.cs`,
`AuthDtos.cs`, `MatchmakingDtos.cs`, `StateDtos.cs` (the `GameStateView` tree),
`CommandDtos.cs`, `EventDtos.cs`.

The three existing DTOs move into their matching family file with **class names
unchanged**, so `ProtocolContractTests` keeps passing.

Type mapping:

| Go | C# |
|---|---|
| `int`, `int64`, `bool`, `string` | `int`, `long`, `bool`, `string` |
| `*int` | `int?` |
| `map[string]any` | `JObject` (opaque passthrough — no invented schema) |
| `[]CardView` | `IReadOnlyList<CardViewDto>` |
| `omitempty` | `NullValueHandling.Ignore` on the property |

No custom `JsonConverter` is written; Newtonsoft `[JsonProperty]` attributes carry
the wire names.

A `ProtocolMessageMap` exposes `MessageId` to DTO `Type` so tests can enumerate all
39 messages rather than asserting them one at a time.

### 4. Verification

- `verify-architecture.ps1` gains one step: `go run ./Tools/protocol -check`,
  throwing on mismatch.
- New EditMode suite `ProtocolDtoContractTests`, driven by `ProtocolMessageMap`:
  - every message's serialized property-name set equals its fixture field set
  - every fixture field marked `nullable` is a nullable or reference type in C#
  - `payload.shape = "empty"` DTOs serialize to `{}`
  - `payload.shape = "none"` messages have no DTO registered
  - the id to C# name table matches the `MessageId` enum
- New frame tests: zero-length payload encode/decode (currently uncovered), and a
  UTF-8 round-trip over the `♥ ♦ ♣ ♠` suit values.

Fast feedback uses the connected Unity MCP editor instance (`run_tests`); the final
gate remains `.\Tools\ci\verify.ps1`.

## Testing strategy

Deterministic EditMode only. No live server, no socket, no catalog, no Lua, no
wall-clock sleeps — consistent with `CONTRIBUTING.md` rule 5.

## Out of scope

Real TCP transport, reconnect and resume, protocol version negotiation, use cases,
UI, Addressables, Lua, and an inner schema for `GameConfigEv`'s character and field
blobs. The Go server's character and field data is dynamic by design; hard-coding a
schema for it would create a false constraint.

## Risks

- **The id to C# name table is hand-maintained.** Mitigated by cross-asserting it
  against the `MessageId` enum; a missing or renamed entry fails the suite.
- **Byte-exact fixture comparison is brittle to formatting.** Mitigated by fixing
  deterministic ordering and indentation in the extractor and generating the
  committed fixture with that same code path.
- **The extractor depends on comment conventions** (`S→C` arrows) that Go's compiler
  does not enforce. Mitigated by failing extraction loudly when a constant has no
  parseable direction, rather than defaulting.

## Notes on available context sources

The sibling repositories carry `.understand-anything` knowledge graphs. The Godot
graph is complete (96 files, 153 nodes) and is useful for auditing legacy client
field assumptions beyond the three documented drift cases. The Go graph is marked
`"partial": true` — only 14 files, with three analysis phases having failed — so it
is treated as a navigation aid only. The Go source itself remains the sole
authority for extraction.
