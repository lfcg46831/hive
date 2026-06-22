# US-F0-04-T06 Escalation Routing Validator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate upward `Escalation` routing to either the source position's direct superior or, for root-unit leadership only, the configured `OrganizationOwner`.

**Architecture:** Add a focused `EscalationRoutingValidator` beside the existing directive and report validators. It consumes both escalation paths from `MessageRoutingRules`, probes confirmed registry absences through `IOrganizationRelations`, and keeps cancellation or technical registry failures exceptional; no shared-validator refactor is included.

**Tech Stack:** .NET 8, C#, xUnit, `Hive.Domain` organization relations and validation contracts

---

### Task 1: Specify escalation routing behavior

**Files:**
- Create: `tests/Hive.Tests/EscalationRoutingValidatorTests.cs`

- [ ] **Step 1: Write the failing tests**

Create `EscalationRoutingValidatorTests.cs` with `using Hive.Domain.Identity;`, `using Hive.Domain.Messaging;`, and `using Hive.Domain.Organization;`, the following cases, and the complete fixture code below:

```csharp
[Fact]
public async Task Direct_subordinate_can_escalate_a_blocker_to_direct_superior()
{
    var validator = new EscalationRoutingValidator(SampleRelations());

    var result = await validator.ValidateAsync(Escalation("engineer", "delivery-lead"));

    Assert.True(result.IsValid);
    Assert.Empty(result.Errors);
}

[Fact]
public async Task Root_leadership_can_escalate_out_of_scope_decision_to_owner()
{
    var validator = new EscalationRoutingValidator(SampleRelations());

    var result = await validator.ValidateAsync(
        Escalation(Position("ceo"), new OrganizationOwnerEndpointRef()));

    Assert.True(result.IsValid);
    Assert.Empty(result.Errors);
}

[Theory]
[InlineData("engineer", "ceo")]
[InlineData("delivery-lead", "engineer")]
[InlineData("ceo", "delivery-lead")]
public async Task Non_direct_position_routes_are_rejected(string from, string to)
{
    var validator = new EscalationRoutingValidator(SampleRelations());

    var result = await validator.ValidateAsync(Escalation(from, to));

    Assert.Equal(
        [new ValidationError(
            "direct-superior-required",
            "to.positionId",
            RejectionReason.InvalidRoute)],
        result.Errors);
}

[Theory]
[InlineData("engineer")]
[InlineData("delivery-lead")]
public async Task Non_root_position_cannot_escalate_directly_to_owner(string from)
{
    var validator = new EscalationRoutingValidator(SampleRelations());

    var result = await validator.ValidateAsync(
        Escalation(Position(from), new OrganizationOwnerEndpointRef()));

    Assert.Equal(
        [new ValidationError(
            "root-leadership-required",
            "from.positionId",
            RejectionReason.InvalidRoute)],
        result.Errors);
}

[Fact]
public async Task Invalid_endpoint_variants_are_aggregated_without_registry_queries()
{
    var validator = new EscalationRoutingValidator(new FailingRelations(
        new InvalidOperationException("Registry must not be queried.")));
    var escalation = Escalation(
        new OrganizationOwnerEndpointRef(),
        new SystemEndpointRef(SystemEndpointKind.Scheduler));

    var result = await validator.ValidateAsync(escalation);

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
    var validator = new EscalationRoutingValidator(SampleRelations());
    var escalation = Escalation(
        OrganizationId.From("unknown-organization"),
        Position("engineer"),
        Position("delivery-lead"));

    var result = await validator.ValidateAsync(escalation);

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
    var validator = new EscalationRoutingValidator(SampleRelations());

    var result = await validator.ValidateAsync(
        Escalation("ghost-source", "ghost-destination"));

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

[Fact]
public async Task Unknown_source_position_on_owner_route_uses_canonical_path()
{
    var validator = new EscalationRoutingValidator(SampleRelations());

    var result = await validator.ValidateAsync(
        Escalation(Position("ghost"), new OrganizationOwnerEndpointRef()));

    Assert.Equal(
        [new ValidationError(
            "position-not-found",
            "from.positionId",
            RejectionReason.InvalidRoute)],
        result.Errors);
}

[Fact]
public void Missing_relations_dependency_is_api_misuse()
{
    Assert.Throws<ArgumentNullException>(() => new EscalationRoutingValidator(null!));
}

[Fact]
public async Task Missing_escalation_is_api_misuse()
{
    var validator = new EscalationRoutingValidator(SampleRelations());

    await Assert.ThrowsAsync<ArgumentNullException>(
        async () => await validator.ValidateAsync(null!));
}

[Fact]
public async Task Cancellation_propagates_before_registry_access()
{
    using var cancellation = new CancellationTokenSource();
    cancellation.Cancel();
    var validator = new EscalationRoutingValidator(new FailingRelations(
        new InvalidOperationException("Registry must not be queried.")));

    await Assert.ThrowsAnyAsync<OperationCanceledException>(
        async () => await validator.ValidateAsync(
            Escalation("engineer", "delivery-lead"),
            cancellation.Token));
}

[Fact]
public async Task Unexpected_registry_failure_propagates()
{
    var failure = new InvalidOperationException("Registry unavailable.");
    var validator = new EscalationRoutingValidator(new FailingRelations(failure));

    var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
        async () => await validator.ValidateAsync(Escalation("engineer", "delivery-lead")));

    Assert.Same(failure, thrown);
}

[Fact]
public async Task Source_missing_during_superior_lookup_returns_canonical_error()
{
    var validator = new EscalationRoutingValidator(new SuperiorFailureRelations(
        OrganizationRelationNotFoundException.ForPosition(Org, PositionId.From("engineer"))));

    var result = await validator.ValidateAsync(Escalation("engineer", "delivery-lead"));

    Assert.Equal(
        [new ValidationError(
            "position-not-found",
            "from.positionId",
            RejectionReason.InvalidRoute)],
        result.Errors);
}

[Fact]
public async Task Organization_missing_during_owner_resolution_returns_canonical_error()
{
    var validator = new EscalationRoutingValidator(new OwnerFailureRelations(
        OrganizationRelationNotFoundException.ForOrganization(Org)));

    var result = await validator.ValidateAsync(
        Escalation(Position("ceo"), new OrganizationOwnerEndpointRef()));

    Assert.Equal(
        [new ValidationError(
            "organization-not-found",
            "organizationId",
            RejectionReason.InvalidRoute)],
        result.Errors);
}
```

