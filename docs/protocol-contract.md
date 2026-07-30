# Protocol Contract Baseline

## Authority and scope

The Go server under `E:\code\_github\magic-card-server-golang` remains authoritative.
This document and `Packages/com.echo.harness/Fixtures/protocol.contract.json` are a
migration snapshot, not a replacement server schema. The Harness implements framing
and typed DTO contracts only; it does not open a socket or execute game messages.

`protocol.contract.json` is **generated**, not hand-written. `Tools/protocol` parses
the Go package `internal/protocol` with `go/ast` and emits the fixture; the
architecture gate regenerates it and compares bytes, so the fixture cannot silently
drift from the server. Hand-editing it will fail that gate.

## Wire frame

```text
[4-byte JSON payload length][2-byte message ID][UTF-8 JSON payload]
```

| Property | Baseline |
|---|---|
| Byte order | big-endian |
| Length prefix | unsigned use is forbidden; decoder validates a signed non-negative `int32` |
| Message ID | `uint16` |
| Length meaning | JSON payload bytes only; excludes the two-byte message ID |
| Maximum payload | 1,048,576 bytes |
| Body encoding | UTF-8 JSON |

`BinaryFrameCodec` is a contract probe. A production transport must additionally own
stream buffering, partial reads, timeouts, cancellation, reconnect state, telemetry,
and backpressure.

## Message families

The fixture contains 39 unique message IDs:

| Range | Responsibility | Count |
|---:|---|---:|
| `1`–`4` | ping/pong and client latency probe | 4 |
| `1001`–`1002` | login | 2 |
| `2001`–`2007` | queue, character selection, game creation/start | 7 |
| `3001`–`3002` | authoritative state and phase | 2 |
| `4001`–`4011` | client battle commands | 11 |
| `5001`–`5013` | server battle events | 13 |

The typed `MessageId` enum and fixture ID set must remain identical. Any change
requires a coordinated server/client review and a protocol fixture update.

## Payload shapes

Every message carries one of three payload shapes. The shape decides whether a typed
DTO exists, and `ProtocolDtoContractTests` asserts that mapping in both directions —
a missing DTO and a surplus DTO both fail.

| Shape | Count | Meaning | DTO in `ProtocolMessageMap.PayloadTypes` |
|---|---:|---|---|
| `struct` | 31 | a Go struct describes the body | yes, with one property per JSON tag |
| `empty` | 4 | a Go struct exists but declares no fields | yes, serializing to `{}` |
| `none` | 4 | no body at all | no, deliberately |

The four `none` messages are `1 Ping`, `2 Pong`, `2003 LeaveQueueRequest`, and
`4011 RokkaActivateRequest`. Ping and Pong are sent with a nil payload; the other two
have no Go struct because their handlers ignore the body. Their absence from the
registry is asserted, not accidental.

Five nested view types back the `3001 GameStateEvent` tree and the card commands:
`CardRef`, `CardView`, `OpponentView`, `PendingAttackView`, and `PlayerView`. Two
rules there are load-bearing for hidden information and are covered by
`ProtocolDtoSerializationTests`:

- `CardView.points` and `raw_points` are `*int` in Go and `int?` in C#. A `null`
  means the server is withholding the value from this viewer. Collapsing it to `0`
  would hand the client a number the server deliberately hid.
- `OpponentView` has no `hand` field, only `hand_count`. The omission is the
  contract; adding a hand property would invite consumers to expect data that the
  server never sends.

## Session layer

`ProtocolSession` (`Runtime/Application/Session/`) owns one receive pump over
`ITransport` and routes each decoded message to exactly one destination, in this
order:

1. decode failure — publish a `SessionFault`, drop the message, stay connected
2. `1 Ping` — reply `2 Pong`, do not dispatch
3. a request is awaiting this id — complete it, do not dispatch
4. otherwise — deliver to `Subscribe<T>` handlers

Because the protocol carries no correlation identifier, `RequestAsync` waits for
the next message of the paired id from `ProtocolMessageMap.ResponseFor`, and a
second in-flight request for the same response id throws rather than queueing.

That order is designed but not currently enforced by the code that expresses it, and
no test distinguishes the alternatives. `1 Ping` is absent from `ResponseFor`, so
step 3 can never match it, and it is payload-shape `none`, so `Subscribe<T>` refuses
it and step 4 can never match either. Deleting the `return` in the Ping branch, or
moving that branch below the pending-request check, is therefore unobservable today.
Each has its own trigger. The `return` becomes load-bearing the moment that
no-payload guard loosens; the branch's position becomes load-bearing only if `Ping`
were added to `ResponseFor` as a response value, which is a different change and
would not follow from loosening the guard.

A receive failure is treated as stream desynchronization: the session moves to
`Faulted` and disconnects. Pending requests are failed on all three paths that end
the pump — a stream fault, `StopAsync`, and `Dispose` — because once the pump is
gone nothing can ever answer a waiter, and leaving one pending would make it wait
out its full timeout and then report a network failure that never happened.
Per-message errors never disconnect, because a server adding a message is normal
version drift.

## Known migration drift

| Event | Go JSON contract | Legacy Godot expectation | Migration rule |
|---|---|---|---|
| Damage `5001` | `attacker_seat`, `defender_seat`, `raw_damage`, `final_damage`, `hp_after`, `detail` | `seat`, `amount`, `damage_type` | use the Go names |
| Liberation `5003` | `player_seat`, `character`, `desc` | `seat` | use `player_seat` |
| Field effect `5004` | `effect_id`, `effect_name`, `desc` | `field_effect` | use the Go fields |
| Disconnect | session lifecycle notification | handler expects a reason although signal declares none | define one typed lifecycle result before implementation |

The current server data set also contains 18 character records while legacy client
documentation mentions 16. Server data wins; a migrated client must not hard-code
that count.

## Change procedure

1. Change or confirm the Go type and JSON tag first. The Go repository is
   authoritative; never adjust the Unity side to paper over a server difference.
2. Regenerate the fixture from that source:

   ```powershell
   cd Tools\protocol
   go run . -source E:\code\_github\magic-card-server-golang\internal\protocol `
            -out ..\..\Packages\com.echo.harness\Fixtures\protocol.contract.json
   ```

   Review the resulting diff. It is the machine-readable statement of what changed
   on the wire.
3. If message ids were added or removed, extend the `MessageId` enum and the
   generator's hand-maintained `csharpNames` table in `Tools/protocol/fixture.go`.
   `FixtureNames_MatchTheMessageIdEnum` fails when the two disagree.
4. Add or update the typed DTO under `Runtime/Contracts/Dtos/` and register it in
   `ProtocolMessageMap`. A Go pointer or slice must map to a nullable C# type; a Go
   `omitempty` on a value type maps to `DefaultValueHandling.Ignore`, because
   `NullValueHandling.Ignore` is a no-op for a type that can never be null.
5. Run `Tools/ci/verify.ps1`. The architecture gate re-runs the generator with
   `-check` and fails on any byte difference, so a stale fixture cannot be committed.
6. Review hidden-information impact: each client must receive only its permitted
   player-specific view.
7. Version the protocol before introducing an incompatible production change.

Regenerating requires the Go repository on disk. CI does not check it out, so the
drift gate skips with a warning there and is enforced locally — see
`docs/verification-matrix.md`.

Protocol negotiation, generated schemas, reconnect/resume semantics, and golden
server-process integration tests are intentionally future work.
