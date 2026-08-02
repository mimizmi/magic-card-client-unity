# Production Wiring and Lifecycle Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Make the harness protocol stack constructible in a player build, runnable from a bootstrap scene, and cleanly stoppable when the player loop goes away.

**Architecture:** `IClock` splits into a wall clock (`IClock`) and a monotonic interval source (`IElapsedTime`), which deletes two named defects structurally and frees `TcpTransport` from depending on a wall clock it never reads. Production implementations move into `Echo.Harness.Infrastructure`, endpoint resolution gains an asset layer over the existing environment variable, and `HarnessComposition` grows into a real composition root driven by a VContainer `LifetimeScope` in a committed bootstrap scene. Shutdown takes two paths: an ordered hook for the quiet case, and a latch on `MainThreadSessionScheduler` that turns a hop-onto-a-dead-loop from a permanent stall into an ordinary cancellation the session already knows how to handle.

**Tech Stack:** Unity 6, C# / .NET Standard 2.1, UniTask 2.5.11 (pinned), VContainer 1.19.0, Newtonsoft.Json 3.2.1, NUnit via Unity Test Framework 1.6.0, PowerShell gates under `Tools/ci/`.

**Spec:** `docs/superpowers/specs/2026-08-02-production-wiring-and-lifecycle-design.md` (committed at `7a2841d`). Branch: `production-wiring-lifecycle`.

## Global Constraints

- **The Go server repository at `E:\code\_github\magic-card-server-golang` is READ-ONLY.** Read it to confirm facts; never write to it.
- **The developer's server address must never enter a tracked file.** Not in code, not in a test, not in a comment, not commented out. `ECHO_SERVER_HOST` has no default for this reason. The port `43966` is *not* a secret and may appear.
- **No new runtime assembly.** `verify-architecture.ps1` asserts `Runtime/**` holds exactly 6 asmdefs and set-compares each one's `references`, `noEngineReferences`, `overrideReferences` and `precompiledReferences`. If a task appears to require editing those tables, stop and report — that is a layering error, not a step.
- **Commit messages are pure ASCII.** File contents may use non-ASCII; commit messages may not.
- **Every task ends green.** `pwsh Tools/ci/verify-architecture.ps1` exits 0, and the EditMode/PlayMode suites pass. Report counts, do not infer them from a clean compile.
- **Unity recompiles cost ~2 minutes.** Batch edits within a task before asking the editor to compile.
- **Test counts at branch point:** EditMode 155, PlayMode 5. Every task states its expected new total.
- **Never claim a test passed without the runner's output.** Controller-verify via `test_status`, not by reading the code.
- **Subagents, if used, run on Opus 5.** Never dispatch on Sonnet.

---

## File Structure

| File | Responsibility | Task |
|---|---|---|
| `docs/findings/2026-08-02-unity-shutdown-callback-order.md` | Measured callback order on the three shutdown paths | 1 |
| `Runtime/Application/HarnessPorts.cs` | `IClock` narrowed, `IElapsedTime` added, `ITransport.ReceiveAsync` contract | 2, 10 |
| `Runtime/Infrastructure/SystemTime.cs` | `SystemClock`, `StopwatchElapsedTime` | 2 |
| `TestKit/DeterministicFakes.cs` | `ManualTime` replaces `ManualClock`; `SystemClock` removed | 2, 11 |
| `Runtime/Infrastructure/SendBudget.cs` | Token bucket on `IElapsedTime` | 3 |
| `Runtime/Infrastructure/TcpTransport.cs` | Takes `IElapsedTime`; no-`SynchronizationContext` guard | 4, 10 |
| `Runtime/Application/Session/ProtocolSession.cs` | Takes both ports; teardown-hop fault | 4, 11 |
| `Runtime/Infrastructure/ServerEndpoint.cs` | Single environment-variable resolution | 5 |
| `TestKit/RemoteServerEndpoint.cs` | Delegates to `ServerEndpoint` | 5 |
| `Runtime/Bootstrap/HarnessEndpointSettings.cs` | ScriptableObject + resolution chain | 6 |
| `Runtime/Infrastructure/MainThreadSessionScheduler.cs` | Shutdown latch | 7 |
| `Runtime/Bootstrap/HarnessComposition.cs` | Full graph registration | 8 |
| `Runtime/Bootstrap/HarnessSessionDriver.cs` | Start/stop lifecycle | 9 |
| `Runtime/Bootstrap/HarnessLifetimeScope.cs` | Scene entry point | 9 |
| `Assets/Scenes/Bootstrap.unity` | The runnable scene | 9 |

---

### Task 1: Measure the Unity shutdown callback order

Nothing is wired in this task. It produces a fact the rest of the plan depends on: **which callbacks fire before the player loop stops.** Tasks 7 and 9 pick their signals from this file. Do not skip it and do not substitute recollection — the previous iteration's ledger records a mechanism that was asserted, told to the developer as fact, and later measured false.

**Files:**
- Create: `Assets/Scripts/Diagnostics/ShutdownProbe.cs` (deleted again in Step 6)
- Create: `docs/findings/2026-08-02-unity-shutdown-callback-order.md`

**Interfaces:**
- Consumes: nothing.
- Produces: a committed findings document. Tasks 7 and 9 cite it by path.

- [ ] **Step 1: Write the probe**

Create `Assets/Scripts/Diagnostics/ShutdownProbe.cs`:

```csharp
using UnityEngine;
#if UNITY_EDITOR
using UnityEditor;
#endif

namespace Echo.Diagnostics
{
    /// <summary>
    /// Temporary instrumentation for one measurement. Deleted at the end of Task 1.
    /// Records which shutdown callbacks fire and whether the player loop is still
    /// running when each one does.
    /// </summary>
    public static class ShutdownProbe
    {
        private static int lastSeenFrame = -1;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void Install()
        {
            lastSeenFrame = -1;
            Note("installed");

            Application.quitting += () => Note("Application.quitting");
            Application.wantsToQuit += () => { Note("Application.wantsToQuit"); return true; };

#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += state => Note($"playModeStateChanged:{state}");
            AssemblyReloadEvents.beforeAssemblyReload += () => Note("beforeAssemblyReload");
#endif
        }

        // A frame counter that has advanced since the previous note proves the loop
        // was still running between the two. A counter that has stopped moving is
        // the signal that a latch installed at that point would already be too late.
        private static void Note(string label)
        {
            var frame = Time.frameCount;
            var moved = lastSeenFrame < 0
                ? "first"
                : (frame > lastSeenFrame ? "loop-advancing" : "loop-STALLED");
            lastSeenFrame = frame;
            Debug.Log($"[ShutdownProbe] {label} | frame={frame} | {moved}");
        }
    }
}
```

- [ ] **Step 2: Measure path A — exit play mode in the editor**

Enter play mode, wait at least 3 seconds so the frame counter is clearly advancing, then leave play mode. Copy every `[ShutdownProbe]` console line verbatim.

- [ ] **Step 3: Measure path B — domain reload during play mode**

Enter play mode, wait 3 seconds, then trigger a recompile (touch any `.cs` file and let the editor reload). Copy every `[ShutdownProbe]` line verbatim.

- [ ] **Step 4: Measure path C — player quit**

Build and run a Windows player, let it run 3 seconds, close it, and read `%USERPROFILE%\AppData\LocalLow\<Company>\<Product>\Player.log`. Copy every `[ShutdownProbe]` line verbatim.

If a player build is not available in this environment, record that explicitly as **not measured** rather than inferring it from path A. An unmeasured path is a stated gap; a guessed one is a fabrication.

- [ ] **Step 5: Write the findings document**

Create `docs/findings/2026-08-02-unity-shutdown-callback-order.md` containing, for each of the three paths: the verbatim log lines, and one sentence naming the **last callback that fires while the loop is still advancing**. That callback is the latch's installation point.

State plainly which paths were measured and which were not.

- [ ] **Step 6: Delete the probe**

```bash
rm Assets/Scripts/Diagnostics/ShutdownProbe.cs Assets/Scripts/Diagnostics/ShutdownProbe.cs.meta
```

The findings document is the deliverable; the probe is scaffolding and must not ship.

- [ ] **Step 7: Commit**

```bash
git add docs/findings/2026-08-02-unity-shutdown-callback-order.md
git commit -m "Measure which shutdown callbacks fire before the loop stops"
```

---

### Task 2: Split the clock port

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/HarnessPorts.cs:80-117`
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/SystemTime.cs`
- Modify: `Packages/com.echo.harness/TestKit/DeterministicFakes.cs:318-355`
- Test: `Packages/com.echo.harness/Tests/EditMode/SystemTimeTests.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/DeterministicFakesTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces:
  - `Echo.Harness.Application.IClock` — `DateTimeOffset UtcNow { get; }` (unchanged member, shrunken doc)
  - `Echo.Harness.Application.IElapsedTime` — `long GetTimestamp()`, `TimeSpan GetElapsedTime(long startingTimestamp)`
  - `Echo.Harness.Infrastructure.SystemClock : IClock`
  - `Echo.Harness.Infrastructure.StopwatchElapsedTime : IElapsedTime`
  - `Echo.Harness.TestKit.ManualTime : IClock, IElapsedTime` with `ManualTime(DateTimeOffset initialTime)` and `void Advance(TimeSpan duration)`

This task deliberately does **not** change any consumer. `SendBudget`, `TcpTransport` and `ProtocolSession` keep taking `IClock`, and `ManualTime` still satisfies them because it implements `IClock`. Tasks 3 and 4 move the consumers. Splitting the port and moving four consumers in one commit would produce a change nobody can review.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/SystemTimeTests.cs`:

```csharp
using System;
using System.Threading;
using Echo.Harness.Infrastructure;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SystemTimeTests
    {
        [Test]
        public void StopwatchElapsedTime_NeverReportsANegativeInterval()
        {
            var time = new StopwatchElapsedTime();

            var start = time.GetTimestamp();
            Thread.Sleep(5);
            var elapsed = time.GetElapsedTime(start);

            Assert.That(elapsed, Is.GreaterThan(TimeSpan.Zero));
        }

        [Test]
        public void StopwatchElapsedTime_TimestampsAreNonDecreasing()
        {
            var time = new StopwatchElapsedTime();

            var first = time.GetTimestamp();
            Thread.Sleep(5);
            var second = time.GetTimestamp();

            Assert.That(second, Is.GreaterThanOrEqualTo(first));
        }

        // The conversion is written out rather than delegated to
        // Stopwatch.GetElapsedTime, which is .NET 7+ and not available here. This
        // pins the arithmetic: a frequency division dropped or inverted makes the
        // reported interval wrong by orders of magnitude while both tests above
        // still pass.
        [Test]
        public void StopwatchElapsedTime_ConvertsFrequencyToWallDuration()
        {
            var time = new StopwatchElapsedTime();

            var start = time.GetTimestamp();
            Thread.Sleep(50);
            var elapsed = time.GetElapsedTime(start);

            Assert.That(elapsed, Is.GreaterThan(TimeSpan.FromMilliseconds(20)));
            Assert.That(elapsed, Is.LessThan(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void SystemClock_ReportsAPlausibleWallTime()
        {
            var clock = new SystemClock();

            Assert.That(
                clock.UtcNow,
                Is.GreaterThan(new DateTimeOffset(2020, 1, 1, 0, 0, 0, TimeSpan.Zero)));
        }
    }
}
```

Add to `Packages/com.echo.harness/Tests/EditMode/DeterministicFakesTests.cs`:

```csharp
        [Test]
        public void ManualTime_AdvancesBothFacesTogether()
        {
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var start = time.GetTimestamp();

            time.Advance(TimeSpan.FromSeconds(5));

            Assert.That(time.UtcNow, Is.EqualTo(DateTimeOffset.UnixEpoch.AddSeconds(5)));
            Assert.That(time.GetElapsedTime(start), Is.EqualTo(TimeSpan.FromSeconds(5)));
        }

        [Test]
        public void ManualTime_RefusesToMoveBackwards()
        {
            var time = new ManualTime(DateTimeOffset.UnixEpoch);

            Assert.Throws<ArgumentOutOfRangeException>(
                () => time.Advance(TimeSpan.FromSeconds(-1)));
        }
```

- [ ] **Step 2: Run the tests to verify they fail**

Recompile the editor, then run the EditMode suite.

Expected: compile errors — `StopwatchElapsedTime` and `ManualTime` do not exist.

- [ ] **Step 3: Add `IElapsedTime` and shrink `IClock`'s doc**

In `HarnessPorts.cs`, replace the whole `IClock` block (`:80-117`) with:

```csharp
    /// <summary>
    /// Wall-clock time, for stamping a moment that leaves the process - a wire
    /// timestamp the server echoes, a log line, a display.
    ///
    /// <para><b>Do not measure an interval with this.</b> Wall time can step in
    /// either direction under a clock synchronisation or a manual change, so a
    /// difference between two reads is not a duration. Use
    /// <see cref="IElapsedTime"/>, which is why this interface no longer carries
    /// the long warning it used to: the two call sites that measured intervals
    /// through it - SendBudget's refill and the round-trip probe - have moved, so a
    /// non-monotonic implementation no longer damages anything.</para>
    /// </summary>
    public interface IClock
    {
        DateTimeOffset UtcNow { get; }
    }

    /// <summary>
    /// Monotonic elapsed time, and only that. It cannot answer "what time is it",
    /// which is the point: a consumer that only needs a duration should be unable
    /// to reach a wall clock by accident.
    ///
    /// <para><see cref="GetTimestamp"/> returns an opaque counter whose unit is an
    /// implementation detail; only <see cref="GetElapsedTime"/> may interpret it.
    /// Implementations must never report a negative interval for a timestamp taken
    /// earlier.</para>
    /// </summary>
    public interface IElapsedTime
    {
        long GetTimestamp();

        TimeSpan GetElapsedTime(long startingTimestamp);
    }
```

- [ ] **Step 4: Add the production implementations**

Create `Packages/com.echo.harness/Runtime/Infrastructure/SystemTime.cs`:

```csharp
using System;
using System.Diagnostics;
using Echo.Harness.Application;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Wall-clock time from the operating system. Moved here from TestKit, which
    /// carries defineConstraints ["UNITY_INCLUDE_TESTS"] and therefore could not
    /// ship in a player build - the whole reason none of this stack was
    /// constructible.
    /// </summary>
    public sealed class SystemClock : IClock
    {
        public DateTimeOffset UtcNow => DateTimeOffset.UtcNow;
    }

    /// <summary>
    /// Monotonic elapsed time from <see cref="Stopwatch"/>, which is backed by the
    /// platform's high-resolution performance counter and is unaffected by clock
    /// synchronisation.
    ///
    /// <para>The conversion is written out because the static
    /// <c>Stopwatch.GetElapsedTime</c> is .NET 7+ and this project targets
    /// .NET Standard 2.1. The multiplication happens before the division and in
    /// <see cref="double"/>, because <c>Stopwatch.Frequency</c> is 10,000,000 on
    /// Windows and differs elsewhere: dividing first in integer arithmetic would
    /// truncate every interval shorter than one whole unit to zero.</para>
    /// </summary>
    public sealed class StopwatchElapsedTime : IElapsedTime
    {
        private static readonly double TicksPerCounterUnit =
            (double)TimeSpan.TicksPerSecond / Stopwatch.Frequency;

        public long GetTimestamp() => Stopwatch.GetTimestamp();

        public TimeSpan GetElapsedTime(long startingTimestamp)
        {
            var counterUnits = Stopwatch.GetTimestamp() - startingTimestamp;
            return new TimeSpan((long)(counterUnits * TicksPerCounterUnit));
        }
    }
}
```

- [ ] **Step 5: Replace `ManualClock` and `SystemClock` in TestKit**

In `DeterministicFakes.cs`, delete the `SystemClock` class (`:318-331`) — it now lives in `Infrastructure`, which TestKit already references (`Echo.Harness.TestKit.asmdef:8`). Replace `ManualClock` (`:333-355`) with:

```csharp
    /// <summary>
    /// Controlled time for tests, implementing both time ports so a test advances
    /// once and both faces move. Two separate manual objects would make every test
    /// advance two things, which is noise that hides what the test is about.
    ///
    /// <para>Monotonic by construction: <see cref="Advance"/> rejects a negative
    /// duration.</para>
    /// </summary>
    public sealed class ManualTime : IClock, IElapsedTime
    {
        // Ticks since construction. The unit is deliberately TimeSpan ticks, so a
        // test reasoning about the wall face and the elapsed face gets the same
        // number from both.
        private long ticks;

        public ManualTime(DateTimeOffset initialTime)
        {
            Origin = initialTime;
        }

        private DateTimeOffset Origin { get; }

        public DateTimeOffset UtcNow => Origin.AddTicks(ticks);

        public long GetTimestamp() => ticks;

        public TimeSpan GetElapsedTime(long startingTimestamp) =>
            TimeSpan.FromTicks(ticks - startingTimestamp);

        public void Advance(TimeSpan duration)
        {
            if (duration < TimeSpan.Zero)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(duration),
                    "Manual time cannot move backwards.");
            }

            ticks += duration.Ticks;
        }
    }
```

- [ ] **Step 6: Update every `ManualClock` / `SystemClock` call site**

These are mechanical renames. Find them all:

```bash
git grep -n "ManualClock\|SystemClock" -- Packages/
```

Rename `new ManualClock(` to `new ManualTime(` and the declared types with it. `SystemClock` call sites need `using Echo.Harness.Infrastructure;` where they do not already have it.

- [ ] **Step 7: Run the full EditMode suite**

Expected: 155 + 6 = **161 pass, 0 fail** (3 end-to-end skipped if no endpoint is configured).

- [ ] **Step 8: Run the architecture gate**

Run: `pwsh Tools/ci/verify-architecture.ps1`
Expected: exit 0. No new assembly was added, so its tables need no edit.

- [ ] **Step 9: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/HarnessPorts.cs \
        Packages/com.echo.harness/Runtime/Infrastructure/SystemTime.cs \
        Packages/com.echo.harness/Runtime/Infrastructure/SystemTime.cs.meta \
        Packages/com.echo.harness/TestKit/DeterministicFakes.cs \
        Packages/com.echo.harness/Tests/EditMode/SystemTimeTests.cs \
        Packages/com.echo.harness/Tests/EditMode/SystemTimeTests.cs.meta \
        Packages/com.echo.harness/Tests/EditMode/DeterministicFakesTests.cs
git commit -m "Give elapsed time a port that cannot answer what time it is"
```

Stage the `.meta` files. A brief that omits them produces a commit Unity regenerates with a fresh GUID on every clone — a defect this project has hit twice.

---

### Task 3: Move `SendBudget` onto `IElapsedTime`

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Infrastructure/SendBudget.cs:19-76`
- Modify: `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs:50-54` (comment only)
- Test: `Packages/com.echo.harness/Tests/EditMode/SendBudgetTests.cs`

**Interfaces:**
- Consumes: `IElapsedTime`, `ManualTime` from Task 2.
- Produces: `SendBudget(int perSecond, IElapsedTime time)`.

The invariant that must survive: **the fractional remainder carries forward.** A bucket polled slightly faster than the refill rate must still refill at the configured rate rather than a slower one. Setting the mark to "now" discards the remainder.

- [ ] **Step 1: Write the failing test**

Add to `SendBudgetTests.cs`:

```csharp
        // The remainder invariant, pinned. With a 30/s budget the refill interval
        // is 1/30 s. Advancing by 1.5 intervals nine times must yield 13 whole
        // intervals of refill (13.5 truncated), not 9 - which is what a
        // mark-set-to-now implementation gives, because it discards the
        // half-interval remainder on every call.
        [Test]
        public void TryConsume_CarriesTheFractionalRemainderForward()
        {
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var budget = new SendBudget(30, time);

            for (var i = 0; i < 30; i++)
            {
                Assert.That(budget.TryConsume(), Is.True, $"token {i} should have been available");
            }

            Assert.That(budget.TryConsume(), Is.False, "the bucket should be empty");

            var oneAndAHalfIntervals = TimeSpan.FromTicks(
                (TimeSpan.TicksPerSecond / 30) * 3 / 2);

            var granted = 0;
            for (var i = 0; i < 9; i++)
            {
                time.Advance(oneAndAHalfIntervals);
                while (budget.TryConsume())
                {
                    granted++;
                }
            }

            Assert.That(granted, Is.EqualTo(13));
        }
```

