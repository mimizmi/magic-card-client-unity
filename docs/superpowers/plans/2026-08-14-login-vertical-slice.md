# Login Vertical Slice Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Build a live chain from a UI Toolkit login screen down to the real Go server — view, view-model, use case, fault sink — and settle the three decisions earlier iterations deferred.

**Architecture:** Clean Architecture across six pinned assemblies. The use case returns an Application-level `LoginOutcome` because Presentation cannot see Contracts; the view lives in Bootstrap because Presentation cannot see VContainer. A `SessionFaultRouter` logs synchronously on the publishing thread and delivers to the UI only after hopping through `ISessionScheduler`.

**Tech Stack:** Unity 6000.2.7f2, C#, UniTask 2.5.11, VContainer, UI Toolkit runtime data binding (`Unity.Properties`), Newtonsoft.Json, NUnit / Unity Test Framework.

**Spec:** `docs/superpowers/specs/2026-08-14-login-vertical-slice-design.md`

## Global Constraints

- **Assembly references are pinned by the architecture gate.** `Tools/ci/verify-architecture.ps1:110-127` holds the expected reference list for every runtime assembly. Any change to an `.asmdef` reference list **must** be mirrored there in the same commit or the gate fails.
- **`Echo.Harness.Application` may not name `UnityEngine`, `Addressables`, `R3`, `VContainer` or `XLua`.** Asserted by source text at `verify-architecture.ps1:345`. It also carries `"noEngineReferences": true`.
- **`Echo.Harness.Presentation` may reference only `Echo.Harness.Application`** after this plan (R3 is dropped in Task 5). It cannot see `Echo.Harness.Contracts`, so no wire DTO may appear in its public or private surface.
- **`Echo.Harness.Bootstrap`** is the only assembly referencing both `Echo.Harness.Presentation` and `VContainer`. Every MonoBehaviour and every `IStartable` goes there.
- **`Echo.Harness.TestKit`** references `Domain, Contracts, Application, Infrastructure, UniTask` and is pinned at `verify-architecture.ps1:276-284`. It may **not** reference Presentation or Bootstrap. Do not add references to it.
- **EditMode async tests use `[Test]` with `.GetAwaiter().GetResult()`.** This is the established pattern (`ProtocolSessionRequestTests.cs:25,43,66`) and is safe **only** because the fakes complete synchronously. `GetResult()` on an incomplete UniTask throws `InvalidOperationException` and returns the promise to the pool underneath the still-running continuation — see the class summary on `HarnessSessionDriver`. Never use it on anything a real socket drives.
- **Run the gate before every commit:** `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`. Expected tail: `Architecture verification passed.` and `protocol fixture matches the Go source (39 messages)`.
- **`MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread` is intermittently red** (once in five batch runs, about one in ten connected). It is unrelated to this work and must not be "fixed" here. If a PlayMode run goes red only on that test, re-run; see `docs/verification-matrix.md`.

## File Structure

**Create:**

| File | Responsibility |
|---|---|
| `Packages/com.echo.harness/Runtime/Application/Session/ISessionStatus.cs` | Read-only session state port |
| `Packages/com.echo.harness/Runtime/Application/Login/LoginOutcome.cs` | `LoginResult` enum + `LoginOutcome` result type |
| `Packages/com.echo.harness/Runtime/Application/Login/ILoginUseCase.cs` | The use-case port |
| `Packages/com.echo.harness/Runtime/Application/Login/LoginUseCase.cs` | Request/response mapping and the exception policy |
| `Packages/com.echo.harness/Runtime/Application/Session/SessionFaultDiagnostics.cs` | `FaultSeverity`, `IFaultLog` |
| `Packages/com.echo.harness/Runtime/Application/Session/SessionFaultRouter.cs` | Subscription, de-duplication, severity, thread split |
| `Packages/com.echo.harness/Runtime/Infrastructure/UnityFaultLog.cs` | `IFaultLog` over `UnityEngine.Debug` |
| `Packages/com.echo.harness/Runtime/Bootstrap/SessionFaultRouterEntryPoint.cs` | Forces the router to be constructed |
| `Packages/com.echo.harness/Runtime/Presentation/LoginViewModel.cs` | Bindable state and the submit command |
| `Packages/com.echo.harness/Runtime/Bootstrap/LoginView.cs` | `UIDocument` binding and button wiring |
| `Packages/com.echo.harness/UI/Login.uxml` | Layout only |
| `Packages/com.echo.harness/TestKit/FakeProtocolSession.cs` | A synchronous `IProtocolSession` with a fault trigger |
| `Packages/com.echo.harness/TestKit/RecordingFaultLog.cs` | Records severity, fault and calling thread |
| `Packages/com.echo.harness/TestKit/LoginTestDoubles.cs` | `FakeSessionStatus`, `FakeLoginUseCase` |
| `Packages/com.echo.harness/Tests/EditMode/LoginUseCaseTests.cs` | The exception-policy table |
| `Packages/com.echo.harness/Tests/EditMode/SessionFaultRouterTests.cs` | Severity, de-duplication, thread split |
| `Packages/com.echo.harness/Tests/EditMode/LoginViewModelTests.cs` | `CanSubmit`, notifications, catch-all |
| `Packages/com.echo.harness/Tests/PlayMode/LoginViewTests.cs` | Binding survives a player-loop frame |
| `Assets/UI/HarnessPanelSettings.asset` | Project-level panel render settings |

**Modify:**

| File | Change |
|---|---|
| `Runtime/Application/Session/ProtocolSession.cs:9` | Add `ISessionStatus` to the implements list |
| `Runtime/Bootstrap/HarnessComposition.cs:77` | `.As<ISessionStatus>()`; register log, router, use case, view-model |
| `Runtime/Bootstrap/HarnessLifetimeScope.cs:23-27` | Register the entry point and the view component |
| `Runtime/Presentation/Echo.Harness.Presentation.asmdef` | Drop `R3.Unity` |
| `Tools/ci/verify-architecture.ps1:117-118` | Drop `R3.Unity`; add the `HarnessLifetimeScope` source assertion |
| `Tests/EditMode/CompositionSmokeTests.cs` | New registrations and the same-instance assertion |
| `Assets/Scenes/Bootstrap.unity` | One GameObject with `UIDocument` + `LoginView` |
| `docs/verification-matrix.md` | New rows and the manual acceptance procedure |
| `docs/migration-checklist.md` | Tick the login items; record the scope trigger and the token gap |

---

### Task 1: The narrow session-status port

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Application/Session/ISessionStatus.cs`
- Modify: `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs:9`
- Modify: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs:77`
- Test: `Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs`

**Interfaces:**
- Consumes: `SessionState` (`Runtime/Application/Session/SessionDiagnostics.cs:6-12`), `ProtocolSession.State` (`:50`).
- Produces: `Echo.Harness.Application.ISessionStatus` with `SessionState State { get; }`. Tasks 5 and 6 depend on it.

- [ ] **Step 1: Write the failing test**

Add to `Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs`, next to `HarnessComposition_RegistersTheSessionAsASingleton`:

```csharp
        // A second ProtocolSession behind ISessionStatus would mean a second
        // TcpTransport and a second socket, and every other test here would still
        // pass: they all resolve one interface at a time.
        [Test]
        public void HarnessComposition_ExposesOneSessionThroughBothInterfaces()
        {
            var builder = new ContainerBuilder();
            HarnessComposition.Configure(builder, EndpointResolution.NotConfigured("test"));
            using var container = builder.Build();

            Assert.That(
                container.Resolve<ISessionStatus>(),
                Is.SameAs(container.Resolve<IProtocolSession>()),
                "ISessionStatus must be the same instance as IProtocolSession.");
        }
```

- [ ] **Step 2: Run the test and verify it fails**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.CompositionSmokeTests`

Expected: FAIL — compile error, `ISessionStatus` does not exist.

- [ ] **Step 3: Create the port**

Create `Packages/com.echo.harness/Runtime/Application/Session/ISessionStatus.cs`:

```csharp
namespace Echo.Harness.Application
{
    /// <summary>
    /// Read-only session state, and nothing else.
    ///
    /// <para>This exists instead of injecting <see cref="IProtocolSession"/> into a
    /// view-model. Both compile and both respect the assembly boundaries the
    /// architecture gate checks; the difference is that the wide one also hands
    /// every view-model <c>SendAsync</c> and <c>RequestAsync</c> - the ability to
    /// talk to the server without passing through a use case. The gate compares
    /// assembly reference lists and cannot see that class of bypass, so this
    /// interface is the only thing that makes the rule structural rather than
    /// advisory.</para>
    ///
    /// <para><b>There is deliberately no state-changed event.</b>
    /// <see cref="IProtocolSession"/> has none either, so an event here would have
    /// to be invented and raised by something that does not exist. Consumers poll;
    /// <c>LoginViewModel.Refresh</c> is the one that does.</para>
    /// </summary>
    public interface ISessionStatus
    {
        SessionState State { get; }
    }
}
```

- [ ] **Step 4: Implement it on the session**

In `Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs`, change line 9 from:

```csharp
    public sealed class ProtocolSession : IProtocolSession
```

to:

```csharp
    public sealed class ProtocolSession : IProtocolSession, ISessionStatus
```

No other change. `public SessionState State { get; private set; }` at `:50` already satisfies the interface.

- [ ] **Step 5: Register the second interface**

In `Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs`, change line 77 from:

```csharp
            builder.Register<ProtocolSession>(Lifetime.Singleton).As<IProtocolSession>();
```

to:

```csharp
            // Two interfaces, ONE instance. VContainer gives each As<T>() on the same
            // Register call the same singleton, which is the entire point: a second
            // registration would build a second ProtocolSession over a second
            // TcpTransport and open a second socket.
            builder.Register<ProtocolSession>(Lifetime.Singleton)
                .As<IProtocolSession>()
                .As<ISessionStatus>();
```

- [ ] **Step 6: Run the test and verify it passes**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.CompositionSmokeTests`

Expected: PASS, including the pre-existing tests in that fixture.

- [ ] **Step 7: Run the architecture gate**

Run: `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`

