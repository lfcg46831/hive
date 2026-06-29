# US-F0-07-T02 AI Gateway Service Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Implement the first executable AI gateway boundary as the provider-neutral DI service `IAiGateway`, without introducing an `AiGatewayActor` or exposing provider SDK types to the domain.

**Architecture:** `Hive.Domain.Ai` owns the public port `IAiGateway` because it already owns the provider-neutral request and response contracts. `Hive.Infrastructure.Ai` owns the executable service, an adapter interface for provider implementations, and the default unavailable provider. The common bootstrap registers the gateway with replaceable `TryAddSingleton` wiring so future provider tasks can plug in the deterministic stub or a real `Microsoft.Extensions.AI` adapter without changing callers.

**Tech Stack:** .NET 8, C#, xUnit, `Microsoft.Extensions.DependencyInjection`, existing HIVE solution/project boundaries.

---

## File Structure

- Create `src/Hive.Domain/Ai/IAiGateway.cs`: provider-neutral async gateway port.
- Create `src/Hive.Infrastructure/Ai/IAiGatewayProvider.cs`: infrastructure provider adapter contract using only HIVE DTOs.
- Create `src/Hive.Infrastructure/Ai/AiGateway.cs`: executable `IAiGateway` implementation that validates the request and delegates to the provider adapter.
- Create `src/Hive.Infrastructure/Ai/UnavailableAiGatewayProvider.cs`: default provider that returns a structured configuration failure until T03/T04/T05 configure a real provider path.
- Create `src/Hive.Infrastructure/Ai/AiGatewayServiceCollectionExtensions.cs`: DI registration extension with replaceable defaults.
- Modify `src/Hive.Infrastructure/Configuration/HiveBootstrapExtensions.cs`: call `AddHiveAiGateway()`.
- Create `tests/Hive.Tests/AiGatewayServiceContractTests.cs`: reflection test for the domain port contract.
- Create `tests/Hive.Tests/AiGatewayServiceTests.cs`: unit tests for request validation and delegation.
- Create `tests/Hive.Tests/AiGatewayBootstrapTests.cs`: DI tests for default registration and provider replacement.
- Modify `tests/Hive.Tests/CompositionTests.cs`: assert host bootstrap resolves `IAiGateway`.
- Optionally modify `docs/bible.html` only after code and tests pass: update the next-iteration marker and record the durable T02 contract if the implementation establishes a contract not already captured by version 0.93.

---

### Task 1: Add the Domain Gateway Port

**Files:**
- Create: `tests/Hive.Tests/AiGatewayServiceContractTests.cs`
- Create: `src/Hive.Domain/Ai/IAiGateway.cs`

- [ ] **Step 1: Write the failing contract test**

Create `tests/Hive.Tests/AiGatewayServiceContractTests.cs`:

```csharp
using Hive.Domain.Ai;

namespace Hive.Tests;

public sealed class AiGatewayServiceContractTests
{
    [Fact]
    public void Gateway_port_lives_in_domain_and_uses_only_hive_contracts()
    {
        Assert.True(typeof(IAiGateway).IsInterface);
        Assert.Equal("Hive.Domain.Ai", typeof(IAiGateway).Namespace);
        Assert.Same(typeof(AiGatewayRequest).Assembly, typeof(IAiGateway).Assembly);

        var method = typeof(IAiGateway).GetMethod(nameof(IAiGateway.CompleteAsync));

        Assert.NotNull(method);
        Assert.Equal(typeof(Task<AiGatewayResponse>), method.ReturnType);

        var parameters = method.GetParameters();
        Assert.Collection(
            parameters,
            request =>
            {
                Assert.Equal("request", request.Name);
                Assert.Equal(typeof(AiGatewayRequest), request.ParameterType);
            },
            cancellation =>
            {
                Assert.Equal("cancellationToken", cancellation.Name);
                Assert.Equal(typeof(CancellationToken), cancellation.ParameterType);
                Assert.True(cancellation.HasDefaultValue);
                Assert.Equal(default(CancellationToken), cancellation.DefaultValue);
            });
    }
}
```

