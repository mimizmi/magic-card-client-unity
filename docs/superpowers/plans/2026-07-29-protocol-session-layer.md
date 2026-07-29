# Protocol Session Layer Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build the codec and session layer that turns the 39 typed DTOs into a usable message pipeline over `ITransport`.

**Architecture:** A stateless `ProtocolCodec` in `Echo.Harness.Contracts` binds message id, DTO type, and bytes. A `ProtocolSession` in `Echo.Harness.Application` owns a single receive pump that decodes each message and routes it through a fixed order: decode failure, heartbeat, pending request, typed subscribers. No new assembly and no new assembly reference, so the architecture gate stays untouched.

**Tech Stack:** C# / Unity 6000.2.7f2, UniTask 2.5.11, Newtonsoft.Json 3.2.1, NUnit via Unity Test Framework, PowerShell verification scripts.

**Spec:** `docs/superpowers/specs/2026-07-29-protocol-session-layer-design.md`

## Global Constraints

- The Go repository at `E:\code\_github\magic-card-server-golang` is authoritative and **read-only**. Never edit it.
- No new assembly definition and no new entry in any asmdef `references` array. `Tools/ci/verify-architecture.ps1:88-119` pins the runtime assembly count at 6 and each assembly's exact reference set.
- `Echo.Harness.Application` sources must not match the regex `\b(UnityEngine|Addressables|R3|VContainer|XLua)\b` (`verify-architecture.ps1:130`). This includes comments — do not write those words anywhere in an Application source file.
- `Echo.Harness.Domain` sources must not match `\b(UnityEngine|Cysharp|R3|VContainer|XLua)\b`. This plan does not touch Domain.
- `HarnessPolicy.ContainsGameplayImplementation` stays `false`.
- Every `.cs` file under `Packages/` needs a `.meta` file. Unity generates them on import; commit them alongside the source.
- A Unity recompile costs roughly two minutes. Batch file creation before triggering `mcp__unity-editor-mcp__recompile`, then read `get_console_logs` for errors before running tests.
- Run the EditMode suite with `mcp__unity-editor-mcp__run_tests` (`mode: editor`, `async_tests: true`) and read the result with `mcp__unity-editor-mcp__test_status`. Do not infer a pass from a clean compile.

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `Packages/com.echo.harness/Runtime/Contracts/ProtocolCodec.cs` | id + bytes to DTO, with a typed failure result | 1 |
| `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs` (modify) | add the `ResponseFor` request-to-response table | 2 |
| `Packages/com.echo.harness/TestKit/DeterministicFakes.cs` (modify) | `FakeTransport` awaits instead of throwing; add `FailNextReceive` | 3 |
| `Packages/com.echo.harness/Runtime/Application/Session/SessionDiagnostics.cs` | `SessionState`, `SessionFaultKind`, `SessionFault` | 4 |
| `Packages/com.echo.harness/Runtime/Application/Session/IProtocolSession.cs` | the session contract | 4 |
| `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs` | lifecycle, pump, dispatch, correlation | 4-8 |
| `Packages/com.echo.harness/Tests/EditMode/ProtocolCodecTests.cs` | codec and pairing table | 1-2 |
| `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs` | state machine, pump, faults, transport failure | 4 |
| `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs` | subscribe, dispatch, isolation, send, heartbeat | 5, 7 |
| `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs` | request/response, single-flight, timeout, RTT probe | 6, 8 |

`ProtocolSession.cs` is built incrementally across tasks 4-8. Each task adds one capability with its own tests; the file stays focused because every member serves the one pump.

---

### Task 1: ProtocolCodec

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Contracts/ProtocolCodec.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolCodecTests.cs`

**Interfaces:**
- Consumes: `MessageId`, `ProtocolMessageMap.PayloadTypes` (existing).
- Produces: `ProtocolDecodeFailure` (enum: `None`, `UnknownMessageId`, `MalformedPayload`); `ProtocolDecodeResult` (readonly struct with `MessageId MessageId`, `object Payload`, `ProtocolDecodeFailure Failure`, `string Diagnostic`, `bool Succeeded`, and static factories `Ok(MessageId, object)` and `Failed(MessageId, ProtocolDecodeFailure, string)`); `ProtocolCodec.EncodePayload(object) -> byte[]` and `ProtocolCodec.Decode(MessageId, byte[]) -> ProtocolDecodeResult`.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/ProtocolCodecTests.cs`:

```csharp
using System;
using System.Text;
using Echo.Harness.Contracts;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolCodecTests
    {
        [Test]
        public void EncodePayload_ProducesTheGoJsonNames()
        {
            var bytes = ProtocolCodec.EncodePayload(
                new LoginRequestDto { PlayerName = "echo" });

            Assert.That(Encoding.UTF8.GetString(bytes), Is.EqualTo("{\"player_name\":\"echo\"}"));
        }

        [Test]
        public void EncodePayload_TreatsNullAsAnEmptyBody()
        {
            // Ping and Pong are sent as Send(id, nil) on the Go side, which puts
            // zero payload bytes on the wire - not the two bytes of "{}".
            Assert.That(ProtocolCodec.EncodePayload(null), Is.Empty);
        }

        [Test]
        public void Decode_RoundTripsAStructPayload()
        {
            var bytes = ProtocolCodec.EncodePayload(
                new LoginResponseDto { Success = true, PlayerId = "p-1" });

            var result = ProtocolCodec.Decode(MessageId.LoginResponse, bytes);

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.None));
            var payload = (LoginResponseDto)result.Payload;
            Assert.That(payload.Success, Is.True);
            Assert.That(payload.PlayerId, Is.EqualTo("p-1"));
        }

        [Test]
        public void Decode_HandlesAnEmptyStructPayload()
        {
            var result = ProtocolCodec.Decode(
                MessageId.EndActionRequest, Encoding.UTF8.GetBytes("{}"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Payload, Is.InstanceOf<EndActionRequestDto>());
        }

        [Test]
        public void Decode_ReturnsANullPayloadForMessagesThatCarryNone()
        {
            // Shape "none" messages are not inspected at all. Ping is one of them,
            // and refusing a Ping would mean never answering with Pong, which the
            // server reads as a dead connection.
            var result = ProtocolCodec.Decode(
                MessageId.Ping, Encoding.UTF8.GetBytes("unexpected junk"));

            Assert.That(result.Succeeded, Is.True);
            Assert.That(result.Payload, Is.Null);
        }

        [Test]
        public void Decode_ReportsAnUnknownMessageId()
        {
            var result = ProtocolCodec.Decode((MessageId)9999, Array.Empty<byte>());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.UnknownMessageId));
            Assert.That(result.Diagnostic, Does.Contain("9999"));
        }

        [Test]
        public void Decode_ReportsMalformedJsonWithoutThrowing()
        {
            var result = ProtocolCodec.Decode(
                MessageId.LoginResponse, Encoding.UTF8.GetBytes("{not json"));

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
            Assert.That(result.Diagnostic, Is.Not.Empty);
        }

        [Test]
        public void Decode_ReportsAnEmptyBodyForAMessageThatRequiresOne()
        {
            // Go always emits at least "{}" for a non-nil struct, so an empty body
            // on a registered type is a real anomaly. Returning null here instead
            // would hand a null to a subscriber whose handler promises a value.
            var result = ProtocolCodec.Decode(MessageId.LoginResponse, Array.Empty<byte>());

            Assert.That(result.Succeeded, Is.False);
            Assert.That(result.Failure, Is.EqualTo(ProtocolDecodeFailure.MalformedPayload));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Trigger `mcp__unity-editor-mcp__recompile`, then read `mcp__unity-editor-mcp__get_console_logs` with `severity: error`.
Expected: compile errors `CS0103: The name 'ProtocolCodec' does not exist` and `CS0246` for `ProtocolDecodeFailure`.

- [ ] **Step 3: Write the implementation**

Create `Packages/com.echo.harness/Runtime/Contracts/ProtocolCodec.cs`:

```csharp
using System;
using System.Text;
using Newtonsoft.Json;

