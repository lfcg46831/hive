# US-F0-04-T05 Report Routing Validator Implementation Plan

> **For agentic workers:** REQUIRED SUB-SKILL: Use superpowers:subagent-driven-development (recommended) or superpowers:executing-plans to implement this plan task-by-task. Steps use checkbox (`- [ ]`) syntax for tracking.

**Goal:** Validate upward `Report` routing so a report can only travel from a direct subordinate to its direct superior, with canonical structured errors for confirmed missing registry entities.

**Architecture:** Add a focused `ReportRoutingValidator` to `Hive.Domain.Messaging`, parallel to the existing directive validator without refactoring it. The validator consumes the `Report` path from `MessageRoutingRules`, probes organization and position existence through `IOrganizationRelations`, then proves the route by comparing the source's direct superior with the destination; confirmed absences become `ValidationResult` errors while cancellation and technical failures propagate.

**Tech Stack:** .NET 8, C#, xUnit, `Hive.Domain` organization relations and validation contracts

---

### Task 1: Specify upward report routing

**Files:**
- Create: `tests/Hive.Tests/ReportRoutingValidatorTests.cs`

- [ ] **Step 1: Write the failing validator tests**

Create `ReportRoutingValidatorTests.cs` with the following coverage and helpers:

```csharp
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Tests;

public sealed class ReportRoutingValidatorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Direct_subordinate_to_direct_superior_is_valid()
    {
        var validator = new ReportRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(Report("engineer", "delivery-lead"));

        Assert.True(result.IsValid);
        Assert.Empty(result.Errors);
    }

    [Theory]
    [InlineData("engineer", "ceo")]
    [InlineData("delivery-lead", "engineer")]
    [InlineData("ceo", "delivery-lead")]
    public async Task Non_direct_upward_routes_are_rejected(string from, string to)
    {
        var validator = new ReportRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(Report(from, to));

        Assert.Equal(
            [new ValidationError(
                "direct-superior-required",
                "to.positionId",
                RejectionReason.InvalidRoute)],
            result.Errors);
    }

    [Fact]
    public async Task Invalid_endpoint_variants_are_aggregated_without_registry_queries()
    {
        var validator = new ReportRoutingValidator(new FailingRelations(
            new InvalidOperationException("Registry must not be queried.")));
        var report = Report(
            new OrganizationOwnerEndpointRef(),
            new SystemEndpointRef(SystemEndpointKind.Scheduler));

        var result = await validator.ValidateAsync(report);

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
        var validator = new ReportRoutingValidator(SampleRelations());
        var report = Report(
            OrganizationId.From("unknown-organization"),
            Position("engineer"),
            Position("delivery-lead"));

        var result = await validator.ValidateAsync(report);

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
        var validator = new ReportRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(
            Report("ghost-source", "ghost-destination"));

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
    [InlineData("ghost", "delivery-lead", "from.positionId")]
    [InlineData("engineer", "ghost", "to.positionId")]
    public async Task A_single_unknown_position_uses_its_canonical_path(
        string from,
        string to,
        string expectedPath)
    {
        var validator = new ReportRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(Report(from, to));

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
            () => new ReportRoutingValidator(null!));
    }

    [Fact]
    public async Task Missing_report_is_api_misuse()
    {
        var validator = new ReportRoutingValidator(SampleRelations());

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await validator.ValidateAsync(null!));
    }

    [Fact]
    public async Task Cancellation_propagates_before_registry_access()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var validator = new ReportRoutingValidator(new FailingRelations(
            new InvalidOperationException("Registry must not be queried.")));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            async () => await validator.ValidateAsync(
                Report("engineer", "delivery-lead"),
                cancellation.Token));
    }

    [Fact]
    public async Task Unexpected_registry_failure_propagates()
    {
        var failure = new InvalidOperationException("Registry unavailable.");
        var validator = new ReportRoutingValidator(new FailingRelations(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await validator.ValidateAsync(
                Report("engineer", "delivery-lead")));

        Assert.Same(failure, thrown);
    }

    [Fact]
    public async Task Source_missing_during_relation_lookup_returns_canonical_error()
    {
        var validator = new ReportRoutingValidator(new SuperiorFailureRelations(
            OrganizationRelationNotFoundException.ForPosition(
                Org,
                PositionId.From("engineer"))));

        var result = await validator.ValidateAsync(Report("engineer", "delivery-lead"));

        Assert.Equal(
            [new ValidationError(
                "position-not-found",
                "from.positionId",
                RejectionReason.InvalidRoute)],
            result.Errors);
    }

    private static Report Report(string from, string to) =>
        Report(Position(from), Position(to));

    private static Report Report(EndpointRef from, EndpointRef to) =>
        Report(Org, from, to);

    private static Report Report(
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
            ReportKind.Progress,
            "Work is progressing");

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
}
```