- [ ] **Step 2: Run it against the current implementation**

`SendBudget` still takes `IClock`, which `ManualTime` satisfies, so this compiles and is expected to **PASS** — the current `DateTimeOffset` implementation already carries the remainder correctly.

Record that plainly: this is a **regression guard for the port, not a bug catch.** Its value is that it fails if Step 3 discards the remainder, which is the mistake the rewrite invites. Do not report it as though it caught something.

- [ ] **Step 3: Port the implementation**

Replace `SendBudget`'s fields, constructor and `TryConsume` (`SendBudget.cs:19-76`):

```csharp
    public sealed class SendBudget
    {
        private readonly int max;
        private readonly long refillIntervalTicks;
        private readonly IElapsedTime time;
        private int tokens;
        private long lastFillTimestamp;

        // The remainder is carried here rather than by advancing the timestamp,
        // because a timestamp's unit is opaque: IElapsedTime exposes no frequency,
        // so there is no way to add "n intervals" to one. This holds what a refill
        // did not consume, preserving the property the DateTimeOffset version got
        // from lastFill.AddTicks.
        private long carryTicks;

        public SendBudget(int perSecond, IElapsedTime time)
        {
            if (perSecond <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(perSecond), perSecond, "A send budget must be positive.");
            }

            // Above one tick per message the refill interval truncates to zero and
            // TryConsume divides by it. Rejected here rather than left to fail at
            // the first send, where the DivideByZeroException would surface as a
            // transport fault naming nothing that led to it.
            if (perSecond > TimeSpan.TicksPerSecond)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(perSecond),
                    perSecond,
                    $"A send budget above {TimeSpan.TicksPerSecond} per second has " +
                    "a refill interval of less than one tick and cannot be measured.");
            }

            this.time = time ?? throw new ArgumentNullException(nameof(time));
            max = perSecond;
            tokens = perSecond;
            refillIntervalTicks = TimeSpan.TicksPerSecond / perSecond;
            lastFillTimestamp = time.GetTimestamp();
        }

        public bool TryConsume()
        {
            var now = time.GetTimestamp();
            var elapsedTicks = time.GetElapsedTime(lastFillTimestamp).Ticks + carryTicks;
            if (elapsedTicks >= refillIntervalTicks)
            {
                var refill = elapsedTicks / refillIntervalTicks;
                tokens = (int)Math.Min(max, tokens + refill);
                carryTicks = elapsedTicks - (refill * refillIntervalTicks);
            }
            else
            {
                carryTicks = elapsedTicks;
            }

            lastFillTimestamp = now;

            if (tokens <= 0)
            {
                return false;
            }

            tokens--;
            return true;
        }
    }
```

In the class doc, "Driven by IClock rather than the wall clock so a test can exhaust and refill it without sleeping" becomes "Driven by IElapsedTime, so a test can exhaust and refill it without sleeping, and so a wall-clock step cannot wedge it at zero."

- [ ] **Step 4: Rewrite the comment that Step 3 made false**

`TcpTransport.cs:50-54` says `SendBudget.TryConsume` is "a read-modify-write over an int and a `DateTimeOffset`... the second is the more dangerous half - it is wide enough to tear." `lastFill` is now a `long`. Replace clause 3 with:

```csharp
        //   3. SendBudget.TryConsume is a read-modify-write over an int and two
        //      longs, and is not thread-safe on its own. The tearing argument this
        //      clause used to make no longer applies - it was about a
        //      DateTimeOffset wide enough to tear, and that field is gone. What
        //      remains is reason enough: a lost update to `tokens` or a stale
        //      carry silently raises the effective send rate above the server's
        //      hard limit, and exceeding it closes the connection with no error
        //      frame. This gate is the whole of what makes it safe, and it is what
        //      keeps tokens in wire order.
```

- [ ] **Step 5: Run the EditMode suite**

Expected: 161 + 1 = **162 pass, 0 fail**.

- [ ] **Step 6: Mutation-check the remainder invariant**

Temporarily replace both `carryTicks = ...` assignments with `carryTicks = 0;`. Re-run `SendBudgetTests`.

Expected: `TryConsume_CarriesTheFractionalRemainderForward` **FAILS** with 9 rather than 13. Revert the mutation and confirm the tree is clean with `git diff`.

Report the observed numbers. If the mutant survives, the test does not pin what it claims and must be strengthened before proceeding.

- [ ] **Step 7: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/SendBudget.cs \
        Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs \
        Packages/com.echo.harness/Tests/EditMode/SendBudgetTests.cs
git commit -m "Put the send budget on a clock that cannot step backwards"
```

---

### Task 4: Move `TcpTransport` and `ProtocolSession` onto the split

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs:14,91-94,139,325-327`
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs:26,37-42,334-355`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs`

**Interfaces:**
- Consumes: `IClock`, `IElapsedTime`, `ManualTime` (Task 2); `SendBudget(int, IElapsedTime)` (Task 3).
- Produces:
  - `TcpTransport(TcpTransportOptions options, IElapsedTime time)`
  - `ProtocolSession(ITransport transport, IClock clock, IElapsedTime time, ISessionScheduler scheduler)`

After this task `TcpTransport` has no `IClock` dependency at all.

- [ ] **Step 1: Write the failing test**

Add to `ProtocolSessionRequestTests.cs`:

```csharp
        // The probe's two time reads have different jobs and must come from
        // different ports: the wire ts is a moment the server echoes back, and the
        // returned figure is a duration. ManualTime advances both faces together,
        // so what this pins is that the duration is computed from the elapsed face
        // - a probe returning `clock.UtcNow - sentAt` would also produce 75 ms
        // here, so this test is a shape guard rather than a discriminator, and the
        // structural win is that IElapsedTime cannot step backwards.
        [Test]
        public void ProbeRoundTrip_ReportsTheIntervalBetweenSendAndReply()
        {
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var transport = new FakeTransport();
            var scheduler = new RecordingSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var probe = session.ProbeRoundTripAsync(CancellationToken.None);

            time.Advance(TimeSpan.FromMilliseconds(75));

            transport.EnqueueInbound(Frame(MessageId.ClientPingResponse, "{\"ts\":0}"));

            var elapsed = probe.GetAwaiter().GetResult();

            Assert.That(elapsed, Is.EqualTo(TimeSpan.FromMilliseconds(75)));
        }
```

The echoed `ts` is `0` because `DateTimeOffset.UnixEpoch.ToUnixTimeMilliseconds()` is exactly 0, and `ClientPingRequestDto.Ts` carries a plain `[JsonProperty("ts")]` with no `DefaultValueHandling.Ignore`, so it serializes as `{"ts":0}` rather than `{}`.

- [ ] **Step 2: Run it to verify it fails**

Expected: compile error — `ProtocolSession` has a 3-argument constructor.

- [ ] **Step 3: Change `TcpTransport`**

At `:14`, `private readonly IClock clock;` becomes `private readonly IElapsedTime time;`.

At `:91-94`:

```csharp
        public TcpTransport(TcpTransportOptions options, IElapsedTime time)
        {
            this.options = options ?? throw new ArgumentNullException(nameof(options));
            this.time = time ?? throw new ArgumentNullException(nameof(time));
```

At `:139`, `budget = new SendBudget(options.SendBudgetPerSecond, clock);` becomes `budget = new SendBudget(options.SendBudgetPerSecond, time);`.

At `:325-327`, the comment reads "Real time, not the injected IClock." Change `IClock` to `IElapsedTime` so the sentence still names the thing it is telling you not to use. Leave "Do not 'fix' that" intact — it is still correct.

- [ ] **Step 4: Change `ProtocolSession`**

At `:26`, add a field beside `clock`:

```csharp
        private readonly IClock clock;
        private readonly IElapsedTime time;
```

At `:37-42`:

```csharp
        public ProtocolSession(
            ITransport transport,
            IClock clock,
            IElapsedTime time,
            ISessionScheduler scheduler)
        {
            this.transport = transport ?? throw new ArgumentNullException(nameof(transport));
            this.clock = clock ?? throw new ArgumentNullException(nameof(clock));
            this.time = time ?? throw new ArgumentNullException(nameof(time));
            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
        }
```

At `:334-355`, replace the two time reads:

```csharp
        public async UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken)
        {
            // Two ports, two jobs. The wall clock stamps the ts the server echoes
            // back, because that value leaves the process and must be a moment. The
            // returned figure is a duration and comes from the monotonic source, so
            // a clock synchronisation landing inside the probe can no longer make
            // this method report a negative latency.
            var sentAt = clock.UtcNow;
            var startedAt = time.GetTimestamp();
            var request = new ClientPingRequestDto { Ts = sentAt.ToUnixTimeMilliseconds() };

            var response = await RequestAsync<ClientPingResponseDto>(
                MessageId.ClientPingRequest, request, RoundTripProbeDeadline, cancellationToken);

            if (response.Ts != request.Ts)
            {
                var diagnostic =
                    $"ClientPingResponse echoed ts {response.Ts} for a request that sent " +
                    $"{request.Ts}.";
                PublishFault(new SessionFault(
                    SessionFaultKind.CorrelationMismatch,
                    MessageId.ClientPingResponse,
                    diagnostic));
                throw new CorrelationMismatchException(MessageId.ClientPingResponse, diagnostic);
            }

            return time.GetElapsedTime(startedAt);
        }
```

- [ ] **Step 5: Update every construction site**

```bash
git grep -n "new ProtocolSession(\|new TcpTransport(" -- Packages/
```

Every `new ProtocolSession(transport, clock, scheduler)` becomes `new ProtocolSession(transport, time, time, scheduler)` where `time` is a `ManualTime`. Every `new TcpTransport(options, clock)` becomes `new TcpTransport(options, time)`.

- [ ] **Step 6: Run the EditMode suite**

Expected: 162 + 1 = **163 pass, 0 fail**.

