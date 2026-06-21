# US-F0-04-T04 Directive Routing Validator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate downward `Directive` routing so only a direct superior can address its direct subordinate, with canonical structured errors for missing registry entities.

**Architecture:** Add a focused `DirectiveRoutingValidator` to `Hive.Domain.Messaging`. It consumes the `Directive` row from `MessageRoutingRules`, probes organization and position existence through `IOrganizationRelations`, then proves the route by comparing the destination's direct superior with the source; confirmed absences become `ValidationResult` errors while technical failures propagate.

**Tech Stack:** .NET 8, C#, xUnit, `Hive.Domain` organization relations and validation contracts

---

### Task 1: Specify downward directive routing

**Files:**
- Create: `tests/Hive.Tests/DirectiveRoutingValidatorTests.cs`

- [ ] **Step 1: Write the failing validator tests**

Create `DirectiveRoutingValidatorTests.cs`:

```csharp
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Tests;

public sealed class DirectiveRoutingValidatorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Direct_superior_to_direct_subordinate_is_valid()
    {
        var validator = new DirectiveRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(Directive("delivery-lead", "engineer"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("ceo", "engineer")]
    [InlineData("engineer", "delivery-lead")]
    [InlineData("delivery-lead", "ceo")]
    public async Task Non_direct_downward_routes_are_rejected(string from, string to)
    {
        var validator = new DirectiveRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(Directive(from, to));

        Assert.Equal(
            [new ValidationError(
                "direct-subordinate-required",
                "to.positionId",
                RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Invalid_endpoint_variants_are_aggregated_without_registry_queries()
    {
        var validator = new DirectiveRoutingValidator(new FailingRelations(
            new InvalidOperationException("Registry must not be queried.")));
        var directive = Directive(
            new OrganizationOwnerEndpointRef(),
            new SystemEndpointRef(SystemEndpointKind.Scheduler));

        var result = await validator.ValidateAsync(directive);

        Assert.Equal(
            [
                new ValidationError("endpoint-not-allowed", "from", RejectionReason.InvalidRoute),
                new ValidationError("endpoint-not-allowed", "to", RejectionReason.InvalidRoute),
            ],
            result.Errors);
    }

    [Fact]
    public async Task Unknown_organization_returns_canonical_error()
    {
        var validator = new DirectiveRoutingValidator(SampleRelations());
        var directive = Directive(
            OrganizationId.From("unknown-organization"),
            Position("delivery-lead"),
            Position("engineer"));

        var result = await validator.ValidateAsync(directive);

        Assert.Equal(
            [new ValidationError(
                "organization-not-found",
                "organizationId",
                RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Unknown_source_and_destination_positions_are_aggregated()
    {
        var validator = new DirectiveRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(
            Directive("ghost-source", "ghost-destination"));

        Assert.Equal(
            [
                new ValidationError(
                    "position-not-found",
                    "from.positionId",
                    RejectionReason.InvalidRoute),
                new ValidationError(
                    "position-not-found",
                    "to.positionId",
                    RejectionReason.InvalidRoute),
            ],
            result.Errors);
    }

    [Theory]
    [InlineData("ghost", "engineer", "from.positionId")]
    [InlineData("delivery-lead", "ghost", "to.positionId")]
    public async Task A_single_unknown_position_uses_its_canonical_path(
        string from,
        string to,
        string expectedPath)
    {
        var validator = new DirectiveRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(Directive(from, to));

        Assert.Equal(
            [new ValidationError(
                "position-not-found",
                expectedPath,
                RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public void Missing_relations_dependency_is_api_misuse()
    {
        Assert.Throws<ArgumentNullException>(
            () => new DirectiveRoutingValidator(null!));
    }

    [Fact]
    public async Task Missing_directive_is_api_misuse()
    {
        var validator = new DirectiveRoutingValidator(SampleRelations());

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await validator.ValidateAsync(null!));
    }

    [Fact]
    public async Task Cancellation_propagates_before_registry_access()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validator = new DirectiveRoutingValidator(new FailingRelations(
            new InvalidOperationException("Registry must not be queried.")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await validator.ValidateAsync(
                Directive("delivery-lead", "engineer"),
                cancellation.Token));
    }

    [Fact]
    public async Task Unexpected_registry_failure_propagates()
    {
        var failure = new InvalidOperationException("Registry unavailable.");
        var validator = new DirectiveRoutingValidator(new FailingRelations(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await validator.ValidateAsync(
                Directive("delivery-lead", "engineer")));

        Assert.Same(failure, thrown);
    }

    private static Directive Directive(string from, string to) =>
        Directive(Position(from), Position(to));

    private static Directive Directive(EndpointRef from, EndpointRef to) =>
        Directive(Org, from, to);

    private static Directive Directive(
        OrganizationId organizationId,
        EndpointRef from,
        EndpointRef to) =>
        new(
            MessageId.New(),
            organizationId,
            from,
            to,
            ThreadId.New(),
            Priority.Normal,
            1,
            SentAt,
            null,
            DirectiveId.New(),
            null,
            "Deliver the assigned work",
            "Use the current organizational context");

    private static PositionEndpointRef Position(string value) =>
        new(PositionId.From(value));

    private static IOrganizationRelations SampleRelations() =>
        new MaterializedOrganizationRelations(
            OrganizationRelationsSnapshot
                .CreateBuilder(Org, new OrganizationOwnerEndpointRef())
                .AddPosition(PositionId.From("ceo"), UnitId.From("root"))
                .AddPosition(
                    PositionId.From("delivery-lead"),
                    UnitId.From("delivery"),
                    PositionId.From("ceo"))
                .AddPosition(
                    PositionId.From("engineer"),
                    UnitId.From("delivery"),
                    PositionId.From("delivery-lead"))
                .Build());

    private sealed class FailingRelations(Exception failure) : IOrganizationRelations
    {
        public ValueTask<PositionId?> GetDirectSuperiorAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<PositionId?>(failure);

        public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<IReadOnlyCollection<PositionId>>(failure);

        public ValueTask<PositionId> GetRootUnitLeadershipAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<PositionId>(failure);

        public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<OrganizationOwnerEndpointRef>(failure);

        public ValueTask<UnitId?> GetUnitOfPositionAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<UnitId?>(failure);
    }
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~DirectiveRoutingValidatorTests -v minimal
```

