using Hive.Domain.Identity;
using Hive.Domain.Organization;

namespace Hive.Infrastructure.Organization.Registry;

public sealed class OrganizationRegistrySnapshot
{
    internal OrganizationRegistrySnapshot(
        OrganizationId organizationId,
        long version,
        string fingerprint,
        DateTimeOffset updatedAt,
        RegistryEntry<RegistryOrganization> organization,
        IReadOnlyDictionary<UnitId, RegistryEntry<RegistryUnit>> units,
        IReadOnlyDictionary<PositionId, RegistryEntry<RegistryPosition>> positions,
        IReadOnlyDictionary<PositionId, RegistryEntry<RegistryOccupant>> occupants,
        IReadOnlyDictionary<PositionId, RegistryEntry<RegistryAuthority>> authorities,
        IReadOnlyDictionary<RegistryScheduleKey, RegistryEntry<RegistrySchedule>> schedules,
        RegistryEntry<OrganizationRelationsSnapshot> relations)
    {
        OrganizationId = organizationId;
        Version = version;
        Fingerprint = fingerprint;
        UpdatedAt = updatedAt;
        Organization = organization;
        Units = units;
        Positions = positions;
        Occupants = occupants;
        Authorities = authorities;
        Schedules = schedules;
        Relations = relations;
    }

    public OrganizationId OrganizationId { get; }

    public long Version { get; }

    public string Fingerprint { get; }

    public DateTimeOffset UpdatedAt { get; }

    public RegistryEntry<RegistryOrganization> Organization { get; }

    public IReadOnlyDictionary<UnitId, RegistryEntry<RegistryUnit>> Units { get; }

    public IReadOnlyDictionary<PositionId, RegistryEntry<RegistryPosition>> Positions { get; }

    public IReadOnlyDictionary<PositionId, RegistryEntry<RegistryOccupant>> Occupants { get; }

    public IReadOnlyDictionary<PositionId, RegistryEntry<RegistryAuthority>> Authorities { get; }

    public IReadOnlyDictionary<RegistryScheduleKey, RegistryEntry<RegistrySchedule>> Schedules { get; }

    public RegistryEntry<OrganizationRelationsSnapshot> Relations { get; }
}