Expected: `Architecture verification passed.` and `protocol fixture matches the Go source (39 messages)`.

- [ ] **Step 8: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/ISessionStatus.cs \
        Packages/com.echo.harness/Runtime/Application/Session/ProtocolSession.cs \
        Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs \
        Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs
git commit -m "Give session state a port a view-model cannot send through"
```

---

### Task 2: The login use case

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Application/Login/LoginOutcome.cs`
- Create: `Packages/com.echo.harness/Runtime/Application/Login/ILoginUseCase.cs`
- Create: `Packages/com.echo.harness/Runtime/Application/Login/LoginUseCase.cs`
- Create: `Packages/com.echo.harness/TestKit/FakeProtocolSession.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/LoginUseCaseTests.cs`

**Interfaces:**
- Consumes: `IProtocolSession.RequestAsync<TResponse>(MessageId, object, TimeSpan, CancellationToken)`; `LoginRequestDto` / `LoginResponseDto` (`Runtime/Contracts/Dtos/AuthDtos.cs`); `MessageId.LoginRequest` / `LoginResponse`; `RequestAlreadyInFlightException` (`SessionDiagnostics.cs:59`).
- Produces:
  - `enum LoginResult { Succeeded, Rejected, NoAnswer }`
  - `readonly struct LoginOutcome` with `Result`, `PlayerId`, `InGame`, `Message`, and static factories `Success(string playerId, bool inGame)`, `Refusal(string message)`, `NoReply(string message)`
  - `interface ILoginUseCase { UniTask<LoginOutcome> LoginAsync(string playerName, CancellationToken cancellationToken); }`
  - `sealed class LoginUseCase : ILoginUseCase` with `public static readonly TimeSpan Deadline`
  - `sealed class FakeProtocolSession : IProtocolSession` with settable `State`, `NextResponse`, `NextRequestFailure`, read-only `RequestCount`, `LastRequestId`, `LastRequestPayload`, and `void PublishFault(SessionFault)` — Task 3 relies on `PublishFault`.

- [ ] **Step 1: Write the fake session**

Create `Packages/com.echo.harness/TestKit/FakeProtocolSession.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;

namespace Echo.Harness.TestKit
{
    /// <summary>
    /// A session that answers synchronously, so EditMode tests may use the
    /// <c>[Test]</c> + <c>GetAwaiter().GetResult()</c> pattern the rest of the
    /// suite uses. Nothing here yields.
    ///
    /// <para><see cref="PublishFault"/> reproduces
    /// <c>ProtocolSession.PublishFault</c> exactly - synchronous, on the caller's
    /// thread, swallowing handler exceptions - because those three properties are
    /// what <c>SessionFaultRouter</c> is designed around, and a double that
    /// dispatched asynchronously would let the router's tests pin a contract
    /// production does not have.</para>
    /// </summary>
    public sealed class FakeProtocolSession : IProtocolSession
    {
        private readonly List<Action<SessionFault>> faultHandlers =
            new List<Action<SessionFault>>();

        public SessionState State { get; set; } = SessionState.Disconnected;

        /// <summary>What the next RequestAsync returns. Cast to TResponse.</summary>
        public object NextResponse { get; set; }

        /// <summary>One-shot. Cleared by the request that consumes it.</summary>
        public Exception NextRequestFailure { get; set; }

        public int RequestCount { get; private set; }

        public MessageId LastRequestId { get; private set; }

        public object LastRequestPayload { get; private set; }

        public bool Disposed { get; private set; }

        public UniTask StartAsync(CancellationToken cancellationToken) => UniTask.CompletedTask;

        public UniTask StopAsync(CancellationToken cancellationToken) => UniTask.CompletedTask;

        public UniTask SendAsync(MessageId messageId, object payload, CancellationToken cancellationToken) =>
            UniTask.CompletedTask;

        public UniTask<TResponse> RequestAsync<TResponse>(
            MessageId requestId,
            object payload,
            TimeSpan timeout,
            CancellationToken cancellationToken)
        {
            RequestCount++;
            LastRequestId = requestId;
            LastRequestPayload = payload;

            if (cancellationToken.IsCancellationRequested)
            {
                return UniTask.FromCanceled<TResponse>(cancellationToken);
            }

            if (NextRequestFailure != null)
            {
                var failure = NextRequestFailure;
                NextRequestFailure = null;
                return UniTask.FromException<TResponse>(failure);
            }

            return UniTask.FromResult((TResponse)NextResponse);
        }

        public UniTask<TimeSpan> ProbeRoundTripAsync(CancellationToken cancellationToken) =>
            UniTask.FromResult(TimeSpan.Zero);

        public IDisposable Subscribe<TPayload>(MessageId messageId, Action<TPayload> handler) =>
            new Unsubscribe(() => { });

        public IDisposable SubscribeToFaults(Action<SessionFault> handler)
        {
            faultHandlers.Add(handler);
            return new Unsubscribe(() => faultHandlers.Remove(handler));
        }

        public void PublishFault(SessionFault fault)
        {
            foreach (var handler in faultHandlers.ToArray())
            {
                try
                {
                    handler(fault);
                }
                catch
                {
                    // Matches ProtocolSession.PublishFault:978-982 exactly.
                }
            }
        }

        public void Dispose() => Disposed = true;

        private sealed class Unsubscribe : IDisposable
        {
            private readonly Action action;

            public Unsubscribe(Action action) => this.action = action;

            public void Dispose() => action();
        }
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/LoginUseCaseTests.cs`:

```csharp
using System;
using System.Linq;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class LoginUseCaseTests
    {
        private static (LoginUseCase UseCase, FakeProtocolSession Session) Build()
        {
            var session = new FakeProtocolSession();
            return (new LoginUseCase(session), session);
        }

        [Test]
        public void ASuccessfulResponseBecomesSucceededWithThePlayerId()
        {
            var (useCase, session) = Build();
            session.NextResponse = new LoginResponseDto
            {
                Success = true,
                PlayerId = "player-7",
                InGame = true,
            };

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Succeeded));
            Assert.That(outcome.PlayerId, Is.EqualTo("player-7"));
            Assert.That(outcome.InGame, Is.True);
            Assert.That(session.LastRequestId, Is.EqualTo(MessageId.LoginRequest));
            Assert.That(
                ((LoginRequestDto)session.LastRequestPayload).PlayerName,
                Is.EqualTo("ada"));
        }

        [Test]
        public void AnUnsuccessfulResponseBecomesRejectedCarryingTheServersReason()
        {
            var (useCase, session) = Build();
            session.NextResponse = new LoginResponseDto
            {
                Success = false,
                Error = "name already taken",
            };

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Rejected));
            Assert.That(outcome.Message, Is.EqualTo("name already taken"));
        }

        [Test]
        public void ARefusalWithNoReasonStillSaysSomething()
        {
            var (useCase, session) = Build();
            session.NextResponse = new LoginResponseDto { Success = false, Error = null };

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Rejected));
            Assert.That(outcome.Message, Is.Not.Null.And.Not.Empty);
        }

        [Test]
        public void ATimeoutBecomesNoAnswerRatherThanEscaping()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new TimeoutException("no LoginResponse");

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.NoAnswer));
        }

        [Test]
        public void ASecondLoginInFlightBecomesNoAnswerRatherThanEscaping()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new RequestAlreadyInFlightException(
                MessageId.LoginResponse, "already in flight");

            var outcome = useCase
                .LoginAsync("ada", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.NoAnswer));
        }

        // Shutdown cancellation is not a login result. Swallowing it would report
        // quitting the game as a failed login.
        [Test]
        public void ACancellationEscapesRatherThanBecomingAnOutcome()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new OperationCanceledException();

            Assert.Throws<OperationCanceledException>(() =>
                useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult());
        }

        // A broken transport must not be dressed up as a clean refusal.
        [Test]
        public void AnUnexpectedFailureEscapes()
        {
            var (useCase, session) = Build();
            session.NextRequestFailure = new InvalidOperationException("the stream desynchronized");

            Assert.Throws<InvalidOperationException>(() =>
                useCase.LoginAsync("ada", CancellationToken.None).GetAwaiter().GetResult());
        }

        [Test]
        public void ABlankPlayerNameIsRefusedWithoutTouchingTheSession()
        {
            var (useCase, session) = Build();

            var outcome = useCase
                .LoginAsync("   ", CancellationToken.None)
                .GetAwaiter().GetResult();

            Assert.That(outcome.Result, Is.EqualTo(LoginResult.Rejected));
            Assert.That(session.RequestCount, Is.Zero);
        }

        // The reconnect token is read from the response and dropped. This is a
        // structural guard rather than a behaviour test: the failure it prevents is
        // a future "helpful" addition of the field, which no behavioural test would
        // notice.
        [Test]
        public void TheReconnectTokenNeverLeavesTheUseCase()
        {
            var names = typeof(LoginOutcome).GetProperties().Select(p => p.Name).ToArray();

            Assert.That(
                names.Any(name => name.IndexOf("Token", StringComparison.OrdinalIgnoreCase) >= 0),
                Is.False,
                "LoginOutcome must not carry the reconnect token. Persisting it is a " +
                "separate, tracked piece of work; see docs/migration-checklist.md.");
        }
    }
}
```

- [ ] **Step 3: Run the tests and verify they fail**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.LoginUseCaseTests`

Expected: FAIL — compile error, `LoginUseCase`, `LoginOutcome` and `LoginResult` do not exist.

- [ ] **Step 4: Create the result type**

Create `Packages/com.echo.harness/Runtime/Application/Login/LoginOutcome.cs`:

```csharp
namespace Echo.Harness.Application
{
    public enum LoginResult
    {
        Succeeded,
        Rejected,
        NoAnswer
    }