- [ ] **Step 2: Run the focused tests and verify RED**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~ReportRoutingValidatorTests -v minimal
```

Expected: compilation fails because `ReportRoutingValidator` does not exist.

### Task 2: Implement the focused validator

**Files:**
- Create: `src/Hive.Domain/Messaging/ReportRoutingValidator.cs`
- Test: `tests/Hive.Tests/ReportRoutingValidatorTests.cs`

- [ ] **Step 1: Add the minimal implementation**

Create `ReportRoutingValidator.cs`:

```csharp
using Hive.Domain.Organization;

namespace Hive.Domain.Messaging;

public sealed class ReportRoutingValidator
{
    private static readonly RoutingPathRule ReportPath =
        MessageRoutingRules.For<Report>().Paths.Single(
            path => path.Relation == RoutingRelation.DirectSubordinateToDirectSuperior);

    private readonly IOrganizationRelations _relations;

    public ReportRoutingValidator(IOrganizationRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        Report report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<ValidationError>();
        var from = RequirePositionEndpoint(
            report.From,
            ReportPath.FromEndpointType,
            "from",
            errors);
        var to = RequirePositionEndpoint(
            report.To,
            ReportPath.ToEndpointType,
            "to",
            errors);

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        var fromProbe = await ProbePositionAsync(report, from!, cancellationToken);
        if (fromProbe.OrganizationMissing)
        {
            return ValidationResult.Create([OrganizationNotFound()]);
        }

        var toProbe = await ProbePositionAsync(report, to!, cancellationToken);
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
                report.OrganizationId,
                from!.PositionId,
                cancellationToken);

            return superior == to!.PositionId
                ? ValidationResult.Valid
                : ValidationResult.Create([DirectSuperiorRequired()]);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([PositionNotFound("from.positionId")]);
        }
    }

    private async ValueTask<PositionProbe> ProbePositionAsync(
        Report report,
        PositionEndpointRef endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var unit = await _relations.GetUnitOfPositionAsync(
                report.OrganizationId,
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

    private static ValidationError DirectSuperiorRequired() =>
        new("direct-superior-required", "to.positionId", RejectionReason.InvalidRoute);

    private readonly record struct PositionProbe(bool Exists, bool OrganizationMissing);
}
```

- [ ] **Step 2: Run the focused tests and verify GREEN**

Run the focused command from Task 1.

Expected: 14 test cases pass with zero failures.

- [ ] **Step 3: Run related routing tests**

Run:

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter "FullyQualifiedName~MessageRoutingRulesTests|FullyQualifiedName~MaterializedOrganizationRelationsTests|FullyQualifiedName~DirectiveRoutingValidatorTests|FullyQualifiedName~ReportRoutingValidatorTests" -v minimal
```

Expected: all selected tests pass with zero failures.

### Task 3: Finalize the durable contract

**Files:**
- Modify: `docs/bible.html`

- [ ] **Step 1: Confirm the version and history entry**

Keep the document at version `0.64` and change the T05 history entry from `Definição` to `Registo` after the implementation is verified.

- [ ] **Step 2: Confirm the implementation contract**

Retain the approved T05 paragraph describing `ReportRoutingValidator`, `GetDirectSuperiorAsync(from)`, `direct-superior-required`, canonical absence handling, and propagated technical failures.

- [ ] **Step 3: Verify the documentation contract**

Run:

```powershell
rg -n "0.64|ReportRoutingValidator|direct-superior-required|v0.65|US-F0-04-T06" docs/bible.html
```

Expected: one history entry, one T05 contract paragraph, and the T06 next-iteration marker are reported.

### Task 4: Verify the completed task

**Files:**
- Test: `tests/Hive.Tests/ReportRoutingValidatorTests.cs`
- Build: `Hive.sln`

- [ ] **Step 1: Run focused tests**

```powershell
dotnet test tests/Hive.Tests/Hive.Tests.csproj --no-restore --filter FullyQualifiedName~ReportRoutingValidatorTests -v minimal
```

Expected: all 14 test cases pass with zero failures.

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

```powershell
git diff --check
git status --short
git diff -- src/Hive.Domain/Messaging/ReportRoutingValidator.cs tests/Hive.Tests/ReportRoutingValidatorTests.cs docs/bible.html docs/superpowers/plans/2026-06-22-us-f0-04-t05-report-routing-validator.md
```

Expected: no whitespace errors; only the approved validator, tests, bible contract, and implementation plan are changed.

- [ ] **Step 5: Prepare the commit message**

Use:

```text
feat(domain): validate upward report routing
```
