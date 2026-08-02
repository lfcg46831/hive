using System.Reflection;
using System.Text.Json;
using Hive.Contracts.Organization;

namespace Hive.Tests;

public sealed class OrganizationPublicContractTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 2, 10, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EventAt = GeneratedAt.AddMinutes(-2);

    [Fact]
    public void Organogram_serializes_a_stable_public_read_only_shape()
    {
        var response = CreateOrganogram();

        var json = JsonSerializer.SerializeToElement(response);

        Assert.Equal(7, json.GetProperty("registry").GetProperty("version").GetInt64());
        Assert.Equal(Fingerprint, json.GetProperty("registry").GetProperty("fingerprint").GetString());
        Assert.Equal(GeneratedAt, json.GetProperty("generated_at_utc").GetDateTimeOffset());
        Assert.Equal("delivery", json.GetProperty("root_unit_id").GetString());
        Assert.Equal("acme", json.GetProperty("organization").GetProperty("id").GetString());
        Assert.Equal(
            ["delivery", "engineering"],
            json.GetProperty("units")
                .EnumerateArray()
                .Select(item => item.GetProperty("id").GetString()));

        var positions = json.GetProperty("positions").EnumerateArray().ToArray();
        Assert.Equal(["delivery-lead", "engineer"], positions.Select(
            item => item.GetProperty("id").GetString()));
        Assert.Equal(
            "AiAgent",
            positions[0].GetProperty("occupant").GetProperty("type").GetString());
        Assert.Equal(
            "Working",
            positions[0].GetProperty("operational_state").GetProperty("state").GetString());
        Assert.Equal(
            "DirectiveReceived",
            positions[0]
                .GetProperty("operational_state")
                .GetProperty("last_correlated_event")
                .GetProperty("type")
                .GetString());
    }

    [Fact]
    public void Contracts_snapshot_and_sort_all_caller_owned_collections()
    {
        var subordinateIds = new List<string> { "engineer-z", "engineer-a" };
        var hierarchy = new PositionHierarchy(null, subordinateIds);
        subordinateIds.Add("engineer-late");

        var units = new List<OrganizationUnit>
        {
            new("engineering", "Engineering", "delivery", "engineer"),
            new("delivery", "Delivery", null, "delivery-lead"),
        };
        var positions = CreatePositions().Reverse().ToList();
        var response = new OrganogramResponse(
            Registry,
            GeneratedAt,
            "delivery",
            Organization,
            units,
            positions);
        units.Clear();
        positions.Clear();

        Assert.Equal(["engineer-a", "engineer-z"], hierarchy.DirectSubordinatePositionIds);
        Assert.Equal(["delivery", "engineering"], response.Units.Select(unit => unit.Id));
        Assert.Equal(
            ["delivery-lead", "engineer"],
            response.Positions.Select(position => position.Id));
    }

    [Fact]
    public void Position_state_snapshot_exposes_staleness_and_canonical_state_ordering()
    {
        var states = CreatePositions()
            .Reverse()
            .Select(position => position.OperationalState)
            .ToArray();

        var response = new PositionStatesResponse(
            Registry,
            GeneratedAt,
            EventAt,
            states);
        var json = JsonSerializer.SerializeToElement(response);

        Assert.Equal(
            EventAt,
            json.GetProperty("last_event_applied_at_utc").GetDateTimeOffset());
        Assert.Equal(
            ["delivery-lead", "engineer"],
            response.States.Select(state => state.PositionId));
        Assert.Equal(
            [PositionOperationalState.Working, PositionOperationalState.Idle],
            response.States.Select(state => state.State));
    }

    [Fact]
    public void Position_detail_reuses_the_same_position_contract()
    {
        var position = CreatePositions()[0];

        var response = new PositionDetailResponse(Registry, GeneratedAt, position);

        Assert.Same(position, response.Position);
        Assert.Equal("delivery-lead", response.Position.Id);
        Assert.Equal("engineer", Assert.Single(
            response.Position.Hierarchy.DirectSubordinatePositionIds));
    }

    [Fact]
    public void Position_rejects_operational_state_from_another_position()
    {
        var state = new OrganizationPositionState(
            "other-position",
            PositionOperationalState.Idle,
            0,
            GeneratedAt);

        Assert.Throws<ArgumentException>(() => new OrganizationPosition(
            "delivery-lead",
            "Delivery Lead",
            "delivery",
            new OrganizationOccupant("configured-ai:acme/delivery-lead", OrganizationOccupantType.AiAgent),
            new PositionHierarchy(null, []),
            state));
    }

    [Theory]
    [InlineData(0, "sha256:aaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaaa")]
    [InlineData(1, "not-a-fingerprint")]
    [InlineData(1, "sha256:AAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAAA")]
    public void Registry_version_rejects_invalid_public_metadata(
        long version,
        string fingerprint)
    {
        Assert.ThrowsAny<ArgumentException>(() => new RegistryVersion(version, fingerprint));
    }

    [Fact]
    public void Public_contract_surface_does_not_expose_runtime_types()
    {
        var contractTypes = typeof(OrganogramResponse).Assembly
            .GetExportedTypes()
            .Where(type => type.Namespace == typeof(OrganogramResponse).Namespace)
            .ToArray();
        var exposedTypes = contractTypes
            .SelectMany(PublicSurfaceTypes)
            .Where(type => type.Namespace is not null)
            .Select(type => type.Namespace!)
            .Distinct(StringComparer.Ordinal)
            .ToArray();

        Assert.DoesNotContain(exposedTypes, value => value.StartsWith("Hive.Domain", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, value => value.StartsWith("Hive.Infrastructure", StringComparison.Ordinal));
        Assert.DoesNotContain(exposedTypes, value => value.StartsWith("Hive.Api", StringComparison.Ordinal));
    }

    private static OrganogramResponse CreateOrganogram() => new(
        Registry,
        GeneratedAt,
        "delivery",
        Organization,
        [
            new OrganizationUnit("engineering", "Engineering", "delivery", "engineer"),
            new OrganizationUnit("delivery", "Delivery", null, "delivery-lead"),
        ],
        CreatePositions().Reverse().ToArray());

    private static OrganizationPosition[] CreatePositions() =>
    [
        new OrganizationPosition(
            "delivery-lead",
            "Delivery Lead",
            "delivery",
            new OrganizationOccupant(
                "configured-ai:acme/delivery-lead",
                OrganizationOccupantType.AiAgent),
            new PositionHierarchy(null, ["engineer"]),
            new OrganizationPositionState(
                "delivery-lead",
                PositionOperationalState.Working,
                12,
                EventAt,
                new PositionCorrelatedEvent(
                    "DirectiveReceived",
                    Guid.Parse("80e3feec-ea3b-4de8-8f59-52932f548b01"),
                    EventAt))),
        new OrganizationPosition(
            "engineer",
            "Engineer",
            "engineering",
            new OrganizationOccupant(null, OrganizationOccupantType.Human),
            new PositionHierarchy("delivery-lead", []),
            new OrganizationPositionState(
                "engineer",
                PositionOperationalState.Idle,
                0,
                GeneratedAt)),
    ];

    private static IEnumerable<Type> PublicSurfaceTypes(Type type)
    {
        yield return type;
        foreach (var property in type.GetProperties(
                     BindingFlags.Instance |
                     BindingFlags.Public |
                     BindingFlags.DeclaredOnly))
        {
            yield return property.PropertyType;
            foreach (var argument in property.PropertyType.GetGenericArguments())
            {
                yield return argument;
            }
        }

        foreach (var constructor in type.GetConstructors())
        {
            foreach (var parameter in constructor.GetParameters())
            {
                yield return parameter.ParameterType;
                foreach (var argument in parameter.ParameterType.GetGenericArguments())
                {
                    yield return argument;
                }
            }
        }
    }

    private static OrganizationSummary Organization { get; } =
        new("acme", "Acme Delivery", "delivery", "delivery-lead");

    private static RegistryVersion Registry { get; } = new(7, Fingerprint);

    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";
}