- [ ] **Step 7: Confirm `TcpTransport` no longer names `IClock`**

```bash
git grep -n "IClock" -- Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs
```

Expected: no output. Report the actual output.

- [ ] **Step 8: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs \
        Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Tests/EditMode/
git commit -m "Stop the transport depending on a wall clock it never reads"
```

---

### Task 5: Give endpoint resolution one implementation

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/ServerEndpoint.cs`
- Modify: `Packages/com.echo.harness/TestKit/RemoteServerEndpoint.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ServerEndpointTests.cs`

**Interfaces:**
- Consumes: nothing.
- Produces: `Echo.Harness.Infrastructure.ServerEndpoint` — readonly struct with `string Host`, `int Port`, `const string HostVariable = "ECHO_SERVER_HOST"`, `const string PortVariable = "ECHO_SERVER_PORT"`, `const int DefaultPort = 43966`, and `static bool TryResolveFromEnvironment(out ServerEndpoint endpoint)`.

`RemoteServerEndpoint` keeps its name and members so the end-to-end tier does not change, and delegates. Two copies of the port guard would drift, and the drift runs the tier against a different endpoint than the one asked for while reporting whatever answers as the truth.

- [ ] **Step 1: Write the failing test**

Create `Packages/com.echo.harness/Tests/EditMode/ServerEndpointTests.cs`:

```csharp
using System;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class ServerEndpointTests
    {
        private string savedHost;
        private string savedPort;

        [SetUp]
        public void SaveEnvironment()
        {
            savedHost = Environment.GetEnvironmentVariable(ServerEndpoint.HostVariable);
            savedPort = Environment.GetEnvironmentVariable(ServerEndpoint.PortVariable);
        }

        [TearDown]
        public void RestoreEnvironment()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, savedHost);
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, savedPort);
        }

        [Test]
        public void TryResolveFromEnvironment_ReportsNotConfiguredWithoutAHost()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, null);

            Assert.That(ServerEndpoint.TryResolveFromEnvironment(out _), Is.False);
        }

        [Test]
        public void TryResolveFromEnvironment_DefaultsThePortWhenOnlyAHostIsSet()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "example.invalid");
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, null);

            Assert.That(ServerEndpoint.TryResolveFromEnvironment(out var endpoint), Is.True);
            Assert.That(endpoint.Host, Is.EqualTo("example.invalid"));
            Assert.That(endpoint.Port, Is.EqualTo(43966));
        }

        // A signed value is a typo, and falling back to the default would run
        // against a different endpoint than the one asked for while reporting
        // whatever answered as the truth. NumberStyles.None is what surfaces it.
        [TestCase("+43966")]
        [TestCase("-1")]
        [TestCase("not-a-port")]
        [TestCase("70000")]
        [TestCase("0")]
        public void TryResolveFromEnvironment_RejectsAnUnusablePort(string configured)
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "example.invalid");
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, configured);

            Assert.Throws<ArgumentException>(
                () => ServerEndpoint.TryResolveFromEnvironment(out _));
        }

        [Test]
        public void RemoteServerEndpoint_ResolvesThroughTheSameImplementation()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "example.invalid");
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, "1234");

            Assert.That(RemoteServerEndpoint.TryResolve(out var endpoint), Is.True);
            Assert.That(endpoint.Host, Is.EqualTo("example.invalid"));
            Assert.That(endpoint.Port, Is.EqualTo(1234));
        }
    }
}
```

`example.invalid` is a reserved name from RFC 2606 that cannot resolve. Nothing here connects, so no address is needed — and none may be written.

- [ ] **Step 2: Run to verify it fails**

Expected: compile error — `ServerEndpoint` does not exist.

- [ ] **Step 3: Create `ServerEndpoint`**

Move the body of `RemoteServerEndpoint` into `Packages/com.echo.harness/Runtime/Infrastructure/ServerEndpoint.cs`, renaming the type and renaming `TryResolve` to `TryResolveFromEnvironment`. Keep the doc paragraphs explaining why `HostVariable` has no default — that reasoning is why the rule holds, and it must not be lost in the move.

- [ ] **Step 4: Make `RemoteServerEndpoint` delegate**

```csharp
using Echo.Harness.Infrastructure;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// Where the authoritative Go server is, for the one test tier that talks to
    /// it. The resolution itself lives in <see cref="ServerEndpoint"/> so the test
    /// tier and the bootstrap scene cannot disagree about what ECHO_SERVER_HOST
    /// means; this type is the name the end-to-end tier already uses.
    /// </summary>
    public readonly struct RemoteServerEndpoint
    {
        public const string HostVariable = ServerEndpoint.HostVariable;

        public const string PortVariable = ServerEndpoint.PortVariable;

        public const int DefaultPort = ServerEndpoint.DefaultPort;

        private RemoteServerEndpoint(ServerEndpoint endpoint)
        {
            Host = endpoint.Host;
            Port = endpoint.Port;
        }

        public string Host { get; }

        public int Port { get; }

        public static bool TryResolve(out RemoteServerEndpoint endpoint)
        {
            if (!ServerEndpoint.TryResolveFromEnvironment(out var resolved))
            {
                endpoint = default;
                return false;
            }

            endpoint = new RemoteServerEndpoint(resolved);
            return true;
        }
    }
}
```

- [ ] **Step 5: Run the EditMode suite**

Expected: 163 + 8 = **171 pass, 0 fail** (the `[TestCase]` attribute contributes 5 cases).

- [ ] **Step 6: Verify no address entered the tree**

```bash
git grep -nE "[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}\.[0-9]{1,3}" -- Packages/ Assets/ docs/ Tools/
```

Expected: no routable address. `127.0.0.1` in `TcpTransportOptions` is the loopback default and is fine. Report every hit and classify it.

- [ ] **Step 7: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/ServerEndpoint.cs \
        Packages/com.echo.harness/Runtime/Infrastructure/ServerEndpoint.cs.meta \
        Packages/com.echo.harness/TestKit/RemoteServerEndpoint.cs \
        Packages/com.echo.harness/Tests/EditMode/ServerEndpointTests.cs \
        Packages/com.echo.harness/Tests/EditMode/ServerEndpointTests.cs.meta
git commit -m "Let the tests and the app agree on what one variable means"
```

---

### Task 6: Add the settings asset and the resolution chain

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessEndpointSettings.cs`
- Modify: `.gitignore`
- Test: `Packages/com.echo.harness/Tests/EditMode/EndpointResolutionTests.cs`

**Interfaces:**
- Consumes: `ServerEndpoint` (Task 5).
- Produces:
  - `Echo.Harness.Bootstrap.EndpointResolution` — readonly struct with `bool IsConfigured`, `string Host`, `int Port`, `string Source`, and statics `NotConfigured(string source)` and `From(string host, int port, string source)`
  - `Echo.Harness.Bootstrap.HarnessEndpointSettings : ScriptableObject` with `string Host`, `int Port`, `const string ResourcePath = "HarnessEndpointSettings"`, `void SetForTests(string, int)`
  - `static EndpointResolution HarnessEndpointSettings.Resolve(HarnessEndpointSettings asset)`
  - `static EndpointResolution HarnessEndpointSettings.ResolveFromResources()`

`Resolve` takes the asset as a parameter so it is testable without a `Resources` folder; `ResolveFromResources` is the thin production wrapper.

- [ ] **Step 1: Write the failing test**

Create `Packages/com.echo.harness/Tests/EditMode/EndpointResolutionTests.cs`:

```csharp
using System;
using Echo.Harness.Bootstrap;
using Echo.Harness.Infrastructure;
using NUnit.Framework;
using UnityEngine;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class EndpointResolutionTests
    {
        private string savedHost;
        private string savedPort;

        [SetUp]
        public void SaveEnvironment()
        {
            savedHost = Environment.GetEnvironmentVariable(ServerEndpoint.HostVariable);
            savedPort = Environment.GetEnvironmentVariable(ServerEndpoint.PortVariable);
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, null);
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, null);
        }

        [TearDown]
        public void RestoreEnvironment()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, savedHost);
            Environment.SetEnvironmentVariable(ServerEndpoint.PortVariable, savedPort);
        }

        [Test]
        public void Resolve_ReportsNotConfiguredWhenNeitherSourceHasAHost()
        {
            var resolution = HarnessEndpointSettings.Resolve(null);

            Assert.That(resolution.IsConfigured, Is.False);
        }

        // A missing asset is the ordinary state of a fresh clone, not an error.
        [Test]
        public void Resolve_FallsBackToTheEnvironmentWhenTheAssetIsAbsent()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "from-env.invalid");

            var resolution = HarnessEndpointSettings.Resolve(null);

            Assert.That(resolution.IsConfigured, Is.True);
            Assert.That(resolution.Host, Is.EqualTo("from-env.invalid"));
            Assert.That(resolution.Source, Does.Contain(ServerEndpoint.HostVariable));
        }

        // A blank host in a present asset means "I have not filled this in", which
        // must fall through rather than resolve to an empty host.
        [Test]
        public void Resolve_FallsThroughAnAssetWithABlankHost()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "from-env.invalid");
            var asset = ScriptableObject.CreateInstance<HarnessEndpointSettings>();
            try
            {
                var resolution = HarnessEndpointSettings.Resolve(asset);

                Assert.That(resolution.IsConfigured, Is.True);
                Assert.That(resolution.Host, Is.EqualTo("from-env.invalid"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }

        [Test]
        public void Resolve_PrefersTheAssetOverTheEnvironment()
        {
            Environment.SetEnvironmentVariable(ServerEndpoint.HostVariable, "from-env.invalid");
            var asset = ScriptableObject.CreateInstance<HarnessEndpointSettings>();
            try
            {
                asset.SetForTests("from-asset.invalid", 1234);

                var resolution = HarnessEndpointSettings.Resolve(asset);

                Assert.That(resolution.Host, Is.EqualTo("from-asset.invalid"));
                Assert.That(resolution.Port, Is.EqualTo(1234));
                Assert.That(resolution.Source, Does.Contain("asset"));
            }
            finally
            {
                UnityEngine.Object.DestroyImmediate(asset);
            }
        }
    }
}
```

- [ ] **Step 2: Run to verify it fails**

