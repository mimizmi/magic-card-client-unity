# TCP Transport and Session Concurrency — Design

The session layer landed driven end to end by deterministic fakes. This iteration
gives it a real socket, and fixes the correctness holes that a real socket opens.

Predecessor: `2026-07-29-protocol-session-layer-design.md`.

## Problem

`ProtocolSession` is correct only under an assumption it cannot check: that one
pump, on one thread, is the only thing touching its state. A socket breaks that
assumption in four places at once.

- `pendingRequests` is a plain `Dictionary` and `State` an ordinary auto-property.
  Both are written from the pump and from the `CancelAfter` timer's thread-pool
  thread. A concurrent resize can misroute a response to subscribers.
- Nothing serializes writes. The session answers a heartbeat from inside the pump,
  so a caller's send and a `Pong` can be in flight together.
- `StopAsync` calls `FailPendingRequests` after the `DisconnectAsync` await, so a
  throwing disconnect strands every waiter.
- `Dispose` cancels the pump but never disconnects, leaving a real socket open.

Separately, the client encodes none of the server's implicit contract. Three of
the server's rules are enforced by silent disconnection, which on the wire is
indistinguishable from a pulled cable.

## Server facts this design is built on

Read from the authoritative Go server, which this iteration does not modify.

| Fact | Source |
| --- | --- |
| Frame is `[4B body length BE][2B msgID BE][body]`, header 6 bytes | `internal/network/codec.go:9`, `:12-16`, `:48-54` |
| Body over `1 << 20` bytes is a read error; server closes | `internal/network/codec.go:31-34` |
| Server sends `Ping` every 15 s, closes if no `Pong` within 35 s | `internal/network/session.go:19`, `:23`, `:216`, `:223` |
| `Ping` carries a `nil` payload, so its body length is 0 | `internal/network/session.go:231` |
| Rate limit is 30 msg/s; exceeding it closes the connection with no error frame | `internal/network/session.go:27`, `:170`, `:188-191` |
| `Pong` is handled before the limiter and is never counted | `internal/network/session.go:180-185` |
| Writes are serialized through one `writeLoop` goroutine because `net.Conn.Write` is not concurrency-safe | `internal/network/session.go:81-85`, `:199-212` |
| Unknown message ids are logged and dropped, without disconnecting | `internal/network/router.go:31-35` |
| A panicking handler is recovered and sends no reply | `internal/network/router.go:37-42` |
| `ClientPingRequest` is echoed back verbatim as `ClientPingResponse` | `main.go:65-67` |
| There is no handshake: `Accept` is followed directly by `sess.run()` | `internal/network/server.go:63-69` |
| `LISTEN_ADDR` and `LOG_LEVEL` are honoured | `internal/config/config.go`, `main.go:28`, `:80` |
| `RATE_LIMIT`, `PingInterval`, and `PongTimeout` are loaded, logged, and never consumed — the three network numbers are compile-time constants | `main.go:30` vs `internal/network/session.go:19`, `:23`, `:27` |
| The server reads `./data/characters.json` and `./data/fields.json` relative to its working directory | `main.go:33-43` |

Two consequences carry into the plan. A test cannot relax the rate limit, so the
budget guard must target the hard-coded 30. A test can choose the port, so an
end-to-end fixture will not collide with a developer's running server.

## Placement

| Unit | Assembly | Why there |
| --- | --- | --- |
| `TcpTransport` | `Echo.Harness.Infrastructure` | Owns `System.Net.Sockets`. Application must not. |
| `MainThreadSessionScheduler` | `Echo.Harness.Infrastructure` | The Unity main thread is a platform concept; the architecture gate bans `UnityEngine` in Application (`Tools/ci/verify-architecture.ps1:130`). |
| `ISessionScheduler` | `Echo.Harness.Application` | The port the session depends on. Pure UniTask, which Application already permits. |
| `ProtocolSession` changes | `Echo.Harness.Application` | Existing file. |
| `LoopbackProtocolServer` | `Echo.Harness.TestKit` | A test double that speaks real bytes. |

No new assembly, and no change to the dependency direction
`Infrastructure -> Application -> Contracts`.

## Components

### ISessionScheduler

```csharp
public interface ISessionScheduler
{
    UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken);
}
```