namespace Echo.Harness.Contracts
{
    public enum ProtocolDecodeFailure
    {
        None,
        UnknownMessageId,
        MalformedPayload
    }

    /// <summary>
    /// The outcome of decoding one inbound message. Decoding never throws:
    /// a single bad message must not be able to tear down a live connection,
    /// so the failure is data the caller decides what to do with.
    /// </summary>
    public readonly struct ProtocolDecodeResult
    {
        private ProtocolDecodeResult(
            MessageId messageId,
            object payload,
            ProtocolDecodeFailure failure,
            string diagnostic)
        {
            MessageId = messageId;
            Payload = payload;
            Failure = failure;
            Diagnostic = diagnostic;
        }

        public MessageId MessageId { get; }

        /// <summary>The decoded DTO, or null for a message that carries no payload.</summary>
        public object Payload { get; }

        public ProtocolDecodeFailure Failure { get; }

        public string Diagnostic { get; }

        public bool Succeeded => Failure == ProtocolDecodeFailure.None;

        public static ProtocolDecodeResult Ok(MessageId messageId, object payload) =>
            new ProtocolDecodeResult(messageId, payload, ProtocolDecodeFailure.None, string.Empty);

        public static ProtocolDecodeResult Failed(
            MessageId messageId,
            ProtocolDecodeFailure failure,
            string diagnostic) =>
            new ProtocolDecodeResult(messageId, null, failure, diagnostic);
    }

    public static class ProtocolCodec
    {
        public static byte[] EncodePayload(object payload)
        {
            if (payload == null)
            {
                return Array.Empty<byte>();
            }

            return Encoding.UTF8.GetBytes(JsonConvert.SerializeObject(payload));
        }