    /// <summary>
    /// What one login attempt produced.
    ///
    /// <para>Three results rather than a boolean, because "the server refused" and
    /// "the server never answered" lead to different next actions - retry the same
    /// name versus check the connection - and collapsing them would throw that
    /// away at the only place it is known.</para>
    ///
    /// <para>This is an Application type and not <c>LoginResponseDto</c> because
    /// <c>Echo.Harness.Presentation</c> does not reference
    /// <c>Echo.Harness.Contracts</c> and structurally cannot name a wire DTO. It
    /// also carries no reconnect token: see <see cref="LoginUseCase"/>.</para>
    /// </summary>
    public readonly struct LoginOutcome
    {
        private LoginOutcome(LoginResult result, string playerId, bool inGame, string message)
        {
            Result = result;
            PlayerId = playerId;
            InGame = inGame;
            Message = message;
        }

        public static LoginOutcome Success(string playerId, bool inGame) =>
            new LoginOutcome(LoginResult.Succeeded, playerId, inGame, null);

        public static LoginOutcome Refusal(string message) =>
            new LoginOutcome(LoginResult.Rejected, null, false, message);

        public static LoginOutcome NoReply(string message) =>
            new LoginOutcome(LoginResult.NoAnswer, null, false, message);

        public LoginResult Result { get; }

        /// <summary>Set only when <see cref="Result"/> is Succeeded.</summary>
        public string PlayerId { get; }

        public bool InGame { get; }

        /// <summary>The reason, when Rejected or NoAnswer. Null on success.</summary>
        public string Message { get; }
    }
}
```

- [ ] **Step 5: Create the port**

Create `Packages/com.echo.harness/Runtime/Application/Login/ILoginUseCase.cs`:

```csharp
using System.Threading;
using Cysharp.Threading.Tasks;

namespace Echo.Harness.Application
{
    /// <summary>
    /// One login attempt. The port exists so that
    /// <c>Echo.Harness.Presentation</c> depends on an interface rather than on the
    /// concrete use case, and so that a view-model test needs no session at all.
    /// </summary>
    public interface ILoginUseCase
    {
        UniTask<LoginOutcome> LoginAsync(string playerName, CancellationToken cancellationToken);
    }
}
```

- [ ] **Step 6: Create the implementation**

Create `Packages/com.echo.harness/Runtime/Application/Login/LoginUseCase.cs`:

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// Sends LoginRequest and turns the answer into a <see cref="LoginOutcome"/>.
    ///
    /// <para><b>The exception policy is the design, and it has one rule:</b> this
    /// converts outcomes of trying to log in, and does not convert the system
    /// being broken. A timeout and a duplicate request are outcomes - the attempt
    /// finished, badly. A cancellation is not: the only thing that cancels here is
    /// shutdown, and reporting a quit as a failed login would be a lie the user
    /// acts on. Anything else is a real failure and is left to escape, because a
    /// broken transport dressed up as a clean refusal sends whoever debugs it to
    /// the wrong layer.</para>
    ///
    /// <para>The cost is real and is paid in <c>LoginViewModel</c>: because two
    /// exception classes escape, the caller needs a catch-all. That is deliberate,
    /// and the alternative - swallowing everything here - would put the same catch
    /// in this class while destroying the information.</para>
    /// </summary>
    public sealed class LoginUseCase : ILoginUseCase
    {
        /// <summary>
        /// How long the server gets to answer. Chosen to match
        /// <c>ProtocolSession.RoundTripProbeDeadline</c>, which is the only other
        /// deadline in the repository measured against this same server.
        /// </summary>
        public static readonly TimeSpan Deadline = TimeSpan.FromSeconds(10);

        private readonly IProtocolSession session;

        public LoginUseCase(IProtocolSession session) =>
            this.session = session ?? throw new ArgumentNullException(nameof(session));

        public async UniTask<LoginOutcome> LoginAsync(
            string playerName,
            CancellationToken cancellationToken)
        {
            // Refused here as well as by LoginViewModel.CanSubmit. The view-model
            // guard is what a user sees; this one is what holds when some other
            // caller arrives.
            if (string.IsNullOrWhiteSpace(playerName))
            {
                return LoginOutcome.Refusal("A player name is required.");
            }

            LoginResponseDto response;
            try
            {
                response = await session.RequestAsync<LoginResponseDto>(
                    MessageId.LoginRequest,
                    new LoginRequestDto { PlayerName = playerName },
                    Deadline,
                    cancellationToken);
            }
            catch (TimeoutException)
            {
                return LoginOutcome.NoReply(
                    $"The server did not answer within {Deadline}.");
            }
            catch (RequestAlreadyInFlightException)
            {
                return LoginOutcome.NoReply("A login is already in flight.");
            }

            // response.ReconnectToken is deliberately not carried out of this
            // method. Persisting it is its own piece of work with its own storage
            // question, and a token held in memory with no reader would be a
            // speculative store that later reads as an implemented feature.
            // LoginUseCaseTests.TheReconnectTokenNeverLeavesTheUseCase pins it.
            return response.Success
                ? LoginOutcome.Success(response.PlayerId, response.InGame)
                : LoginOutcome.Refusal(
                    string.IsNullOrWhiteSpace(response.Error)
                        ? "The server refused the login without saying why."
                        : response.Error);
        }
    }
}
```

- [ ] **Step 7: Run the tests and verify they pass**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.LoginUseCaseTests`

Expected: PASS, 9 tests.

- [ ] **Step 8: Run the gate and commit**

Run: `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`

```bash
git add Packages/com.echo.harness/Runtime/Application/Login \
        Packages/com.echo.harness/TestKit/FakeProtocolSession.cs \
        Packages/com.echo.harness/Tests/EditMode/LoginUseCaseTests.cs
git commit -m "Turn a login response into something Presentation can see"
```

---

### Task 3: The fault router

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Application/Session/SessionFaultDiagnostics.cs`
- Create: `Packages/com.echo.harness/Runtime/Application/Session/SessionFaultRouter.cs`
- Create: `Packages/com.echo.harness/TestKit/RecordingFaultLog.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/SessionFaultRouterTests.cs`

**Interfaces:**
- Consumes: `IProtocolSession.SubscribeToFaults(Action<SessionFault>)`; `SessionFault`, `SessionFaultKind` (`SessionDiagnostics.cs:14-51`); `ISessionScheduler.SwitchToSessionContextAsync` (`HarnessPorts.cs:174`); `FakeProtocolSession.PublishFault` (Task 2); `RecordingSessionScheduler` (`TestKit/RecordingSessionScheduler.cs`).
- Produces:
  - `enum FaultSeverity { Info, Warning, Error }`
  - `interface IFaultLog { void Write(FaultSeverity severity, SessionFault fault); }`
  - `sealed class SessionFaultRouter : IDisposable` — constructor `(IProtocolSession, ISessionScheduler, IFaultLog)`, method `IDisposable ObserveConnectionFaults(Action<SessionFault> handler)`
  - `sealed class RecordingFaultLog : IFaultLog` with `IReadOnlyList<FaultLogEntry> Entries` and `readonly struct FaultLogEntry { FaultSeverity Severity; SessionFault Fault; int ThreadId; }`

- [ ] **Step 1: Write the log port and severity enum**

Create `Packages/com.echo.harness/Runtime/Application/Session/SessionFaultDiagnostics.cs`:

```csharp
namespace Echo.Harness.Application
{
    public enum FaultSeverity
    {
        Info,
        Warning,
        Error
    }

    /// <summary>
    /// Where a routed fault is written. A port rather than a direct call because
    /// <c>Echo.Harness.Application</c> may not name <c>UnityEngine</c> - the
    /// architecture gate asserts that by source text
    /// (<c>Tools/ci/verify-architecture.ps1:345</c>) - and because a test needs to
    /// read what was written and on which thread.
    ///
    /// <para>Implementations must be safe to call from any thread. The router
    /// writes without hopping first, deliberately; see
    /// <see cref="SessionFaultRouter"/>.</para>
    /// </summary>
    public interface IFaultLog
    {
        void Write(FaultSeverity severity, SessionFault fault);
    }
}
```

- [ ] **Step 2: Write the recording log double**

Create `Packages/com.echo.harness/TestKit/RecordingFaultLog.cs`:

```csharp
using System.Collections.Generic;
using System.Threading;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    public readonly struct FaultLogEntry
    {
        public FaultLogEntry(FaultSeverity severity, SessionFault fault, int threadId)
        {
            Severity = severity;
            Fault = fault;
            ThreadId = threadId;
        }

        public FaultSeverity Severity { get; }

        public SessionFault Fault { get; }

        /// <summary>
        /// The thread the write happened on. This is the evidence for the router's
        /// central claim - that logging does not hop - so it is recorded rather
        /// than assumed.
        /// </summary>
        public int ThreadId { get; }
    }

    public sealed class RecordingFaultLog : IFaultLog
    {
        private readonly List<FaultLogEntry> entries = new List<FaultLogEntry>();

        /// <summary>A snapshot taken under the same lock the write takes.</summary>
        public IReadOnlyList<FaultLogEntry> Entries
        {
            get
            {
                lock (entries)
                {
                    return new List<FaultLogEntry>(entries);
                }
            }
        }

        public void Write(FaultSeverity severity, SessionFault fault)
        {
            lock (entries)
            {
                entries.Add(new FaultLogEntry(
                    severity, fault, Thread.CurrentThread.ManagedThreadId));
            }
        }
    }
}
```