One method, one purpose: move the current continuation onto the session's
context. `MainThreadSessionScheduler` implements it with UniTask's main-thread
switch. The TestKit implementation completes synchronously, which is what keeps
the existing EditMode suite deterministic — a real main-thread hop would make
those tests depend on the player loop running, which in EditMode it does not.

This is the only new abstraction in the iteration. It exists because the
alternative — locks inside the session — makes subscriber callbacks arrive on an
arbitrary thread and turns "hop before you touch the UI" into an unwritten rule.
The previous iteration produced a documented example of how those rules fare.

### TcpTransport

Implements the existing `ITransport` with no interface change.

- **Receive.** Read the 6-byte header to completion, looping until full because a
  TCP read returns what has arrived, not what was asked for. Validate the declared
  length against `WireFrameSpec.MaxPayloadBytes`. Read the body to completion.
  `BinaryFrameCodec` already decodes a whole frame and already carries the
  matching 1 MiB bound; the streaming reader is the half it does not have.
- **Send.** One `SemaphoreSlim(1, 1)` around the whole write. `BinaryFrameCodec.Encode`
  produces the header and body as a single buffer, so one `WriteAsync` puts a whole
  frame on the wire — the same reason the server merges them in `EncodeFrame`.
- **Send budget.** A token bucket sized to the server's 30 msg/s. Exceeding it
  throws at the call site. `MessageId.Pong` is exempt, because the server excludes
  `Pong` from its own limiter before the limiter runs. A budget guard that blocked
  a `Pong` would cause the disconnect it exists to prevent, and the symptom would
  surface 35 seconds later as a heartbeat timeout.
- **Read-idle watchdog.** A healthy server sends a `Ping` at least every 15 s, so
  silence is itself a signal. The default threshold is 45 s, three missed pings.
  Exceeding it fails `ReceiveAsync`, which the session already grades as fatal.
- **`DisconnectAsync` is idempotent.**

The budget is checked inside the send gate, not before it. Tokens must correspond
to bytes actually placed on the wire in wire order; checking outside lets two
callers both pass and then acquire the gate in the opposite order. The gate is
held for the duration of one buffered write, so the cost of the stricter ordering
is negligible.

### ProtocolSession changes

The session gains no lock. It gains one hop and a set of lifecycle repairs.

## Data flow

### Receive

```
NetworkStream (thread pool)
  TcpTransport.ReceiveAsync
    read 6-byte header to completion
    validate declared length in [0, 1 MiB]
    read body to completion
  ProtocolSession.RunPumpAsync
    await scheduler.SwitchToSessionContextAsync()   <-- the only hop
    Dispatch                                        <-- provably single-threaded
      decode failure   -> fault, drop, keep connection
      Ping             -> reply Pong
      pending request  -> complete it
      typed subscribers-> invoke
      no destination   -> fault, drop
```

`Dispatch` and everything it calls run only on the session context. That is not a
convention; it is the line above it, and a test asserts it by recording the thread
the scheduler fake observes.

The `Dispatch` call itself moves inside the pump's `try`, so an exception thrown
below it publishes a fault and continues rather than killing the pump while
`State` still reads `Connected`.

### Send

```
caller (any thread)
  ProtocolSession.SendAsync
    encode
  TcpTransport.SendAsync
    await sendGate.WaitAsync()
    try { budget check; single WriteAsync of the whole frame }
    finally { release }
```

### Request

`RequestAsync` keeps its single-flight gate and its "next message of the paired
id" correlation, both forced by the protocol's lack of a correlation identifier.
Two changes:

- The timeout resumes on the session context, so a caller no longer returns from
  `RequestAsync` on a thread-pool thread.
- A decode failure whose message id is a pending response id fails that waiter
  with the decode error instead of dropping it and letting the caller stall its
  whole timeout.

## Failure policy

The graded policy from the previous iteration, extended.

| Failure | Grade | Action |
| --- | --- | --- |
| Body fails to decode | One message | Fault, drop, keep connection |
| Unknown message id | One message | Fault, drop |
| Subscriber throws | One message | Fault, drop |
| No destination | One message | Fault, drop — **a change**: today `DeliverToSubscribers` returns silently when no handler is registered (`ProtocolSession.cs:415-418`), so a message arriving before its subscriber is lost without a trace. The server's reconnect path sends `LoginResponse` and `MatchFoundEvent` back to back, which makes that reachable. |
| Declared frame length out of range | Stream desynchronized | `Faulted`, disconnect, fail all pending |
| EOF mid-frame | Stream desynchronized | As above |
| Read-idle threshold exceeded | Link is dead | As above |
| Write fails | Stream desynchronized | As above |
| Send budget exceeded | Caller defect | Throw to the caller; keep the connection; do not fault |

