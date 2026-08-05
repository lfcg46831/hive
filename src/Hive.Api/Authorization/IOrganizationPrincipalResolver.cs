using System.Security.Claims;
using Hive.Domain.Identity;

namespace Hive.Api.Authorization;

public interface IOrganizationPrincipalResolver
{
    ValueTask<OrganizationPrincipal> ResolveAsync(
        ClaimsPrincipal principal,
        CancellationToken cancellationToken = default);
}

public sealed class OrganizationPrincipal
{
    private readonly OrganizationId[] _organizationIds;

    public OrganizationPrincipal(
        IEnumerable<OrganizationId> organizationIds,
        PersonPrincipal? person = null)
    {
        ArgumentNullException.ThrowIfNull(organizationIds);

        _organizationIds = organizationIds
            .Distinct()
            .OrderBy(static organizationId => organizationId.Value, StringComparer.Ordinal)
            .ToArray();
        OrganizationIds = Array.AsReadOnly(_organizationIds);
        if (person is not null &&
            person.Positions.Any(position => !CanRead(position.OrganizationId)))
        {
            throw new ArgumentException(
                "Every occupied position must belong to an authorized organization.",
                nameof(person));
        }

        Person = person;
    }

    public IReadOnlyList<OrganizationId> OrganizationIds { get; }

    public PersonPrincipal? Person { get; }

    public bool CanRead(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        return Array.IndexOf(_organizationIds, organizationId) >= 0;
    }

    public PersonOrganizationScope? PersonScopeFor(OrganizationId organizationId)
    {
        ArgumentNullException.ThrowIfNull(organizationId);

        if (!CanRead(organizationId) || Person is null)
        {
            return null;
        }

        var positions = Person.Positions
            .Where(position => position.OrganizationId == organizationId)
            .Select(static position => position.PositionId);
        return new PersonOrganizationScope(Person.Id, organizationId, positions);
    }
}

public sealed class PersonPrincipal
{
    private readonly OccupiedPosition[] _positions;

    public PersonPrincipal(string id, IEnumerable<OccupiedPosition> positions)
    {
        ArgumentNullException.ThrowIfNull(id);
        if (string.IsNullOrWhiteSpace(id) ||
            !string.Equals(id, id.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Person identifier cannot be empty or contain surrounding whitespace.",
                nameof(id));
        }

        ArgumentNullException.ThrowIfNull(positions);
        var suppliedPositions = positions.ToArray();
        if (suppliedPositions.Any(static position => position is null))
        {
            throw new ArgumentException(
                "Occupied positions cannot contain null entries.",
                nameof(positions));
        }

        Id = id;
        _positions = suppliedPositions
            .Distinct()
            .OrderBy(static position => position.OrganizationId.Value, StringComparer.Ordinal)
            .ThenBy(static position => position.PositionId.Value, StringComparer.Ordinal)
            .ToArray();
        Positions = Array.AsReadOnly(_positions);
    }

    public string Id { get; }

    public IReadOnlyList<OccupiedPosition> Positions { get; }
}

public sealed record OccupiedPosition
{
    public OccupiedPosition(OrganizationId organizationId, PositionId positionId)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }
}

public sealed class PersonOrganizationScope
{
    private readonly PositionId[] _positionIds;

    public PersonOrganizationScope(
        string personId,
        OrganizationId organizationId,
        IEnumerable<PositionId> positionIds)
    {
        ArgumentNullException.ThrowIfNull(personId);
        if (string.IsNullOrWhiteSpace(personId) ||
            !string.Equals(personId, personId.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Person identifier cannot be empty or contain surrounding whitespace.",
                nameof(personId));
        }

        PersonId = personId;
        OrganizationId = organizationId
            ?? throw new ArgumentNullException(nameof(organizationId));
        ArgumentNullException.ThrowIfNull(positionIds);
        var suppliedPositionIds = positionIds.ToArray();
        if (suppliedPositionIds.Any(static positionId => positionId is null))
        {
            throw new ArgumentException(
                "Position identifiers cannot contain null entries.",
                nameof(positionIds));
        }

        _positionIds = suppliedPositionIds
            .Distinct()
            .OrderBy(static positionId => positionId.Value, StringComparer.Ordinal)
            .ToArray();
        PositionIds = Array.AsReadOnly(_positionIds);
    }

    public string PersonId { get; }

    public OrganizationId OrganizationId { get; }

    public IReadOnlyList<PositionId> PositionIds { get; }

    public bool Occupies(PositionId positionId)
    {
        ArgumentNullException.ThrowIfNull(positionId);

        return Array.IndexOf(_positionIds, positionId) >= 0;
    }
}
