using Hive.Domain.Identity;

namespace Hive.Infrastructure.Identity;

/// <summary>
/// Identity-subsystem boundary for inbound occupant email. Implementations resolve the current
/// user-to-occupation relationship and its active email binding as one fail-closed operation.
/// </summary>
internal interface IInboundOccupantEmailIdentityResolver
{
    Task<InboundOccupantEmailIdentityResolution> ResolveActiveAsync(
        InboundOccupantEmailIdentityQuery query,
        CancellationToken cancellationToken);
}

internal sealed record InboundOccupantEmailIdentityQuery(
    OrganizationId OrganizationId,
    PositionId PositionId);

internal enum InboundOccupantEmailIdentityResolutionStatus
{
    Active = 1,
    OccupationMissing = 2,
    OccupationRevoked = 3,
    BindingMissing = 4,
    BindingRevoked = 5,
    Ambiguous = 6,
    IdentityUnavailable = 7,
}

internal sealed record InboundOccupantEmailIdentityResolution
{
    private InboundOccupantEmailIdentityResolution(
        InboundOccupantEmailIdentityResolutionStatus status,
        OccupantId? occupantId,
        UserId? userId,
        OccupantChannelBindingId? bindingId,
        string? normalizedEndpoint)
    {
        Status = status;
        OccupantId = occupantId;
        UserId = userId;
        BindingId = bindingId;
        NormalizedEndpoint = normalizedEndpoint;
    }

    public InboundOccupantEmailIdentityResolutionStatus Status { get; }

    public OccupantId? OccupantId { get; }

    public UserId? UserId { get; }

    public OccupantChannelBindingId? BindingId { get; }

    /// <summary>Attempt-local personal data. Callers must never persist or log this value.</summary>
    public string? NormalizedEndpoint { get; }

    public static InboundOccupantEmailIdentityResolution Active(
        OccupantId occupantId,
        UserId userId,
        OccupantChannelBindingId bindingId,
        string normalizedEndpoint) =>
        new(
            InboundOccupantEmailIdentityResolutionStatus.Active,
            occupantId ?? throw new ArgumentNullException(nameof(occupantId)),
            userId ?? throw new ArgumentNullException(nameof(userId)),
            bindingId ?? throw new ArgumentNullException(nameof(bindingId)),
            !string.IsNullOrWhiteSpace(normalizedEndpoint)
                ? normalizedEndpoint
                : throw new ArgumentException(
                    "Normalized email endpoint cannot be empty.",
                    nameof(normalizedEndpoint)));

    public static InboundOccupantEmailIdentityResolution OccupationMissing() =>
        Inactive(InboundOccupantEmailIdentityResolutionStatus.OccupationMissing);

    public static InboundOccupantEmailIdentityResolution OccupationRevoked() =>
        Inactive(InboundOccupantEmailIdentityResolutionStatus.OccupationRevoked);

    public static InboundOccupantEmailIdentityResolution BindingMissing() =>
        Inactive(InboundOccupantEmailIdentityResolutionStatus.BindingMissing);

    public static InboundOccupantEmailIdentityResolution BindingRevoked() =>
        Inactive(InboundOccupantEmailIdentityResolutionStatus.BindingRevoked);

    public static InboundOccupantEmailIdentityResolution Ambiguous() =>
        Inactive(InboundOccupantEmailIdentityResolutionStatus.Ambiguous);

    public static InboundOccupantEmailIdentityResolution IdentityUnavailable() =>
        Inactive(InboundOccupantEmailIdentityResolutionStatus.IdentityUnavailable);

    private static InboundOccupantEmailIdentityResolution Inactive(
        InboundOccupantEmailIdentityResolutionStatus status) =>
        new(status, occupantId: null, userId: null, bindingId: null, normalizedEndpoint: null);
}

internal sealed class UnavailableInboundOccupantEmailIdentityResolver
    : IInboundOccupantEmailIdentityResolver
{
    public static UnavailableInboundOccupantEmailIdentityResolver Instance { get; } = new();

    private UnavailableInboundOccupantEmailIdentityResolver()
    {
    }

    public Task<InboundOccupantEmailIdentityResolution> ResolveActiveAsync(
        InboundOccupantEmailIdentityQuery query,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(query);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(InboundOccupantEmailIdentityResolution.IdentityUnavailable());
    }
}