- [ ] **Step 3: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/SessionFaultRouterTests.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Linq;
using System.Threading;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class SessionFaultRouterTests
    {
        private FakeProtocolSession session;
        private RecordingSessionScheduler scheduler;
        private RecordingFaultLog log;
        private SessionFaultRouter router;

        [SetUp]
        public void SetUp()
        {
            session = new FakeProtocolSession();
            scheduler = new RecordingSessionScheduler();
            log = new RecordingFaultLog();
            router = new SessionFaultRouter(session, scheduler, log);
        }

        [TearDown]
        public void TearDown() => router.Dispose();

        private static SessionFault Fault(SessionFaultKind kind, MessageId id) =>
            new SessionFault(kind, id, $"{kind} on {id}");

        [Test]
        public void ATransportFailureIsLoggedAsAnError()
        {
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(log.Entries.Single().Severity, Is.EqualTo(FaultSeverity.Error));
        }

        [Test]
        public void ANoDestinationIsLoggedAsInformation()
        {
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));

            Assert.That(log.Entries.Single().Severity, Is.EqualTo(FaultSeverity.Info));
        }

        [Test]
        public void AMalformedPayloadIsLoggedAsAWarning()
        {
            session.PublishFault(Fault(SessionFaultKind.MalformedPayload, MessageId.LoginResponse));

            Assert.That(log.Entries.Single().Severity, Is.EqualTo(FaultSeverity.Warning));
        }

        // The session publishes one NoDestination per unrouted message and that
        // contract is not weakened. The volume is handled here instead.
        [Test]
        public void NoDestinationIsLoggedOnlyOncePerMessageId()
        {
            for (var i = 0; i < 5; i++)
            {
                session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));
            }

            Assert.That(log.Entries.Count, Is.EqualTo(1));
        }

        [Test]
        public void ADifferentUnroutedMessageIsStillReportedOnce()
        {
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginRequest));

            Assert.That(log.Entries.Count, Is.EqualTo(2));
        }

        // De-duplication must not leak into the kinds that are never noisy.
        [Test]
        public void ARepeatedTransportFailureIsLoggedEveryTime()
        {
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(log.Entries.Count, Is.EqualTo(2));
        }

        [Test]
        public void OnlyConnectionFaultsReachAConnectionObserver()
        {
            var seen = new List<SessionFaultKind>();
            using var subscription = router.ObserveConnectionFaults(f => seen.Add(f.Kind));

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.DispatchFailure, MessageId.LoginRequest));
            session.PublishFault(Fault(SessionFaultKind.NoDestination, MessageId.LoginResponse));
            session.PublishFault(Fault(SessionFaultKind.MalformedPayload, MessageId.LoginResponse));
            session.PublishFault(Fault(SessionFaultKind.UnknownMessageId, MessageId.LoginResponse));

            Assert.That(seen, Is.EquivalentTo(new[]
            {
                SessionFaultKind.TransportFailure,
                SessionFaultKind.DispatchFailure,
            }));
        }

        // The whole point of the design: the log does not wait for a hop, and the
        // UI does. Deleting the hop makes the second assertion fail.
        [Test]
        public void TheLogTakesNoHopAndTheObserverTakesOne()
        {
            using var subscription = router.ObserveConnectionFaults(_ => { });

            session.PublishFault(Fault(SessionFaultKind.MalformedPayload, MessageId.LoginResponse));
            Assert.That(scheduler.SwitchCount, Is.Zero, "Logging must not hop.");

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));
            Assert.That(scheduler.SwitchCount, Is.EqualTo(1), "UI delivery must hop.");
        }

        [Test]
        public void TheLogIsWrittenOnThePublishingThread()
        {
            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(
                log.Entries.Single().ThreadId,
                Is.EqualTo(Thread.CurrentThread.ManagedThreadId));
        }

        [Test]
        public void AThrowingObserverDoesNotStopTheOthers()
        {
            var reached = false;
            using var first = router.ObserveConnectionFaults(_ => throw new InvalidOperationException());
            using var second = router.ObserveConnectionFaults(_ => reached = true);

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(reached, Is.True);
        }

        // ProtocolSession.PublishFault swallows what a handler throws, so a failure
        // in here has nowhere to go unless the router reports it itself.
        [Test]
        public void AFailingHopIsReportedRatherThanLost()
        {
            using var subscription = router.ObserveConnectionFaults(_ => { });
            scheduler.NextFailure = new InvalidOperationException("the loop is gone");

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(
                log.Entries.Any(e => e.Fault.Kind == SessionFaultKind.SubscriberFailure),
                Is.True,
                "A delivery that never reached the UI must still leave a trace.");
        }

        [Test]
        public void DisposeStopsRouting()
        {
            router.Dispose();

            session.PublishFault(Fault(SessionFaultKind.TransportFailure, MessageId.LoginRequest));

            Assert.That(log.Entries, Is.Empty);
        }
    }
}
```

- [ ] **Step 4: Run the tests and verify they fail**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.SessionFaultRouterTests`

Expected: FAIL — compile error, `SessionFaultRouter` does not exist.

- [ ] **Step 5: Write the router**

Create `Packages/com.echo.harness/Runtime/Application/Session/SessionFaultRouter.cs`:

```csharp
using System;
using System.Collections.Generic;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Contracts;

namespace Echo.Harness.Application
{
    /// <summary>
    /// The one production subscriber to <c>IProtocolSession.SubscribeToFaults</c>.
    /// Before this existed, <c>ProtocolSession.PublishFault</c> iterated an empty
    /// list and all seven fault kinds were produced and never read.
    ///
    /// <para><b>The two halves have different threading on purpose, and that is
    /// the design rather than an inconsistency.</b> Logging is synchronous, on
    /// whichever thread published the fault: <c>UnityEngine.Debug</c> is safe from
    /// any thread, and fault logs matter most on the shutdown path - the one
    /// <c>HarnessSessionDriver</c> documents as having no further player-loop tick,
    /// where anything that hopped first would never be emitted at all. UI delivery
    /// does hop, through <see cref="ISessionScheduler"/>, because a handler that
    /// touches UI on a pool thread is a crash; that is the review finding this
    /// class closes.</para>
    ///
    /// <para><b>Nothing here may rely on an exception escaping.</b>
    /// <c>ProtocolSession.PublishFault</c> catches everything a handler throws and
    /// says so: "there is nowhere left to report it". So the delivery half carries
    /// its own catch and writes what happened to the log, which is the only
    /// surface left.</para>
    /// </summary>
    public sealed class SessionFaultRouter : IDisposable
    {
        private readonly ISessionScheduler scheduler;
        private readonly IFaultLog log;
        private readonly IDisposable subscription;
        private readonly HashSet<MessageId> reportedNoDestination = new HashSet<MessageId>();
        private readonly List<Action<SessionFault>> connectionObservers =
            new List<Action<SessionFault>>();
        private bool disposed;

        public SessionFaultRouter(
            IProtocolSession session,
            ISessionScheduler scheduler,
            IFaultLog log)
        {
            if (session == null)
            {
                throw new ArgumentNullException(nameof(session));
            }

            this.scheduler = scheduler ?? throw new ArgumentNullException(nameof(scheduler));
            this.log = log ?? throw new ArgumentNullException(nameof(log));

            // Subscribing in the constructor is why this type must be RESOLVED and
            // not merely registered. See SessionFaultRouterEntryPoint.
            subscription = session.SubscribeToFaults(OnFault);
        }

        /// <summary>
        /// Faults a user could act on: the link is gone or a message could not be
        /// dispatched. Delivered on the session context, so a handler may touch UI.
        /// The other five kinds are logged and stop there - there is no interface
        /// element that could express them while a login screen is the only screen.
        /// </summary>
        public IDisposable ObserveConnectionFaults(Action<SessionFault> handler)
        {
            if (handler == null)
            {
                throw new ArgumentNullException(nameof(handler));
            }

            lock (connectionObservers)
            {
                connectionObservers.Add(handler);
            }

            return new Unsubscribe(() =>
            {
                lock (connectionObservers)
                {
                    connectionObservers.Remove(handler);
                }
            });
        }

        private void OnFault(SessionFault fault)
        {
            // De-duplication sits ahead of the log rather than beside it, so the
            // count the log shows is the count a reader is meant to act on.
            if (fault.Kind == SessionFaultKind.NoDestination
                && !IsFirstUnroutedMessageOfItsId(fault.MessageId))
            {
                return;
            }

            log.Write(SeverityOf(fault.Kind), fault);

            if (IsConnectionFault(fault.Kind))
            {
                DeliverToObserversAsync(fault).Forget();
            }
        }

        /// <summary>
        /// The set is locked because this runs on whichever thread published the
        /// fault, and the session publishes from its pump, from a timer, and from a
        /// caller's own frame. This is a real race, not a defensive habit.
        /// </summary>
        private bool IsFirstUnroutedMessageOfItsId(MessageId messageId)
        {
            lock (reportedNoDestination)
            {
                return reportedNoDestination.Add(messageId);
            }
        }

        private static bool IsConnectionFault(SessionFaultKind kind) =>
            kind == SessionFaultKind.TransportFailure
            || kind == SessionFaultKind.DispatchFailure;

        private static FaultSeverity SeverityOf(SessionFaultKind kind)
        {
            switch (kind)
            {
                case SessionFaultKind.NoDestination:
                    // Ordinary during a slice that subscribes to almost nothing.
                    return FaultSeverity.Info;
                case SessionFaultKind.TransportFailure:
                case SessionFaultKind.DispatchFailure:
                    return FaultSeverity.Error;
                default:
                    return FaultSeverity.Warning;
            }
        }

        private async UniTaskVoid DeliverToObserversAsync(SessionFault fault)
        {
            try
            {
                await scheduler.SwitchToSessionContextAsync(CancellationToken.None);

                Action<SessionFault>[] observers;
                lock (connectionObservers)
                {
                    observers = connectionObservers.ToArray();
                }

                foreach (var observer in observers)
                {
                    try
                    {
                        observer(fault);
                    }
                    catch
                    {
                        // One broken observer must not deny the others the fault.
                    }
                }
            }
            catch (Exception failure)
            {
                // SubscriberFailure rather than DispatchFailure: this is the
                // delivery failing, not the session's dispatch. Written straight to
                // the log rather than back through OnFault, which would recurse.
                log.Write(FaultSeverity.Warning, new SessionFault(
                    SessionFaultKind.SubscriberFailure,
                    fault.MessageId,
                    "A connection fault never reached the UI: " +
                    $"{failure.GetType().Name}: {failure.Message}"));
            }
        }

        public void Dispose()
        {
            if (disposed)
            {
                return;
            }

            disposed = true;
            subscription.Dispose();
        }

        private sealed class Unsubscribe : IDisposable
        {
            private readonly Action action;

            public Unsubscribe(Action action) => this.action = action;

            public void Dispose() => action();
        }
    }
}
```