        public static ProtocolDecodeResult Decode(MessageId messageId, byte[] payload)
        {
            if (!Enum.IsDefined(typeof(MessageId), messageId))
            {
                return ProtocolDecodeResult.Failed(
                    messageId,
                    ProtocolDecodeFailure.UnknownMessageId,
                    $"Message id {(ushort)messageId} is not part of the typed contract.");
            }

            if (!ProtocolMessageMap.PayloadTypes.TryGetValue(messageId, out var payloadType))
            {
                // Shape "none". The body is deliberately not inspected.
                return ProtocolDecodeResult.Ok(messageId, null);
            }

            var json = payload == null ? string.Empty : Encoding.UTF8.GetString(payload);
            try
            {
                var dto = JsonConvert.DeserializeObject(json, payloadType);
                if (dto == null)
                {
                    return ProtocolDecodeResult.Failed(
                        messageId,
                        ProtocolDecodeFailure.MalformedPayload,
                        $"{messageId} expects a {payloadType.Name} body but the payload was empty.");
                }

                return ProtocolDecodeResult.Ok(messageId, dto);
            }
            catch (JsonException exception)
            {
                return ProtocolDecodeResult.Failed(
                    messageId,
                    ProtocolDecodeFailure.MalformedPayload,
                    exception.Message);
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors in `get_console_logs`, then run `mcp__unity-editor-mcp__run_tests` with `mode: editor`, `async_tests: true`, and read `mcp__unity-editor-mcp__test_status`.
Expected: all 8 `ProtocolCodecTests` pass and the previously green suite stays green.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Contracts/ProtocolCodec.cs \
        Packages/com.echo.harness/Runtime/Contracts/ProtocolCodec.cs.meta \
        Packages/com.echo.harness/Tests/EditMode/ProtocolCodecTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolCodecTests.cs.meta
git commit -m "Add the protocol codec

Decoding returns a typed failure instead of throwing, because one bad
message must not be able to tear down a live connection."
```

---

### Task 2: Response pairing table

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolCodecTests.cs` (append)

**Interfaces:**
- Consumes: `MessageId`, `ProtocolContractFixture.Load()`, `ProtocolMessageDocument.Kind`.
- Produces: `ProtocolMessageMap.ResponseFor` of type `IReadOnlyDictionary<MessageId, MessageId>`.

- [ ] **Step 1: Write the failing tests**

Append to `ProtocolCodecTests.cs`, inside the same class:

```csharp
        [Test]
        public void ResponseFor_PairsEveryRequestThatHasAResponse()
        {
            Assert.That(
                ProtocolMessageMap.ResponseFor[MessageId.ClientPingRequest],
                Is.EqualTo(MessageId.ClientPingResponse));
            Assert.That(
                ProtocolMessageMap.ResponseFor[MessageId.LoginRequest],
                Is.EqualTo(MessageId.LoginResponse));
            Assert.That(
                ProtocolMessageMap.ResponseFor[MessageId.JoinQueueRequest],
                Is.EqualTo(MessageId.JoinQueueResponse));
        }

        [Test]
        public void ResponseFor_CoversEveryFixtureMessageOfKindResponse()
        {
            // Driven from the generated fixture so a server-side addition cannot
            // leave the hand-maintained table silently incomplete.
            var fixture = ProtocolContractFixture.Load();
            var responseIds = new System.Collections.Generic.List<MessageId>();
            foreach (var message in fixture.Messages)
            {
                if (message.Kind == "response")
                {
                    responseIds.Add((MessageId)message.Id);
                }
            }

            Assert.That(responseIds, Is.Not.Empty);
            Assert.That(
                ProtocolMessageMap.ResponseFor.Values,
                Is.EquivalentTo(responseIds),
                "Every fixture response must be paired exactly once in ResponseFor.");
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Recompile and read `get_console_logs`.
Expected: `CS0117: 'ProtocolMessageMap' does not contain a definition for 'ResponseFor'`.

- [ ] **Step 3: Write the implementation**

In `ProtocolMessageMap.cs`, add this member after the `NestedTypes` property, inside the class:

```csharp
        /// <summary>
        /// Maps a request to the response the server answers it with.
        ///
        /// The protocol carries no correlation identifier, so waiting for the
        /// next message of the paired id is the only correlation available.
        /// Keeping the three pairs in one table makes that assumption auditable
        /// rather than scattered through call sites.
        /// </summary>
        public static IReadOnlyDictionary<MessageId, MessageId> ResponseFor { get; } =
            new Dictionary<MessageId, MessageId>
            {
                { MessageId.ClientPingRequest, MessageId.ClientPingResponse },
                { MessageId.LoginRequest, MessageId.LoginResponse },
                { MessageId.JoinQueueRequest, MessageId.JoinQueueResponse },
            };
```

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors, run the EditMode suite, read `test_status`.
Expected: both new tests pass.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Contracts/ProtocolMessageMap.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolCodecTests.cs
git commit -m "Pair the three request/response messages

The table is asserted against the fixture's kind field so a server-side
addition cannot leave it silently incomplete."
```

---

### Task 3: Make FakeTransport drivable

**Files:**
- Modify: `Packages/com.echo.harness/TestKit/DeterministicFakes.cs:9-66`
- Test: `Packages/com.echo.harness/Tests/EditMode/DeterministicFakesTests.cs` (append)

**Interfaces:**
- Consumes: `ITransport`, `TransportMessage`, `TransportState`.
- Produces: `FakeTransport.EnqueueInbound(TransportMessage)` now completes a pending receive synchronously; `FakeTransport.FailNextReceive(Exception)`; `FakeTransport.Sent` unchanged.

**Why:** the current `ReceiveAsync` throws when its queue is empty (`DeterministicFakes.cs:44-47`). A receive pump loops on that call and would fail on its first idle iteration. Completing the continuation synchronously from `EnqueueInbound` is also what makes every later session test deterministic: when `EnqueueInbound` returns, the pump has already dispatched.

- [ ] **Step 1: Write the failing tests**

First add this using to the top of `DeterministicFakesTests.cs`. The file does not
have it today, and both `Preserve()` and `Status.IsCompleted()` are UniTask
extension methods that will not resolve without it:

```csharp
using Cysharp.Threading.Tasks;
```

Then append to `DeterministicFakesTests.cs`, inside the class:

```csharp
        [Test]
        public void FakeTransport_ReceiveAwaitsAnEmptyQueueInsteadOfThrowing()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();

            var pending = transport.ReceiveAsync(default).Preserve();
            Assert.That(pending.Status.IsCompleted(), Is.False);

            transport.EnqueueInbound(
                new TransportMessage(MessageId.Pong, System.Array.Empty<byte>()));

            Assert.That(pending.Status.IsCompleted(), Is.True);
            Assert.That(pending.GetAwaiter().GetResult().MessageId, Is.EqualTo(MessageId.Pong));
        }

        [Test]
        public void FakeTransport_FailNextReceiveSurfacesTheInjectedException()
        {
            var transport = new FakeTransport();
            transport.ConnectAsync(default).GetAwaiter().GetResult();
            transport.FailNextReceive(new System.IO.IOException("stream desynchronized"));

            var error = Assert.Throws<System.IO.IOException>(
                () => transport.ReceiveAsync(default).GetAwaiter().GetResult());

            Assert.That(error.Message, Is.EqualTo("stream desynchronized"));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Recompile and read `get_console_logs`.
Expected: `CS1061: 'FakeTransport' does not contain a definition for 'FailNextReceive'`.

- [ ] **Step 3: Write the implementation**

Replace the `FakeTransport` class body (`DeterministicFakes.cs:9-66`) with:

```csharp
    public sealed class FakeTransport : ITransport
    {
        private readonly Queue<TransportMessage> inbound = new Queue<TransportMessage>();
        private readonly List<TransportMessage> sent = new List<TransportMessage>();
        private UniTaskCompletionSource<TransportMessage> pendingReceive;
        private Exception nextReceiveFailure;

        public TransportState State { get; private set; } = TransportState.Disconnected;

        public IReadOnlyList<TransportMessage> Sent => sent;

        /// <summary>
        /// Queues an inbound message. When a receive is already awaiting, its
        /// continuation runs synchronously from this call, so a test can assert
        /// on the effects of the message as soon as this method returns.
        /// </summary>
        public void EnqueueInbound(TransportMessage message)
        {
            var waiter = pendingReceive;
            if (waiter != null)
            {
                pendingReceive = null;
                waiter.TrySetResult(message);
                return;
            }

            inbound.Enqueue(message);
        }

        /// <summary>Makes the next receive fail, standing in for a desynchronized stream.</summary>
        public void FailNextReceive(Exception failure)
        {
            if (failure == null)
            {
                throw new ArgumentNullException(nameof(failure));
            }

            var waiter = pendingReceive;
            if (waiter != null)
            {
                pendingReceive = null;
                waiter.TrySetException(failure);
                return;
            }

            nextReceiveFailure = failure;
        }

        public UniTask ConnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Connected;
            return UniTask.CompletedTask;
        }

        public UniTask SendAsync(
            TransportMessage message,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();
            sent.Add(message);
            return UniTask.CompletedTask;
        }

        public UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            EnsureConnected();

            if (nextReceiveFailure != null)
            {
                var failure = nextReceiveFailure;
                nextReceiveFailure = null;
                return UniTask.FromException<TransportMessage>(failure);
            }

            if (inbound.Count > 0)
            {
                return UniTask.FromResult(inbound.Dequeue());
            }

            if (pendingReceive != null)
            {
                throw new InvalidOperationException(
                    "Only one receive may await this transport at a time.");
            }

            pendingReceive = new UniTaskCompletionSource<TransportMessage>();
            return pendingReceive.Task;
        }

        public UniTask DisconnectAsync(CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            State = TransportState.Disconnected;

            var waiter = pendingReceive;
            if (waiter != null)
            {
                pendingReceive = null;
                waiter.TrySetCanceled(cancellationToken);
            }

            return UniTask.CompletedTask;
        }

        private void EnsureConnected()
        {
            if (State != TransportState.Connected)
            {
                throw new InvalidOperationException("Fake transport is not connected.");
            }
        }
    }
```

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors, run the EditMode suite, read `test_status`.
Expected: the two new tests pass and `FakeTransport_RecordsOutboundFramesAndDequeuesInboundFrames` still passes — it enqueues before receiving, so it never took the empty-queue path.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/TestKit/DeterministicFakes.cs \
        Packages/com.echo.harness/Tests/EditMode/DeterministicFakesTests.cs
git commit -m "Make FakeTransport drivable by a receive pump

An empty queue now awaits rather than throwing, and the continuation runs
synchronously from EnqueueInbound so session tests need no polling."
```

---

### Task 4: Session lifecycle and receive pump

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Application/Session/SessionDiagnostics.cs`
- Create: `Packages/com.echo.harness/Runtime/Application/Session/IProtocolSession.cs`
- Create: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs`

**Interfaces:**
- Consumes: `ITransport`, `TransportMessage`, `IClock`, `ProtocolCodec`, `MessageId`.
- Produces: `SessionState` (enum: `Disconnected`, `Connecting`, `Connected`, `Faulted`); `SessionFaultKind` (enum: `UnknownMessageId`, `MalformedPayload`, `CorrelationMismatch`, `SubscriberFailure`, `TransportFailure`); `SessionFault` (readonly struct with `Kind`, `MessageId`, `Diagnostic`); `IProtocolSession` with `State`, `StartAsync`, `StopAsync`, `SendAsync`, `RequestAsync`, `ProbeRoundTripAsync`, `Subscribe<TPayload>`, `SubscribeToFaults`, `Dispose`; `ProtocolSession(ITransport, IClock)`.

This task implements only `State`, `StartAsync`, `StopAsync`, `SubscribeToFaults`, `Dispose`, and the pump's decode-and-fault path. Tasks 5-8 fill in the rest of the interface; declare the remaining members now and have them throw `NotImplementedException` so the file compiles.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.IO;
using System.Text;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionLifecycleTests
    {
        private static ProtocolSession NewSession(FakeTransport transport) =>
            new ProtocolSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

        private static TransportMessage Frame(MessageId id, string json) =>
            new TransportMessage(id, Encoding.UTF8.GetBytes(json));

        [Test]
        public void StartAsync_ConnectsTheTransportAndReportsConnected()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);