Add these exact fields, factories, and fakes inside the test class:

```csharp
private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
private static readonly DateTimeOffset SentAt =
    new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

private static Escalation Escalation(string from, string to) =>
    Escalation(Position(from), Position(to));

private static Escalation Escalation(EndpointRef from, EndpointRef to) =>
    Escalation(Org, from, to);

private static Escalation Escalation(
    OrganizationId organizationId,
    EndpointRef from,
    EndpointRef to) =>
    new(
        MessageId.New(),
        organizationId,
        from,
        to,
        ThreadId.New(),
        Priority.High,
        1,
        SentAt,
        null,
        "Delivery is blocked",
        "A dependency requires a decision outside the source position's authority.",
        ["Wait for the dependency", "Use the fallback"]);

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

private sealed class SuperiorFailureRelations(Exception failure) : IOrganizationRelations
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
        throw new NotSupportedException();

    public ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        new(UnitId.From("delivery"));
}

private sealed class OwnerFailureRelations(Exception failure) : IOrganizationRelations
{
    public ValueTask<PositionId?> GetDirectSuperiorAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        throw new NotSupportedException();

    public ValueTask<PositionId> GetRootUnitLeadershipAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        new(PositionId.From("ceo"));

    public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OrganizationOwnerEndpointRef>(failure);

    public ValueTask<UnitId?> GetUnitOfPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken = default) =>
        new(UnitId.From("root"));
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~EscalationRoutingValidatorTests -v minimal
```

Expected: compilation fails because `EscalationRoutingValidator` does not exist. Confirm the failure is solely the missing production type.

### Task 2: Implement the focused validator

**Files:**
- Create: `src/Hive.Domain/Messaging/EscalationRoutingValidator.cs`
- Test: `tests/Hive.Tests/EscalationRoutingValidatorTests.cs`

- [ ] **Step 1: Add the minimal implementation**

Implement this complete validator:

```csharp
using Hive.Domain.Organization;

namespace Hive.Domain.Messaging;

public sealed class EscalationRoutingValidator
{
    private static readonly RoutingPathRule PositionPath =
        MessageRoutingRules.For<Escalation>().Paths.Single(
            path => path.Relation == RoutingRelation.DirectSubordinateToDirectSuperior);

    private static readonly RoutingPathRule OwnerPath =
        MessageRoutingRules.For<Escalation>().Paths.Single(
            path => path.Relation == RoutingRelation.RootLeadershipToOrganizationOwner);

    private readonly IOrganizationRelations _relations;

    public EscalationRoutingValidator(IOrganizationRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        Escalation escalation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(escalation);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<ValidationError>();
        var from = RequirePositionEndpoint(escalation.From, "from", errors);
        var destination = RequireDestination(escalation.To, errors);
        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        return destination switch
        {
            PositionEndpointRef position => await ValidatePositionRouteAsync(
                escalation,
                from!,
                position,
                cancellationToken),
            OrganizationOwnerEndpointRef => await ValidateOwnerRouteAsync(
                escalation,
                from!,
                cancellationToken),
            _ => throw new InvalidOperationException("Validated escalation destination is unsupported."),
        };
    }

    private async ValueTask<ValidationResult> ValidatePositionRouteAsync(
        Escalation escalation,
        PositionEndpointRef from,
        PositionEndpointRef to,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();
        var sourceProbe = await ProbePositionAsync(escalation, from, cancellationToken);
        if (sourceProbe.OrganizationMissing)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }

        var destinationProbe = await ProbePositionAsync(escalation, to, cancellationToken);
        if (destinationProbe.OrganizationMissing)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }

        if (!sourceProbe.Exists)
        {
            errors.Add(PositionNotFound("from.positionId"));
        }

        if (!destinationProbe.Exists)
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
                escalation.OrganizationId,
                from.PositionId,
                cancellationToken);

            return superior == to.PositionId
                ? ValidationResult.Valid
                : ValidationResult.Create([DirectSuperiorRequired()]);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([PositionNotFound("from.positionId")]);
        }
    }

    private async ValueTask<ValidationResult> ValidateOwnerRouteAsync(
        Escalation escalation,
        PositionEndpointRef from,
        CancellationToken cancellationToken)
    {
        var sourceProbe = await ProbePositionAsync(escalation, from, cancellationToken);
        if (sourceProbe.OrganizationMissing)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }

        if (!sourceProbe.Exists)
        {
            return ValidationResult.Create([PositionNotFound("from.positionId")]);
        }

        try
        {
            var rootLeadership = await _relations.GetRootUnitLeadershipAsync(
                escalation.OrganizationId,
                cancellationToken);
            if (rootLeadership != from.PositionId)
            {
                return ValidationResult.Create([RootLeadershipRequired()]);
            }

            _ = await _relations.GetOrganizationOwnerAsync(
                escalation.OrganizationId,
                cancellationToken);
            return ValidationResult.Valid;
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }
    }

    private async ValueTask<PositionProbe> ProbePositionAsync(
        Escalation escalation,
        PositionEndpointRef endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var unit = await _relations.GetUnitOfPositionAsync(
                escalation.OrganizationId,
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
        string path,
        ICollection<ValidationError> errors)
    {
        if (endpoint.GetType() == PositionPath.FromEndpointType)
        {
            return (PositionEndpointRef)endpoint;
        }

        errors.Add(new ValidationError(
            "endpoint-not-allowed",
            path,
            RejectionReason.InvalidRoute));
        return null;
    }

    private static EndpointRef? RequireDestination(
        EndpointRef endpoint,
        ICollection<ValidationError> errors)
    {
        if (endpoint.GetType() == PositionPath.ToEndpointType
            || endpoint.GetType() == OwnerPath.ToEndpointType)
        {
            return endpoint;
        }

        errors.Add(new ValidationError(
            "endpoint-not-allowed",
            "to",
            RejectionReason.InvalidRoute));
        return null;
    }

    private static ValidationError OrganizationNotFound() =>
        new("organization-not-found", "organizationId", RejectionReason.InvalidRoute);

    private static ValidationError PositionNotFound(string path) =>
        new("position-not-found", path, RejectionReason.InvalidRoute);

    private static ValidationError DirectSuperiorRequired() =>
        new("direct-superior-required", "to.positionId", RejectionReason.InvalidRoute);

    private static ValidationError RootLeadershipRequired() =>
        new("root-leadership-required", "from.positionId", RejectionReason.InvalidRoute);

    private readonly record struct PositionProbe(bool Exists, bool OrganizationMissing);
}
```

- [ ] **Step 2: Run focused tests and verify GREEN**

Run the focused command from Task 1.

Expected: all `EscalationRoutingValidatorTests` cases pass with zero failures.

- [ ] **Step 3: Run related routing tests**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageRoutingRulesTests|FullyQualifiedName~MaterializedOrganizationRelationsTests|FullyQualifiedName~DirectiveRoutingValidatorTests|FullyQualifiedName~ReportRoutingValidatorTests|FullyQualifiedName~EscalationRoutingValidatorTests" -v minimal
```

Expected: all selected routing and relation tests pass.

### Task 3: Finalize the durable contract

**Files:**
- Modify: `docs/bible.html`

- [ ] **Step 1: Mark v0.65 as implemented**

Change the v0.65 history entry from `Definição do contrato` to `Registo` only after tests pass. Retain the approved validator paragraph and change the footer to identify `US-F0-04-T07` as v0.66's next iteration.

- [ ] **Step 2: Verify the documentation contract**

Run:

```powershell
rg -n "0.65|EscalationRoutingValidator|root-leadership-required|v0.66|US-F0-04-T07" docs/bible.html
```

Expected: one v0.65 history entry, one T06 validator contract, and the T07 next-iteration marker.

### Task 4: Verify the completed task

**Files:**
- Test: `tests/Hive.Tests/EscalationRoutingValidatorTests.cs`
- Build: `Hive.sln`

- [ ] **Step 1: Run focused tests**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~EscalationRoutingValidatorTests -v minimal
```

- [ ] **Step 2: Run the full test project**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore -v minimal
```

- [ ] **Step 3: Build the solution**

```powershell
dotnet build Hive.sln --no-restore -v minimal
```

- [ ] **Step 4: Inspect the final diff**

```powershell
git diff --check
git status --short
git diff -- src/Hive.Domain/Messaging/EscalationRoutingValidator.cs tests/Hive.Tests/EscalationRoutingValidatorTests.cs docs/bible.html docs/superpowers/plans/2026-06-22-us-f0-04-t06-escalation-routing-validator.md
```

Expected: no whitespace errors; only the approved validator, tests, Bible contract, and implementation plan are changed.

- [ ] **Step 5: Prepare the commit message**

```text
feat(domain): validate upward escalation routing
```
