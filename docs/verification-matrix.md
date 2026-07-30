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
| Architecture | `verify-architecture.ps1` | editor revision, package pins, asmdef direction, source restrictions, 39 IDs, frame rules | console |
| Protocol drift | `verify-architecture.ps1` → `go run . -check` in `Tools/protocol` | the checked-in `protocol.contract.json` still matches the Go server byte for byte | console |
| Unity tests | `run-unity-tests.ps1` | EditMode and PlayMode suites both have zero failures | `Artifacts/unity-test-summary.json` when connected; XML/logs in batchmode |
| Go baseline | `go test ./...` | existing authoritative server remains green | console |
| Aggregate | `verify.ps1` | all gates above | `Artifacts/verification-summary.md` |

## Test layers

| Layer | Current Harness coverage | Explicitly not covered yet |
|---|---|---|
| Static | package/version pins, dependency direction, forbidden references, fixture shape, fixture-vs-Go drift | API compatibility across upgrades |
| EditMode | frame codec, message IDs, all 39 typed DTOs driven from the fixture, nullable/omitempty behavior, nested view tree, protocol session routing and correlation, fakes, DI, third-party type resolution, optional xLua probe | real TCP, catalogs, Lua VM, gameplay, heartbeat timing under real latency |
| PlayMode | UniTask player-loop yield, R3 disposal, UI Toolkit data source | scenes, final UI, assets, device input |
| Go | existing repository unit/integration baseline | Unity↔Go end-to-end process test |
| Performance | framework installed | budgets and measurements |

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
normal CI condition and is not evidence of drift. The consequence is that **the gate
is enforced locally, not in CI** — regenerate and commit the fixture in the same
change as any Go protocol edit. Point `-GoServerRoot` elsewhere if the server lives
at a different path. The comparison is byte-exact and safe on Windows because
`.gitattributes` pins `*.json` to LF in the working tree.
