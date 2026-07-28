# ADR-0002: Controlled Lua Hot Update

- Status: accepted for Harness seam; runtime selection deferred
- Date: 2026-07-29

## Decision

Expose Lua only through `ILuaRuntime`. Keep xLua optional and absent from the Harness.
A reflection-based capability probe may report whether a future audited xLua package
is installed without making it a compile dependency.

Lua may orchestrate presentation and non-authoritative client configuration. It may
not decide damage, legality, turns, rewards, inventory, matchmaking, or other server
authority, and it may not own sockets or Addressables handles.

## Production gate

Before enabling Lua, pin and checksum an official xLua release; define AOT/IL2CPP
preservation; sign/version bundles; sandbox APIs; test rollback and compatibility;
and verify every target architecture. Until those gates pass, the production adapter
must remain unregistered.