Expected: compile error — `HarnessEndpointSettings` does not exist.

- [ ] **Step 3: Create the settings type**

Create `Packages/com.echo.harness/Runtime/Bootstrap/HarnessEndpointSettings.cs`:

```csharp
using Echo.Harness.Infrastructure;
using UnityEngine;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// A resolved endpoint, or the fact that there is not one.
    /// <see cref="Source"/> exists so a log line can say where the address came
    /// from: with two sources and a fallthrough, "connecting to X" alone leaves a
    /// developer unable to tell why their asset edit had no effect.
    /// </summary>
    public readonly struct EndpointResolution
    {
        private EndpointResolution(bool isConfigured, string host, int port, string source)
        {
            IsConfigured = isConfigured;
            Host = host;
            Port = port;
            Source = source;
        }

        public bool IsConfigured { get; }

        public string Host { get; }

        public int Port { get; }

        public string Source { get; }

        public static EndpointResolution NotConfigured(string source) =>
            new EndpointResolution(false, null, 0, source);

        public static EndpointResolution From(string host, int port, string source) =>
            new EndpointResolution(true, host, port, source);
    }

    /// <summary>
    /// The endpoint, as an asset a developer can edit in the Inspector without
    /// restarting the editor.
    ///
    /// <para><b>The asset is gitignored and is loaded through Resources.Load
    /// rather than a serialized reference from the scene.</b> That is not a
    /// convenience. The scene is committed and the asset is not, so a serialized
    /// reference would ship a dangling GUID inside a committed scene and break for
    /// every fresh clone. Resources.Load returns null when the asset is absent,
    /// which is exactly the not-configured path, with nothing broken to
    /// explain.</para>
    /// </summary>
    public sealed class HarnessEndpointSettings : ScriptableObject
    {
        public const string ResourcePath = "HarnessEndpointSettings";

        [SerializeField]
        [Tooltip("Blank means fall through to the ECHO_SERVER_HOST environment variable.")]
        private string host = string.Empty;

        [SerializeField]
        private int port = ServerEndpoint.DefaultPort;

        public string Host => host;

        public int Port => port;

        /// <summary>
        /// Test-only seam. The fields are serialized and private so the Inspector
        /// owns them; a test still needs some way to populate an in-memory
        /// instance.
        /// </summary>
        public void SetForTests(string hostValue, int portValue)
        {
            host = hostValue;
            port = portValue;
        }

        public static EndpointResolution ResolveFromResources() =>
            Resolve(Resources.Load<HarnessEndpointSettings>(ResourcePath));

        public static EndpointResolution Resolve(HarnessEndpointSettings asset)
        {
            if (asset != null && !string.IsNullOrWhiteSpace(asset.Host))
            {
                return EndpointResolution.From(
                    asset.Host.Trim(),
                    asset.Port,
                    $"the {ResourcePath} asset");
            }

            if (ServerEndpoint.TryResolveFromEnvironment(out var fromEnvironment))
            {
                return EndpointResolution.From(
                    fromEnvironment.Host,
                    fromEnvironment.Port,
                    $"the {ServerEndpoint.HostVariable} environment variable");
            }

            return EndpointResolution.NotConfigured(
                $"no {ResourcePath} asset with a host, and no {ServerEndpoint.HostVariable}");
        }
    }
}
```

- [ ] **Step 4: Ignore the asset**

Append to `.gitignore`:

```
# The developer's server endpoint. The address must not enter the repository -
# see ServerEndpoint for why ECHO_SERVER_HOST has no default. This asset is the
# local, editable half of the same rule.
/Assets/Resources/HarnessEndpointSettings.asset
/Assets/Resources/HarnessEndpointSettings.asset.meta
```

- [ ] **Step 5: Run the EditMode suite**

Expected: 171 + 4 = **175 pass, 0 fail**.

- [ ] **Step 6: Verify the ignore rule works**

```bash
mkdir -p Assets/Resources
printf 'stub\n' > Assets/Resources/HarnessEndpointSettings.asset
git status --short Assets/Resources/
rm Assets/Resources/HarnessEndpointSettings.asset
```

Expected: `git status` prints nothing for that path. Report the actual output.

- [ ] **Step 7: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Bootstrap/HarnessEndpointSettings.cs \
        Packages/com.echo.harness/Runtime/Bootstrap/HarnessEndpointSettings.cs.meta \
        Packages/com.echo.harness/Tests/EditMode/EndpointResolutionTests.cs \
        Packages/com.echo.harness/Tests/EditMode/EndpointResolutionTests.cs.meta \
        .gitignore
git commit -m "Let a developer change the endpoint without restarting the editor"
```

---

### Task 7: Give the scheduler a shutdown latch

**Read `docs/findings/2026-08-02-unity-shutdown-callback-order.md` before starting.** The signals wired here come from that measurement, not from this plan's guesses.

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Infrastructure/MainThreadSessionScheduler.cs`
- Test: `Packages/com.echo.harness/Tests/PlayMode/MainThreadSessionSchedulerTests.cs`

**Interfaces:**
- Consumes: `ISessionScheduler` (existing).
- Produces: `void MainThreadSessionScheduler.LatchForShutdown()` and `bool MainThreadSessionScheduler.IsLatched { get; }`.

- [ ] **Step 1: Write the failing tests**

Add to `Packages/com.echo.harness/Tests/PlayMode/MainThreadSessionSchedulerTests.cs`:

```csharp
        // The failure this closes: UniTask.SwitchToMainThread queues its
        // continuation on the player loop WITHOUT consulting the token, so once the
        // loop stops a pending hop never resumes and never throws. A session can
        // handle a hop that fails; it has no answer for one that never returns.
        [UnityTest]
        public IEnumerator ALatchedSchedulerCancelsInsteadOfQueueingOntoADeadLoop()
        {
            var scheduler = new MainThreadSessionScheduler();
            scheduler.LatchForShutdown();

            Exception caught = null;
            var completed = false;

            RunAsync().Forget();

            async UniTaskVoid RunAsync()
            {
                try
                {
                    await scheduler.SwitchToSessionContextAsync(CancellationToken.None);
                }
                catch (Exception ex)
                {
                    caught = ex;
                }
                finally
                {
                    completed = true;
                }
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while (!completed && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(completed, Is.True, "a latched hop must complete rather than park");
            Assert.That(caught, Is.InstanceOf<OperationCanceledException>());
        }

        [UnityTest]
        public IEnumerator AnUnlatchedSchedulerStillHops()
        {
            var scheduler = new MainThreadSessionScheduler();

            Assert.That(scheduler.IsLatched, Is.False);

            var completed = false;
            RunAsync().Forget();

            async UniTaskVoid RunAsync()
            {
                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);
                completed = true;
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while (!completed && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(completed, Is.True);
        }
```

- [ ] **Step 2: Run to verify they fail**

Expected: compile error — `LatchForShutdown` does not exist.

- [ ] **Step 3: Implement the latch**

Replace the class body of `MainThreadSessionScheduler`, keeping the existing doc comment above it:

```csharp
    public sealed class MainThreadSessionScheduler : ISessionScheduler
    {
        // Static because the signal is a property of the process, not of one
        // scheduler: a scheduler constructed after the loop has begun stopping is
        // just as unable to hop as one constructed before it.
        private static volatile bool processIsShuttingDown;

        private volatile bool latched;

        public bool IsLatched => latched || processIsShuttingDown;

        /// <summary>
        /// Declares that the player loop is going away, after which
        /// <see cref="SwitchToSessionContextAsync"/> cancels rather than queueing a
        /// continuation that will never run.
        ///
        /// <para>One-way on purpose. There is no path on which a loop that has
        /// begun stopping starts again within the same process lifetime, and an
        /// unlatch would invite a caller to clear it during teardown.</para>
        /// </summary>
        public void LatchForShutdown() => latched = true;

        [RuntimeInitializeOnLoadMethod(RuntimeInitializeLoadType.AfterSceneLoad)]
        private static void InstallShutdownSignals()
        {
            // Reset first. With "Enter Play Mode Options" and domain reload
            // disabled, this method re-runs while the static still carries the
            // previous play session's value, which would latch every scheduler in
            // the new session before it began.
            processIsShuttingDown = false;

            Application.quitting += () => processIsShuttingDown = true;

#if UNITY_EDITOR
            EditorApplication.playModeStateChanged += state =>
            {
                if (state == PlayModeStateChange.ExitingPlayMode)
                {
                    processIsShuttingDown = true;
                }
            };

            AssemblyReloadEvents.beforeAssemblyReload += () => processIsShuttingDown = true;
#endif
        }

        public async UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken)
        {
            // Checked before the hop, not after. After is too late by construction:
            // the await is the thing that never returns.
            if (IsLatched)
            {
                throw new OperationCanceledException(
                    "The session context is gone: the player loop is shutting down, so a " +
                    "continuation queued onto it would never run. This is an orderly " +
                    "shutdown signal, not a transport failure.");
            }

            await UniTask.SwitchToMainThread(cancellationToken);
        }
    }
```

Add the needed usings at the top of the file: `System`, `UnityEngine`, and `UnityEditor` under `#if UNITY_EDITOR`.

**If the Task 1 findings show a signal fires only after the loop has stalled, do not wire it.** Report that instead of wiring a signal the measurement disqualified.

- [ ] **Step 4: Run the PlayMode suite**

Expected: 5 + 2 = **7 pass, 0 fail**.

- [ ] **Step 5: Mutation-check the latch**

Comment out the `if (IsLatched) { throw ... }` block. Re-run the PlayMode suite.

Expected: `ALatchedSchedulerCancelsInsteadOfQueueingOntoADeadLoop` **FAILS** on `completed Is.True` after burning its 5 s deadline — the hop parks, which is the defect. Revert and confirm `git diff` is clean.

Report the observed result. A mutant that survives means the test is watching something other than the latch.

- [ ] **Step 6: Extend the class doc**

Its last paragraph says "Do not read the token argument as a promise of a prompt return." That stays true of the unlatched path. Add one paragraph recording that the latched path *is* prompt, and that this is the whole reason the latch exists.