- [ ] **Step 6: Run the tests and verify they pass**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.SessionFaultRouterTests`

Expected: PASS, 13 tests.

- [ ] **Step 7: Mutation check — delete the hop and confirm a test notices**

Temporarily delete the `await scheduler.SwitchToSessionContextAsync(CancellationToken.None);` line, re-run the fixture, and confirm `TheLogTakesNoHopAndTheObserverTakesOne` fails. **Restore the line and re-run to confirm green before committing.** Record the result in the commit message.

- [ ] **Step 8: Run the gate and commit**

Run: `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`

```bash
git add Packages/com.echo.harness/Runtime/Application/Session/SessionFaultDiagnostics.cs \
        Packages/com.echo.harness/Runtime/Application/Session/SessionFaultRouter.cs \
        Packages/com.echo.harness/TestKit/RecordingFaultLog.cs \
        Packages/com.echo.harness/Tests/EditMode/SessionFaultRouterTests.cs
git commit -m "Give seven kinds of fault their first reader"
```

---

### Task 4: Wire the sink into the container

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Infrastructure/UnityFaultLog.cs`
- Create: `Packages/com.echo.harness/Runtime/Bootstrap/SessionFaultRouterEntryPoint.cs`
- Modify: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs`
- Modify: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs`
- Modify: `Tools/ci/verify-architecture.ps1`
- Test: `Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs`

**Interfaces:**
- Consumes: `IFaultLog`, `SessionFaultRouter`, `ILoginUseCase`, `LoginUseCase` (Tasks 2-3).
- Produces: `Echo.Harness.Infrastructure.UnityFaultLog : IFaultLog`; `Echo.Harness.Bootstrap.SessionFaultRouterEntryPoint : IStartable`. Registrations for `IFaultLog`, `SessionFaultRouter` and `ILoginUseCase` that Tasks 5-6 rely on.

- [ ] **Step 1: Write the failing test**

Add to `Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs`:

```csharp
        // Resolving each of these is not ceremony: every VContainer registration is
        // lazy, and SessionFaultRouter subscribes in its constructor, so a
        // registration nothing resolves is a fault sink that never sees a fault.
        [Test]
        public void HarnessComposition_ResolvesTheFaultSinkAndTheLoginUseCase()
        {
            var builder = new ContainerBuilder();
            HarnessComposition.Configure(builder, EndpointResolution.NotConfigured("test"));
            using var container = builder.Build();

            Assert.That(container.Resolve<IFaultLog>(), Is.Not.Null);
            Assert.That(container.Resolve<ILoginUseCase>(), Is.Not.Null);
            Assert.That(
                container.Resolve<SessionFaultRouter>(),
                Is.SameAs(container.Resolve<SessionFaultRouter>()),
                "Two routers would mean two subscriptions and every fault logged twice.");
        }
```

- [ ] **Step 2: Run it and verify it fails**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.CompositionSmokeTests`

Expected: FAIL — `VContainerException: No such registration of type: Echo.Harness.Application.IFaultLog`.

- [ ] **Step 3: Write the Unity log**

Create `Packages/com.echo.harness/Runtime/Infrastructure/UnityFaultLog.cs`:

```csharp
using Echo.Harness.Application;
using UnityEngine;

namespace Echo.Harness.Infrastructure
{
    /// <summary>
    /// Writes routed faults to the Unity console. It lives here rather than in
    /// Application for one mechanical reason: the architecture gate asserts by
    /// source text that Application names no <c>UnityEngine</c> type
    /// (<c>Tools/ci/verify-architecture.ps1:345</c>).
    ///
    /// <para><c>Debug</c>'s three methods are safe to call from any thread, which
    /// is what lets <see cref="SessionFaultRouter"/> log without hopping first.</para>
    /// </summary>
    public sealed class UnityFaultLog : IFaultLog
    {
        public void Write(FaultSeverity severity, SessionFault fault)
        {
            var line = $"[Harness] {fault.Kind} on {fault.MessageId}: {fault.Diagnostic}";

            switch (severity)
            {
                case FaultSeverity.Error:
                    Debug.LogError(line);
                    break;
                case FaultSeverity.Warning:
                    Debug.LogWarning(line);
                    break;
                default:
                    Debug.Log(line);
                    break;
            }
        }
    }
}
```

- [ ] **Step 4: Add the registrations**

In `Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs`, after the `ProtocolSession` registration edited in Task 1, add:

```csharp
            // The sink. Registered here rather than in HarnessLifetimeScope so that
            // the EditMode composition tests can resolve it from a bare
            // ContainerBuilder with no scene.
            builder.Register<UnityFaultLog>(Lifetime.Singleton).As<IFaultLog>();
            builder.Register<SessionFaultRouter>(Lifetime.Singleton);

            builder.Register<LoginUseCase>(Lifetime.Singleton).As<ILoginUseCase>();
```

- [ ] **Step 5: Run the test and verify it passes**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.CompositionSmokeTests`

Expected: PASS.

- [ ] **Step 6: Write the entry point**

Create `Packages/com.echo.harness/Runtime/Bootstrap/SessionFaultRouterEntryPoint.cs`:

```csharp
using Echo.Harness.Application;
using VContainer.Unity;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Exists to force <see cref="SessionFaultRouter"/> to be constructed, and to
    /// do nothing else.
    ///
    /// <para>The router subscribes to the session in its constructor, and every
    /// VContainer registration is lazy - the trap
    /// <c>HarnessComposition.Configure</c>'s own summary names: "Registering is not
    /// resolving." Without something asking for the router, it is never built, no
    /// fault is ever logged, and every test that constructs one directly still
    /// passes.</para>
    ///
    /// <para>The router cannot solve this itself: <c>IStartable</c> is a VContainer
    /// type and <c>Echo.Harness.Application</c> may not reference VContainer.</para>
    ///
    /// <para>Two alternatives were rejected for the same reason. Hanging the router
    /// off <c>LoginViewModel</c>'s constructor works today and breaks silently the
    /// first time someone removes what looks like an unused parameter; hanging it
    /// off <c>HarnessSessionDriver</c> gives that class an argument it never uses
    /// and invites the same cleanup. An empty class named for its only job cannot
    /// be tidied away by accident.</para>
    ///
    /// <para>The parameter is deliberately unused. Taking it IS the work.</para>
    /// </summary>
    public sealed class SessionFaultRouterEntryPoint : IStartable
    {
        public SessionFaultRouterEntryPoint(SessionFaultRouter router)
        {
        }

        public void Start()
        {
        }
    }
}
```

- [ ] **Step 7: Register the entry point**

In `Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs`, change the body of `Configure` to:

```csharp
        protected override void Configure(IContainerBuilder builder)
        {
            HarnessComposition.Configure(builder);
            builder.RegisterEntryPoint<HarnessSessionDriver>();

            // Not decoration. See SessionFaultRouterEntryPoint: without this line
            // the router is registered and never constructed, and nothing fails.
            builder.RegisterEntryPoint<SessionFaultRouterEntryPoint>();
        }
```

- [ ] **Step 8: Guard the registration in the architecture gate**

In `Tools/ci/verify-architecture.ps1`, after the Application source-text assertion at line 345, add:

```powershell
# The two entry-point registrations live in HarnessLifetimeScope.Configure, which
# is a protected override on a MonoBehaviour and therefore unreachable from an
# EditMode test -- there is no way to call it without a scene. So they are pinned
# by source text instead.
#
# Be precise about what this proves and what it does not. It proves the lines are
# present. It does NOT prove VContainer runs them, and no test on this runtime
# proves that either. What makes the weaker check worth having is the failure it
# catches: deleting the SessionFaultRouterEntryPoint line leaves the router
# registered, never constructed, subscribed to nothing -- and every other test in
# the repository still green.
$LifetimeScopeText = Get-Content -Raw -LiteralPath (
    [System.IO.Path]::Combine(
        $ProjectRoot,
        'Packages\com.echo.harness\Runtime\Bootstrap\HarnessLifetimeScope.cs'))
Assert-True ($LifetimeScopeText -match 'RegisterEntryPoint<HarnessSessionDriver>') `
    'HarnessLifetimeScope must register the session driver as an entry point.'
Assert-True ($LifetimeScopeText -match 'RegisterEntryPoint<SessionFaultRouterEntryPoint>') `
    'HarnessLifetimeScope must register the fault router entry point, or no fault is ever read.'
```

- [ ] **Step 9: Run the gate and verify it passes**

Run: `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`

Expected: `Architecture verification passed.`

- [ ] **Step 10: Mutation check — delete the registration and confirm the gate notices**

Temporarily delete the `builder.RegisterEntryPoint<SessionFaultRouterEntryPoint>();` line, re-run the gate, and confirm it fails with the message above. **Restore the line and re-run to confirm green.**

- [ ] **Step 11: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Infrastructure/UnityFaultLog.cs \
        Packages/com.echo.harness/Runtime/Bootstrap/SessionFaultRouterEntryPoint.cs \
        Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs \
        Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs \
        Tools/ci/verify-architecture.ps1 \
        Packages/com.echo.harness/Tests/EditMode/CompositionSmokeTests.cs
git commit -m "Make the fault sink something the container actually builds"
```

---

### Task 5: The login view-model

**Files:**
- Create: `Packages/com.echo.harness/Runtime/Presentation/LoginViewModel.cs`
- Create: `Packages/com.echo.harness/TestKit/LoginTestDoubles.cs`
- Modify: `Packages/com.echo.harness/Runtime/Presentation/Echo.Harness.Presentation.asmdef`
- Modify: `Tools/ci/verify-architecture.ps1:117-118`
- Modify: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs`
- Test: `Packages/com.echo.harness/Tests/EditMode/LoginViewModelTests.cs`

**Interfaces:**
- Consumes: `ISessionStatus` (Task 1), `ILoginUseCase` / `LoginOutcome` / `LoginResult` (Task 2), `SessionFaultRouter.ObserveConnectionFaults` (Task 3).
- Produces: `Echo.Harness.Presentation.LoginViewModel`, constructor `(ISessionStatus, ILoginUseCase, SessionFaultRouter)`, methods `void Refresh()` and `UniTask SubmitAsync()`, bindable properties `PlayerName` (settable), `ConnectionStatus`, `CanSubmit`, `IsBusy`, `ResultText`, `ConnectionFaultText`. Task 6 binds to these exact names.
- Produces: `Echo.Harness.TestKit.FakeSessionStatus` (settable `State`) and `FakeLoginUseCase` (settable `NextOutcome`, `NextFailure`; read-only `CallCount`, `LastPlayerName`).

