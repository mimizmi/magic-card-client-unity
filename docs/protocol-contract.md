# Protocol Contract Baseline

## Authority and scope

The Go server under `E:\code\_github\magic-card-server-golang` remains authoritative.
This document and `Packages/com.echo.harness/Fixtures/protocol.contract.json` are a
migration snapshot, not a replacement server schema. The Harness implements framing
and selected DTO contracts only; it does not open a socket or execute game messages.

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

1. Change or confirm the Go type and JSON tag first.
2. Add a golden JSON fixture and update `protocol.contract.json`.
3. Add/update a typed DTO and its EditMode contract test.
4. Run `Tools/ci/verify.ps1`.
5. Review hidden-information impact: each client must receive only its permitted
   player-specific view.
6. Version the protocol before introducing an incompatible production change.

Protocol negotiation, generated schemas, reconnect/resume semantics, and golden
server-process integration tests are intentionally future work.