- [ ] **Step 2: Run the test to verify RED**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayServiceContractTests" -v minimal
```

Expected: build fails with `CS0246` or equivalent because `IAiGateway` does not exist.

- [ ] **Step 3: Add the minimal domain interface**

Create `src/Hive.Domain/Ai/IAiGateway.cs`:

```csharp
namespace Hive.Domain.Ai;

public interface IAiGateway
{
    Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default);
}
```

- [ ] **Step 4: Run the test to verify GREEN**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayServiceContractTests" -v minimal
```

Expected: `AiGatewayServiceContractTests` passes.

- [ ] **Step 5: Commit Task 1**

```powershell
git add src\Hive.Domain\Ai\IAiGateway.cs tests\Hive.Tests\AiGatewayServiceContractTests.cs
git commit -m "feat(domain): add AI gateway service port (US-F0-07-T02)"
```

---

### Task 2: Implement the Thin Infrastructure Gateway

**Files:**
- Create: `tests/Hive.Tests/AiGatewayServiceTests.cs`
- Create: `src/Hive.Infrastructure/Ai/IAiGatewayProvider.cs`
- Create: `src/Hive.Infrastructure/Ai/AiGateway.cs`

- [ ] **Step 1: Write the failing infrastructure service tests**

Create `tests/Hive.Tests/AiGatewayServiceTests.cs`:

```csharp
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiGatewayServiceTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task CompleteAsync_rejects_null_request_without_calling_provider()
    {
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await gateway.CompleteAsync(null!));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void Constructor_rejects_null_provider()
    {
        Assert.Throws<ArgumentNullException>(() => new AiGateway(null!));
    }

    [Fact]
    public async Task CompleteAsync_delegates_request_and_cancellation_to_provider()
    {
        var request = Request();
        var response = SuccessResponse();
        var provider = new RecordingAiGatewayProvider(response);
        var gateway = new AiGateway(provider);
        using var cancellation = new CancellationTokenSource();

        var result = await gateway.CompleteAsync(request, cancellation.Token);

        Assert.Same(response, result);
        Assert.Equal(1, provider.CallCount);
        Assert.Same(request, provider.Request);
        Assert.Equal(cancellation.Token, provider.CancellationToken);
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.");

    private static AiGatewayResponse SuccessResponse() =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The bug is reproducible.",
            AiFinishReason.Stop);

    private sealed class RecordingAiGatewayProvider(AiGatewayResponse response)
        : IAiGatewayProvider
    {
        public int CallCount { get; private set; }

        public AiGatewayRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;

            return Task.FromResult(response);
        }
    }
}
```

- [ ] **Step 2: Run the test to verify RED**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayServiceTests" -v minimal
```

Expected: build fails with `CS0234` or `CS0246` because `Hive.Infrastructure.Ai`, `AiGateway`, and `IAiGatewayProvider` do not exist.

- [ ] **Step 3: Add the provider adapter interface**

Create `src/Hive.Infrastructure/Ai/IAiGatewayProvider.cs`:

```csharp
using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

public interface IAiGatewayProvider
{
    Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken);
}
```

- [ ] **Step 4: Add the thin gateway implementation**

Create `src/Hive.Infrastructure/Ai/AiGateway.cs`:

```csharp
using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

public sealed class AiGateway : IAiGateway
{
    private readonly IAiGatewayProvider _provider;

    public AiGateway(IAiGatewayProvider provider)
    {
        _provider = provider ?? throw new ArgumentNullException(nameof(provider));
    }

    public Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);

        return _provider.CompleteAsync(request, cancellationToken);
    }
}
```

- [ ] **Step 5: Run the service tests to verify GREEN**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayServiceTests" -v minimal
```

Expected: `AiGatewayServiceTests` passes.

- [ ] **Step 6: Commit Task 2**

```powershell
git add src\Hive.Infrastructure\Ai\IAiGatewayProvider.cs src\Hive.Infrastructure\Ai\AiGateway.cs tests\Hive.Tests\AiGatewayServiceTests.cs
git commit -m "feat(infrastructure): add executable AI gateway service (US-F0-07-T02)"
```

