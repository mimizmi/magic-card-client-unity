# Login Vertical Slice — Design

Date: 2026-08-14
Status: approved for planning
Predecessor: `docs/superpowers/specs/2026-08-02-production-wiring-and-lifecycle-design.md`

## Problem

The composition root builds a session that connects to the real server, and a
person can watch it do so. Nothing above the session exists. There is no use
case, no view, and no view-model that displays anything a server said.

Three facts bound what this iteration actually has to build, all read from the
source rather than assumed, because the checklist item that names this work —
"Implement typed login DTOs and one login use case" — is already half done.

**The typed login DTOs exist.** `LoginRequestDto` and `LoginResponseDto` are in
`Runtime/Contracts/Dtos/AuthDtos.cs`, complete with the `DefaultValueHandling`
that reproduces Go's `omitempty` for a `bool`. `MessageId.LoginRequest = 1001`
and `LoginResponse = 1002` are declared, and `ProtocolMessageMap.cs:23,24`
register both types while `:89` registers the request-to-response pairing. The
contract-typing iteration did this work.

**Login already succeeds over a real socket.**
`GoServerEndToEndTests.LoginOverARealSocketReturnsATypedResponse` issues
`session.RequestAsync<LoginResponseDto>(MessageId.LoginRequest, …)` against the
remote Go server and asserts a typed response. The wire is proved. What is
unproved is every layer above it.

**Nothing consumes faults.** `IProtocolSession.SubscribeToFaults` has no
production subscriber, so `ProtocolSession.PublishFault` (`:970-984`) iterates an
empty handler list. Seven `SessionFaultKind` values are produced and none is ever
read.

So the content of this iteration is the use case, the view/view-model pair, the
fault sink, and three decisions that previous iterations deferred.

## Acceptance

A person opens `Assets/Scenes/Bootstrap.unity`, presses Play, and:

1. sees a connection status that reaches "connected";
2. types a player name into a field that is disabled until then;
3. presses a button and sees the `player_id` the real server returned, or the
   reason it refused;
4. leaves play mode with no console error and no unexplained `SessionFault`.

The UI is plain on purpose. No art, no layout polish, no reconnect flow. The
deliverable is a live chain — view to view-model to use case to session to a real
server — not a login screen anyone would ship.

## Constraints that shaped every decision below

**Presentation may reference only `Echo.Harness.Application` and `R3.Unity`** as
pinned today. The architecture gate pins that list at
`Tools/ci/verify-architecture.ps1:117-118` and pins its assembly flags at
`:173-177`. (This iteration drops `R3.Unity` from both — see below — which
narrows the list rather than widening it, so the two consequences here survive
the change.) Two consequences follow and neither is negotiable without editing
the gate:

- A view-model **cannot name `LoginResponseDto`**, because Contracts is not on
  that list. Anything crossing into Presentation must be an Application type.
- Presentation **cannot reference VContainer**, so an `[Inject]`-decorated
  MonoBehaviour cannot live there.

**Application may not name `UnityEngine`.** The gate asserts it by source text at
`:345`. Any logging implementation lives in Infrastructure.

**Fault handlers can run on a thread that is not the main thread.**
`ProtocolSession.PublishFault` invokes each handler synchronously on the calling
thread, and the class documents `FaultTheStreamAsync` publishing "from the very
thread a failing pump hop could not leave" (`:389-390`). This is the deferred
review finding R3, and this iteration closes it rather than deferring it again.

