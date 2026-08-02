# Unity shutdown callback order — measured 2026-08-02

Every line below was read out of a real console. Nothing here is recalled, inferred,
or carried over from documentation. Where a path was not exercised, it says so and
stays empty rather than borrowing another path's answer.

## What was instrumented

A throwaway `Echo.Diagnostics.ShutdownProbe` (in `Assembly-CSharp`, since
`Assets/Scripts/` carries no .asmdef) registered four handlers from a
`[RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]`:

- `Application.quitting`
- `Application.wantsToQuit`
- `EditorApplication.playModeStateChanged` (editor only)
- `AssemblyReloadEvents.beforeAssemblyReload` (editor only)

Each handler logged `[ShutdownProbe] <label> | frame=<Time.frameCount> | <classification>`,
where the classification compares `Time.frameCount` against the value seen at the
previous note: higher is `loop-advancing`, otherwise `loop-STALLED`.

The probe has been deleted. It was scaffolding for this measurement only.

**Environment.** Unity 6000.2.7f2, Windows, editor attached on port 7800, driven
through the Unity CLI (`editor_play` / `editor_stop` / `recompile`).
`ProjectSettings/EditorSettings.asset` has `m_EnterPlayModeOptionsEnabled: 1` with
`m_EnterPlayModeOptions: 0`, i.e. entering play mode performs a full domain reload
and a scene reload. `m_ScriptChangesDuringPlay` is absent, i.e. the default
"Recompile And Continue Playing".

---

## Path A — exit play mode in the editor: MEASURED

Two runs, identical in shape. Verbatim, in emission order.

Run A1 (~5 s in play mode):

```
[ShutdownProbe] installed | frame=0 | first
[ShutdownProbe] playModeStateChanged:EnteredPlayMode | frame=1 | loop-advancing
[ShutdownProbe] playModeStateChanged:ExitingPlayMode | frame=5015 | loop-advancing
[ShutdownProbe] Application.wantsToQuit | frame=5016 | loop-advancing
[ShutdownProbe] Application.quitting | frame=5016 | loop-STALLED
[ShutdownProbe] playModeStateChanged:EnteredEditMode | frame=1 | loop-STALLED
```

Run A2 (~6 s in play mode):

```
[ShutdownProbe] installed | frame=0 | first
[ShutdownProbe] playModeStateChanged:EnteredPlayMode | frame=1 | loop-advancing
[ShutdownProbe] playModeStateChanged:ExitingPlayMode | frame=661 | loop-advancing
[ShutdownProbe] Application.wantsToQuit | frame=662 | loop-advancing
[ShutdownProbe] Application.quitting | frame=662 | loop-STALLED
[ShutdownProbe] playModeStateChanged:EnteredEditMode | frame=1 | loop-STALLED
```

**Last callback that fires while the loop is still advancing: `Application.wantsToQuit`.**

Read that with the qualification in "How far the classification can be trusted"
below. `wantsToQuit` and `quitting` fire in the *same frame* in both runs, so the
frame counter cannot separate them; what it does establish is that a whole frame
elapsed between `ExitingPlayMode` and `wantsToQuit`, so the loop was demonstrably
still turning when `ExitingPlayMode` fired. `ExitingPlayMode` is therefore the
latest point on this path with *positive* evidence of a live loop after it.

`EnteredEditMode` is listed because the probe's subscription outlives play mode
(see "Subscription lifetime"), not because it is part of the shutdown sequence.
Note that `EnteredPlayMode` fires but `ExitingEditMode` does not appear at the head
of a run: the probe installs after the play-mode domain reload, which is after
`ExitingEditMode` has already been raised.

---

## Path B — domain reload during play mode: MEASURED

Triggered by appending a comment to a `.cs` file and forcing a recompile while
playing. Two runs, identical in shape. Verbatim, in emission order.

Run B1 (~5 s in play mode before the recompile):

```
[ShutdownProbe] installed | frame=0 | first
[ShutdownProbe] playModeStateChanged:EnteredPlayMode | frame=1 | loop-advancing
[ShutdownProbe] beforeAssemblyReload | frame=9152 | loop-advancing
```

Run B2 (~6 s in play mode before the recompile):

```
[ShutdownProbe] installed | frame=0 | first
[ShutdownProbe] playModeStateChanged:EnteredPlayMode | frame=1 | loop-advancing
[ShutdownProbe] beforeAssemblyReload | frame=820 | loop-advancing
```

**Last callback that fires while the loop is still advancing: `beforeAssemblyReload`.**
It is also the *only* shutdown callback on this path.

Three facts about this path matter more than the ordering, and all three were
measured, not assumed:

1. **`Application.quitting` never fires.** Neither does `Application.wantsToQuit`,
   nor `playModeStateChanged:ExitingPlayMode`. Anything hung on those three is
   simply never told that the domain is about to be destroyed under it.
2. **Play mode continues afterwards.** `editor_status` reported
   `"playMode":"playing"` after the reload completed, in both runs.
3. **The probe does not come back.** No second `installed` line ever appeared.
   With "Recompile And Continue Playing" the domain is reloaded without a scene
   reload, so `[RuntimeInitializeOnLoadMethod]` does not re-run. Every static field
   in the reloaded domain is back at its default and nothing re-registers it. A
   latch that lives in a static and is installed from `RuntimeInitializeOnLoadMethod`
   is silently dead for the remainder of that play session.

