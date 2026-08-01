# Verification Matrix

## Local entry point

From the Unity project root:

```powershell
.\Tools\ci\verify.ps1
```

The aggregate command performs no deployment and does not require a live game
server. It verifies the checked-in Harness, runs Unity tests, then runs the existing
Go repository baseline.

| Gate | Entry point | Proves | Output |
|---|---|---|---|
| NuGet presence | `restore-nuget.ps1 -CheckOnly` | all six pinned R3 runtime assemblies exist | console |
| Fixture generator | `go test ./...` in `Tools/protocol` | the generator that produces `protocol.contract.json` is itself correct | console |
| Architecture | `verify-architecture.ps1` | editor revision, package pins, asmdef direction and purity flags, source restrictions, 39 IDs, frame rules | console |
| Protocol drift | `verify-architecture.ps1` → `go run . -check` in `Tools/protocol` | the checked-in `protocol.contract.json` still matches the Go server byte for byte | console |
| Unity tests | `run-unity-tests.ps1` | EditMode and PlayMode suites both have zero failures | `Artifacts/unity-test-summary.json` when connected; XML/logs in batchmode |
| Go baseline | `go test ./...` | existing authoritative server remains green | console |
| Aggregate | `verify.ps1` | all gates above | `Artifacts/verification-summary.md` |

The architecture gate's source-text assertions are narrower than "the whole runtime",
and reasoning about them as if they were uniform has misled reviews. They cover
`Runtime/Domain` and `Runtime/Application` only, and the two banned-word lists differ:
`Cysharp` is forbidden in `Domain` and permitted in `Application`, which is what keeps
the UniTask async boundary legal there. `Runtime/Contracts` has no source-text
assertion at all. What constrains it is its asmdef: the gate pins `references` to
zero, and also pins `noEngineReferences`, `overrideReferences`, and
`precompiledReferences` per assembly. Those three flags, not the reference list, are
what make `using UnityEngine;` and `using R3;` fail to compile there — so flipping
one to make an illegal DTO field build is now a gate failure rather than a silent
loss of the pure-contract layer.

The fixture generator's own `go test ./...` runs from `verify.ps1` before the
architecture gate. It has to, because the drift gate compares the generator against
the generator's own committed output and is therefore blind to a generator regression
by construction; those unit tests are the only thing that can catch one. Like the
drift gate itself, they are **enforced locally, not in CI** — CI invokes
`verify-architecture.ps1` directly and never runs a Go test of either module.

## Test layers

| Layer | Current Harness coverage | Explicitly not covered yet |
|---|---|---|
| Static | package/version pins, dependency direction, forbidden references, fixture shape, fixture-vs-Go drift | API compatibility across upgrades |
| EditMode | frame codec, message IDs, all 39 typed DTOs driven from the fixture, nullable/omitempty behavior, nested view tree, protocol session routing and correlation, fakes, DI, third-party type resolution, optional xLua probe, real TCP framing/cancellation/backpressure against a byte-level loopback, and a live socket against the remote Go server | catalogs, Lua VM, gameplay, reconnect after a dropped link |
| PlayMode | UniTask player-loop yield, R3 disposal, UI Toolkit data source, the main-thread scheduler hop | scenes, final UI, assets, device input |
| Go | existing repository unit/integration baseline | a locally spawned server; the end-to-end tier talks to a remote one and belongs to the EditMode row |
| Performance | framework installed | budgets and measurements |

## Transport and session properties

Each row is a property the transport or the session now guarantees, and the test
that would fail if it stopped holding. Two of them are marked **mutation-verified**:
the property was removed from the production code and the test was confirmed to
fail, because both are the kind of claim a test can assert while passing for an
unrelated reason.

