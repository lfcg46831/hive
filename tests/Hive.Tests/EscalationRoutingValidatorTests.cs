using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Tests;

public sealed class EscalationRoutingValidatorTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");
    private static readonly DateTimeOffset SentAt =
        new(2026, 6, 22, 10, 0, 0, TimeSpan.Zero);

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

    [Theory]
    [InlineData("ghost", "delivery-lead", "from.positionId")]
    [InlineData("engineer", "ghost", "to.positionId")]
    public async Task A_single_unknown_position_uses_its_canonical_path(
        string from,
        string to,
        string expectedPath)
    {
        var validator = new EscalationRoutingValidator(SampleRelations());

        var result = await validator.ValidateAsync(Escalation(from, to));

        Assert.Equal(
            [new ValidationError(
                "position-not-found",
                expectedPath,
                RejectionReason.InvalidRoute)],
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
    public async Task Unexpected_owner_registry_failure_propagates()
    {
        var failure = new InvalidOperationException("Registry unavailable.");
        var validator = new EscalationRoutingValidator(new OwnerFailureRelations(failure));

        var thrown = await Assert.ThrowsAsync<InvalidOperationException>(
            async () => await validator.ValidateAsync(
                Escalation(Position("ceo"), new OrganizationOwnerEndpointRef())));

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
}