The last row is the only failure that is not the link's fault. Faulting the
session for it would escalate one caller's loop bug into a global disconnect.

### Lifecycle

- `StopAsync` wraps its body in `try`/`finally` with `FailPendingRequests` in the
  `finally`, so a throwing `DisconnectAsync` or an already-cancelled token cannot
  strand waiters.
- `Dispose` performs a bounded fire-and-forget disconnect. Leaving the socket open
  makes the server hold the session until its 35 s pong timeout.
- `StopAsync` from `Faulted` is defined: it reaches `Disconnected` without a second
  disconnect attempt, and `StartAsync` afterwards is permitted. This is the seam
  reconnect will use, so it is specified and tested now rather than left to exist
  undesigned.
- `SessionFault.MessageId` means "the message this fault concerns, or `default`
  when no single message does". The design text and the code agree on that one
  meaning, replacing the contradiction where the design called the field
  meaningless for `TransportFailure` while the heartbeat path populated it.
- `FaultTheStreamAsync` publishes the root-cause receive failure before the
  disconnect failure, so a consumer reading the first `TransportFailure` gets the
  cause rather than the symptom.
- The single-flight gate rejection and a stale round-trip echo get distinct
  exception types, so a probe loop can tell them apart without matching message
  text.
- `DefaultRequestTimeout` is renamed to name what it is: the round-trip probe's
  deadline. It is not a default for `RequestAsync`, which has no such overload.

### The borrowed guarantee, and how these two changes make it local

`ProtocolSession.cs:354-373` documents that `completion.TrySetResult` is safe only
by borrowing: a requester's continuation resumes inside `AttachExternalCancellation`'s
own `try`, so nothing it throws can reach the pump. The comment warns in as many
words that dropping `AttachExternalCancellation` takes the guarantee with it.

This iteration touches exactly that mechanism, since the timeout must resume on
the session context. Two rules follow, and they are the reason the two changes are
specified together rather than separately:

- Changing the timeout mechanism must either keep `AttachExternalCancellation` or
  re-establish the guarantee explicitly. It may not silently inherit it.
- Moving the `Dispatch` call inside the pump's `try` **makes the guarantee local**.
  A throwing continuation is then caught by the pump itself, published as a fault,
  and the pump survives. After that change the reasoning no longer has to be
  borrowed from a third-party implementation detail, and the comment can be
  narrowed to describe what the code now enforces on its own.

The hop itself happens *before* `Dispatch`, so UniTask's inline-continuation
semantics inside `Dispatch` are unchanged. That part of the comment stays correct
and stays version-pinned to 2.5.11, and must still be re-verified on any upgrade.

## TestKit changes

`LoopbackProtocolServer`: a `TcpListener` on `127.0.0.1` that speaks the real
frame format and exposes byte-level control — deliver a frame in arbitrary
fragments, coalesce frames into one segment, emit a declared length that exceeds
the bound, and close mid-body.

`FakeTransport` keeps its existing role for the deterministic tier, and gains the
test coverage of `FailNextSend` that it has never had.

## Testing

### Tier 1 — deterministic, no sockets

The existing 97 EditMode tests keep their timing, because the TestKit scheduler
completes synchronously rather than hopping to a player loop that EditMode does
not run. Two groups of them do change, and deliberately: assertions naming
`DefaultRequestTimeout`, and the `InvalidOperationException` assertions that
become the two new distinct types. New tests cover:

- Thread confinement: the scheduler fake records the observed thread; `Dispatch`
  sees the session's thread every time. This test is the only evidence for the
  confinement decision.
- `StopAsync` still fails all waiters when `DisconnectAsync` throws.
- `Dispose` requests a disconnect.
- `StopAsync` from `Faulted` reaches `Disconnected`, and `StartAsync` then succeeds.
- Gate rejection and stale echo throw distinguishable types.
- A decode failure on a pending response id fails that waiter.
- `FakeTransport.FailNextSend` null guard and one-shot semantics.