- [ ] **Step 7: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/MainThreadSessionScheduler.cs \
        Packages/com.echo.harness/Tests/PlayMode/MainThreadSessionSchedulerTests.cs
git commit -m "Turn a hop onto a dead loop into a cancellation instead of a stall"
```

---

### Task 8: Register the whole graph

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs`

**Interfaces:**
- Consumes: `SystemClock`, `StopwatchElapsedTime` (Task 2); `TcpTransport`, `ProtocolSession` new signatures (Task 4); `EndpointResolution`, `HarnessEndpointSettings.ResolveFromResources` (Task 6).
- Produces: `HarnessComposition.Configure(IContainerBuilder builder, EndpointResolution endpoint)` plus the existing one-argument overload, which now resolves from `Resources`.

- [ ] **Step 1: Write the failing test**

Add to `CompositionSmokeTests.cs`:

```csharp
        // Registration is identical whether or not an endpoint is configured. If
        // the unconfigured case registered less, this test would cover a shape that
        // never runs in anger.
        [TestCase(true)]
        [TestCase(false)]
        public void HarnessComposition_ResolvesTheWholeSessionStack(bool configured)
        {
            var endpoint = configured
                ? EndpointResolution.From("example.invalid", 43966, "test")
                : EndpointResolution.NotConfigured("test");

            var builder = new ContainerBuilder();
            HarnessComposition.Configure(builder, endpoint);
            using var container = builder.Build();

            Assert.That(container.Resolve<IClock>(), Is.Not.Null);
            Assert.That(container.Resolve<IElapsedTime>(), Is.Not.Null);
            Assert.That(container.Resolve<ISessionScheduler>(), Is.Not.Null);
            Assert.That(container.Resolve<ITransport>(), Is.Not.Null);
            Assert.That(container.Resolve<IProtocolSession>(), Is.Not.Null);
            Assert.That(container.Resolve<EndpointResolution>().IsConfigured, Is.EqualTo(configured));
        }

        // The session and the transport must be the same instances everything else
        // sees. Two ProtocolSessions over two sockets is a defect a resolve-once
        // test cannot see.
        [Test]
        public void HarnessComposition_RegistersTheSessionAsASingleton()
        {
            var builder = new ContainerBuilder();
            HarnessComposition.Configure(
                builder, EndpointResolution.From("example.invalid", 43966, "test"));
            using var container = builder.Build();

            Assert.That(
                container.Resolve<IProtocolSession>(),
                Is.SameAs(container.Resolve<IProtocolSession>()));
            Assert.That(
                container.Resolve<ITransport>(),
                Is.SameAs(container.Resolve<ITransport>()));
        }
```

Add `using Echo.Harness.Application;` and `using Echo.Harness.Infrastructure;` to the file.

- [ ] **Step 2: Run to verify it fails**

Expected: compile error — `Configure` takes one argument.

- [ ] **Step 3: Implement the registration**

Replace `HarnessComposition` (keep `HarnessRuntimeDescriptor` above it unchanged):

```csharp
    public static class HarnessComposition
    {
        public static void Configure(IContainerBuilder builder) =>
            Configure(builder, HarnessEndpointSettings.ResolveFromResources());

        /// <summary>
        /// The single registration point. The endpoint is a parameter rather than
        /// something this method reads, so the whole graph can be built in EditMode
        /// from a bare ContainerBuilder with no Resources folder and no scene.
        ///
        /// <para>The graph has the same shape whether or not an endpoint is
        /// configured. <see cref="HarnessSessionDriver"/> decides whether to
        /// connect; registering less in the unconfigured case would mean the
        /// EditMode resolution test covers a shape that never runs.</para>
        /// </summary>
        public static void Configure(IContainerBuilder builder, EndpointResolution endpoint)
        {
            builder.RegisterInstance(new HarnessRuntimeDescriptor(
                "Echo Unity Harness",
                HarnessPolicy.ContainsGameplayImplementation));

            builder.RegisterInstance(endpoint);

            builder.Register<SystemClock>(Lifetime.Singleton).As<IClock>();
            builder.Register<StopwatchElapsedTime>(Lifetime.Singleton).As<IElapsedTime>();
            builder.Register<MainThreadSessionScheduler>(Lifetime.Singleton).As<ISessionScheduler>();

            // Host and port are the only options configuration supplies. The rest
            // are derived from the authoritative Go server and are not negotiable,
            // so they keep their defaults. An unconfigured endpoint leaves the
            // loopback default in place, which nothing connects to because the
            // driver does not start.
            var defaults = new TcpTransportOptions();
            builder.RegisterInstance(new TcpTransportOptions
            {
                Host = endpoint.IsConfigured ? endpoint.Host : defaults.Host,
                Port = endpoint.IsConfigured ? endpoint.Port : defaults.Port,
            });

            builder.Register<TcpTransport>(Lifetime.Singleton).As<ITransport>();
            builder.Register<ProtocolSession>(Lifetime.Singleton).As<IProtocolSession>();
        }
    }
```

Add `using Echo.Harness.Application;` and `using Echo.Harness.Infrastructure;` to the file.

- [ ] **Step 4: Run the EditMode suite**

Expected: 175 + 3 = **178 pass, 0 fail** (the `[TestCase]` pair plus the singleton test).

- [ ] **Step 5: Mutation-check the registration**

Comment out the `ITransport` registration. Re-run.

Expected: both `HarnessComposition_ResolvesTheWholeSessionStack` cases **FAIL** resolving `ITransport` or the `IProtocolSession` that depends on it. Revert and confirm `git diff` is clean.

- [ ] **Step 6: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs \
        Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs
git commit -m "Make the composition root register something worth resolving"
```

---

### Task 9: Drive the lifecycle from a bootstrap scene

**Read `docs/findings/2026-08-02-unity-shutdown-callback-order.md` before choosing the ordered-shutdown hook.**

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessSessionDriver.cs`
- Create: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs`
- Create: `Assets/Scenes/Bootstrap.unity`
- Modify: `ProjectSettings/EditorBuildSettings.asset`
- Test: `Packages/com.echo.harness/Tests/PlayMode/HarnessSessionDriverTests.cs`

**Interfaces:**
- Consumes: `HarnessComposition.Configure` (Task 8), `EndpointResolution` (Task 6), `MainThreadSessionScheduler.LatchForShutdown` (Task 7).
- Produces: `HarnessSessionDriver(IProtocolSession session, EndpointResolution endpoint)` implementing `VContainer.Unity.IAsyncStartable` and `IDisposable`, with `UniTask StartAsync(CancellationToken)` and `UniTask ShutdownAsync(CancellationToken)`.

- [ ] **Step 0: Check the PlayMode assembly can see what these tests need**

```bash
cat Packages/com.echo.harness/Tests/PlayMode/Echo.Harness.Tests.PlayMode.asmdef
```

The new tests name `Echo.Harness.Bootstrap`, `Echo.Harness.Application`, `Echo.Harness.Infrastructure` and `Echo.Harness.TestKit`. Add whichever are missing to `references`.

This is safe: `verify-architecture.ps1` enumerates `Packages/com.echo.harness/Runtime` only, so a test asmdef is outside its tables and editing one does not touch the gate. Confirm that by re-reading the `$RuntimeAsmdefs` assignment rather than trusting this sentence. Note the precedent from the contract-typing iteration: the EditMode asmdef sets `overrideReferences: true`, and a missing `precompiledReferences` entry produced `CS0246` on types the `references` list already allowed — if the PlayMode asmdef does the same, check that list too.

- [ ] **Step 1: Write the failing tests**

Create `Packages/com.echo.harness/Tests/PlayMode/HarnessSessionDriverTests.cs`:

```csharp
using System;
using System.Collections;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Bootstrap;
using Echo.Harness.Infrastructure;
using Echo.Harness.TestKit;
using NUnit.Framework;
using UnityEngine;
using UnityEngine.TestTools;

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class HarnessSessionDriverTests
    {
        // The quiet path. An ordinary shutdown must produce no fault at all -
        // otherwise a log full of shutdown faults makes a real one invisible.
        [UnityTest]
        public IEnumerator TheOrdinaryShutdownPathPublishesNoFault()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new RecordingSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.From("example.invalid", 43966, "test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            yield return driver.ShutdownAsync(CancellationToken.None).ToCoroutine();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(faults, Is.Empty);
        }

        // An unconfigured start is the ordinary state of a machine that has not
        // opted in, and must not be a failure.
        [UnityTest]
        public IEnumerator AnUnconfiguredEndpointDoesNotStartAndDoesNotThrow()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new RecordingSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.NotConfigured("test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();

            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
            Assert.That(transport.State, Is.EqualTo(TransportState.Disconnected));
        }

        // The backstop. With the loop gone the hop cancels, and shutdown must still
        // finish in bounded time rather than parking forever.
        [UnityTest]
        public IEnumerator ShutdownFinishesEvenWhenTheSchedulerIsAlreadyLatched()
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new MainThreadSessionScheduler();
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var driver = new HarnessSessionDriver(
                session, EndpointResolution.From("example.invalid", 43966, "test"));

            yield return driver.StartAsync(CancellationToken.None).ToCoroutine();

            scheduler.LatchForShutdown();

            var finished = false;
            ShutdownAsync().Forget();

            async UniTaskVoid ShutdownAsync()
            {
                await driver.ShutdownAsync(CancellationToken.None);
                finished = true;
            }

            var deadline = Time.realtimeSinceStartup + 5f;
            while (!finished && Time.realtimeSinceStartup < deadline)
            {
                yield return null;
            }

            Assert.That(finished, Is.True, "shutdown must not park on a dead loop");
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }
    }
}
```

- [ ] **Step 2: Run to verify they fail**

Expected: compile error — `HarnessSessionDriver` does not exist.

- [ ] **Step 3: Implement the driver**

Create `Packages/com.echo.harness/Runtime/Bootstrap/HarnessSessionDriver.cs`:

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Infrastructure;
using UnityEngine;
using VContainer.Unity;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Starts the session when an endpoint is configured, and stops it before the
    /// player loop goes away.
    ///
    /// <para><b>Two shutdown paths, deliberately.</b> The ordered hook below runs
    /// while the loop is still alive, so every hop inside StopAsync behaves
    /// normally and an ordinary quit publishes no fault. The latch on
    /// MainThreadSessionScheduler is the backstop for the case where the loop is
    /// already gone - a domain reload, or a shutdown this hook did not see. With
    /// the latch alone every exit would travel the cancellation path, and a normal
    /// shutdown would be indistinguishable in the logs from a real failure.</para>
    /// </summary>
    public sealed class HarnessSessionDriver : IAsyncStartable, IDisposable
    {
        private readonly IProtocolSession session;
        private readonly EndpointResolution endpoint;
        private readonly CancellationTokenSource shutdown = new CancellationTokenSource();
        private bool hookInstalled;
        private bool shutdownStarted;

        public HarnessSessionDriver(IProtocolSession session, EndpointResolution endpoint)
        {
            this.session = session ?? throw new ArgumentNullException(nameof(session));
            this.endpoint = endpoint;
        }

        /// <summary>
        /// The token handed to the session is a SHUTDOWN token and nothing else.
        /// Cancelling it destroys the transport, because TcpTransport.ReceiveAsync
        /// closes the link on any cancellation - closing the socket is the only way
        /// this runtime can unpark a blocked read. Anything finer than "stop for
        /// good" needs a different mechanism.
        /// </summary>
        public async UniTask StartAsync(CancellationToken cancellation)
        {
            if (!endpoint.IsConfigured)
            {
                // Not a failure. It is the ordinary state of a machine that has not
                // opted in, and it matches how the end-to-end tier skips itself.
                Debug.Log(
                    $"[Harness] No server endpoint configured ({endpoint.Source}). " +
                    "The session stays disconnected. Set a host in the " +
                    $"{HarnessEndpointSettings.ResourcePath} asset or in " +
                    $"{ServerEndpoint.HostVariable}.");
                return;
            }

            Application.quitting += OnApplicationQuitting;
            hookInstalled = true;

            Debug.Log($"[Harness] Connecting to the server, endpoint from {endpoint.Source}.");
            await session.StartAsync(shutdown.Token);
        }

        public async UniTask ShutdownAsync(CancellationToken cancellationToken)
        {
            if (shutdownStarted)
            {
                return;
            }

            shutdownStarted = true;
            RemoveHook();

            try
            {
                await session.StopAsync(cancellationToken);
            }
            catch (OperationCanceledException)
            {
                // The backstop path: the scheduler was already latched, so the
                // teardown hop cancelled. StopAsync's own try/finally has still run
                // FailPendingRequests, so this is orderly rather than a failure -
                // and it is logged differently from the quiet path on purpose, so
                // the two are distinguishable after the fact.
                Debug.Log(
                    "[Harness] Shut down through the latched path; the player loop had " +
                    "already stopped.");
            }
        }

        private void OnApplicationQuitting() =>
            ShutdownAsync(CancellationToken.None).GetAwaiter().GetResult();

        private void RemoveHook()
        {
            if (!hookInstalled)
            {
                return;
            }

            Application.quitting -= OnApplicationQuitting;
            hookInstalled = false;
        }

        public void Dispose()
        {
            RemoveHook();
            shutdown.Cancel();
            shutdown.Dispose();
        }
    }
}
```

