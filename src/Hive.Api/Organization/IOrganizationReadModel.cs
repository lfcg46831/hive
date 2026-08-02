using Hive.Contracts.Organization;
using Hive.Domain.Identity;

namespace Hive.Api.Organization;

/// <summary>
/// Read-only seam between the public organization API and the materialized organogram/state views.
/// </summary>
public interface IOrganizationReadModel
{
    ValueTask<OrganizationReadResult<OrganogramResponse>> ReadOrganogramAsync(
        OrganizationId organizationId,
        UnitId? rootUnitId,
        CancellationToken cancellationToken);

    ValueTask<OrganizationReadResult<PositionDetailResponse>> ReadPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken);

    ValueTask<OrganizationReadResult<PositionStatesResponse>> ReadPositionStatesAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken);
}

/// <summary>
/// Distinguishes a missing resource from a read model that is not available on this node.
/// </summary>
public readonly record struct OrganizationReadResult<T>
    where T : class
{
    private OrganizationReadResult(bool isAvailable, T? value)
    {
        IsAvailable = isAvailable;
        Value = value;
    }

    public bool IsAvailable { get; }

    public T? Value { get; }

    public static OrganizationReadResult<T> Available(T? value) => new(true, value);

    public static OrganizationReadResult<T> Unavailable { get; } = new(false, null);
}

internal sealed class UnavailableOrganizationReadModel : IOrganizationReadModel
{
    public static UnavailableOrganizationReadModel Instance { get; } = new();

    private UnavailableOrganizationReadModel()
    {
    }

    public ValueTask<OrganizationReadResult<OrganogramResponse>> ReadOrganogramAsync(
        OrganizationId organizationId,
        UnitId? rootUnitId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(OrganizationReadResult<OrganogramResponse>.Unavailable);

    public ValueTask<OrganizationReadResult<PositionDetailResponse>> ReadPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(OrganizationReadResult<PositionDetailResponse>.Unavailable);

    public ValueTask<OrganizationReadResult<PositionStatesResponse>> ReadPositionStatesAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken) =>
        ValueTask.FromResult(OrganizationReadResult<PositionStatesResponse>.Unavailable);
}
