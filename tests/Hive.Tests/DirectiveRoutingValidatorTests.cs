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

    [Fact]
    public async Task Destination_missing_during_relation_lookup_returns_canonical_error()
    {
        var validator = new DirectiveRoutingValidator(new SuperiorFailureRelations(
            OrganizationRelationNotFoundException.ForPosition(
                Org,
                PositionId.From("engineer"))));

        var result = await validator.ValidateAsync(Directive("delivery-lead", "engineer"));

        Assert.Equal(
            [new ValidationError(
                "position-not-found",
                "to.positionId",
                RejectionReason.InvalidRoute)],
            result.Errors);
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
