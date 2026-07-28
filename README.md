# Echo Unity Harness

Unity `6000.2.7f2` migration harness for the existing Echo Godot client and authoritative Go backend.

This repository intentionally contains **no game implementation**. It provides the boundaries and verification machinery required to migrate safely:

- Clean Architecture assembly boundaries
- UI Toolkit MVVM integration seam
- VContainer composition seam
- UniTask asynchronous boundaries
- R3 reactive presentation seam
- Addressables content abstraction and lifecycle checks
- xLua hot-update abstraction and compatibility probe
- typed protocol contract and drift fixtures
- deterministic fakes for transport, content, Lua, and time
- EditMode, PlayMode, static architecture, and CI test entry points

## Source systems

- Godot client: `E:\code\_Godot\gd_Project\server-test`
- Go server: `E:\code\_github\magic-card-server-golang`

The Go server remains authoritative for game rules and data. Lua is restricted to client presentation/orchestration policies and must never become an authority for battle results.

## Quick verification

```powershell
.\Tools\ci\verify.ps1
```

The script validates repository policy, package pins, assembly dependency direction, EditMode tests, and PlayMode tests. See `docs/verification-matrix.md` for prerequisites and expected outputs.

## Start here

- Architecture: `docs/plans/2026-07-29-echo-unity-harness-design.md`
- Build plan: `docs/plans/2026-07-29-echo-unity-harness.md`
- Protocol baseline: `docs/protocol-contract.md`
- Migration gates: `docs/migration-checklist.md`
- Third-party policy: `docs/third-party.md`