            session.StartAsync(default).GetAwaiter().GetResult();

            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
            Assert.That(transport.State, Is.EqualTo(TransportState.Connected));
        }

        [Test]
        public void StartAsync_RejectsASecondStart()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            session.StartAsync(default).GetAwaiter().GetResult();

            Assert.Throws<InvalidOperationException>(
                () => session.StartAsync(default).GetAwaiter().GetResult());
        }

        [Test]
        public void StopAsync_FromDisconnectedIsANoOp()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);

            Assert.DoesNotThrow(() => session.StopAsync(default).GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }

        [Test]
        public void Pump_PublishesAFaultForAnUnknownMessageId()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));

            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.UnknownMessageId));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected),
                "An unknown id is version drift, not a reason to drop the player.");
        }

        [Test]
        public void Pump_PublishesAFaultForAMalformedPayloadAndKeepsRunning()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{not json"));
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{not json either"));

            Assert.That(faults, Has.Count.EqualTo(2));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.MalformedPayload));
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
        }

        [Test]
        public void Pump_TreatsAReceiveFailureAsStreamDesynchronization()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            transport.FailNextReceive(new IOException("length prefix out of range"));

            Assert.That(session.State, Is.EqualTo(SessionState.Faulted));
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.TransportFailure));
        }

        [Test]
        public void FaultSubscription_StopsDeliveringAfterDisposal()
        {
            var transport = new FakeTransport();
            using var session = NewSession(transport);
            var faults = new List<SessionFault>();
            var subscription = session.SubscribeToFaults(faults.Add);
            session.StartAsync(default).GetAwaiter().GetResult();

            subscription.Dispose();
            transport.EnqueueInbound(Frame((MessageId)9999, "{}"));

            Assert.That(faults, Is.Empty);
        }
    }
}
```

Note: `FailNextReceive` completes the pump's already-pending receive with an exception, so the fault and the state transition happen synchronously inside that call.

- [ ] **Step 2: Run the tests to verify they fail**

Recompile and read `get_console_logs`.
Expected: `CS0246` for `ProtocolSession`, `SessionState`, and `SessionFault`.

- [ ] **Step 3: Write the implementation**

Create `Packages/com.echo.harness/Runtime/Application/Session/SessionDiagnostics.cs`:

```csharp
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    public enum SessionState
    {
        Disconnected,
        Connecting,
        Connected,
        Faulted
    }

    public enum SessionFaultKind
    {
        UnknownMessageId,
        MalformedPayload,
        CorrelationMismatch,
        SubscriberFailure,
        TransportFailure
    }

    /// <summary>
    /// A recoverable problem the session decided not to raise as an exception,
    /// because the caller that would have caught it is not on the stack.
    /// </summary>
    public readonly struct SessionFault
    {
        public SessionFault(SessionFaultKind kind, MessageId messageId, string diagnostic)
        {
            Kind = kind;
            MessageId = messageId;
            Diagnostic = diagnostic;
        }

        public SessionFaultKind Kind { get; }

        /// <summary>Carries no meaning when <see cref="Kind"/> is TransportFailure.</summary>
        public MessageId MessageId { get; }

        public string Diagnostic { get; }
    }
}
```

Create `Packages/com.echo.harness/Runtime/Application/Session/IProtocolSession.cs`:

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// Owns one receive pump over an <see cref="ITransport"/> and routes each
    /// decoded message to exactly one destination.
    /// </summary>
    public interface IProtocolSession : IDisposable
    {
        SessionState State { get; }

        UniTask StartAsync(CancellationToken cancellationToken);

        UniTask StopAsync(CancellationToken cancellationToken);

        UniTask SendAsync(MessageId messageId, object payload, CancellationToken cancellationToken);

        UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken);

        UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken);

        IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler);

        IDisposable SubscribeToFaults(Action<SessionFault> handler);
    }
}
```