---

### Task 3: Add Default Provider and DI Registration

**Files:**
- Create: `tests/Hive.Tests/AiGatewayBootstrapTests.cs`
- Create: `src/Hive.Infrastructure/Ai/UnavailableAiGatewayProvider.cs`
- Create: `src/Hive.Infrastructure/Ai/AiGatewayServiceCollectionExtensions.cs`

- [ ] **Step 1: Write the failing DI tests**

Create `tests/Hive.Tests/AiGatewayBootstrapTests.cs`:

```csharp
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class AiGatewayBootstrapTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task AddHiveAiGateway_registers_default_executable_gateway()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGateway();

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        Assert.IsType<AiGateway>(gateway);

        var response = await gateway.CompleteAsync(Request());

        Assert.False(response.IsSuccess);
        Assert.True(response.IsFailure);
        Assert.NotNull(response.Error);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, response.Error.Code);
        Assert.Equal("AI gateway provider is not configured.", response.Error.Message);
        Assert.False(response.Error.IsRetryable);
        Assert.Equal(Organization, response.Error.OrganizationId);
        Assert.Equal(Position, response.Error.PositionId);
        Assert.Equal(Thread, response.Error.ThreadId);
        Assert.Equal(Message, response.Error.MessageId);
        Assert.Null(response.Error.Provider);
    }

    [Fact]
    public async Task AddHiveAiGateway_preserves_pre_registered_provider()
    {
        var expected = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The injected provider handled the call.",
            AiFinishReason.Stop);
        var services = new ServiceCollection();
        services.AddSingleton<IAiGatewayProvider>(new FixedAiGatewayProvider(expected));
        services.AddHiveAiGateway();

        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.Same(expected, response);
    }

    [Fact]
    public async Task Default_provider_propagates_cancellation()
    {
        var services = new ServiceCollection();
        services.AddHiveAiGateway();
        using var provider = services.BuildServiceProvider();
        var gateway = provider.GetRequiredService<IAiGateway>();
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await gateway.CompleteAsync(Request(), cancellation.Token));
    }

    private static AiGatewayRequest Request() =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.");

    private sealed class FixedAiGatewayProvider(AiGatewayResponse response)
        : IAiGatewayProvider
    {
        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken) =>
            Task.FromResult(response);
    }
}
```

- [ ] **Step 2: Run the test to verify RED**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayBootstrapTests" -v minimal
```

Expected: build fails with `CS1061` or equivalent because `AddHiveAiGateway` does not exist.

- [ ] **Step 3: Add the unavailable provider**

Create `src/Hive.Infrastructure/Ai/UnavailableAiGatewayProvider.cs`:

```csharp
using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal sealed class UnavailableAiGatewayProvider : IAiGatewayProvider
{
    public Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var error = new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.ConfigurationInvalid,
            "AI gateway provider is not configured.",
            isRetryable: false);

        return Task.FromResult(AiGatewayResponse.Failed(error));
    }
}
```

- [ ] **Step 4: Add the service registration extension**

Create `src/Hive.Infrastructure/Ai/AiGatewayServiceCollectionExtensions.cs`:

```csharp
using Hive.Domain.Ai;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;

namespace Hive.Infrastructure.Ai;

public static class AiGatewayServiceCollectionExtensions
{
    public static IServiceCollection AddHiveAiGateway(this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.TryAddSingleton<IAiGatewayProvider, UnavailableAiGatewayProvider>();
        services.TryAddSingleton<IAiGateway, AiGateway>();

        return services;
    }
}
```

- [ ] **Step 5: Run the DI tests to verify GREEN**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayBootstrapTests" -v minimal
```

Expected: `AiGatewayBootstrapTests` passes.

- [ ] **Step 6: Commit Task 3**

```powershell
git add src\Hive.Infrastructure\Ai\UnavailableAiGatewayProvider.cs src\Hive.Infrastructure\Ai\AiGatewayServiceCollectionExtensions.cs tests\Hive.Tests\AiGatewayBootstrapTests.cs
git commit -m "feat(infrastructure): register AI gateway defaults (US-F0-07-T02)"
```