- [ ] **Step 1: Write the test doubles**

Create `Packages/com.echo.harness/TestKit/LoginTestDoubles.cs`:

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;

namespace Echo.Harness.TestKit
{
    public sealed class FakeSessionStatus : ISessionStatus
    {
        public SessionState State { get; set; } = SessionState.Disconnected;
    }

    /// <summary>
    /// Completes synchronously, so a view-model test may use the
    /// <c>[Test]</c> + <c>GetAwaiter().GetResult()</c> pattern.
    /// </summary>
    public sealed class FakeLoginUseCase : ILoginUseCase
    {
        public LoginOutcome NextOutcome { get; set; } = LoginOutcome.Success("player-1", false);

        /// <summary>One-shot, and it wins over <see cref="NextOutcome"/>.</summary>
        public Exception NextFailure { get; set; }

        public int CallCount { get; private set; }

        public string LastPlayerName { get; private set; }

        public UniTask<LoginOutcome> LoginAsync(
            string playerName,
            CancellationToken cancellationToken)
        {
            CallCount++;
            LastPlayerName = playerName;

            if (NextFailure != null)
            {
                var failure = NextFailure;
                NextFailure = null;
                return UniTask.FromException<LoginOutcome>(failure);
            }

            return UniTask.FromResult(NextOutcome);
        }
    }
}
```

- [ ] **Step 2: Write the failing tests**

Create `Packages/com.echo.harness/Tests/EditMode/LoginViewModelTests.cs`:

```csharp
using System;
using Echo.Harness.Application;
using Echo.Harness.Contracts;
using Echo.Harness.Presentation;
using Echo.Harness.TestKit;
using NUnit.Framework;

namespace Echo.Harness.Tests.EditMode
{
    public sealed class LoginViewModelTests
    {
        private FakeSessionStatus status;
        private FakeLoginUseCase login;
        private FakeProtocolSession session;
        private RecordingSessionScheduler scheduler;
        private SessionFaultRouter router;
        private LoginViewModel viewModel;

        [SetUp]
        public void SetUp()
        {
            status = new FakeSessionStatus();
            login = new FakeLoginUseCase();
            session = new FakeProtocolSession();
            scheduler = new RecordingSessionScheduler();
            router = new SessionFaultRouter(session, scheduler, new RecordingFaultLog());
            viewModel = new LoginViewModel(status, login, router);
        }

        [TearDown]
        public void TearDown()
        {
            viewModel.Dispose();
            router.Dispose();
        }

        [Test]
        public void SubmitIsRefusedUntilTheSessionIsConnected()
        {
            viewModel.PlayerName = "ada";
            status.State = SessionState.Connecting;
            viewModel.Refresh();

            Assert.That(viewModel.CanSubmit, Is.False);
        }

        [Test]
        public void SubmitIsRefusedWithoutAPlayerName()
        {
            viewModel.PlayerName = "   ";
            status.State = SessionState.Connected;
            viewModel.Refresh();

            Assert.That(viewModel.CanSubmit, Is.False);
        }

        [Test]
        public void SubmitIsAllowedOnceConnectedAndNamed()
        {
            viewModel.PlayerName = "ada";
            status.State = SessionState.Connected;
            viewModel.Refresh();

            Assert.That(viewModel.CanSubmit, Is.True);
        }

        [Test]
        public void RefreshRaisesNothingWhenNothingChanged()
        {
            status.State = SessionState.Connected;
            viewModel.Refresh();

            var raised = 0;
            viewModel.propertyChanged += (_, __) => raised++;
            viewModel.Refresh();

            Assert.That(raised, Is.Zero);
        }

        [Test]
        public void RefreshRaisesWhenTheStateChanged()
        {
            viewModel.Refresh();

            var raised = 0;
            viewModel.propertyChanged += (_, __) => raised++;
            status.State = SessionState.Connected;
            viewModel.Refresh();

            Assert.That(raised, Is.GreaterThan(0));
        }

        [Test]
        public void ASuccessfulLoginShowsThePlayerId()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextOutcome = LoginOutcome.Success("player-7", false);

            viewModel.SubmitAsync().GetAwaiter().GetResult();

            Assert.That(viewModel.ResultText, Does.Contain("player-7"));
            Assert.That(viewModel.IsBusy, Is.False);
        }

        [Test]
        public void ARefusalShowsTheServersReason()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextOutcome = LoginOutcome.Refusal("name already taken");

            viewModel.SubmitAsync().GetAwaiter().GetResult();

            Assert.That(viewModel.ResultText, Does.Contain("name already taken"));
        }

        // LoginUseCase deliberately lets a broken transport escape. Without this
        // catch the exception reaches UniTask's unobserved handler and the button
        // appears to do nothing at all.
        [Test]
        public void AnEscapingFailureBecomesVisibleTextRatherThanSilence()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextFailure = new InvalidOperationException("the stream desynchronized");

            Assert.DoesNotThrow(() => viewModel.SubmitAsync().GetAwaiter().GetResult());

            Assert.That(viewModel.ResultText, Is.Not.Null.And.Not.Empty);
            Assert.That(viewModel.IsBusy, Is.False, "A failed submit must release the button.");
        }

        // A dropped connection must not erase the refusal the user is reading.
        [Test]
        public void AConnectionFaultDoesNotOverwriteTheLoginResult()
        {
            status.State = SessionState.Connected;
            viewModel.PlayerName = "ada";
            viewModel.Refresh();
            login.NextOutcome = LoginOutcome.Refusal("name already taken");
            viewModel.SubmitAsync().GetAwaiter().GetResult();

            session.PublishFault(new SessionFault(
                SessionFaultKind.TransportFailure, MessageId.LoginRequest, "the link died"));

            Assert.That(viewModel.ResultText, Does.Contain("name already taken"));
            Assert.That(viewModel.ConnectionFaultText, Is.Not.Null.And.Not.Empty);
        }
    }
}
```

- [ ] **Step 3: Run the tests and verify they fail**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode -Filter Echo.Harness.Tests.EditMode.LoginViewModelTests`

Expected: FAIL — compile error, `LoginViewModel` does not exist.

- [ ] **Step 4: Write the view-model**

Create `Packages/com.echo.harness/Runtime/Presentation/LoginViewModel.cs`:

```csharp
using System;
using System.Threading;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Unity.Properties;

namespace Echo.Harness.Presentation
{
    /// <summary>
    /// The login screen's state, bindable by UI Toolkit's runtime data binding and
    /// free of every engine UI type - which is what lets the whole of it be tested
    /// in EditMode with no player loop.
    ///
    /// <para><b><see cref="ResultText"/> and <see cref="ConnectionFaultText"/> are
    /// separate on purpose.</b> A login refusal and a dropped link arrive from
    /// unrelated sources at unrelated times; one field would let whichever landed
    /// second erase the other, so a disconnect could silently wipe the very
    /// rejection message the user was reading.</para>
    /// </summary>
    public sealed class LoginViewModel : INotifyBindablePropertyChanged, IDisposable
    {
        private readonly ISessionStatus status;
        private readonly ILoginUseCase login;
        private readonly IDisposable faultSubscription;

        private string playerName = string.Empty;
        private string connectionStatus;
        private string resultText = string.Empty;
        private string connectionFaultText = string.Empty;
        private bool busy;
        private SessionState lastSeenState = (SessionState)(-1);

        public LoginViewModel(ISessionStatus status, ILoginUseCase login, SessionFaultRouter faults)
        {
            this.status = status ?? throw new ArgumentNullException(nameof(status));
            this.login = login ?? throw new ArgumentNullException(nameof(login));

            if (faults == null)
            {
                throw new ArgumentNullException(nameof(faults));
            }

            // Taking the router here is ordinary consumption. It is NOT what forces
            // the router to be constructed - SessionFaultRouterEntryPoint is - so
            // that removing this line degrades the UI without silently switching
            // off fault logging.
            faultSubscription = faults.ObserveConnectionFaults(OnConnectionFault);

            Refresh();
        }

        public event EventHandler<BindablePropertyChangedEventArgs> propertyChanged;

        [CreateProperty]
        public string PlayerName
        {
            get => playerName;
            set
            {
                if (playerName == value)
                {
                    return;
                }

                playerName = value ?? string.Empty;
                Notify(nameof(PlayerName));
                Notify(nameof(CanSubmit));
            }
        }

        [CreateProperty]
        public string ConnectionStatus => connectionStatus;

        [CreateProperty]
        public bool CanSubmit =>
            status.State == SessionState.Connected
            && !busy
            && !string.IsNullOrWhiteSpace(playerName);

        [CreateProperty]
        public bool IsBusy => busy;

        [CreateProperty]
        public string ResultText => resultText;

        [CreateProperty]
        public string ConnectionFaultText => connectionFaultText;

        /// <summary>
        /// Re-reads the session state. Polling is not a choice:
        /// <see cref="ISessionStatus"/> exposes no change event because
        /// <c>IProtocolSession</c> has none either. Called once a frame by
        /// <c>LoginView.Update</c>, and it raises nothing unless something moved.
        /// </summary>
        public void Refresh()
        {
            var current = status.State;
            if (current == lastSeenState)
            {
                return;
            }

            lastSeenState = current;
            connectionStatus = Describe(current);
            Notify(nameof(ConnectionStatus));
            Notify(nameof(CanSubmit));
        }

        public async UniTask SubmitAsync()
        {
            if (busy)
            {
                return;
            }

            SetBusy(true);
            connectionFaultText = string.Empty;
            Notify(nameof(ConnectionFaultText));

            try
            {
                var outcome = await login.LoginAsync(playerName, CancellationToken.None);
                resultText = Describe(outcome);
            }
            catch (OperationCanceledException)
            {
                // Shutdown. Nothing to show, and the screen is going away.
                resultText = string.Empty;
            }
            catch (Exception failure)
            {
                // LoginUseCase lets a genuinely broken transport escape, and this is
                // where that lands. Without this catch the exception reaches
                // UniTask's unobserved handler and the button appears inert.
                resultText = $"Login failed: {failure.GetType().Name}: {failure.Message}";
            }
            finally
            {
                SetBusy(false);
                Notify(nameof(ResultText));
            }
        }

        public void Dispose() => faultSubscription.Dispose();

        private void OnConnectionFault(SessionFault fault)
        {
            // Delivered on the session context by SessionFaultRouter, so touching
            // bindable state here is safe.
            connectionFaultText = $"Connection problem: {fault.Kind} — {fault.Diagnostic}";
            Notify(nameof(ConnectionFaultText));
        }

        private void SetBusy(bool value)
        {
            if (busy == value)
            {
                return;
            }

            busy = value;
            Notify(nameof(IsBusy));
            Notify(nameof(CanSubmit));
        }

        private void Notify(string property) =>
            propertyChanged?.Invoke(this, new BindablePropertyChangedEventArgs(property));

        private static string Describe(SessionState state)
        {
            switch (state)
            {
                case SessionState.Connected:
                    return "Connected.";
                case SessionState.Connecting:
                    return "Connecting…";
                case SessionState.Faulted:
                    return "The session faulted.";
                default:
                    return "Not connected.";
            }
        }

        private static string Describe(LoginOutcome outcome)
        {
            switch (outcome.Result)
            {
                case LoginResult.Succeeded:
                    return $"Logged in as {outcome.PlayerId}" +
                        (outcome.InGame ? " (a game is already in progress)." : ".");
                case LoginResult.Rejected:
                    return $"The server refused the login: {outcome.Message}";
                default:
                    return $"No answer from the server: {outcome.Message}";
            }
        }
    }
}
```

