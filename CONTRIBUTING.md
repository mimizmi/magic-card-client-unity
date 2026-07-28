# Contributing

The current repository is a migration Harness, not a game implementation.

## Change rules

1. Keep Domain pure and preserve the dependency graph in
   `docs/plans/2026-07-29-echo-unity-harness-design.md`.
2. Treat Go JSON tags and message IDs as authoritative; update contract fixtures and
   tests before client consumers.
3. Put transport, Addressables, and Lua integrations behind Application ports.
4. Own every asynchronous operation with cancellation and a VContainer lifetime.
5. Add deterministic EditMode tests first; add PlayMode only for Unity lifecycle
   behavior.
6. Pin dependency versions. Upgrade one dependency family per change with a rollback
   point and clean test evidence.
7. Run `.\Tools\ci\verify.ps1` before review.

Do not commit `Library`, `Temp`, restored `Assets/Packages`, generated Addressables
content, test artifacts, credentials, licenses, signing keys, or remote bundle
secrets.