Expected: compilation fails because `DirectiveRoutingValidator` does not exist.

### Task 2: Implement the focused validator

**Files:**
- Create: `src/Hive.Domain/Messaging/DirectiveRoutingValidator.cs`
- Test: `tests/Hive.Tests/DirectiveRoutingValidatorTests.cs`

- [ ] **Step 1: Add the minimal implementation**

Create `DirectiveRoutingValidator.cs`:

```csharp
using Hive.Domain.Organization;

namespace Hive.Domain.Messaging;

public sealed class DirectiveRoutingValidator
{
    private static readonly RoutingPathRule DirectivePath =
        MessageRoutingRules.For<Directive>().Paths.Single(
            path => path.Relation == RoutingRelation.DirectSuperiorToDirectSubordinate);

    private readonly IOrganizationRelations _relations;

    public DirectiveRoutingValidator(IOrganizationRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        Directive directive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<ValidationError>();
        var from = RequirePositionEndpoint(
            directive.From,
            DirectivePath.FromEndpointType,
            "from",
            errors);
        var to = RequirePositionEndpoint(
            directive.To,
            DirectivePath.ToEndpointType,
            "to",
            errors);

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        var fromProbe = await ProbePositionAsync(
            directive,
            from!,
            cancellationToken);
        if (fromProbe.OrganizationMissing)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }

        var toProbe = await ProbePositionAsync(
            directive,
            to!,
            cancellationToken);
        if (toProbe.OrganizationMissing)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }

        if (!fromProbe.Exists)
        {
            errors.Add(PositionNotFound("from.positionId"));
        }

        if (!toProbe.Exists)
        {
            errors.Add(PositionNotFound("to.positionId"));
        }

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        try
        {
            var superior = await _relations.GetDirectSuperiorAsync(
                directive.OrganizationId,
                to!.PositionId,
                cancellationToken);

            return superior == from!.PositionId
                ? ValidationResult.Valid
                : ValidationResult.Create([DirectSubordinateRequired()]);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([PositionNotFound("to.positionId")]);
        }
    }

    private async ValueTask<PositionProbe> ProbePositionAsync(
        Directive directive,
        PositionEndpointRef endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var unit = await _relations.GetUnitOfPositionAsync(
                directive.OrganizationId,
                endpoint.PositionId,
                cancellationToken);
            return new PositionProbe(unit is not null, OrganizationMissing: false);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return new PositionProbe(Exists: false, OrganizationMissing: true);
        }
    }

    private static PositionEndpointRef? RequirePositionEndpoint(
        EndpointRef endpoint,
        Type expectedType,
        string path,
        ICollection<ValidationError> errors)
    {
        if (endpoint.GetType() == expectedType)
        {
            return (PositionEndpointRef)endpoint;
        }

        errors.Add(new ValidationError(
            "endpoint-not-allowed",
            path,
            RejectionReason.InvalidRoute));
        return null;
    }

    private static ValidationError OrganizationNotFound() =>
        new("organization-not-found", "organizationId", RejectionReason.InvalidRoute);

    private static ValidationError PositionNotFound(string path) =>
        new("position-not-found", path, RejectionReason.InvalidRoute);

    private static ValidationError DirectSubordinateRequired() =>
        new("direct-subordinate-required", "to.positionId", RejectionReason.InvalidRoute);

    private readonly record struct PositionProbe(bool Exists, bool OrganizationMissing);
}
```

