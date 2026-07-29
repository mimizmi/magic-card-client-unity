# Protocol Session Layer — Design

Date: 2026-07-29
Status: approved for planning
Predecessor: `docs/superpowers/specs/2026-07-29-protocol-contract-typing-design.md`

## Problem

The previous iteration produced typed DTOs for all 39 messages plus the five nested
view types, generated from and gated against the authoritative Go source. Nothing
consumes them. `ProtocolMessageMap` exists, but no code turns a `TransportMessage`
into a DTO or a DTO into bytes, and no code decides what happens to a message once
it arrives.

That gap blocks both remaining paths. A real TCP transport would hand up raw bytes
with nothing to interpret them; a login use case would have to invent its own
serialization and its own wait-for-reply logic. Whichever came first would set an
ad-hoc precedent the other had to live with.

This iteration builds the layer between them: a codec that binds message id, DTO,
and bytes, and a session that pumps, routes, and correlates.

## Protocol constraints that shape the design

These were established by reading the generated fixture, not assumed.

**There is no correlation identifier anywhere in the protocol.** The frame is
`[4-byte length][2-byte message id][UTF-8 JSON]` with no sequence number, and none of
the 39 payloads carries a request id, sequence, or trace field. The only
correlation-shaped names in the whole contract are business identifiers:
`player_id`, `game_id`, `character_id`, `effect_id`, `reconnect_token`,
`ai_char_id`, `player_char_id`, `second_char_id`.

**There are exactly three responses**, by the fixture's own `kind` field:

| Request | Response | Response fields |
|---|---|---|
| `3 ClientPingRequest` | `4 ClientPingResponse` | `ts` |
| `1001 LoginRequest` | `1002 LoginResponse` | `success`, `player_id`, `reconnect_token`, `in_game`, `config_hash`, `error` |
| `2001 JoinQueueRequest` | `2002 JoinQueueResponse` | `success`, `error` |

Every other client command is one-way; the server answers with a `3001` state
snapshot or a `5001`–`5013` event.

The consequence is that request/response correlation can only mean *wait for the
next message of the expected id*. True multiplexing is impossible without a server
change, and the Go repository is authoritative and read-only.

`4 ClientPingResponse.ts` echoes the request's `ts`. That is the single genuinely
correlatable field in the protocol, and the round-trip probe uses it.

## Placement

| Component | Path | Assembly |
|---|---|---|
| `ProtocolCodec`, `ProtocolDecodeResult`, `ProtocolDecodeFailure` | `Runtime/Contracts/` | `Echo.Harness.Contracts` |
| `ProtocolMessageMap.ResponseFor` | `Runtime/Contracts/ProtocolMessageMap.cs` | `Echo.Harness.Contracts` |
| `IProtocolSession`, `ProtocolSession`, `SessionState`, `SessionFault` | `Runtime/Application/Session/` | `Echo.Harness.Application` |
| `FakeTransport` changes | `TestKit/` | `Echo.Harness.TestKit` |

No new assembly and no new assembly reference. `verify-architecture.ps1` pins both
the runtime assembly count and each assembly's exact reference set, so avoiding both
keeps the gate untouched.

The codec belongs in `Contracts` because it needs only Newtonsoft and the existing
`BinaryFrameCodec` and `ProtocolMessageMap`; that assembly already declares
`Newtonsoft.Json.dll` in `precompiledReferences` and sets `noEngineReferences: true`.
The session belongs in `Application` because it orchestrates `ITransport`, which is
defined there. The reverse placement would make `Application` depend on
`Infrastructure` and invert the dependency direction the gate enforces.

Note for implementation: the gate greps `Application` sources for
`\b(UnityEngine|Addressables|R3|VContainer|XLua)\b`. None of this design needs them,
but a comment mentioning one of those words verbatim would fail the build.

## Components

### ProtocolCodec

Static and stateless.

```csharp
public enum ProtocolDecodeFailure { None, UnknownMessageId, MalformedPayload }

public readonly struct ProtocolDecodeResult
{
    public MessageId MessageId { get; }
    public object Payload { get; }        // null for messages with no payload
    public ProtocolDecodeFailure Failure { get; }
    public string Diagnostic { get; }
    public bool Succeeded => Failure == ProtocolDecodeFailure.None;
}

public static class ProtocolCodec
{
    public static byte[] EncodePayload(object payload);              // null -> empty array
    public static ProtocolDecodeResult Decode(MessageId id, byte[] payload);
}
```