**Replace `Application.quitting` with whichever signal Task 1 measured as the last one firing while the loop still advances.** If the findings show `quitting` fires after the loop has stalled, this hook is the wrong one and the task must report that rather than ship it.

- [ ] **Step 4: Implement the scope**

Create `Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs`:

```csharp
using VContainer;
using VContainer.Unity;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// The app root scope, and the only one this iteration builds. Session and
    /// scene scopes are deferred: with one scene and no login flow, a child scope
    /// with one child and a lifetime identical to its parent is ceremony. See the
    /// design spec for the reasoning.
    /// </summary>
    public sealed class HarnessLifetimeScope : LifetimeScope
    {
        protected override void Configure(IContainerBuilder builder)
        {
            HarnessComposition.Configure(builder);
            builder.RegisterEntryPoint<HarnessSessionDriver>();
        }
    }
}
```

- [ ] **Step 5: Create the scene**

In the editor: File > New Scene (empty), add an empty GameObject named `HarnessLifetimeScope`, add the `HarnessLifetimeScope` component to it, save as `Assets/Scenes/Bootstrap.unity`, and add it to Build Settings as index 0.

- [ ] **Step 6: Run the PlayMode suite**

Expected: 7 + 3 = **10 pass, 0 fail**.

- [ ] **Step 7: Run the full gate**

Run: `pwsh Tools/ci/verify.ps1`
Expected: exit 0. Report the EditMode and PlayMode counts it prints.

- [ ] **Step 8: The manual acceptance check**

With a host configured in `Assets/Resources/HarnessEndpointSettings.asset`, open `Assets/Scenes/Bootstrap.unity` and press Play.

Expected, all of it observed rather than assumed:
1. Console logs `[Harness] Connecting to the server, endpoint from the HarnessEndpointSettings asset.`
2. The session reaches `Connected`.
3. Leaving play mode logs no error and no `SessionFault`.
4. The editor does not hang on exit.

Record the actual console output. If any of the four does not hold, that is the task's finding and it must be reported rather than worked around.

- [ ] **Step 9: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Bootstrap/ \
        Packages/com.echo.harness/Tests/PlayMode/HarnessSessionDriverTests.cs \
        Packages/com.echo.harness/Tests/PlayMode/HarnessSessionDriverTests.cs.meta \
        Assets/Scenes/ \
        ProjectSettings/EditorBuildSettings.asset
git commit -m "Give the stack a scene that starts it and a hook that stops it"
```

---

### Task 10: Let a context-less thread reach the write gate

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs:207,263`
- Modify: `Packages/com.echo.harness/Runtime/Application/HarnessPorts.cs` (`ITransport.ReceiveAsync` doc)
- Test: `Packages/com.echo.harness/Tests/EditMode/TcpTransportSendTests.cs`

**Interfaces:**
- Consumes: `TcpTransport(TcpTransportOptions, IElapsedTime)` (Task 4).
- Produces: no signature change.

**Verified fact:** UniTask 2.5.11 declares `public static UniTask AsUniTask(this Task task, bool useCurrentSynchronizationContext = true)` in `Runtime/UniTaskExtensions.cs`. Re-confirm against `Library/PackageCache/com.cysharp.unitask@*` before relying on it; do not take this plan's word for it.

- [ ] **Step 1: Write the failing test**

Add to `TcpTransportSendTests.cs`:

```csharp
        // Masked in production only because Unity's main thread always has a
        // SynchronizationContext. Task.AsUniTask() calls
        // TaskScheduler.FromCurrentSynchronizationContext() eagerly, which throws
        // InvalidOperationException when there is no current context - before the
        // write gate is taken, so the failure names nothing about the send.
        [Test]
        public void SendAsync_FromAThreadWithNoSynchronizationContextReachesTheGate()
        {
            var transport = new TcpTransport(
                new TcpTransportOptions { Host = "127.0.0.1", Port = 1 },
                new ManualTime(DateTimeOffset.UnixEpoch));

            var thrown = System.Threading.Tasks.Task.Run(() =>
            {
                Assert.That(
                    SynchronizationContext.Current,
                    Is.Null,
                    "the premise of this test is a thread with no context");

                try
                {
                    transport
                        .SendAsync(
                            new TransportMessage(MessageId.Ping, Array.Empty<byte>()),
                            CancellationToken.None)
                        .GetAwaiter()
                        .GetResult();
                    return (Exception)null;
                }
                catch (Exception ex)
                {
                    return ex;
                }
            }).GetAwaiter().GetResult();

            // A disconnected transport is expected to refuse the send. What must
            // NOT happen is the eager-scheduler InvalidOperationException, whose
            // message names a synchronization context rather than the transport.
            Assert.That(thrown, Is.Not.Null);
            Assert.That(
                thrown.Message,
                Does.Not.Contain("synchronization context").IgnoreCase,
                "the send failed on scheduler capture rather than on transport state");
        }
```

- [ ] **Step 2: Run to verify it fails**

Expected: FAIL — the caught exception's message names the synchronization context.

Record the exact message. **If it does not fail, the residual does not reproduce on this Unity version, and that is the finding** — report it rather than applying a fix for a defect you could not observe.

- [ ] **Step 3: Apply the fix**

At `:207`:

```csharp
            // useCurrentSynchronizationContext: false, because the default calls
            // TaskScheduler.FromCurrentSynchronizationContext() eagerly and throws
            // on a thread that has none - before this gate is taken, so the caller
            // gets a bookkeeping failure in place of a transport one. Nothing here
            // needs the caller's context: the continuation only takes the gate, and
            // everything it then touches is either volatile or under the gate.
            await sendGate.WaitAsync(cancellationToken).AsUniTask(false);
```

At `:263`, `FlushAsync` returns a `Task` and takes the same overload:

```csharp
                    await active.FlushAsync(cancellationToken).AsUniTask(false);
```

`WriteAsync` at `:260-262` returns a `ValueTask`, whose `AsUniTask` has no such overload. Leave it, and say so in the commit message rather than leaving a reader to wonder why the two lines differ.

- [ ] **Step 4: Run the EditMode suite**

Expected: 178 + 1 = **179 pass, 0 fail**.

- [ ] **Step 5: State the receive-cancellation contract**

In `HarnessPorts.cs`, add above `ITransport.ReceiveAsync`:

```csharp
        /// <summary>
        /// <b>Cancelling a receive means abandoning the link.</b> Implementations
        /// close the connection on any cancellation of this token, because closing
        /// the socket is the only way this runtime can unpark a blocked read. That
        /// is the contract rather than an implementation accident: a caller must
        /// not cancel a receive to pause reading, to apply backpressure, or to
        /// impose a per-message deadline, because all three destroy the transport
        /// as a side effect.
        ///
        /// <para>ProtocolSession honours this by cancelling only through
        /// CancelPump(), which is teardown. That is correct today partly by luck -
        /// it has had no other reason to cancel - so the constraint is written here
        /// rather than left to be rediscovered by whoever adds the first one.</para>
        /// </summary>
        UniTask<TransportMessage> ReceiveAsync(CancellationToken cancellationToken);
```

