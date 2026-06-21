using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;

namespace Hive.Tests;

/// <summary>
/// Executable specification for the <see cref="IOrganizationRelations"/> contract (US-F0-04-T01).
/// The behavior under test is the documented semantics of the contract surface, exercised through
/// a minimal in-test fake. The materialized implementation arrives in US-F0-04-T02.
/// </summary>
public sealed class OrganizationRelationsContractTests
{
    private static readonly OrganizationId Org = OrganizationId.From("engineering-delivery");

    [Fact]
    public async Task Root_unit_leadership_has_no_direct_superior()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var superior = await relations.GetDirectSuperiorAsync(Org, Position("ceo"));

        Assert.Null(superior);
    }

    [Fact]
    public async Task Direct_superior_is_resolved_for_a_subordinate()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var superior = await relations.GetDirectSuperiorAsync(Org, Position("delivery-lead"));

        Assert.Equal(Position("ceo"), superior);
    }

    [Fact]
    public async Task Direct_subordinates_are_returned_without_duplicates()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var subordinates = await relations.GetDirectSubordinatesAsync(Org, Position("ceo"));

        Assert.Equal(new[] { Position("delivery-lead") }, subordinates);
    }

    [Fact]
    public async Task A_leaf_position_has_no_subordinates()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var subordinates = await relations.GetDirectSubordinatesAsync(Org, Position("engineer"));

        Assert.Empty(subordinates);
    }

    [Fact]
    public async Task Root_unit_leadership_is_the_top_position()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var leader = await relations.GetRootUnitLeadershipAsync(Org);

        Assert.Equal(Position("ceo"), leader);
    }

    [Fact]
    public async Task Organization_owner_is_resolved_as_a_routing_endpoint()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var owner = await relations.GetOrganizationOwnerAsync(Org);

        Assert.NotNull(owner);
        Assert.IsType<OrganizationOwnerEndpointRef>(owner);
    }

    [Fact]
    public async Task Unit_membership_is_resolved_for_a_known_position()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var unit = await relations.GetUnitOfPositionAsync(Org, Position("engineer"));

        Assert.Equal(UnitId.From("delivery"), unit);
    }

    [Fact]
    public async Task Unit_membership_probe_returns_null_for_an_unknown_position()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        var unit = await relations.GetUnitOfPositionAsync(Org, Position("ghost"));

        Assert.Null(unit);
    }

    [Fact]
    public async Task Relation_queries_throw_for_an_unknown_position()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(
            async () => await relations.GetDirectSuperiorAsync(Org, Position("ghost")));
    }

    [Fact]
    public async Task Relation_queries_throw_for_an_unknown_organization()
    {
        var relations = FakeOrganizationRelations.SampleOrganization();

        await Assert.ThrowsAsync<OrganizationRelationNotFoundException>(
            async () => await relations.GetRootUnitLeadershipAsync(OrganizationId.From("other-org")));
    }

    private static PositionId Position(string value) => PositionId.From(value);

    /// <summary>
    /// Minimal in-memory fake that honors the documented contract semantics for a tiny
    /// organization: CEO (root unit leadership) → delivery-lead → engineer, all in the delivery
    /// unit except the CEO in the root unit.
    /// </summary>
    private sealed class FakeOrganizationRelations : IOrganizationRelations
    {
        private readonly OrganizationId _organizationId;
        private readonly Dictionary<string, string?> _superiorByPosition;
        private readonly Dictionary<string, List<PositionId>> _subordinatesByPosition;
        private readonly Dictionary<string, UnitId> _unitByPosition;
        private readonly PositionId _rootLeadership;

        private FakeOrganizationRelations(
            OrganizationId organizationId,
            Dictionary<string, string?> superiorByPosition,
            Dictionary<string, List<PositionId>> subordinatesByPosition,
            Dictionary<string, UnitId> unitByPosition,
            PositionId rootLeadership)
        {
            _organizationId = organizationId;
            _superiorByPosition = superiorByPosition;
            _subordinatesByPosition = subordinatesByPosition;
            _unitByPosition = unitByPosition;
            _rootLeadership = rootLeadership;
        }

        public static FakeOrganizationRelations SampleOrganization() =>
            new(
                Org,
                new Dictionary<string, string?>
                {
                    ["ceo"] = null,
                    ["delivery-lead"] = "ceo",
                    ["engineer"] = "delivery-lead",
                },
                new Dictionary<string, List<PositionId>>
                {
                    ["ceo"] = new() { PositionId.From("delivery-lead") },
                    ["delivery-lead"] = new() { PositionId.From("engineer") },
                    ["engineer"] = new(),
                },
                new Dictionary<string, UnitId>
                {
                    ["ceo"] = UnitId.From("root"),
                    ["delivery-lead"] = UnitId.From("delivery"),
                    ["engineer"] = UnitId.From("delivery"),
                },
                PositionId.From("ceo"));

        public ValueTask<PositionId?> GetDirectSuperiorAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default)
        {
            RequireOrganization(organizationId);
            if (!_superiorByPosition.TryGetValue(positionId.Value, out var superior))
            {
                throw OrganizationRelationNotFoundException.ForPosition(organizationId, positionId);
            }

            return new ValueTask<PositionId?>(superior is null ? null : PositionId.From(superior));
        }

        public ValueTask<IReadOnlyCollection<PositionId>> GetDirectSubordinatesAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default)
        {
            RequireOrganization(organizationId);
            if (!_subordinatesByPosition.TryGetValue(positionId.Value, out var subordinates))
            {
                throw OrganizationRelationNotFoundException.ForPosition(organizationId, positionId);
            }

            return new ValueTask<IReadOnlyCollection<PositionId>>(subordinates.ToArray());
        }

        public ValueTask<PositionId> GetRootUnitLeadershipAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default)
        {
            RequireOrganization(organizationId);
            return new ValueTask<PositionId>(_rootLeadership);
        }

        public ValueTask<OrganizationOwnerEndpointRef> GetOrganizationOwnerAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default)
        {
            RequireOrganization(organizationId);
            return new ValueTask<OrganizationOwnerEndpointRef>(new OrganizationOwnerEndpointRef());
        }

        public ValueTask<UnitId?> GetUnitOfPositionAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default)
        {
            RequireOrganization(organizationId);
            return new ValueTask<UnitId?>(
                _unitByPosition.TryGetValue(positionId.Value, out var unit) ? unit : null);
        }

        private void RequireOrganization(OrganizationId organizationId)
        {
            if (organizationId != _organizationId)
            {
                throw OrganizationRelationNotFoundException.ForOrganization(organizationId);
            }
        }
    }
}