| Property | Enforced by | What would otherwise pass unnoticed |
|---|---|---|
| A frame is read whole regardless of how TCP segments it | `TcpTransportFramingTests.AFragmentedFrameIsReadAsOneMessage`, `.TwoFramesInOneSegmentAreReadAsTwoMessages` | A reader assuming one read per frame. The loopback double writes the split deliberately: header alone, then body, and two whole frames in a single write. |
| A declared length over the bound is refused, and an EOF mid-body fails the receive | `TcpTransportFramingTests.ADeclaredLengthOverTheBoundFailsTheReceive`, `.ACloseMidBodyFailsTheReceive` | A four-byte header claiming gigabytes, allocated before it is judged; and a peer that vanishes mid-body read back as a short but valid frame. |
| Concurrent sends arrive as whole frames | `TcpTransportSendTests.ConcurrentSendsArriveAsWholeFrames` | **Mutation-verified.** Judged by the loopback reader's own framing rather than by the test's arithmetic, so an interleaved length prefix desynchronizes the reader instead of merely reordering payloads. The session answers a heartbeat from the receive pump while a caller sends, so this is the real concurrency, not a hypothetical. |
| The send budget refuses the message past the server's limit, and `Pong` is exempt | `TcpTransportSendTests.ExceedingTheBudgetThrowsAndKeepsTheConnection`, `.PongIsExemptFromTheBudget`; `SendBudgetTests` for the token arithmetic | The server's limit is a compile-time 30 per second and exceeding it closes the connection with no error frame — on the wire indistinguishable from a pulled cable. A budget that could swallow a heartbeat reply would cause exactly the disconnect it exists to prevent. |
| Silence beyond the read-idle deadline fails the receive, and every frame resets it | `TcpTransportIdleTests.SilenceBeyondTheIdleTimeoutFailsTheReceive`, `.TheIdleDeadlineResetsForEachFrame`, `.ACallerCancellationIsNotReportedAsAnIdleTimeout` | A half-open link the kernel takes minutes to notice, parking the pump indefinitely. The reset is the other half: without it a healthy connection dies at a fixed age, and a test of the timeout alone cannot tell the two apart. |
| Every dispatched message hops to the session's context before anything reads or writes session state | `ProtocolSessionConfinementTests` (four tests) | **Mutation-verified.** Confinement, not locking, is what makes a plain `Dictionary` of pending requests and a plain `State` property safe once a real socket introduces a second thread — including the request timeout, which fires on a thread-pool thread and must hop before its `finally` touches the single-flight gate. |
| `MainThreadSessionScheduler` really reaches the main thread, and costs no frame when already on it | `MainThreadSessionSchedulerTests` (PlayMode, four tests) | A scheduler that only appeared to switch, and a hop billed a frame on the common path. Measured rather than asserted. Nothing constructs this type in production yet — see the open checklist item — so PlayMode is currently its only consumer. |
| Our reading of the protocol matches the server's | `GoServerEndToEndTests` (EditMode, three tests) | Field names, framing, and the heartbeat reply, all against the authoritative Go server over a real socket. See below. |

### The end-to-end tier skips itself, and what that costs

`GoServerEndToEndTests` needs `ECHO_SERVER_HOST`. There is no default and the
address is deliberately not committed — it is a developer endpoint, and a variable
with no fallback is the whole mechanism keeping it out of the tree. On a machine
that has not set it the three tests call `Assert.Ignore` and the gate tolerates the
skip for that one class alone, naming any other class that skips.

The cost is worth stating plainly: **on such a machine the suite goes green having
never run the one test that can disagree with our own understanding of the
protocol.** Everything else in the repository — the fakes, the byte-level loopback
double, even the generated fixture — encodes our reading of the server rather than
checking it. CI is such a machine, permanently. So the skip count in the runner's
summary line is not cosmetic: `149/152 passed, 3 skipped` and `152/152 passed` are
different claims about how much is known, and only the second one has been
contradicted by a real server.

Note that the variable is read by the process running the tests. On the
connected-editor path that is the **Unity editor**, not the shell invoking
`verify.ps1`, so setting it in a shell after the editor started has no effect;
the editor has to have been launched with it.

### A PlayMode run straight after a failed PlayMode run is not fully trustworthy

Measured during the main-thread scheduler work: a PlayMode run immediately
following a failed one in the same editor process reported
`Expected: 1 But was: 170` on
`MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread`
with the source confirmed restored. Two consecutive re-runs and an independent run
were all green. The plausible cause is continuations stranded on the player loop
contaminating UniTask's `PlayerLoopHelper` across PlayMode sessions.

This is recorded as a property of the apparatus, not a bug anyone is chasing.
**Re-run before investigating a strange PlayMode failure that follows another
failure**, and if a fresh editor process reproduces it, then it is real.

## Prerequisites

- PowerShell 7.
- Unity `6000.2.7f2`; local default is
  `E:\code\_Unity\editor\6000.2.7f2\Editor\Unity.exe`, or set
  `UNITY_EDITOR_PATH`.
- Go available on `PATH`.
- When `Assets/Packages` is absent, install the pinned CLI and restore:

```powershell
dotnet tool install --global NuGetForUnity.Cli --version 4.5.0
.\Tools\ci\restore-nuget.ps1
```

If this project is already open, the test script uses the connected Unity Pipeline
instance. Otherwise it uses hidden batchmode and writes NUnit XML.

## CI boundary

The workflow uses a labeled self-hosted Windows runner so the exact licensed Unity
editor revision is controlled. The sibling Go repository is not part of this
checkout, so CI runs Unity Harness gates only; the aggregate local command also
checks Go. Before production, pin third-party GitHub Actions to reviewed commit SHAs.

The protocol drift gate inherits that boundary. `verify-architecture.ps1` looks for
`internal/protocol` under `-GoServerRoot` (default
`E:\code\_github\magic-card-server-golang`); when the directory is absent it emits a
warning and skips rather than failing, because a missing sibling checkout is the
normal CI condition and is not evidence of drift. A missing `go` on `PATH` skips the
same way and for the same reason: without that second guard the gate threw
`CommandNotFoundException`, which fails the architecture job with an error naming
neither the gate nor its optional dependency. When the source and the toolchain are
both present the gate is strict and real drift fails the build. The consequence is
that **the gate
is enforced locally, not in CI** — regenerate and commit the fixture in the same
change as any Go protocol edit. Point `-GoServerRoot` elsewhere if the server lives
at a different path. The comparison is byte-exact and safe on Windows because
`.gitattributes` pins `*.json` to LF in the working tree.
