# Echo Unity Harness Implementation Plan

> **For Claude:** REQUIRED SUB-SKILL: Use superpowers:executing-plans to implement this plan task-by-task.

**Goal:** Create a Unity 6000.2.7f2 Harness that compiles and verifies the migration architecture without implementing any game feature.

**Architecture:** Use an embedded UPM package with inward Clean Architecture dependencies. Keep protocol contracts machine-readable, hide transport/content/Lua behind ports, compose only in VContainer, and prove the seams with deterministic EditMode and PlayMode tests.

**Tech Stack:** Unity 6000.2.7f2, UI Toolkit runtime binding, C#, NUnit/Unity Test Framework, VContainer, UniTask, R3, Addressables, Newtonsoft JSON, PowerShell CI checks, optional xLua adapter seam.

---

### Task 1: Pin project and dependency policy

**Files:**

- Modify: `Packages/manifest.json`
- Create: `Assets/packages.config`
- Create: `Assets/NuGet.config`
- Create: `Tools/ci/restore-nuget.ps1`
- Create: `docs/third-party.md`

**Steps:**

1. Pin Unity registry packages and Git dependencies.
2. Pin R3 core and transitive runtime dependencies through NuGetForUnity and
   R3.Unity through its tagged Git package.
3. Document license, owner, update cadence, and rollback policy.
4. Launch Unity in batch mode to resolve packages; record any restore limitation.

### Task 2: Establish red tests and assembly contracts

**Files:**

- Create: `Packages/com.echo.harness/package.json`
- Create: `Packages/com.echo.harness/Tests/EditMode/*.cs`
- Create: `Packages/com.echo.harness/Tests/PlayMode/*.cs`
- Create: `Packages/com.echo.harness/**/Echo.Harness.*.asmdef`

**Steps:**

1. Create test assemblies and tests that name the required contract/fake types before those types exist.
2. Run EditMode tests and confirm the expected compilation failure.
3. Add only the minimal assembly definitions needed for the intended dependency graph.

### Task 3: Add minimal contracts and deterministic TestKit

**Files:**

- Create: `Packages/com.echo.harness/Runtime/Domain/*`
- Create: `Packages/com.echo.harness/Runtime/Contracts/*`
- Create: `Packages/com.echo.harness/Runtime/Application/*`
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/*`
- Create: `Packages/com.echo.harness/Runtime/Presentation/*`
- Create: `Packages/com.echo.harness/Runtime/Bootstrap/*`
- Create: `Packages/com.echo.harness/TestKit/*`
- Create: `Packages/com.echo.harness/Fixtures/protocol.contract.json`

**Steps:**

1. Define marker/value contracts only; do not add game use cases.
2. Encode the current Go message-ID and wire-format baseline.
3. Add transport, content, Lua, and clock fakes with explicit lifecycle behavior.
4. Add an empty VContainer health composition seam.
5. Run EditMode tests until green.

### Task 4: Add repository and CI verification

**Files:**

- Create: `Tools/ci/verify-architecture.ps1`
- Create: `Tools/ci/run-unity-tests.ps1`
- Create: `Tools/ci/verify.ps1`
- Create: `.github/workflows/unity-tests.yml`
- Create: `docs/protocol-contract.md`
- Create: `docs/migration-checklist.md`
- Create: `docs/verification-matrix.md`

**Steps:**

1. Validate exact Unity version, package pins, assembly direction, and forbidden runtime-to-TestKit references.
2. Provide repeatable EditMode and PlayMode CLI commands and XML output paths.
3. Add a CI template that expects a Unity license secret but performs no deployment.
4. Run static verification and fix every reported violation.

### Task 5: Prove Unity lifecycle seams

**Files:**

- Modify: `Packages/com.echo.harness/Tests/PlayMode/*.cs`
- Modify: `Packages/com.echo.harness/Runtime/Bootstrap/*`

**Steps:**

1. Verify a VContainer scope resolves the Harness health marker.
2. Verify UniTask can yield through the Unity player loop with cancellation ownership.
3. Verify UI Toolkit and R3 types load and dispose without leaks.
4. Verify optional xLua absence is a reported capability state rather than a compile failure.
5. Run PlayMode tests until green.

### Task 6: Final verification

**Files:**

- Create: `Artifacts/verification-summary.md` (generated locally and ignored by Git)

**Steps:**

1. Run `Tools/ci/verify.ps1`.
2. Confirm fresh EditMode and PlayMode results show zero failures (connected-editor
   JSON locally or NUnit XML in batchmode).
3. Confirm the Go baseline still passes `go test ./...`.
4. Review the tree for accidental gameplay implementation.
5. Report exact commands, outcomes, warnings, and future implementation gates.