Create `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    public sealed class ProtocolSession : IProtocolSession
    {
        public static readonly TimeSpan DefaultRequestTimeout = TimeSpan.FromSeconds(10);

        private readonly ITransport transport;
        private readonly IClock clock;
        private readonly List<Action<SessionFault>> faultHandlers = new List<Action<SessionFault>>();

        private CancellationTokenSource pumpCancellation;
        private bool disposed;

        public ProtocolSession(ITransport transport, IClock clock)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
        }

        public SessionState State { get; private set; } = SessionState.Disconnected;

        public async UniTask StartAsync(CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (State != SessionState.Disconnected)
            {
                throw new InvalidOperationException(
                    $"A session can only be started from Disconnected; it is {State}.");
            }

            State = SessionState.Connecting;
            try
            {
                await transport.ConnectAsync(cancellationToken);
            }
            catch
            {
                State = SessionState.Disconnected;
                throw;
            }

            State = SessionState.Connected;
            pumpCancellation = CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
            RunPumpAsync(pumpCancellation.Token).Forget();
        }

        public async UniTask StopAsync(CancellationToken cancellationToken)
        {
            if (State == SessionState.Disconnected)
            {
                return;
            }

            CancelPump();
            await transport.DisconnectAsync(cancellationToken);
            State = SessionState.Disconnected;
        }

        public UniTask SendAsync(
            MessageId messageId,
            object payload,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken) =>
            throw new NotImplementedException();

        public IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler) =>
            throw new NotImplementedException();

        public IDisposable SubscribeToFaults(Action<SessionFault> handler)
        {
            ThrowIfDisposed();
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            faultHandlers.Add(handler);
            return new Subscription(() => faultHandlers.Remove(handler));
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            CancelPump();
            faultHandlers.Clear();
            State = SessionState.Disconnected;
        }

        private async UniTaskVoid RunPumpAsync(CancellationToken cancellationToken)
        {
            while (!cancellationToken.IsCancellationRequested)
            {
                TransportMessage message;
                try
                {
                    message = await transport.ReceiveAsync(cancellationToken);
                }
                catch (OperationCanceledException)
                {
                    return;
                }
                catch (Exception exception)
                {
                    await FaultTheStreamAsync(exception);
                    return;
                }

                Dispatch(message);
            }
        }

        private void Dispatch(TransportMessage message)
        {
            var result = ProtocolCodec.Decode(message.MessageId, message.Payload);
            if (!result.Succeeded)
            {
                PublishFault(new SessionFault(
                    result.Failure == ProtocolDecodeFailure.UnknownMessageId
                        ? SessionFaultKind.UnknownMessageId
                        : SessionFaultKind.MalformedPayload,
                    message.MessageId,
                    result.Diagnostic));
            }
        }

        /// <summary>
        /// A receive failure means the byte stream has lost its frame boundaries,
        /// so every later read returns garbage. Disconnecting here is what makes
        /// the problem diagnosable instead of silently endless.
        /// </summary>
        private async UniTask FaultTheStreamAsync(Exception exception)
        {
            State = SessionState.Faulted;
            try
            {
                await transport.DisconnectAsync(CancellationToken.None);
            }
            catch (Exception disconnectFailure)
            {
                PublishFault(new SessionFault(
                    SessionFaultKind.TransportFailure,
                    default,
                    disconnectFailure.Message));
            }

            PublishFault(new SessionFault(
                SessionFaultKind.TransportFailure, default, exception.Message));
        }

        private void PublishFault(SessionFault fault)
        {
            foreach (var handler in faultHandlers.ToArray())
            {
                try
                {
                    handler(fault);
                }
                catch
                {
                    // A fault handler that throws must not stop the others from
                    // being told, and there is nowhere left to report it.
                }
            }
        }

        private void CancelPump()
        {
            if (pumpCancellation == null)
            {
                return;
            }

            pumpCancellation.Cancel();
            pumpCancellation.Dispose();
            pumpCancellation = null;
        }

        private void ThrowIfDisposed()
        {
            if (disposed)
            {
                throw new ObjectDisposedException(nameof(ProtocolSession));
            }
        }

        private sealed class Subscription : IDisposable
        {
            private Action unsubscribe;

            public Subscription(Action unsubscribe)
            {
                this.unsubscribe = unsubscribe;
            }

            public void Dispose()
            {
                var action = unsubscribe;
                unsubscribe = null;
                action?.Invoke();
            }
        }
    }
}
```

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors, run the EditMode suite, read `test_status`.
Expected: all 7 `ProtocolSessionLifecycleTests` pass.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionLifecycleTests.cs.meta
git commit -m "Add the session lifecycle and receive pump

Grades failures: an unknown id or bad payload drops one message, while a
receive failure means the stream lost its frame boundaries and disconnects."
```

---

### Task 5: Typed subscription, dispatch, and send

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs`

**Interfaces:**
- Consumes: everything from Task 4, plus `ProtocolMessageMap.PayloadTypes` and `ProtocolCodec.EncodePayload`.
- Produces: working `Subscribe<TPayload>(MessageId, Action<TPayload>)` and `SendAsync(MessageId, object, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Text;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionDispatchTests
    {
        private static TransportMessage Frame(MessageId id, string json) =>
            new TransportMessage(id, Encoding.UTF8.GetBytes(json));

        private static ProtocolSession StartedSession(FakeTransport transport)
        {
            var session = new ProtocolSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));
            session.StartAsync(default).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void Subscribe_DeliversTheTypedPayload()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
            GameOverEventDto received = null;
            session.Subscribe<GameOverEventDto>(MessageId.GameOverEvent, dto => received = dto);

            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));

            Assert.That(received, Is.Not.Null);
        }

        [Test]
        public void Subscribe_RejectsATypeThatDoesNotMatchTheContract()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            // Caught at subscription time on purpose. Deferring it to dispatch
            // would surface the mistake when the message first arrives, which for
            // a damage event could be ten minutes into a match.
            Assert.Throws<ArgumentException>(
                () => session.Subscribe<LoginResponseDto>(MessageId.DamageEvent, _ => { }));
        }

        [Test]
        public void Subscribe_StopsDeliveringAfterDisposal()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
            var count = 0;
            var subscription = session.Subscribe<GameOverEventDto>(
                MessageId.GameOverEvent, _ => count++);

            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));
            subscription.Dispose();
            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));

            Assert.That(count, Is.EqualTo(1));
        }

        [Test]
        public void Dispatch_IsolatesASubscriberThatThrows()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);
            var secondSubscriberRan = false;
            session.Subscribe<GameOverEventDto>(
                MessageId.GameOverEvent, _ => throw new InvalidOperationException("boom"));
            session.Subscribe<GameOverEventDto>(
                MessageId.GameOverEvent, _ => secondSubscriberRan = true);

            transport.EnqueueInbound(Frame(MessageId.GameOverEvent, "{}"));

            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.SubscriberFailure));
            Assert.That(secondSubscriberRan, Is.True,
                "One view model's null reference must not silence the others.");
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));
        }

        [Test]
        public void Dispatch_TreatsAMessageWithNoSubscribersAsNormal()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            transport.EnqueueInbound(Frame(MessageId.TurnTimerEvent, "{}"));

            Assert.That(faults, Is.Empty);
        }

        [Test]
        public void SendAsync_WritesTheEncodedFrameToTheTransport()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            session.SendAsync(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "echo" },
                default).GetAwaiter().GetResult();

            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(transport.Sent[0].MessageId, Is.EqualTo(MessageId.LoginRequest));
            Assert.That(
                Encoding.UTF8.GetString(transport.Sent[0].Payload),
                Is.EqualTo("{\"player_name\":\"echo\"}"));
        }

        [Test]
        public void SendAsync_RejectsAPayloadThatDoesNotMatchTheMessageId()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            Assert.Throws<ArgumentException>(
                () => session.SendAsync(
                    MessageId.PlayCardRequest,
                    new LoginRequestDto(),
                    default).GetAwaiter().GetResult());
        }

        [Test]
        public void SendAsync_AcceptsANullPayloadForAMessageThatCarriesNone()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            session.SendAsync(MessageId.Pong, null, default).GetAwaiter().GetResult();

            Assert.That(transport.Sent[0].Payload, Is.Empty);
        }

        [Test]
        public void SendAsync_RequiresAConnectedSession()
        {
            var transport = new FakeTransport();
            using var session = new ProtocolSession(
                transport, new ManualClock(DateTimeOffset.UnixEpoch));

            Assert.Throws<InvalidOperationException>(
                () => session.SendAsync(MessageId.Pong, null, default).GetAwaiter().GetResult());
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Recompile and read `get_console_logs` — the file compiles, so the failures appear in `test_status` instead.
Expected: every test in `ProtocolSessionDispatchTests` fails with `NotImplementedException`.

- [ ] **Step 3: Write the implementation**

In `ProtocolSession.cs`, add this field beside `faultHandlers`:

```csharp
        private readonly Dictionary<MessageId, List<Action<object>>> subscribers =
            new Dictionary<MessageId, List<Action<object>>>();
