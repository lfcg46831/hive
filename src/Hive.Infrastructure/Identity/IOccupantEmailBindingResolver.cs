using Hive.Domain.Identity;

namespace Hive.Infrastructure.Identity;

/// <summary>
/// Identity-subsystem boundary used by the SMTP adapter to resolve personal data only for the
/// duration of one delivery attempt. Implementations must verify the user, occupation and binding
/// as one active relationship; an id match on its own is insufficient.
/// </summary>
internal interface IOccupantEmailBindingResolver
{
    Task<OccupantEmailBindingResolution> ResolveActiveAsync(
        OccupantEmailBindingQuery query,
        CancellationToken cancellationToken);
}

internal sealed record OccupantEmailBindingQuery(
    OrganizationId OrganizationId,
    PositionId PositionId,
    OccupantId OccupantId,
    UserId UserId,
    OccupantChannelBindingId BindingId);

internal enum OccupantEmailBindingResolutionStatus
{
    Active = 1,
    Missing = 2,
    Revoked = 3,
    IdentityUnavailable = 4,
}

internal sealed record OccupantEmailBindingResolution
{
    private OccupantEmailBindingResolution(
        OccupantEmailBindingResolutionStatus status,
        string? normalizedEndpoint)
    {
        Status = status;
        NormalizedEndpoint = normalizedEndpoint;
    }

    public OccupantEmailBindingResolutionStatus Status { get; }

    /// <summary>
    /// Personal data with attempt-local lifetime. Callers must not persist or log this value.
    /// </summary>
    public string? NormalizedEndpoint { get; }

    public static OccupantEmailBindingResolution Active(string normalizedEndpoint)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(normalizedEndpoint);
        return new(OccupantEmailBindingResolutionStatus.Active, normalizedEndpoint);
    }

    public static OccupantEmailBindingResolution Missing() =>
        new(OccupantEmailBindingResolutionStatus.Missing, normalizedEndpoint: null);

    public static OccupantEmailBindingResolution Revoked() =>
        new(OccupantEmailBindingResolutionStatus.Revoked, normalizedEndpoint: null);

    public static OccupantEmailBindingResolution IdentityUnavailable() =>
        new(OccupantEmailBindingResolutionStatus.IdentityUnavailable, normalizedEndpoint: null);
}

internal sealed class UnavailableOccupantEmailBindingResolver : IOccupantEmailBindingResolver
{
    public static UnavailableOccupantEmailBindingResolver Instance { get; } = new();

    private UnavailableOccupantEmailBindingResolver()
    {
    }

    public Task<OccupantEmailBindingResolution> ResolveActiveAsync(
        OccupantEmailBindingQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(OccupantEmailBindingResolution.IdentityUnavailable());
    }
}
