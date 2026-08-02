# Unity shutdown callback order — measured 2026-08-02

Every line below was read out of a real console. No *measurement* here is recalled or
inferred. Where a path was not exercised, it says so and stays empty rather than
borrowing another path's answer.

Two statements in this file are API shape rather than measurement, and both are marked
`(API shape, not measured here)` where they appear. They are load-bearing — the whole
disqualification of `Application.wantsToQuit` rests on the first — so they are called
out rather than left to blend in with the log evidence.

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

Two different questions have two different answers on this path, and the brief conflates
them by equating "last callback while the loop is advancing" with "the latch's
installation point". They are kept apart here:

- **By the brief's frame-boundary metric, the last callback that fires while the loop is
  still advancing is `Application.wantsToQuit`.** That is what the label says, and it is
  reported honestly. It is *not* a recommendation.
- **The recommended latch installation point is `Application.quitting`.**

The criterion for the recommendation is: the latest callback on this path that (i) is
measured to fire unconditionally, (ii) cannot be cancelled by another subscriber, and
(iii) is not disqualified by a known artefact of the metric.

`Application.wantsToQuit` wins the metric but fails (ii). It is a `Func<bool>` veto hook —
a subscriber returning `false` aborts the quit (API shape, not measured here; the probe
registered it as a bool-returning handler but never exercised a `false` return). A latch
armed there would close on a
shutdown that may never happen, and any handler ordered ahead of it can cancel the very
event the latch is reacting to. Winning a frame-counter comparison does not make a
cancellable veto hook an installation point.

`Application.quitting` loses the metric only to an artefact. It fires in the *same frame*
as `wantsToQuit` in both runs (5016/5016, then 662/662), so the counter cannot separate
the two at all; its `loop-STALLED` label is produced by the same-frame collision described
in "How far the classification can be trusted" below, not by any measured loss of the
loop. It fires unconditionally once the quit is committed, which is what a latch needs.

`playModeStateChanged:ExitingPlayMode` is the latest point on this path with *positive*
evidence of a live loop after it — a whole frame elapsed between it and `wantsToQuit`.
It is sound corroboration and a usable earlier warning, but it is editor-only
(`EditorApplication` does not exist in a player — API shape, not measured here; path C
was never exercised, see below), so it cannot be the primary signal for code that must
also run outside the editor.

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
2. **Play mode continues afterwards.** `editor_status`, polled after
   `recompile_status` reported `completed`, verbatim for run B1:

   ```
   {"status":"playing","compiling":false,"domainReloadInProgress":false,"playMode":"playing","lastHeartbeat":"2026-08-02T17:51:42.1927513Z","projectPath":"E:\\code\\_Codex\\unity-project\\EchoUnity","unityVersion":"6000.2.7f2"}
   ```

   Run B2 returned the same blob with `"lastHeartbeat":"2026-08-02T17:54:26.6859437Z"`.
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
relying on unmeasured behaviour. Path A's answer must not be copied here. In the editor,
`Application.quitting` is raised by `Internal_ApplicationQuit` on leaving play mode — the
stack frame directly above the probe's handler at `Editor.log` lines 3190 and 3680, i.e.
in both Path A runs:

```
Echo.Diagnostics.ShutdownProbe/<>c:<Install>b__1_0 () (at Assets/Scripts/Diagnostics/ShutdownProbe.cs:23)
UnityEngine.Application:Internal_ApplicationQuit ()
```

Whether a real player raises it at the same point relative to the last loop tick has not
been checked in this project.

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

The last two columns answer different questions and must not be collapsed. The recommended
install point is the latest callback on the path that fires unconditionally, cannot be
cancelled by another subscriber, and is not disqualified by an artefact of the metric.
The metric column is the brief's frame-counter comparison, reported for completeness.

| Path | Measured | **Recommended latch installation point** | Brief's frame-boundary metric winner |
| --- | --- | --- | --- |
| A — exit play mode in the editor | yes | **`Application.quitting`** — unconditional, fires once the quit is committed | `Application.wantsToQuit`, which is a cancellable `Func<bool>` veto hook and is **not** the install point; it is listed here only because it wins the metric |
| B — domain reload during play mode | yes | **`AssemblyReloadEvents.beforeAssemblyReload`** | the same callback — it is the only one that fires on this path |
| C — built Windows player | **no** | not measured; the project has no scene to build | not measured |

`playModeStateChanged:ExitingPlayMode` is the last point on path A with a frame boundary
*after* it, and is a sound earlier warning, but it is editor-only and cannot replace
`Application.quitting`.

A latch that must close on both measured paths has to be armed from **two** signals,
because the two paths share none: `Application.quitting` for path A and
`AssemblyReloadEvents.beforeAssemblyReload` for path B. Do not arm it from
`Application.wantsToQuit` on either path — a subscriber there can veto the quit outright,
and the only reason it appears in this document is that it happens to win the brief's
frame-boundary metric. Path B additionally destroys all static state without re-running
`RuntimeInitializeOnLoadMethod`, so a latch stored in a static cannot be assumed to still
exist after it.