### Tier 2 — loopback socket, EditMode

| Case | Construction |
| --- | --- |
| Fragmented frame | Header one byte at a time, body in two chunks |
| Coalesced frames | Two frames in one segment |
| Oversize declared length | Header declaring 2 MiB |
| EOF mid-body | Half a frame, then close |
| Write serialization | N concurrent sends plus a pump-issued `Pong` |
| Read-idle | Connect and send nothing |
| Send budget | 31 sends within a second; then 100 `Pong`s |

The write-serialization case is judged by the loopback server's own frame reader
failing, not by an assertion we write: interleaved frames make the length prefix
disagree with the following bytes. That is the same mechanism the Go server would
hit, so the test reproduces the production failure rather than modelling it.

### Tier 3 — one real end-to-end against the Go server

Launched with `LISTEN_ADDR=127.0.0.1:<preselected free port>` and a working
directory set to the server root, because the server loads its data files
relative to it.

1. Connect, `LoginRequest`, decode `LoginResponse`.
2. `ClientPingRequest`, echoed `ts`, `ProbeRoundTripAsync` returns a positive
   `TimeSpan`.
3. Receive the server's first `Ping`, reply `Pong`, connection survives.

Case 3 costs roughly 15-20 seconds, because the ping interval is a compile-time
constant that no environment variable can shorten. It is the only test that can
show the client answers heartbeats on a real link, and failing to do so costs the
connection 35 seconds later. It is the suite's one slow case.

Absent Go toolchain or server checkout: warn and skip, matching the guard already
in `verify-architecture.ps1`.

### Socket hygiene

A leaked `TcpListener` or background thread stalls the Unity editor at domain
reload. `TcpTransport` and `LoopbackProtocolServer` are both strictly disposable,
and every socket test tears its listener down.

## Verification

`Tools/ci/verify.ps1` gains the tiers above through the existing Unity test run.
The architecture gate needs no relaxation: its source-text bans cover `Domain` and
`Application` only, and `System.Net.Sockets` enters in `Infrastructure`. That is
confirmed by mutation rather than assumed.

## Checklist items this closes

From `docs/migration-checklist.md`, Phase 1:

Closed: write serialization and idempotent disconnect; `StopAsync` `try`/`finally`;
the `Dispose` transport story; `StopAsync` from `Faulted`; session thread safety;
`Dispatch` outside the pump's `try`; answering a requester whose response fails to
decode; the `DefaultRequestTimeout` rename; the parking transport double and
`FailNextSend` coverage; distinguishable exception types; root-cause ordering in
`FaultTheStreamAsync`; the `SessionFault.MessageId` contradiction; the fault
assertion convention.

Partially closed: the TCP transport item — framing, cancellation, and backpressure
land here; reconnect policy and structured telemetry do not. Disposable-server
golden integration tests — one end-to-end lands here, not a golden suite.

Not closed, and blocked: protocol version and capability negotiation. The server
has no handshake, so this requires changing it. It moves to a later
protocol-evolution iteration, where the read-only constraint on the Go repository
is lifted and both sides change together.

## Out of scope

- Reconnect, resume, and session restoration.
- Structured telemetry and metrics.
- Any change to the Go server.
- A correlation identifier and a version handshake. Both are agreed as the right
  next protocol changes, and both are deferred: adding them here would change the
  wire format in the same iteration that introduces the first real socket, leaving
  two variables to attribute a failure to. A working socket is also what makes a
  protocol change verifiable.
- Protobuf or any schema-driven codec. Its payload-size and parse-speed benefits
  are irrelevant at 1-3 msg/s; its real benefit is replacing `Tools/protocol` with
  generated code, which is worth revisiting only when DTO drift maintenance
  becomes the constraint.
- UI, use cases, and gameplay. `HarnessPolicy.ContainsGameplayImplementation`
  stays `false`.

## One honest limitation

Main-thread confinement is verified by a fake that records threads, and by
loopback tests that exercise a real socket. Neither proves the absence of a race
under production scheduling; they prove that every path this design routes through
the scheduler does in fact go through it. A path added later that skips the hop
would not be caught by these tests. The hop is therefore placed at one site in the
pump rather than spread across call sites, so that "did it hop?" stays a question
with one place to look.