---

### Task 4: Wire the Gateway into Common Bootstrap

**Files:**
- Modify: `tests/Hive.Tests/CompositionTests.cs`
- Modify: `src/Hive.Infrastructure/Configuration/HiveBootstrapExtensions.cs`

- [ ] **Step 1: Write the failing composition assertions**

Modify `tests/Hive.Tests/CompositionTests.cs`.

Add this using:

```csharp
using Hive.Domain.Ai;
```

In `Api_entry_point_starts_with_required_services_and_api_role`, add the gateway assertion after `NodeDiagnosticsProvider` is resolved:

```csharp
Assert.NotNull(app.Services.GetRequiredService<IAiGateway>());
```

In `Worker_entry_point_starts_with_required_services_and_backend_roles`, add the gateway assertion after `NodeDiagnosticsProvider` is resolved:

```csharp
Assert.NotNull(host.Services.GetRequiredService<IAiGateway>());
```

- [ ] **Step 2: Run the composition tests to verify RED**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~CompositionTests" -v minimal
```

Expected: at least the API and worker startup tests fail with `InvalidOperationException: No service for type 'Hive.Domain.Ai.IAiGateway' has been registered.`

- [ ] **Step 3: Register the gateway in common bootstrap**

Modify `src/Hive.Infrastructure/Configuration/HiveBootstrapExtensions.cs`.

Add this using:

```csharp
using Hive.Infrastructure.Ai;
```

Add this registration after `builder.Services.AddSingleton<ActiveNodeRoles>();`:

```csharp
builder.Services.AddHiveAiGateway();
```

The top of the file should include:

```csharp
using Hive.Domain.Positions;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Diagnostics;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.Logging;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Hive.Infrastructure.Persistence.PostgreSql;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.DependencyInjection.Extensions;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Options;
```

The bootstrap body should contain:

```csharp
builder.Services.AddSingleton<ActiveNodeRoles>();
builder.Services.AddHiveAiGateway();
builder.Services.TryAddSingleton<IPositionConfigurationProvider>(serviceProvider =>
{
    var connectionString = serviceProvider
        .GetRequiredService<IConfiguration>()
        .GetConnectionString(ConnectionStringNames.PostgreSql);

    return string.IsNullOrWhiteSpace(connectionString)
        ? new UnavailablePositionConfigurationProvider(ConnectionStringNames.PostgreSql)
        : new PostgreSqlPositionConfigurationProvider(connectionString);
});
```

- [ ] **Step 4: Run the composition tests to verify GREEN**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~CompositionTests" -v minimal
```

Expected: `CompositionTests` passes.

- [ ] **Step 5: Commit Task 4**

```powershell
git add src\Hive.Infrastructure\Configuration\HiveBootstrapExtensions.cs tests\Hive.Tests\CompositionTests.cs
git commit -m "feat(hosting): wire AI gateway into bootstrap (US-F0-07-T02)"
```

---

### Task 5: Verify Boundaries and Update the Bible if Needed

**Files:**
- Test: `tests/Hive.Tests/DomainIsolationTests.cs`
- Optional modify: `docs/bible.html`

