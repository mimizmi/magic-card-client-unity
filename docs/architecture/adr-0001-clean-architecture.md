# ADR-0001: Clean Architecture Harness Boundaries

- Status: accepted
- Date: 2026-07-29

## Decision

Use pure Domain and Contracts at the center; Application owns ports; Infrastructure
and Presentation point inward; Bootstrap is the only runtime composition root; and
TestKit is test-only. Enforce the graph with assembly definitions and
`Tools/ci/verify-architecture.ps1`.

UI Toolkit native runtime binding is the MVVM surface. R3 is limited to in-process
presentation reactivity, UniTask to Unity-facing asynchronous ports, and VContainer
to lifetime composition.

## Consequences

- Features cannot call TCP, Addressables, or Lua directly.
- The server protocol can be tested without Unity scenes or a live process.
- More boundaries and mapping code are accepted in exchange for deterministic tests,
  replaceable infrastructure, and safer team ownership.
- This ADR establishes structure only; it authorizes no gameplay implementation.