Decoding rules:

- An id absent from `MessageId` yields `UnknownMessageId`.
- An id whose fixture payload shape is `none` yields `Payload == null` **without
  inspecting the body**. This leniency is deliberate: those four ids include
  `1 Ping`, and dropping a Ping means failing to answer with Pong, which makes the
  server treat the connection as dead. Strictness there costs a disconnect and buys
  nothing.
- Anything else is deserialized into `ProtocolMessageMap.PayloadTypes[id]`. A
  Newtonsoft failure yields `MalformedPayload` with the exception message as the
  diagnostic; the exception does not escape.

`object Payload` is unavoidable. The receive pump learns the id at runtime, so no
static type is available at that point. Type safety is restored at subscription
time instead.

### ProtocolMessageMap.ResponseFor

```csharp
public static IReadOnlyDictionary<MessageId, MessageId> ResponseFor { get; }
```

Three entries. A test drives the fixture's `kind` field and asserts that every
message of kind `response` appears exactly once as a value, so a server-side
addition cannot leave the table silently incomplete.

### IProtocolSession

```csharp
public enum SessionState { Disconnected, Connecting, Connected, Faulted }

public enum SessionFaultKind
{
    UnknownMessageId,
    MalformedPayload,
    CorrelationMismatch,
    SubscriberFailure,
    TransportFailure
}

public readonly struct SessionFault
{
    public SessionFaultKind Kind { get; }
    public MessageId MessageId { get; }        // meaningless for TransportFailure
    public string Diagnostic { get; }
}

public interface IProtocolSession : IDisposable
{
    SessionState State { get; }

    UniTask StartAsync(CancellationToken ct);
    UniTask StopAsync(CancellationToken ct);

    UniTask SendAsync(MessageId id, object payload, CancellationToken ct);
    UniTask<TResponse> RequestAsync<TResponse>(
        MessageId requestId, object payload, TimeSpan timeout, CancellationToken ct);
    UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken ct);

    IDisposable Subscribe<TPayload>(MessageId id, Action<TPayload> handler);
    IDisposable SubscribeToFaults(Action<SessionFault> handler);
}
```

`Subscribe<TPayload>` validates `ProtocolMessageMap.PayloadTypes[id] == typeof(TPayload)`
and throws on mismatch **at subscription time**. Deferring that check to dispatch
would surface it when the message first arrives, which for a damage event could be
ten minutes into a match. `SendAsync` applies the same runtime check to its payload,
because `MessageId` and payload type are independent arguments and nothing else
would catch a mismatched pair.

`RequestAsync` does not take a response id. It looks the pairing up in
`ResponseFor`, keeping the three pairs in one auditable place.

## Data flow

The receive pump is a single loop with a fixed order:

```
ReceiveAsync -> Decode
  1. decode failed?                  publish SessionFault, drop, continue
  2. id == Ping?                     send Pong, do not dispatch, continue
  3. a request is awaiting this id?  complete it, do not dispatch, continue
  4. otherwise                       dispatch to Subscribe<T> handlers
```

Step 3 means a response is never also delivered to subscribers; it belongs to its
requester. A message with no subscribers is not an error.

`ProbeRoundTripAsync` reads `IClock.UtcNow`, sends `ClientPingRequest{ts}`, awaits
`ClientPingResponse`, reads `UtcNow` again, and returns the difference.

It also verifies the echoed `ts` matches what it sent. On a mismatch it publishes a
`CorrelationMismatch` fault **and throws**, rather than returning a latency figure
derived from an unrelated reply — a wrong number that looks plausible is worse than
a failure, because it would silently feed whatever the caller does with it.

### State machine rules

- `StartAsync` transitions `Disconnected -> Connecting -> Connected` and starts the
  pump. Calling it in any state other than `Disconnected` throws.
- `SendAsync`, `RequestAsync`, and `ProbeRoundTripAsync` throw unless the state is
  `Connected`.