- [ ] **Step 2: Run the focused tests and verify GREEN**

Run the focused command from Task 1.

Expected: 13 test cases pass with zero failures.

- [ ] **Step 3: Run existing routing and relations tests**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageRoutingRulesTests|FullyQualifiedName~MaterializedOrganizationRelationsTests|FullyQualifiedName~DirectiveRoutingValidatorTests" -v minimal
```

Expected: all selected tests pass with zero failures.

### Task 3: Record the durable T04 contract

**Files:**
- Modify: `docs/bible.html`

- [ ] **Step 1: Add the history entry**

After version `0.62`, add:

```html
<tr><td>0.63</td><td>2026-06-22</td><td>Registo de <code>US-F0-04-T04</code>: <code>DirectiveRoutingValidator</code> valida diretivas descendentes contra <code>IOrganizationRelations</code>, aceita apenas superior direto → subordinado direto, agrega ausências confirmadas com <code>InvalidRoute</code> e mantém cancelamento/falhas técnicas como exceções sujeitas a retry</td></tr>
```

- [ ] **Step 2: Add the implementation contract**

After the `US-F0-04-T03` routing-matrix paragraph, add:

```html
<p><strong>Validador de diretivas descendentes (US-F0-04-T04):</strong> <code>DirectiveRoutingValidator</code> consome a linha de <code>Directive</code> de <code>MessageRoutingRules</code> e consulta <code>IOrganizationRelations</code>. Exige <code>PositionEndpointRef</code> na origem e no destino, confirma a existência da organização e das duas posições e aceita a rota apenas quando <code>GetDirectSuperiorAsync</code> do destino devolve a posição de origem. Organização/posições inexistentes usam os erros canónicos de §9.8; endpoint incompatível usa <code>endpoint-not-allowed</code> no campo correspondente e uma relação que não seja superior direto → subordinado direto usa <code>direct-subordinate-required</code> em <code>to.positionId</code>, sempre com <code>InvalidRoute</code>. Cancelamento, indisponibilidade e falhas inesperadas do registry permanecem falhas técnicas e são propagadas.</p>
```

- [ ] **Step 3: Verify the documentation contract**

Run:

```powershell
rg -n "0.63|DirectiveRoutingValidator|direct-subordinate-required" docs/bible.html
```

Expected: one history entry and one T04 contract paragraph are reported.

### Task 4: Verify the completed task

**Files:**
- Test: `tests/Hive.Tests/DirectiveRoutingValidatorTests.cs`
- Test: `tests/Hive.Tests/Hive.Tests.csproj`
- Build: `Hive.sln`

- [ ] **Step 1: Run focused tests**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~DirectiveRoutingValidatorTests -v minimal
```

Expected: all 13 test cases pass with zero failures.

- [ ] **Step 2: Run the full test project**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore -v minimal
```

Expected: all tests pass with zero failures.

- [ ] **Step 3: Build the solution**

```powershell
dotnet build Hive.sln --no-restore -v minimal
```

Expected: build succeeds with zero warnings and zero errors.

- [ ] **Step 4: Inspect the final diff**

Run each command:

```powershell
git diff --check
git status --short
git diff -- src/Hive.Domain/Messaging/DirectiveRoutingValidator.cs tests/Hive.Tests/DirectiveRoutingValidatorTests.cs docs/bible.html
```

Expected: no whitespace errors; only the approved validator, tests, bible contract, design, and plan are changed.

- [ ] **Step 5: Prepare the commit message**

Use:

```text
feat(domain): validate downward directive routing
```