**`PublishFault` swallows handler exceptions** (`:978-982`, "there is nowhere left
to report it"). A fault sink that throws fails silently, so the sink must catch
its own failures.

## Placement

| Type | Assembly | Why there |
|---|---|---|
| `LoginResult`, `LoginOutcome`, `ILoginUseCase`, `LoginUseCase` | `Echo.Harness.Application` | Presentation must see the result type, and Presentation sees only Application. |
| `ISessionStatus` | `Echo.Harness.Application` | A read-only port over session state; see below. |
| `FaultSeverity`, `IFaultLog`, `SessionFaultRouter` | `Echo.Harness.Application` | The routing and de-duplication are policy, not platform. |
| `UnityFaultLog` | `Echo.Harness.Infrastructure` | It calls `UnityEngine.Debug`, which the gate bans in Application. |
| `LoginViewModel` | `Echo.Harness.Presentation` | Plain C#, `[CreateProperty]`, no engine UI types. |
| `LoginView` | `Echo.Harness.Bootstrap` | It is a MonoBehaviour and needs `[Inject]`; Bootstrap is the only assembly with both VContainer and Presentation. |
| `SessionFaultRouterEntryPoint` | `Echo.Harness.Bootstrap` | It implements VContainer's `IStartable`, which Application may not name. |

Splitting the view from its view-model across two assemblies is a real cost and
is accepted deliberately. The alternative — adding VContainer to Presentation —
was rejected because it makes the DI container reachable from every future
view-model, which is a permanent loosening bought for one file's tidiness.

## The login use case

```csharp
public enum LoginResult { Succeeded, Rejected, NoAnswer }

public readonly struct LoginOutcome
{
    public LoginResult Result { get; }
    public string PlayerId { get; }   // set only when Succeeded
    public bool InGame { get; }
    public string Message { get; }    // the reason, when Rejected or NoAnswer
}

public interface ILoginUseCase
{
    UniTask<LoginOutcome> LoginAsync(string playerName, CancellationToken cancellationToken);
}
```

Three results rather than two. "The server refused" and "the server never
answered" lead to different next actions and must not collapse into one boolean.

### The exception policy

The rule is that the use case converts **outcomes of trying to log in** and does
not convert **the system being broken**.

| Thrown | Handling | Reason |
|---|---|---|
| `TimeoutException` (`ProtocolSession.cs:334`) | → `NoAnswer` | No `LoginResponse` inside the deadline is an outcome of the attempt. |
| `RequestAlreadyInFlightException` | → `NoAnswer` | A double-click. `CanSubmit` should prevent it; this is defence in depth. |
| `OperationCanceledException` | **rethrown** | Shutdown cancellation is not a login result. Swallowing it disguises quitting as a failed login. |
| anything else | **rethrown** | A broken transport must not be dressed up as a clean refusal. |

The cost is stated rather than hidden: the view-model therefore needs a
catch-all. That is not laziness. The submit path is a fire-and-forget triggered by
a button, so without a catch the exception reaches UniTask's unobserved-exception
handler and the user sees nothing happen at all.

### `reconnect_token` is read and dropped

`LoginResponseDto.ReconnectToken` exists and this iteration does not persist it.
`LoginOutcome` carries no such field; `LoginUseCase` reads the response and
discards the token, with a comment saying so. It is not stashed in memory "for
later" — that would be a speculative store with no reader. A checklist item
records the gap.

## The fault sink

```csharp
public enum FaultSeverity { Info, Warning, Error }

public interface IFaultLog
{
    void Write(FaultSeverity severity, SessionFault fault);
}
```

`SessionFaultRouter` (Application) subscribes to `IProtocolSession.SubscribeToFaults`
in its constructor and fans out:

```csharp
public sealed class SessionFaultRouter : IDisposable
{
    public SessionFaultRouter(IProtocolSession session, ISessionScheduler scheduler, IFaultLog log);

    /// Delivered on the session context. Only the two connection kinds arrive.
    public IDisposable ObserveConnectionFaults(Action<SessionFault> handler);
}
```

**Its two halves have deliberately different threading, and the difference is the
design.**

- **Logging is synchronous, on whichever thread the fault arrived on.**
  `UnityEngine.Debug` is safe to call from any thread, and fault logs matter most
  on the shutdown path — the one `HarnessSessionDriver` documents as having no
  further player-loop tick. A log that hopped first would never be emitted there.
- **UI delivery hops first**, via the existing `ISessionScheduler.SwitchToSessionContextAsync`.
  This is what closes review finding R3. Because the subscription signature is a
  synchronous `Action<SessionFault>`, this half is necessarily a fire-and-forget
  `UniTaskVoid`, and it carries its own `try`/`catch`: `PublishFault` will not
  report a failure for it.

Only `TransportFailure` and `DispatchFailure` reach the UI. The other five kinds
have no interface element that could express them while a login screen is the
only screen, and inventing one would be UI written for no reader.

### `NoDestination` is de-duplicated at the sink, not at the source

`ProtocolSession` keeps publishing a fault for every unrouted message. That
contract is deliberate — `IProtocolSession.Subscribe`'s own documentation calls it
the mechanism by which a late subscription becomes visible — and this iteration
does not weaken it. The router keeps a `HashSet<MessageId>` and forwards the
first `NoDestination` per message id only.

The set is guarded by a `lock`. This is not defensive habit: de-duplication sits
on the synchronous logging half, which runs on whatever thread published the
fault, so the set is genuinely reachable from more than one thread.

De-duplicating at the sink rather than in the session keeps the discarded
information recoverable — a future diagnostics view can subscribe to the session
directly and see every occurrence.

### The router must be resolved, not merely registered

This is the trap `HarnessComposition.Configure`'s own summary names: *"Registering
is not resolving. Every Register call below is lazy, so nothing is constructed
until something asks."* A router that subscribes in its constructor and that
nothing resolves is a fault sink that never sees a fault, and every test that
constructs it directly would still pass.

It cannot solve this itself. VContainer's entry-point interfaces are VContainer
types, and Application may not reference VContainer.

So Bootstrap carries a deliberately empty adapter:

```csharp
// Exists to force resolution and nothing else. SessionFaultRouter subscribes in
// its constructor, and VContainer will never construct it unless something asks.
public sealed class SessionFaultRouterEntryPoint : IStartable
{
    public SessionFaultRouterEntryPoint(SessionFaultRouter router) { }
    public void Start() { }
}
```

An empty class is worth more than the alternatives considered. Hanging the router
off `LoginViewModel`'s constructor would work today and break silently the first
time someone removes an unused parameter. Hanging it off `HarnessSessionDriver`
would give that class a constructor argument it never uses, which invites the same
cleanup. This one is named for its only job.

A test asserts the entry point is registered. Without it, deleting the
`RegisterEntryPoint` line leaves every other test green.

## Session state reaches the view-model through a narrow port

`IProtocolSession` exposes `SessionState State { get; }` and **no state-changed
event**, so connection status can only be polled.

```csharp
public interface ISessionStatus
{
    SessionState State { get; }
}
```

`ProtocolSession` implements it with the property it already has, and
`HarnessComposition.Configure` adds `.As<ISessionStatus>()` to the existing
registration.

The alternative was injecting `IProtocolSession` into the view-model, which costs
nothing to write. It was rejected because it leaves every view-model able to call
`SendAsync` directly and bypass the use-case layer, and the architecture gate
cannot see that class of bypass. Six lines buy a boundary that is structural
rather than advisory.

`CompositionSmokeTests.HarnessComposition_RegistersTheSessionAsASingleton` gains
an assertion that both interfaces resolve to the same instance. Without it, a
second registration that accidentally built a second `ProtocolSession` — and
therefore a second `TcpTransport` — would pass.

## The view-model and the view

`LoginViewModel` is a plain class with `[CreateProperty]` properties and
`INotifyBindablePropertyChanged`, matching the only precedent in the repository:
`HarnessHealthViewModel` and the `dataSource` binding already exercised by
`HarnessPlayerLoopTests.cs:20`.

| Property | Meaning |
|---|---|
| `PlayerName` | two-way bound to the text field |
| `ConnectionStatus` | a human string derived from `ISessionStatus.State` |
| `CanSubmit` | `Connected && !IsBusy && PlayerName` is not blank |
| `IsBusy` | a request is in flight |
| `ResultText` | the login outcome, or the caught failure |
| `ConnectionFaultText` | the last connection fault from the router |

**`ResultText` and `ConnectionFaultText` are separate properties on purpose.** A
transport fault and a login refusal arrive from different sources at unrelated
times, and a single field would let whichever landed second erase the other — so
a dropped connection could silently overwrite the very rejection message the user
was reading.

```csharp
public LoginViewModel(ISessionStatus status, ILoginUseCase login, SessionFaultRouter faults);
```

The router is a constructor dependency here for the ordinary reason — the
view-model calls `ObserveConnectionFaults` — and **not** as the thing that forces
the router to be resolved. That job belongs to the entry point above, so that
removing this parameter degrades the UI without silently disabling fault logging.

Two methods: `Refresh()` re-reads `ISessionStatus.State` and raises change
notifications only when something actually changed; `SubmitAsync()` runs the use
case and sets `ResultText`.

Polling lives in `Refresh()` on the view-model rather than in the MonoBehaviour so
that all of it is reachable from EditMode tests, which already reference
Presentation. The MonoBehaviour is left with three jobs and no logic.

```csharp
[SerializeField] private UIDocument document;
[Inject] public void Construct(LoginViewModel vm) => viewModel = vm;

private void Start()
{
    var root = document.rootVisualElement;
    root.dataSource = viewModel;
    root.Q<Button>("submit").clicked += OnSubmit;
}

private void Update() => viewModel.Refresh();
private void OnSubmit() => viewModel.SubmitAsync().Forget();
```

**Binding happens in `Start`, not `OnEnable`, and the reason is ordering rather
than taste.** VContainer's hierarchy injection completes inside
`LifetimeScope.Awake`, which is guaranteed to precede `Start`. Nothing guarantees
it precedes `OnEnable`, and `document.rootVisualElement` is null before
`UIDocument`'s own `OnEnable` has run.

`HarnessLifetimeScope.Configure` gains `builder.RegisterComponentInHierarchy<LoginView>()`.

## Assets

- `Packages/com.echo.harness/UI/Login.uxml` — layout only: `player-name`,
  `submit`, `connection-status`, `result`. It lives in the package because
  `LoginView.cs` does, and separating a view from its layout across the
  package/Assets boundary is worse than either placement alone.
- `Assets/UI/HarnessPanelSettings.asset` — a project-level render setting, and
  **committed**, unlike `HarnessEndpointSettings.asset`, whose ignore rule is
  unqualified.
- `Assets/Scenes/Bootstrap.unity` — one new GameObject carrying `UIDocument` and
  `LoginView`.

Bindings are declared in UXML with `data-source-path`. If that proves awkward in
practice the fallback is explicit `SetBinding` calls in `LoginView`; that choice
is made from a measurement, not guessed at now.

## R3 is removed from Presentation

`R3.Unity` is referenced by `Echo.Harness.Presentation` and used nowhere in the
`Runtime` tree — the only `using R3;` in the repository are in the two test
assemblies. This design does not need it: `INotifyBindablePropertyChanged` covers
change notification, and the reactive alternative would bypass the `dataSource`
binding the repository has already committed to.

It is removed from `Echo.Harness.Presentation.asmdef` and from the gate's pinned
list at `verify-architecture.ps1:117-118`.

Nothing breaks. `Echo.Harness.Tests.EditMode` and `Echo.Harness.Tests.PlayMode`
both reference `R3.Unity` **directly**, and the gate deliberately does not pin
test-assembly references (`References = $null` at `:288` and `:294`, with the
reasoning in the comment above them). `ThirdPartyPackageSmokeTests` continues to
verify the R3 package itself.

## Scopes: still one, with a written trigger

`HarnessLifetimeScope` remains the only scope. The login use case, the
view-model, the fault router and the log all register in it.

The reason the deferral still holds is narrower than it was: the login screen in
this iteration never goes away, so a child scope would have a lifetime identical
to its parent — the same ceremony the production-wiring spec rejected.

What changes is that the deferral stops being open-ended. `docs/migration-checklist.md`
records the trigger: **the first screen that is destroyed while the application
keeps running forces a UI scope, and the first flow that must survive a logout
without reusing the same `ProtocolSession` forces a session scope.** Until one of
those exists there is nothing for a second scope to mean.

`CompositionSmokeTests` keeps its comment about `Singleton` versus `Scoped`; it is
still the warning that matters on the day the trigger fires.

## Testing

**EditMode.** All three new units are testable without a player loop, because the
EditMode assembly already references Presentation, Bootstrap and TestKit.

- `LoginUseCaseTests` — every row of the exception-policy table against a fake
  session, plus the success and rejection mappings, plus the assertion that
  `reconnect_token` does not appear in `LoginOutcome`.
- `SessionFaultRouterTests` — severity mapping; `NoDestination` forwarded once per
  message id and logged once; only the two connection kinds reach the UI observer;
  the log written without a hop while the UI observer is delivered after one,
  measured with the existing `RecordingSessionScheduler`; and a throwing observer
  not preventing the log.
- `LoginViewModelTests` — `CanSubmit` across state and input combinations,
  `Refresh` raising notifications only on real change, the catch-all turning a
  rethrown transport failure into visible text, and a connection fault arriving
  mid-read leaving `ResultText` untouched.
- `CompositionSmokeTests` — extended for `ISessionStatus` resolving to the same
  instance as `IProtocolSession`, for the new registrations, and for
  `SessionFaultRouterEntryPoint` being registered as an entry point. That last one
  is the only thing standing between a deleted `RegisterEntryPoint` line and a
  fault sink that is never constructed.

**PlayMode.** One test that the `dataSource` binding and the button wiring hold
through a player-loop frame, in the shape `HarnessPlayerLoopTests` already uses.

**Manual acceptance.** The four numbered steps under **Acceptance**, recorded in
`docs/verification-matrix.md` alongside the existing manual check, with the same
caveat: nothing in the gate runs it and nothing notices if it is never run again.

**Mutation checks to run and record**, chosen where a green suite would otherwise
prove nothing: delete the hop in the router's UI half and confirm a test fails;
break the `.As<ISessionStatus>()` registration and confirm only the new
composition assertion fails; make `CanSubmit` always true and confirm a
view-model test fails rather than only the manual run; and delete the
`RegisterEntryPoint<SessionFaultRouterEntryPoint>` line and confirm a test fails
rather than the suite staying green over a sink that is never constructed.

## Out of scope

- Reconnect, token persistence, and the reconnect path through `LoginRequestDto.ReconnectToken`.
- Queue and match-found flow, the authoritative room, and player-specific state —
  the remaining Phase 2 checklist items.
- Any visual design: no USS beyond what makes the four elements legible.
- A diagnostics surface for the five fault kinds that do not reach the UI.
- Child scopes of any kind.
- The intermittent `MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread`
  failure. It stays open and undiagnosed; this iteration does not touch the hop.
  Note the adjacency honestly: the router's UI half awaits the same
  `ISessionScheduler` hop, so a run of this iteration's PlayMode suite can go red
  for that reason and it must not be mistaken for a defect in this work.