- [ ] **Step 6: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/TcpTransport.cs \
        Packages/com.echo.harness/Runtime/Application/HarnessPorts.cs \
        Packages/com.echo.harness/Tests/EditMode/TcpTransportSendTests.cs
git commit -m "Stop a send failing for a reason that has nothing to do with sending"
```

---

### Task 11: Report the teardown hop's off-context bookkeeping

**Files:**
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs:320-332`
- Modify: `Packages/com.echo.harness/TestKit/DeterministicFakes.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs`

**Interfaces:**
- Consumes: `SessionFault`, `SessionFaultKind` (existing); `ManualTime` (Task 2).
- Produces: `Echo.Harness.TestKit.ThrowingSessionScheduler(Exception failure)`.

`SwitchToSessionContextForTeardownAsync` swallows every exception so nothing outranks the failure being reported to the caller. That reasoning is correct and stays. What is missing is that a **non-cancellation** failure leaves the `finally` un-registering the gate entry from the wrong context with nothing recorded anywhere.

- [ ] **Step 1: Add the test double**

Add to `DeterministicFakes.cs`:

```csharp
    /// <summary>
    /// A scheduler whose hop always fails with a supplied exception, for pinning
    /// what the session does when the context it needs is unreachable.
    /// </summary>
    public sealed class ThrowingSessionScheduler : ISessionScheduler
    {
        private readonly Exception failure;

        public ThrowingSessionScheduler(Exception failure)
        {
            this.failure = failure ?? throw new ArgumentNullException(nameof(failure));
        }

        public UniTask SwitchToSessionContextAsync(CancellationToken cancellationToken) =>
            UniTask.FromException(failure);
    }
```

- [ ] **Step 2: Write the failing test**

Add to `ProtocolSessionRequestTests.cs`:

```csharp
        // A cancelled teardown hop is orderly and must stay silent - that is the
        // shutdown path, and a fault there would make every quit look like a
        // failure. A hop that fails for any OTHER reason is a real anomaly: the
        // finally still un-registers the request gate while running off the
        // session's context, and until now nothing recorded that it happened.
        [Test]
        public void AFailingTeardownHopIsReportedButACancelledOneIsNot()
        {
            var fromFailure = RunTeardownHopWith(new InvalidOperationException("no player loop"));
            var fromCancel = RunTeardownHopWith(new OperationCanceledException());

            Assert.That(fromFailure.Count, Is.EqualTo(1));
            Assert.That(fromFailure[0].Kind, Is.EqualTo(SessionFaultKind.SubscriberFailure));
            Assert.That(fromFailure[0].Diagnostic, Does.Contain("off the session context"));

            Assert.That(fromCancel, Is.Empty);
        }

        private static List<SessionFault> RunTeardownHopWith(Exception hopFailure)
        {
            var transport = new FakeTransport();
            var time = new ManualTime(DateTimeOffset.UnixEpoch);
            var scheduler = new ThrowingSessionScheduler(hopFailure);
            using var session = new ProtocolSession(transport, time, time, scheduler);

            var faults = new List<SessionFault>();
            session.SubscribeToFaults(faults.Add);

            session.StartAsync(CancellationToken.None).GetAwaiter().GetResult();

            var request = session.RequestAsync<LoginResponseDto>(
                MessageId.LoginRequest,
                new LoginRequestDto { Username = "u", Password = "p" },
                TimeSpan.FromMilliseconds(20),
                CancellationToken.None);

            try
            {
                request.AsTask().GetAwaiter().GetResult();
            }
            catch (TimeoutException)
            {
                // Expected. The point of this test is what the hop did, not what
                // the caller received.
            }

            return faults;
        }
```

`.AsTask()` before `.GetAwaiter().GetResult()` is required here for the same reason Task 6 of the session-layer iteration needed it: UniTask's own awaiter refuses to block, and this is a genuinely asynchronous completion.

If `LoginRequestDto`'s property names differ from `Username`/`Password`, use the real ones — check `Runtime/Contracts/Dtos/AuthDtos.cs` rather than guessing.

- [ ] **Step 3: Run to verify it fails**

Expected: FAIL — `fromFailure` is empty, because the catch publishes nothing today.

- [ ] **Step 4: Implement**

Replace the catch at `:326-331`:

```csharp
            catch (OperationCanceledException)
            {
                // Orderly. This is the shutdown path - the scheduler is latched
                // because the player loop is going away - and a fault here would
                // make every ordinary quit look like a failure.
            }
            catch (Exception ex)
            {
                // Still swallowed, for the reason above: nothing here may outrank
                // the failure being reported to the caller. But it is no longer
                // silent. The finally that follows un-registers this request's gate
                // entry while running off the session's context, and a reader of
                // faultHandlers is the only one who can see that happened.
                PublishFault(new SessionFault(
                    SessionFaultKind.SubscriberFailure,
                    default,
                    "A request's teardown hop failed, so its gate entry was un-registered " +
                    $"off the session context: {ex.GetType().Name}: {ex.Message}"));
            }
```

`default` for the `MessageId`, because this failure belongs to no single message. If a more suitable `SessionFaultKind` member already exists, use it and say why in the commit message — do not add an enum member for this without raising it first.

- [ ] **Step 5: Run the EditMode suite**

Expected: 179 + 1 = **180 pass, 0 fail**.

- [ ] **Step 6: Update the method's doc comment**

Its existing paragraph "The failure a swallow cannot reach" describes a hop that never completes and cites "nothing in production constructs MainThreadSessionScheduler" as why the exposure is narrow. **Task 8 makes that false** — the composition root now constructs one. Rewrite that clause: the exposure is no longer narrow, and what closes it is the Task 7 latch, which converts the never-completes case into the cancellation this method now catches explicitly.

- [ ] **Step 7: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/TestKit/DeterministicFakes.cs \
        Packages/com.echo.harness/Tests/EditMode/ProtocolSessionRequestTests.cs
git commit -m "Let a failed teardown hop leave a trace of what it did anyway"
```

---

### Task 12: Prove it end to end, then land the documentation

**Files:**
- Modify: `Packages/com.echo.harness/Tests/EditMode/GoServerEndToEndTests.cs`
- Modify: `docs/verification-matrix.md`
- Modify: `docs/migration-checklist.md`
- Delete: `.superpowers/sdd/2026-07-30-tcp-transport-and-session-concurrency/`

**Interfaces:**
- Consumes: everything.
- Produces: the iteration's tracked record.

- [ ] **Step 1: Add the composed-graph end-to-end test**

Add to `GoServerEndToEndTests.cs`:

```csharp
        // Every other end-to-end case builds its own objects. This one resolves
        // them from the real composition root, so it is the only test that can fail
        // when the WIRING is wrong rather than the protocol.
        [UnityTest]
        public IEnumerator TheComposedGraphConnectsAndProbes()
        {
            if (!RemoteServerEndpoint.TryResolve(out var endpoint))
            {
                Assert.Ignore(SkipReason);
                yield break;
            }

            var builder = new ContainerBuilder();
            HarnessComposition.Configure(
                builder,
                EndpointResolution.From(endpoint.Host, endpoint.Port, "the end-to-end tier"));
            using var container = builder.Build();

            var session = container.Resolve<IProtocolSession>();

            yield return session.StartAsync(CancellationToken.None).ToCoroutine();
            Assert.That(session.State, Is.EqualTo(SessionState.Connected));

            var probe = session.ProbeRoundTripAsync(CancellationToken.None);
            yield return probe.ToCoroutine();

            Assert.That(probe.GetAwaiter().GetResult(), Is.GreaterThan(TimeSpan.Zero));

            yield return session.StopAsync(CancellationToken.None).ToCoroutine();
            Assert.That(session.State, Is.EqualTo(SessionState.Disconnected));
        }
```

Add `using Echo.Harness.Bootstrap;` and `using VContainer;` to the file.

- [ ] **Step 2: Run it both ways**

With `ECHO_SERVER_HOST` set: expect **PASS**. Without it: expect **Skipped**, and the gate still exit 0.

Both runs are required. The unconfigured path is CI's permanent state, and the previous iteration discovered it had never once been exercised.

- [ ] **Step 3: Update `docs/verification-matrix.md`**

Add rows for the new EditMode and PlayMode tiers. Extend the local-only paragraph to say plainly that the composed-graph end-to-end test and the manual press-Play check both require an endpoint CI does not have, so neither is gate-enforced.

- [ ] **Step 4: Update `docs/migration-checklist.md`**

Tick, with one sentence each on what actually closed it:
- production `IClock` / session-stack production wiring
- caller-cancelling-a-receive semantics
- the scheduler's stall failure mode
- `SendAsync` with no `SynchronizationContext`
- the request-timeout hop's non-cancellation path

Annotate rather than tick: "Define app/session/scene VContainer lifetime scopes" — the app root scope exists; session and scene scopes are deferred to Phase 2, with the reason.

- [ ] **Step 5: Remove the previous iteration's stale workspace**

First confirm its content already reached tracked docs:

```bash
git show 6ef2b63 --stat
```

Then, only if that confirms it:

```bash
rm -rf .superpowers/sdd/2026-07-30-tcp-transport-and-session-concurrency/
```

- [ ] **Step 6: Run the full gate**

Run: `pwsh Tools/ci/verify.ps1`
Expected: exit 0. Report every count it prints.

- [ ] **Step 7: Commit**

```bash
git add Packages/com.echo.harness/Tests/EditMode/GoServerEndToEndTests.cs \
        docs/verification-matrix.md docs/migration-checklist.md
git commit -m "Prove the composed graph talks to the real server, and record it"
```

---

## Expected final state

- EditMode **181** (155 + 26), PlayMode **10** (5 + 5).
- `pwsh Tools/ci/verify.ps1` exit 0; `pwsh Tools/ci/verify-architecture.ps1` exit 0 with its tables unedited.
- `Assets/Scenes/Bootstrap.unity` committed; `Assets/Resources/HarnessEndpointSettings.asset` ignored.
- No routable address in any tracked file.
- `docs/findings/2026-08-02-unity-shutdown-callback-order.md` records what was measured and what was not.
