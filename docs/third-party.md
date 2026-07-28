# Third-Party Dependency Policy

All dependencies are version-pinned. Upgrades require a dedicated branch, release-note review, clean import, EditMode/PlayMode verification, and a rollback commit. Generated package caches and restored binaries are not source artifacts.

| Dependency | Pin | Harness role | License / source | Update policy |
|---|---:|---|---|---|
| Addressables | `2.7.6` | asynchronous local/remote content lifecycle | Unity package | quarterly review |
| Input System | `1.17.0` | future input abstraction baseline | Unity package | with Unity LTS patch review |
| Unity Test Framework | `1.6.0` | EditMode and PlayMode tests | Unity package | with Unity editor upgrades |
| Performance Test Framework | `3.2.0` | future allocation/performance gates | Unity package | quarterly review |
| Newtonsoft JSON for Unity | `3.2.1` | explicit wire-name contracts and fixture parsing | Unity package | with Unity editor upgrades |
| UniTask | `2.5.11` | cancellation-aware Unity async ports | MIT, <https://github.com/Cysharp/UniTask> | review every release; pin tags |
| VContainer | `1.19.0` | lifetime scopes and composition root | MIT, <https://github.com/hadashiA/VContainer> | review every minor release |
| R3 | `1.3.1` | presentation observables and frame providers | MIT, <https://github.com/Cysharp/R3> | core NuGet and Unity package must move together |
| xLua | not vendored | optional hot-update adapter target | MIT, <https://github.com/Tencent/xLua> | import an audited official release during implementation |

## xLua import gate

The Harness intentionally does not copy xLua into the repository. Production enablement requires:

1. an audited official release compatible with all target architectures;
2. Android 16 KiB page-size validation where applicable;
3. a pinned artifact checksum;
4. AOT/IL2CPP generation and preservation rules;
5. signed Lua manifests, version compatibility, rollback, and sandbox tests;
6. the `ECHO_XLUA` scripting define enabled only after the adapter passes its compatibility probe.

## Dependency boundaries

- Domain may not reference any third-party package.
- Application may use UniTask but not UnityEngine, Addressables, VContainer, R3.Unity, or xLua.
- Presentation may use R3 and UI Toolkit.
- Bootstrap may use VContainer.
- Infrastructure adapters may use Addressables or xLua, but game feature assemblies must only see ports.

## R3 restore

R3.Unity is a tagged UPM dependency, while R3 core and its transitive runtime
dependencies are restored from the pinned `Assets/packages.config` by
NuGetForUnity `4.5.0`. `Tools/ci/restore-nuget.ps1` restores through the official
NuGetForUnity CLI when needed and always verifies all six expected assembly paths.
Restored binaries remain ignored; a clean environment must reproduce them from the
manifest.