```

Replace the `SendAsync` and `Subscribe<TPayload>` stubs with:

```csharp
        public UniTask SendAsync(
            MessageId messageId,
            object payload,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (State != SessionState.Connected)
            {
                throw new InvalidOperationException(
                    $"A message can only be sent from a Connected session; it is {State}.");
            }

            var hasContract = ProtocolMessageMap.PayloadTypes.TryGetValue(
                messageId, out var expectedType);
            if (payload == null)
            {
                if (hasContract)
                {
                    throw new ArgumentException(
                        $"{messageId} requires a {expectedType.Name} payload.", nameof(payload));
                }
            }
            else if (!hasContract)
            {
                throw new ArgumentException(
                    $"{messageId} carries no payload.", nameof(payload));
            }
            else if (payload.GetType() != expectedType)
            {
                throw new ArgumentException(
                    $"{messageId} expects {expectedType.Name}, not {payload.GetType().Name}.",
                    nameof(payload));
            }

            return transport.SendAsync(
                new TransportMessage(messageId, ProtocolCodec.EncodePayload(payload)),
                cancellationToken);
        }

        public IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler)
        {
            ThrowIfDisposed();
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            if (!ProtocolMessageMap.PayloadTypes.TryGetValue(messageId, out var expectedType))
            {
                throw new ArgumentException(
                    $"{messageId} carries no payload and cannot be subscribed to.",
                    nameof(messageId));
            }

            if (expectedType != typeof(TPayload))
            {
                throw new ArgumentException(
                    $"{messageId} carries {expectedType.Name}, not {typeof(TPayload).Name}.",
                    nameof(TPayload));
            }

            if (!subscribers.TryGetValue(messageId, out var handlers))
            {
                handlers = new List<Action<object>>();
                subscribers[messageId] = handlers;
            }

            Action<object> boxed = payload => handler((TPayload)payload);
            handlers.Add(boxed);
            return new Subscription(() => handlers.Remove(boxed));
        }
```

Replace the body of `Dispatch` with:

```csharp
        private void Dispatch(TransportMessage message)
        {
            var result = ProtocolCodec.Decode(message.MessageId, message.Payload);
            if (!result.Succeeded)
            {
                PublishFault(new SessionFault(
                    result.Failure == ProtocolDecodeFailure.UnknownMessageId
                        ? SessionFaultKind.UnknownMessageId
                        : SessionFaultKind.MalformedPayload,
                    message.MessageId,
                    result.Diagnostic));
                return;
            }

            DeliverToSubscribers(result);
        }

        private void DeliverToSubscribers(ProtocolDecodeResult result)
        {
            if (!subscribers.TryGetValue(result.MessageId, out var handlers))
            {
                return;
            }

            foreach (var handler in handlers.ToArray())
            {
                try
                {
                    handler(result.Payload);
                }
                catch (Exception exception)
                {
                    PublishFault(new SessionFault(
                        SessionFaultKind.SubscriberFailure,
                        result.MessageId,
                        exception.Message));
                }
            }
        }
```

Add `subscribers.Clear();` to `Dispose`, beside `faultHandlers.Clear();`.

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors, run the EditMode suite, read `test_status`.
Expected: all 9 `ProtocolSessionDispatchTests` pass.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs.meta
git commit -m "Route decoded messages to typed subscribers

Subscribe validates the payload type against the contract at subscription
time, and a throwing subscriber becomes a fault rather than a dead pump."
```

---

### Task 6: Request/response with a single-flight gate

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs`

**Interfaces:**
- Consumes: `ProtocolMessageMap.ResponseFor` (Task 2), `SendAsync` (Task 5).
- Produces: working `RequestAsync<TResponse>(MessageId, object, TimeSpan, CancellationToken)`.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs`:

```csharp
using System;
using System.Text;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ProtocolSessionRequestTests
    {
        private static readonly TimeSpan Generous = TimeSpan.FromSeconds(30);

        private static TransportMessage Frame(MessageId id, string json) =>
            new TransportMessage(id, Encoding.UTF8.GetBytes(json));

        private static ProtocolSession StartedSession(FakeTransport transport, ManualClock clock)
        {
            var session = new ProtocolSession(transport, clock);
            session.StartAsync(default).GetAwaiter().GetResult();
            return session;
        }

        [Test]
        public void RequestAsync_CompletesWithTheTypedResponse()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { PlayerName = "echo" },
                Generous,
                default).Preserve();
            transport.EnqueueInbound(Frame(
                MessageId.LoginResponse, "{\"success\":true,\"player_id\":\"p-1\"}"));

            var response = pending.GetAwaiter().GetResult();

            Assert.That(response.Success, Is.True);
            Assert.That(response.PlayerId, Is.EqualTo("p-1"));
        }

        [Test]
        public void RequestAsync_RejectsASecondConcurrentRequestForTheSameResponse()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            var first = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();

            // The protocol has no correlation id, so two in-flight logins would
            // race for one reply and one caller would get the other's answer.
            Assert.Throws<InvalidOperationException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest, new LoginRequestDto(), Generous, default)
                    .GetAwaiter().GetResult());

            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));
            Assert.That(first.GetAwaiter().GetResult().Success, Is.True);
        }

        [Test]
        public void RequestAsync_ReleasesTheGateAfterCompleting()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            var first = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));
            first.GetAwaiter().GetResult();

            var second = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":false}"));

            Assert.That(second.GetAwaiter().GetResult().Success, Is.False);
        }

        [Test]
        public void RequestAsync_DoesNotAlsoDeliverTheResponseToSubscribers()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));
            var subscriberRan = false;
            session.Subscribe<LoginResponseDto>(MessageId.LoginResponse, _ => subscriberRan = true);

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.LoginResponse, "{\"success\":true}"));
            pending.GetAwaiter().GetResult();

            Assert.That(subscriberRan, Is.False, "A response belongs to its requester.");
        }

        [Test]
        public void RequestAsync_RejectsAMessageThatHasNoPairedResponse()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            Assert.Throws<ArgumentException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.SurrenderRequest, null, Generous, default).GetAwaiter().GetResult());
        }

        [Test]
        public void RequestAsync_PropagatesCallerCancellation()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));
            using var cancellation = new CancellationTokenSource();

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, cancellation.Token)
                .Preserve();
            cancellation.Cancel();

            Assert.Throws<OperationCanceledException>(() => pending.GetAwaiter().GetResult());
        }

        [Test]
        public void RequestAsync_TimesOutWhenNoResponseArrives()
        {
            // The only test in this plan that waits on real time. The timeout is
            // built on CancellationTokenSource.CancelAfter; removing the real-time
            // dependency would mean introducing a scheduler port for one assertion.
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            Assert.Throws<TimeoutException>(
                () => session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto(),
                    TimeSpan.FromMilliseconds(50),
                    default).GetAwaiter().GetResult());
        }

        [Test]
        public void RequestAsync_FailsPendingRequestsWhenTheStreamDesynchronizes()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport, new ManualClock(DateTimeOffset.UnixEpoch));

            var pending = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest, new LoginRequestDto(), Generous, default).Preserve();
            transport.FailNextReceive(new System.IO.IOException("length prefix out of range"));

            Assert.Throws<System.IO.IOException>(() => pending.GetAwaiter().GetResult());
            Assert.That(session.State, Is.EqualTo(SessionState.Faulted));
        }
    }
}
```

- [ ] **Step 2: Run the tests to verify they fail**

Recompile, then run the suite and read `test_status`.
Expected: every test in `ProtocolSessionRequestTests` fails with `NotImplementedException`.

- [ ] **Step 3: Write the implementation**

In `ProtocolSession.cs`, add this field:

```csharp
        private readonly Dictionary<MessageId, UniTaskCompletionSource<object>> pendingRequests =
            new Dictionary<MessageId, UniTaskCompletionSource<object>>();
```

Replace the `RequestAsync` stub with:

```csharp
        public async UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            ThrowIfDisposed();
            if (!ProtocolMessageMap.ResponseFor.TryGetValue(requestId, out var responseId))
            {
                throw new ArgumentException(
                    $"{requestId} is one-way; the server answers with an event, not a response.",
                    nameof(requestId));
            }

            var expectedType = ProtocolMessageMap.PayloadTypes[responseId];
            if (expectedType != typeof(TResponse))
            {
                throw new ArgumentException(
                    $"{responseId} carries {expectedType.Name}, not {typeof(TResponse).Name}.",
                    nameof(TResponse));
            }

            if (pendingRequests.ContainsKey(responseId))
            {
                throw new InvalidOperationException(
                    $"A request awaiting {responseId} is already in flight. The protocol has " +
                    "no correlation id, so a second one could be answered with the first reply.");
            }

            var completion = new UniTaskCompletionSource<object>();
            pendingRequests[responseId] = completion;
            try
            {
                await SendAsync(requestId, payload, cancellationToken);

                using var timeoutCancellation =
                    CancellationTokenSource.CreateLinkedTokenSource(cancellationToken);
                timeoutCancellation.CancelAfter(timeout);
                try
                {
                    var response = await completion.Task
                        .AttachExternalCancellation(timeoutCancellation.Token);
                    return (TResponse)response;
                }
                catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
                {
                    throw new TimeoutException(
                        $"{requestId} received no {responseId} within {timeout}.");
                }
            }
            finally
            {
                pendingRequests.Remove(responseId);
            }
        }
```

In `Dispatch`, insert the pending-request check between the decode-failure branch and `DeliverToSubscribers`:

```csharp
            if (pendingRequests.TryGetValue(result.MessageId, out var completion))
            {
                pendingRequests.Remove(result.MessageId);
                completion.TrySetResult(result.Payload);
                return;
            }

            DeliverToSubscribers(result);
```

In `FaultTheStreamAsync`, fail every waiter before publishing the fault:

```csharp
            foreach (var pair in new List<KeyValuePair<MessageId, UniTaskCompletionSource<object>>>(
                pendingRequests))
            {
                pair.Value.TrySetException(exception);
            }

            pendingRequests.Clear();
```

Add `pendingRequests.Clear();` to `Dispose`.

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors, run the EditMode suite, read `test_status`.
Expected: all 8 `ProtocolSessionRequestTests` pass.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs.meta
git commit -m "Correlate requests with the next matching response

A second in-flight request for the same response id throws rather than
queueing: with no correlation id it could be handed the first one's reply."
```

---

### Task 7: Automatic heartbeat reply

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs` (append)

**Interfaces:**
- Consumes: `SendAsync` (Task 5), `Dispatch` (Tasks 4-6).
- Produces: no new public member; the pump answers `MessageId.Ping` with `MessageId.Pong` before any other routing.

- [ ] **Step 1: Write the failing tests**

Append to `ProtocolSessionDispatchTests.cs`, inside the class:

```csharp
        [Test]
        public void Heartbeat_AnswersAnInboundPingWithPong()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);

            transport.EnqueueInbound(new TransportMessage(MessageId.Ping, Array.Empty<byte>()));

            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(transport.Sent[0].MessageId, Is.EqualTo(MessageId.Pong));
            Assert.That(transport.Sent[0].Payload, Is.Empty,
                "The Go server sends heartbeats with a nil body; the reply matches.");
        }

        [Test]
        public void Heartbeat_DoesNotSurfaceToFaultHandlers()
        {
            var transport = new FakeTransport();
            using var session = StartedSession(transport);
            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            transport.EnqueueInbound(new TransportMessage(MessageId.Ping, Array.Empty<byte>()));

            Assert.That(faults, Is.Empty);
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Recompile, run the suite, read `test_status`.
Expected: `Heartbeat_AnswersAnInboundPingWithPong` fails because `transport.Sent` is empty.

- [ ] **Step 3: Write the implementation**

In `ProtocolSession.cs`, insert this branch in `Dispatch` immediately after the decode-failure branch and before the pending-request check:

```csharp
            if (result.MessageId == MessageId.Ping)
            {
                // Answered here rather than by a subscriber: missing one Pong makes
                // the server treat the connection as dead, which is too important to
                // depend on someone remembering to subscribe.
                SendAsync(MessageId.Pong, null, CancellationToken.None).Forget();
                return;
            }