- [ ] **Step 5: Register the view-model**

In `Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs`, after the `LoginUseCase` registration from Task 4, add:

```csharp
            builder.Register<LoginViewModel>(Lifetime.Singleton);
```

Add `using Echo.Harness.Presentation;` to the file's using block.

- [ ] **Step 6: Drop R3 from Presentation**

In `Packages/com.echo.harness/Runtime/Presentation/Echo.Harness.Presentation.asmdef`, change:

```json
  "references": [
    "Echo.Harness.Application",
    "R3.Unity"
  ],
```

to:

```json
  "references": [
    "Echo.Harness.Application"
  ],
```

- [ ] **Step 7: Mirror it in the gate**

In `Tools/ci/verify-architecture.ps1`, change lines 117-118 from:

```powershell
    'Echo.Harness.Presentation' = @(
        'Echo.Harness.Application', 'R3.Unity')
```

to:

```powershell
    # R3.Unity was dropped when this assembly got its first real content. It was
    # referenced here and used nowhere in Runtime -- the only `using R3;` in the
    # repository are in the two test assemblies, which reference it directly and
    # whose reference lists this gate deliberately does not pin. UI Toolkit's
    # INotifyBindablePropertyChanged covers change notification, and a reactive
    # alternative would have bypassed the dataSource binding this repository
    # already committed to in HarnessHealthViewModel.
    'Echo.Harness.Presentation' = @(
        'Echo.Harness.Application')
```

- [ ] **Step 8: Run the tests and the gate**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform EditMode`

Expected: the whole EditMode suite passes, including `ThirdPartyPackageSmokeTests` and `HarnessPlayerLoopTests`' R3 usage, which are unaffected.

Run: `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`

Expected: `Architecture verification passed.`

- [ ] **Step 9: Mutation check — make `CanSubmit` always true**

Temporarily replace `CanSubmit`'s body with `=> true`, re-run `LoginViewModelTests`, and confirm at least two tests fail. **Restore and re-run to confirm green.**

- [ ] **Step 10: Commit**

```bash
git add Packages/com.echo.harness/Runtime/Presentation \
        Packages/com.echo.harness/TestKit/LoginTestDoubles.cs \
        Packages/com.echo.harness/Runtime/Bootstrap/HarnessComposition.cs \
        Tools/ci/verify-architecture.ps1 \
        Packages/com.echo.harness/Tests/EditMode/LoginViewModelTests.cs
git commit -m "Give Presentation its first real content, and drop the package it never used"
```

---

### Task 6: The view, the layout, and the scene

**Files:**
- Create: `Packages/com.echo.harness/UI/Login.uxml`
- Create: `Packages/com.echo.harness/Runtime/Bootstrap/LoginView.cs`
- Create: `Assets/UI/HarnessPanelSettings.asset`
- Modify: `Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs`
- Modify: `Assets/Scenes/Bootstrap.unity`
- Test: `Packages/com.echo.harness/Tests/PlayMode/LoginViewTests.cs`

**Interfaces:**
- Consumes: `LoginViewModel` and its six bindable property names from Task 5.
- Produces: `Echo.Harness.Bootstrap.LoginView : MonoBehaviour`.

- [ ] **Step 1: Write the layout**

Create `Packages/com.echo.harness/UI/Login.uxml`:

```xml
<ui:UXML xmlns:ui="UnityEngine.UIElements">
    <ui:VisualElement name="login-root" style="padding: 24px; max-width: 420px;">
        <ui:Label name="connection-status" data-source-path="ConnectionStatus" />
        <ui:TextField name="player-name" label="Player name" data-source-path="PlayerName" />
        <ui:Button name="submit" text="Log in" data-source-path="CanSubmit" />
        <ui:Label name="result" data-source-path="ResultText" style="white-space: normal;" />
        <ui:Label name="connection-fault" data-source-path="ConnectionFaultText" style="white-space: normal;" />
    </ui:VisualElement>
</ui:UXML>
```

- [ ] **Step 2: Create the panel settings asset**

In the Unity Editor: `Assets/UI/` → right-click → **Create → UI Toolkit → Panel Settings Asset**, named `HarnessPanelSettings`. Leave every field at its default. Commit it — unlike `HarnessEndpointSettings.asset`, whose ignore rule is unqualified, this is a project-level render setting with nothing machine-specific in it.

- [ ] **Step 3: Write the failing PlayMode test**

Create `Packages/com.echo.harness/Tests/PlayMode/LoginViewTests.cs`:

```csharp
using System.Collections;
using Cysharp.Threading.Tasks;
using Echo.Harness.Application;
using Echo.Harness.Presentation;
using Echo.Harness.TestKit;
using NUnit.Framework;
using UnityEngine.TestTools;
using UnityEngine.UIElements;

namespace Echo.Harness.Tests.PlayMode
{
    public sealed class LoginViewTests
    {
        // The binding is what carries every value to the screen, and a view-model
        // that updates a field nobody is bound to is the failure this catches.
        [UnityTest]
        public IEnumerator TheBindingSurvivesAPlayerLoopFrame()
        {
            var status = new FakeSessionStatus { State = SessionState.Connected };
            var session = new FakeProtocolSession();
            using var router = new SessionFaultRouter(
                session, new RecordingSessionScheduler(), new RecordingFaultLog());
            using var viewModel = new LoginViewModel(status, new FakeLoginUseCase(), router);

            var root = new VisualElement { dataSource = viewModel };
            var label = new Label();
            label.SetBinding(nameof(Label.text), new DataBinding
            {
                dataSourcePath = new PropertyPath(nameof(LoginViewModel.ConnectionStatus)),
            });
            root.Add(label);

            var advanced = false;
            yield return UniTask.ToCoroutine(async () =>
            {
                await UniTask.DelayFrame(2);
                advanced = true;
            });

            Assert.That(advanced, Is.True);
            Assert.That(root.dataSource, Is.SameAs(viewModel));
            Assert.That(viewModel.ConnectionStatus, Is.EqualTo("Connected."));
        }
    }
}
```

- [ ] **Step 4: Run it and verify it fails**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform PlayMode -Filter Echo.Harness.Tests.PlayMode.LoginViewTests`

Expected: FAIL — compile error until Task 5's types resolve; if Task 5 is complete, this may already pass, in which case confirm it fails when `Describe(SessionState.Connected)` is temporarily changed, then restore.

- [ ] **Step 5: Write the view**

Create `Packages/com.echo.harness/Runtime/Bootstrap/LoginView.cs`:

```csharp
using Cysharp.Threading.Tasks;
using Echo.Harness.Presentation;
using UnityEngine;
using UnityEngine.UIElements;
using VContainer;

namespace Echo.Harness.Bootstrap
{
    /// <summary>
    /// Binds <see cref="LoginViewModel"/> to a <see cref="UIDocument"/> and forwards
    /// the button. It holds no logic, because everything it could hold would then
    /// be unreachable from EditMode.
    ///
    /// <para>It lives in Bootstrap rather than beside its view-model because
    /// <c>Echo.Harness.Presentation</c> may not reference VContainer, so an
    /// <c>[Inject]</c> attribute cannot appear there. That split is a real cost,
    /// accepted so the container stays unreachable from every future
    /// view-model.</para>
    /// </summary>
    public sealed class LoginView : MonoBehaviour
    {
        [SerializeField] private UIDocument document;

        private LoginViewModel viewModel;

        [Inject]
        public void Construct(LoginViewModel viewModel) => this.viewModel = viewModel;

        /// <summary>
        /// Start, not OnEnable, and the reason is ordering rather than taste.
        /// VContainer's hierarchy injection completes inside
        /// <c>LifetimeScope.Awake</c>, which is guaranteed to precede Start. Nothing
        /// guarantees it precedes OnEnable, and <c>rootVisualElement</c> is null
        /// until UIDocument's own OnEnable has run - so the OnEnable version fails
        /// in one of two different ways depending on script execution order.
        /// </summary>
        private void Start()
        {
            var root = document.rootVisualElement;
            root.dataSource = viewModel;
            root.Q<Button>("submit").clicked += OnSubmit;
        }

        private void Update() => viewModel.Refresh();

        private void OnSubmit() => viewModel.SubmitAsync().Forget();
    }
}
```

- [ ] **Step 6: Register the component**

In `Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs`, add to `Configure` after the two `RegisterEntryPoint` calls:

```csharp
            builder.RegisterComponentInHierarchy<LoginView>();
```

- [ ] **Step 7: Build the scene**

In the Unity Editor, open `Assets/Scenes/Bootstrap.unity`:

1. Create an empty GameObject named `Login UI`.
2. Add a `UIDocument` component to it.
3. Set its **Panel Settings** to `Assets/UI/HarnessPanelSettings.asset`.
4. Set its **Source Asset** to `Packages/com.echo.harness/UI/Login.uxml`.
5. Add the `LoginView` component to the same GameObject.
6. Drag the `UIDocument` component into `LoginView`'s **Document** field.
7. Save the scene.

- [ ] **Step 8: Run the PlayMode suite**

Run: `pwsh Tools/ci/run-unity-tests.ps1 -Platform PlayMode`

Expected: PASS. If the only failure is `MainThreadSessionSchedulerTests.SwitchingFromAThreadPoolThreadReachesTheMainThread`, re-run — that test is intermittently red for reasons unrelated to this work; see the Global Constraints.

- [ ] **Step 9: Manual acceptance**

With an endpoint configured (`ECHO_SERVER_HOST` or `Assets/Resources/HarnessEndpointSettings.asset`), open `Assets/Scenes/Bootstrap.unity` and press Play. Confirm all four:

1. the status label reaches "Connected.";
2. the button is not usable before that;
3. entering a name and pressing the button shows the real `player_id`;
4. leaving play mode logs no error and no unexplained `SessionFault`.

Record what actually happened, including anything that differed.

- [ ] **Step 10: Run the gate and commit**

Run: `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`

```bash
git add Packages/com.echo.harness/UI \
        Packages/com.echo.harness/Runtime/Bootstrap/LoginView.cs \
        Packages/com.echo.harness/Runtime/Bootstrap/HarnessLifetimeScope.cs \
        Assets/UI Assets/Scenes/Bootstrap.unity \
        Packages/com.echo.harness/Tests/PlayMode/LoginViewTests.cs
git commit -m "Put a login screen on the graph that already talks to the server"
```

---

### Task 7: Documentation

**Files:**
- Modify: `docs/verification-matrix.md`
- Modify: `docs/migration-checklist.md`

**Interfaces:**
- Consumes: everything above. Produces no code.

- [ ] **Step 1: Add the new rows to the verification matrix**

In `docs/verification-matrix.md`, add to the properties table:

```markdown
| A login response becomes something Presentation can act on, and a broken transport is not disguised as a refusal | `LoginUseCaseTests` (9 tests) | The exception policy is the design: a timeout and a duplicate request become `NoAnswer` because the attempt finished badly, while cancellation and everything else escape. `TheReconnectTokenNeverLeavesTheUseCase` is structural rather than behavioural — the failure it prevents is a future helpful addition of the field, which no behavioural test would notice. |
| Faults reach a reader, once, on the right thread | `SessionFaultRouterTests` (13 tests) | **Mutation-verified.** Deleting the hop in the router's UI half fails `TheLogTakesNoHopAndTheObserverTakesOne`. Before this iteration nothing subscribed to `SubscribeToFaults` at all, so all seven kinds were produced and never read. The log deliberately does not hop and the UI delivery does; `NoDestination` is de-duplicated here rather than in `ProtocolSession`, whose contract of publishing every unrouted message is what makes a late subscription visible. |
| The fault sink is constructed rather than merely registered | `verify-architecture.ps1` source assertion on `HarnessLifetimeScope.cs` | **Mutation-verified.** `SessionFaultRouter` subscribes in its constructor and every VContainer registration is lazy, so deleting the `RegisterEntryPoint<SessionFaultRouterEntryPoint>` line leaves a sink that never sees a fault with every test still green. **What this does not prove:** that VContainer runs the registration. It proves the line is present. No test on this runtime can prove the rest, for the same reason `HarnessSessionDriver`'s subscription cannot be asserted. |
| The login screen shows connection state, and a dropped link does not erase a refusal | `LoginViewModelTests` (10 tests), `LoginViewTests` (PlayMode) | **Mutation-verified.** Forcing `CanSubmit` to `true` fails two tests. `ResultText` and `ConnectionFaultText` are separate fields so that a fault arriving mid-read cannot overwrite the message the user is looking at. |
```

- [ ] **Step 2: Record the manual acceptance procedure**

In `docs/verification-matrix.md`, in the section on what the gate does not enforce, extend the manual-acceptance paragraph:

```markdown
The manual acceptance check now has a second half, and it has the same standing as
the first: **nothing in the gate runs it, and nothing notices if it is never run
again.** With an endpoint configured, open `Assets/Scenes/Bootstrap.unity` and
press Play; the status label must reach "Connected.", the login button must be
unusable before that, a submitted name must return a real `player_id` from the Go
server, and leaving play mode must log no error and no unexplained `SessionFault`.
No automated test loads that scene, and the end-to-end tier that could check the
wire half skips itself on any machine without an endpoint.
```

- [ ] **Step 3: Update the checklist**

In `docs/migration-checklist.md`, under **Phase 2 — vertical slice**, replace:

```markdown
- [ ] Implement typed login DTOs and one login use case.
- [ ] Build one UI Toolkit view/view-model pair without infrastructure access.
```

with:

```markdown
- [x] Implement typed login DTOs and one login use case. The DTOs predate this
  iteration — `LoginRequestDto`/`LoginResponseDto` landed with the contract typing,
  and `GoServerEndToEndTests.LoginOverARealSocketReturnsATypedResponse` already
  proved the wire. What this iteration added is `LoginUseCase` and the
  Application-level `LoginOutcome` that Presentation can actually see. **Not
  closed by it:** `LoginResponseDto.ReconnectToken` is read and dropped. There is
  no persistence and no reconnect path, and `LoginOutcome` carries no token field —
  `LoginUseCaseTests.TheReconnectTokenNeverLeavesTheUseCase` keeps it that way
  until someone builds the storage decision that goes with it.
- [x] Build one UI Toolkit view/view-model pair without infrastructure access.
  `LoginViewModel` is in Presentation and reaches infrastructure through nothing:
  it takes `ISessionStatus`, `ILoginUseCase` and `SessionFaultRouter`, all
  Application types. The pair is split across two assemblies — the view is in
  Bootstrap — because Presentation may not reference VContainer and so cannot carry
  an `[Inject]` attribute. That cost was accepted rather than widening the
  reference list; see the design spec.
```

- [ ] **Step 4: Record the scope trigger**

In `docs/migration-checklist.md`, replace the closing sentences of the "Define app/session/scene VContainer lifetime scopes" item with:

```markdown
  The login slice did not change this, and the reason is now narrower rather than
  restated: the login screen never goes away, so a child scope would have a
  lifetime identical to its parent. **What has changed is that the deferral is no
  longer open-ended.** Two events force the decision, and whoever hits either one
  owns it: the first screen that is destroyed while the application keeps running
  forces a UI scope, and the first flow that must survive a logout without reusing
  the same `ProtocolSession` forces a session scope. Until one of those exists there
  is nothing for a second scope to mean. `CompositionSmokeTests` still carries the
  warning that matters on that day — with a child scope, `Scoped` would give every
  scope its own `ProtocolSession` over its own `TcpTransport`.
```

- [ ] **Step 5: Close the fault-sink remainder**

In `docs/migration-checklist.md`, in the "Revisit the request-timeout hop's non-cancellation failure path" item, replace the sentence beginning "**Who reads it is a separate question and the honest answer today is nobody**" with:

```markdown
  **Who reads it is no longer nobody.** `SessionFaultRouter` subscribes to
  `SubscribeToFaults` and writes every kind to the Unity console through
  `IFaultLog`, so the fault this path publishes now leaves a trace a person can
  find. The two connection kinds also reach the login screen. The five that do not
  are logged and stop there, because no interface element could express them while
  a login screen is the only screen.
```

- [ ] **Step 6: Run the gate**

Run: `pwsh Tools/ci/verify-architecture.ps1 -GoServerRoot 'E:\code\_github\magic-card-server-golang'`

Expected: `Architecture verification passed.`

- [ ] **Step 7: Commit**

```bash
git add docs/verification-matrix.md docs/migration-checklist.md
git commit -m "Record what the login slice proved, and what it left open"
```

---

## Self-Review

**Spec coverage.** Every section of the spec maps to a task: Placement → all; the login use case and its exception policy → Task 2; `reconnect_token` dropped → Task 2 Step 6 plus its structural test; the fault sink and the thread split → Task 3; `NoDestination` de-duplication → Task 3; "the router must be resolved" → Task 4; `ISessionStatus` → Task 1; the view-model and view → Tasks 5-6; assets → Task 6; R3 removal → Task 5; scopes → Task 7 Step 4; testing including the three mutation checks → Tasks 3, 4, 5; Out of scope → nothing in this plan touches reconnect, matchmaking, child scopes, or the flaky scheduler test.

**One deviation from the spec, stated rather than buried.** The spec said `CompositionSmokeTests` would assert `SessionFaultRouterEntryPoint` is registered. It cannot: that registration is in `HarnessLifetimeScope.Configure`, a `protected override` on a MonoBehaviour with no scene in EditMode. Task 4 substitutes a source-text assertion in the architecture gate, which the repository already uses for exactly this class of check, and both the gate comment and the verification-matrix row say plainly that it proves the line exists and not that VContainer runs it.

**Type consistency.** `LoginOutcome` factories are `Success` / `Refusal` / `NoReply` throughout — deliberately not matching the `LoginResult` members `Succeeded` / `Rejected` / `NoAnswer`, so the two are never confused at a call site. `SessionFaultRouter`'s constructor is `(IProtocolSession, ISessionScheduler, IFaultLog)` in Tasks 3, 5 and 6. `LoginViewModel`'s constructor is `(ISessionStatus, ILoginUseCase, SessionFaultRouter)` in Tasks 5 and 6. The six bindable property names in Task 5 match the five `data-source-path` values in Task 6's UXML plus `IsBusy`, which is bound by nothing yet and is used only by `CanSubmit` and the tests.