- [ ] **Step 1: Run the domain boundary test**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~DomainIsolationTests" -v minimal
```

Expected: `DomainIsolationTests` passes. This proves `Hive.Domain` still has no project/package references and no `Microsoft.Extensions.AI`, provider SDK, hosting, DI, or Akka assembly references.

- [ ] **Step 2: Inspect whether the bible needs a durable contract update**

Run:

```powershell
rg -n -C 4 "US-F0-07-T02|Proxima iteracao|IAiGateway|AI Gateway" docs\bible.html
```

Expected: version 0.93 already records the architectural decision, section 6.3 names `IAiGateway`, and the footer still points at implementing `US-F0-07-T02`.

- [ ] **Step 3: If implementation introduces only the planned contract, update the footer and history surgically**

Apply a surgical edit to `docs/bible.html`:

1. Add this version-history row immediately after the existing `0.93` row:

```html
<tr><td>0.94</td><td>2026-06-29</td><td>Registo de <code>US-F0-07-T02</code>: <code>IAiGateway</code> passa a fronteira executavel provider-neutral registada no bootstrap comum, com implementacao inicial em infraestrutura e provider indisponivel estruturado ate configuracao/stub/provider real das tarefas seguintes; <code>AiGatewayActor</code> permanece fora da tarefa.</td></tr>
```

2. Replace the final next-iteration marker with:

```html
<p><em>Proxima iteracao (v0.95): implementar <code>US-F0-07-T03</code> para mapear configuracao de AI da posicao a partir do registry.</em></p>
```

Do not rewrite the whole file.

- [ ] **Step 4: Verify bible integrity if edited**

Run:

```powershell
git diff -- docs\bible.html
Get-Content docs\bible.html -Tail 1
(Get-Content docs\bible.html).Count
```

Expected:

- `git diff -- docs\bible.html` shows only the new `0.94` row and the footer update.
- The last line is `</html>`.
- The line count has not dropped unexpectedly.

- [ ] **Step 5: Commit Task 5 if the bible was edited**

```powershell
git add docs\bible.html
git commit -m "docs: record AI gateway service implementation (US-F0-07-T02)"
```

If `docs/bible.html` was not edited because the durable contract was already fully captured, do not create a documentation commit for this task.

---

### Task 6: Final Verification

**Files:**
- Verify all files changed by Tasks 1 through 5.

- [ ] **Step 1: Run focused AI gateway and boundary tests**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~AiGatewayServiceContractTests|FullyQualifiedName~AiGatewayServiceTests|FullyQualifiedName~AiGatewayBootstrapTests|FullyQualifiedName~DomainIsolationTests|FullyQualifiedName~CompositionTests" -v minimal
```

Expected: all selected tests pass.

- [ ] **Step 2: Run the full test project**

Run:

```powershell
dotnet test tests\Hive.Tests\Hive.Tests.csproj --no-restore -v minimal
```

Expected: the full `Hive.Tests` project passes. If PostgreSQL/Testcontainers infrastructure is unavailable, record the exact failure and keep the focused test results as completed verification.

- [ ] **Step 3: Inspect final diff and status**

Run:

```powershell
git status --short
git diff --stat HEAD
```

Expected: the working tree only contains intentional changes for `US-F0-07-T02`, unless local user changes were present before the task and are unrelated.

- [ ] **Step 4: Commit any remaining implementation changes**

If any intentional implementation changes remain uncommitted after Tasks 1 through 5, commit them:

```powershell
git add src\Hive.Domain\Ai\IAiGateway.cs src\Hive.Infrastructure\Ai tests\Hive.Tests\AiGatewayServiceContractTests.cs tests\Hive.Tests\AiGatewayServiceTests.cs tests\Hive.Tests\AiGatewayBootstrapTests.cs src\Hive.Infrastructure\Configuration\HiveBootstrapExtensions.cs tests\Hive.Tests\CompositionTests.cs
git commit -m "feat(ai): add executable gateway service boundary (US-F0-07-T02)"
```

If all task commits already exist and `git status --short` has no intentional uncommitted implementation changes, skip this commit.

## Self-Review

- Spec coverage: Task 1 adds the provider-neutral `IAiGateway` port; Tasks 2 and 3 add the executable infrastructure service, adapter seam, unavailable default, and replaceable DI registration; Task 4 wires the common bootstrap; Task 5 preserves the bible as the source of truth; Task 6 verifies focused and full behavior.
- Scope check: no `AiGatewayActor`, no registry AI configuration mapping, no deterministic stub, no real provider adapter, no provider SDK package, no network call, no policy/budget/timeout/audit implementation.
- Type consistency: `IAiGateway.CompleteAsync(AiGatewayRequest, CancellationToken = default)` returns `Task<AiGatewayResponse>` throughout; `IAiGatewayProvider.CompleteAsync(AiGatewayRequest, CancellationToken)` is the infrastructure adapter contract; `AiGateway` implements the domain port and delegates to the adapter.
- Placeholder scan: no open implementation placeholders are required by the plan.