```

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors, run the EditMode suite, read `test_status`.
Expected: both new tests pass; the 9 earlier dispatch tests stay green.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionDispatchTests.cs
git commit -m "Answer server heartbeats in the pump

Missing one Pong makes the server treat the connection as dead, so it must
not depend on a caller remembering to subscribe."
```

---

### Task 8: Round-trip probe

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs` (append)

**Interfaces:**
- Consumes: `RequestAsync` (Task 6), `IClock`, `ClientPingRequestDto`, `ClientPingResponseDto`.
- Produces: working `ProbeRoundTripAsync(CancellationToken) -> UniTask<TimeSpan>`.

- [ ] **Step 1: Write the failing tests**

Append to `ProtocolSessionRequestTests.cs`, inside the class:

```csharp
        [Test]
        public void ProbeRoundTripAsync_MeasuresTheIntervalTheClockAdvanced()
        {
            var transport = new FakeTransport();
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            using var session = StartedSession(transport, clock);
            var sentAt = DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds();

            var pending = session.ProbeRoundTripAsync(default).Preserve();
            clock.Advance(TimeSpan.FromMilliseconds(120));
            transport.EnqueueInbound(Frame(
                MessageId.ClientPingResponse, "{\"ts\":" + sentAt + "}"));

            Assert.That(
                pending.GetAwaiter().GetResult(),
                Is.EqualTo(TimeSpan.FromMilliseconds(120)));
        }

        [Test]
        public void ProbeRoundTripAsync_SendsTheClockTimestamp()
        {
            var transport = new FakeTransport();
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            using var session = StartedSession(transport, clock);
            var sentAt = DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds();

            var pending = session.ProbeRoundTripAsync(default).Preserve();

            Assert.That(transport.Sent, Has.Count.EqualTo(1));
            Assert.That(transport.Sent[0].MessageId, Is.EqualTo(MessageId.ClientPingRequest));
            Assert.That(
                Encoding.UTF8.GetString(transport.Sent[0].Payload),
                Is.EqualTo("{\"ts\":" + sentAt + "}"));

            transport.EnqueueInbound(Frame(
                MessageId.ClientPingResponse, "{\"ts\":" + sentAt + "}"));
            pending.GetAwaiter().GetResult();
        }

        [Test]
        public void ProbeRoundTripAsync_RejectsAMismatchedEcho()
        {
            var transport = new FakeTransport();
            var clock = new ManualClock(DateTimeOffset.UnixEpoch);
            using var session = StartedSession(transport, clock);
            var faults = new System.Collections.Generic.List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            var pending = session.ProbeRoundTripAsync(default).Preserve();
            transport.EnqueueInbound(Frame(MessageId.ClientPingResponse, "{\"ts\":999}"));

            // A latency number derived from an unrelated reply looks perfectly
            // plausible, which is exactly why it must not be returned.
            Assert.Throws<InvalidOperationException>(() => pending.GetAwaiter().GetResult());
            Assert.That(faults, Has.Count.EqualTo(1));
            Assert.That(faults[0].Kind, Is.EqualTo(SessionFaultKind.CorrelationMismatch));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Recompile, run the suite, read `test_status`.
Expected: the three new tests fail with `NotImplementedException`.

- [ ] **Step 3: Write the implementation**

Replace the `ProbeRoundTripAsync` stub in `ProtocolSession.cs` with:

```csharp
        public async UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken)
        {
            var sentAt = clock.UtcNow;
            var request = new ClientPingRequestDto { Ts = sentAt.ToUnixTimeMilliseconds() };

            var response = await RequestAsync<ClientPingResponseDto>(
                MessageId.ClientPingRequest, request, DefaultRequestTimeout, cancellationToken);

            if (response.Ts != request.Ts)
            {
                var diagnostic =
                    $"ClientPingResponse echoed ts {response.Ts} for a request that sent " +
                    $"{request.Ts}.";
                PublishFault(new SessionFault(
                    SessionFaultKind.CorrelationMismatch,
                    MessageId.ClientPingResponse,
                    diagnostic));
                throw new InvalidOperationException(diagnostic);
            }

            return clock.UtcNow - sentAt;
        }
```

- [ ] **Step 4: Run the tests to verify they pass**

Recompile, confirm zero errors, run the EditMode suite, read `test_status`.
Expected: all 11 `ProtocolSessionRequestTests` pass.

- [ ] **Step 5: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs
git commit -m "Add the round-trip probe

Verifies the echoed ts, the one genuinely correlatable field in the whole
protocol, and refuses to return a latency derived from an unrelated reply."
```

---

### Task 9: Documentation and full verification

**Files:**
- Modify: `docs/protocol-contract.md`
- Modify: `docs/verification-matrix.md`
- Modify: `docs/migration-checklist.md`

- [ ] **Step 1: Document the session layer in `docs/protocol-contract.md`**

Add a `## Session layer` section after `## Payload shapes`:

```markdown
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

A receive failure is treated as stream desynchronization: the session moves to
`Faulted`, disconnects, and fails every pending request. Per-message errors never
disconnect, because a server adding a message is normal version drift.
```

- [ ] **Step 2: Update the test-layer row in `docs/verification-matrix.md`**

In the EditMode row of the "Test layers" table, add `protocol session routing and correlation` to the covered list, and add `heartbeat timing under real latency` to the not-covered list.

- [ ] **Step 3: Tick the checklist item in `docs/migration-checklist.md`**

Under `## Foundation`, after the typed-DTO item, add:

```markdown
- [x] Message codec and session routing over `ITransport`, driven end to end by
  deterministic fakes.
```

- [ ] **Step 4: Run the full verification**

Run: `.\Tools\ci\verify.ps1`
Expected: the NuGet check passes, architecture verification passes including the protocol drift gate, EditMode and PlayMode suites pass, `go test ./...` passes, and `Artifacts/verification-summary.md` is written. Record the actual console output. If any gate fails, fix it before committing — do not commit a red build.

- [ ] **Step 5: Commit**

```bash
git add docs/protocol-contract.md docs/verification-matrix.md docs/migration-checklist.md
git commit -m "Document the session layer routing order

Records why a response never also reaches subscribers and why per-message
errors never disconnect."
```

---

## Definition of Done

- All 9 tasks complete with their tests passing.
- `.\Tools\ci\verify.ps1` green end to end, output recorded.
- No new assembly and no new asmdef `references` entry; `verify-architecture.ps1` unmodified.
- `HarnessPolicy.ContainsGameplayImplementation` still `false`.
- Every new `.cs` file under `Packages/` has a committed `.meta`.
