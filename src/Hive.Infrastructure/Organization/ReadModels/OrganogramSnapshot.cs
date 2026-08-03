using Hive.Domain.Organization.Configuration;

namespace Hive.Infrastructure.Organization.ReadModels;

public sealed record OrganogramSnapshot
{
    internal OrganogramSnapshot(
        string organizationId,
        long registryVersion,
        string registryFingerprint,
        DateTimeOffset importedAtUtc,
        string? organizationName,
        string rootUnitId,
        string rootPositionId,
        IReadOnlyList<OrganogramUnitSnapshot> units,
        IReadOnlyList<OrganogramPositionSnapshot> positions,
        IReadOnlyList<PositionLiveStateSnapshot> positionStates)
    {
        OrganizationId = organizationId;
        RegistryVersion = registryVersion;
        RegistryFingerprint = registryFingerprint;
        ImportedAtUtc = importedAtUtc;
        OrganizationName = organizationName;
        RootUnitId = rootUnitId;
        RootPositionId = rootPositionId;
        Units = units;
        Positions = positions;
        PositionStates = positionStates;
    }

    public string OrganizationId { get; }

    public long RegistryVersion { get; }

    public string RegistryFingerprint { get; }

    public DateTimeOffset ImportedAtUtc { get; }

    public string? OrganizationName { get; }

    public string RootUnitId { get; }

    public string RootPositionId { get; }

    public IReadOnlyList<OrganogramUnitSnapshot> Units { get; }

    public IReadOnlyList<OrganogramPositionSnapshot> Positions { get; }

    public IReadOnlyList<PositionLiveStateSnapshot> PositionStates { get; }
}

public sealed record OrganogramUnitSnapshot(
    string Id,
    string? Name,
    string? ParentUnitId,
    string LeadershipPositionId);

public sealed record OrganogramPositionSnapshot(
    string Id,
    string? Name,
    string UnitId,
    OccupantType OccupantType,
    string? ReportsToPositionId);