---

## Path C — a built Windows player: NOT MEASURED

Not measured. Not inferred from Path A. There is no line of console output for
this path and none is invented here.

The reason it was not measured is structural, not a matter of time:

- The project contains **no scene asset at all** — zero `.unity` files under
  `Assets/` or `Packages/`.
- `ProjectSettings/EditorBuildSettings.asset` has `m_Scenes: []`.

A player build therefore has nothing to load, and `RuntimeInitializeLoadType.AfterSceneLoad`
has no scene load to run after. Producing a meaningful Path C measurement would
require authoring a scene and adding it to the build settings — which is exactly
what Task 9 of this iteration exists to do. Build support is not the obstacle:
`StandaloneWindows64` reports `isInstalled: true`.

**Consequence for downstream tasks.** Any task that needs player-quit behaviour must
either re-run this probe once a bootstrap scene exists, or state plainly that it is
relying on unmeasured behaviour. Path A's answer must not be copied here. In the
editor, `Application.quitting` is raised by `Internal_ApplicationQuit` on leaving
play mode; whether a real player raises it at the same point relative to the last
loop tick has not been checked in this project.

---

## How far the classification can be trusted

The probe's `loop-advancing` / `loop-STALLED` label is weaker than its name suggests,
and two of the measurements above expose the gap. Downstream tasks should treat it
as follows.

**It is a frame-boundary detector, not a liveness detector.** It answers "did
`Time.frameCount` increase since the previous note", which is not the same question
as "is the player loop still running right now". Two callbacks that fire within one
frame both read `loop-STALLED` for the second of the pair even if the loop is
perfectly healthy. This is exactly what happens to `Application.quitting` in Path A:
it is marked `loop-STALLED` only because `Application.wantsToQuit` already consumed
frame 5016 (resp. 662). **That label is not evidence that the loop had stopped.**

**Outside play mode the label is meaningless.** `Time.frameCount` is not the
play-mode loop counter when read from an editor-only callback in edit mode; it
resets. Measured directly:

```
[ShutdownProbe] Application.quitting | frame=5016 | loop-STALLED
[ShutdownProbe] playModeStateChanged:EnteredEditMode | frame=1 | loop-STALLED
[ShutdownProbe] playModeStateChanged:ExitingEditMode | frame=1 | loop-STALLED
[ShutdownProbe] beforeAssemblyReload | frame=0 | loop-STALLED
```

The counter went 5016 -> 1 -> 1 -> 0. A *decrease* is a reset, not a stall, and the
probe has no way to tell them apart. Every `loop-STALLED` on an edit-mode callback
above should be read as "not classifiable".

**What survives the caveats.** Only the positive direction is sound: a
`loop-advancing` label on a play-mode callback does prove that at least one frame
boundary was crossed between the previous note and this one, and therefore that the
loop was alive across that interval. All the ordering claims in this document rest
on the emission order in the log, which is independent of the frame counter.

---

## Subscription lifetime across repeated play sessions

Worth recording because it decides whether the second and third measurements above
are clean. They are.

- Each play session logs **exactly one** `installed` line. Across four play sessions
  in a single editor process, no callback line ever appeared twice for one event.
  Entering play mode performs a full domain reload here (`m_EnterPlayModeOptions: 0`),
  which destroys the previous session's handlers. Subscriptions therefore do **not**
  accumulate, and none of the runs above is contaminated by an earlier one.
- They do, however, **outlive play mode within the editor domain**. The handlers
  registered during a play session keep firing after play mode ends: they deliver
  `EnteredEditMode`, then `ExitingEditMode` and `beforeAssemblyReload` belonging to
  the *next* play-mode entry, before that entry's reload finally frees them. Any
  editor-side subscription made from runtime code has this tail and should
  unsubscribe rather than assume play-mode exit ended it.

## A tooling note for whoever re-runs this

The CLI's `get_console_logs` buffer **does not survive a domain reload** — it came
back with zero entries immediately after the Path B reload, having been cleared
before the run. The measurements above were read from
`%LOCALAPPDATA%\Unity\Editor\Editor.log`, which is append-only and does survive.
Use the log file, not the captured buffer, for anything that spans a reload.

---

## Summary for Tasks 7 and 9

| Path | Measured | Last callback with the loop demonstrably advancing |
| --- | --- | --- |
| A — exit play mode in the editor | yes | `Application.wantsToQuit` (same frame as `Application.quitting`; `playModeStateChanged:ExitingPlayMode` is the last point with a frame boundary *after* it) |
| B — domain reload during play mode | yes | `AssemblyReloadEvents.beforeAssemblyReload`, which is also the only callback on this path |
| C — built Windows player | **no** | not measured; the project has no scene to build |

A latch that must close on both measured paths has to be armed from **two** signals,
because the two paths share none: `Application.quitting` (or `ExitingPlayMode`) for
path A, and `beforeAssemblyReload` for path B. Path B additionally destroys all
static state without re-running `RuntimeInitializeOnLoadMethod`, so a latch stored
in a static cannot be assumed to still exist after it.