- `StopAsync` is valid from `Connected` or `Faulted` and is a no-op from
  `Disconnected`, so teardown paths do not have to branch on state.
- `Subscribe` and `SubscribeToFaults` are valid in any state except after
  `Dispose`, so callers can wire subscriptions before starting.

### Single-flight gate

At most one in-flight request per response id. A second `RequestAsync` awaiting the
same response id throws immediately rather than queueing. With no correlation
identifier, two concurrent requests would race for the same reply and one caller
would receive the other's answer — a defect that is nearly untraceable at runtime,
so it is made impossible at the call site instead.

## Failure policy

| Condition | Handling |
|---|---|
| Unknown message id | `SessionFault`, drop, connection survives |
| Malformed payload | `SessionFault`, drop, connection survives |
| Subscriber throws | caught, published as `SessionFault`, pump continues |
| `ReceiveAsync` throws (not cancellation) | treat the stream as desynchronized: set `Faulted`, disconnect, fail all pending requests |

Per-message errors and stream errors are graded differently on purpose. A server
adding a message is normal version drift and must not disconnect players. A broken
length prefix means the byte stream has lost its boundaries, and every subsequent
read returns garbage — disconnecting early is what makes that diagnosable.

Subscriber isolation matters as much: a null reference in one view model must not
take down the connection.

`ITransport` already absorbs frame parsing and returns a `TransportMessage`, so
frame-level validation (illegal length prefix, payloads above the 1 MiB cap) belongs
to the real TCP transport in Phase 1. At this layer it surfaces as row four.

## TestKit changes

`FakeTransport.ReceiveAsync` currently throws when its inbound queue is empty
(`TestKit/DeterministicFakes.cs:44-47`). A receive pump loops on that call, so it
would fail on the first idle iteration. Required changes:

- `ReceiveAsync` returns a pending `UniTask` when the queue is empty, completed by a
  later `EnqueueInbound`.
- Add `FailNextReceive(Exception)` so the stream-desynchronization path can be
  driven from a test.
Verified: `DeterministicFakesTests.FakeTransport_RecordsOutboundFramesAndDequeuesInboundFrames`
enqueues before receiving and never exercises the empty-queue throw, so the change is
backward compatible with the existing suite.

The awaited task must complete its continuation synchronously from `EnqueueInbound`.
That is what makes session tests deterministic: by the time `EnqueueInbound` returns,
the pump has already decoded and dispatched the message, so a test can assert
immediately without polling or sleeping.

## Testing

All EditMode, driven by `FakeTransport` and `ManualClock`. Coverage:

- codec round-trip for a representative payload, an empty-payload message, and a
  no-payload message
- unknown message id and malformed JSON each produce the right
  `ProtocolDecodeFailure` without throwing
- `ResponseFor` covers every fixture message of kind `response`
- heartbeat: an inbound Ping produces an outbound Pong and reaches no subscriber
- request/response completes with the typed response
- single-flight: a second concurrent request for the same response id throws
- a response does not reach subscribers of that id
- `Subscribe<T>` with a mismatched type throws at subscription time
- `SendAsync` with a mismatched payload type throws
- a throwing subscriber produces a `SessionFault` and the pump keeps running
- `ProbeRoundTripAsync` returns the exact interval the `ManualClock` advanced
- a mismatched echoed `ts` publishes `CorrelationMismatch` and throws
- `StartAsync` twice throws; `SendAsync` before `StartAsync` throws; `StopAsync`
  from `Disconnected` is a no-op
- a receive failure sets `Faulted`, disconnects, and fails pending requests
- disposal stops dispatch

### One honest limitation

The request timeout is implemented with a linked `CancellationTokenSource` and
`CancelAfter`, which uses real time. The cancellation path is tested deterministically
by cancelling a token manually; the timeout path is tested with a single 50 ms real
wait. Removing that dependency would require introducing a scheduler port, which is
not worth an extra abstraction for one assertion.

## Out of scope

Real TCP transport, reconnect and resume, UI or view models, and any gameplay logic.
`HarnessPolicy.ContainsGameplayImplementation` stays `false`; a protocol session is
plumbing, not gameplay.
