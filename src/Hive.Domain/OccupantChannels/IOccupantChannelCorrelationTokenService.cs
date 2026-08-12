using Hive.Domain.Identity;

namespace Hive.Domain.OccupantChannels;

/// <summary>
/// Issues and validates channel-neutral correlation tokens. Validation is pure; decision tokens
/// are consumed only through <see cref="RedeemDecisionAsync"/> after the caller has completed the
/// remaining identity and sender checks.
/// </summary>
public interface IOccupantChannelCorrelationTokenService
{
    OccupantChannelCorrelationToken Issue(OccupantChannelCorrelationTokenRequest request);

    OccupantChannelCorrelationTokenValidation Validate(string? token);

    ValueTask<OccupantChannelCorrelationTokenValidation> RedeemDecisionAsync(
        string? token,
        CancellationToken cancellationToken = default);
}

/// <summary>Organizational correlation bound into one signed occupant-channel token.</summary>
public sealed record OccupantChannelCorrelationTokenRequest
{
    public OccupantChannelCorrelationTokenRequest(
        OrganizationId organizationId,
        PositionId positionId,
        MessageId messageId,
        ThreadId threadId,
        MessageId? requestId = null)
    {
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        RequestId = requestId;
    }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    /// <summary>
    /// The approval request being decided. Absence identifies an ordinary work-reply token.
    /// </summary>
    public MessageId? RequestId { get; }
}

/// <summary>Authenticated claims returned only after signature and temporal validation.</summary>
public sealed record OccupantChannelCorrelationTokenClaims
{
    public OccupantChannelCorrelationTokenClaims(
        Guid tokenId,
        OrganizationId organizationId,
        PositionId positionId,
        MessageId messageId,
        ThreadId threadId,
        MessageId? requestId,
        DateTimeOffset issuedAtUtc,
        DateTimeOffset expiresAtUtc)
    {
        if (tokenId == Guid.Empty)
        {
            throw new ArgumentException("Correlation token id cannot be empty.", nameof(tokenId));
        }

        if (issuedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Correlation token issue time must use the UTC offset.",
                nameof(issuedAtUtc));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Correlation token expiration must use the UTC offset.",
                nameof(expiresAtUtc));
        }

        if (expiresAtUtc <= issuedAtUtc)
        {
            throw new ArgumentException(
                "Correlation token expiration must be later than its issue time.",
                nameof(expiresAtUtc));
        }

        TokenId = tokenId;
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        RequestId = requestId;
        IssuedAtUtc = issuedAtUtc;
        ExpiresAtUtc = expiresAtUtc;
    }

    public Guid TokenId { get; }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public MessageId MessageId { get; }

    public ThreadId ThreadId { get; }

    public MessageId? RequestId { get; }

    public DateTimeOffset IssuedAtUtc { get; }

    public DateTimeOffset ExpiresAtUtc { get; }

    public bool IsDecision => RequestId is not null;
}

public enum OccupantChannelCorrelationTokenFailure
{
    Malformed = 1,
    UnsupportedVersion = 2,
    InvalidSignature = 3,
    NotYetValid = 4,
    Expired = 5,
    NotDecision = 6,
    AlreadyUsed = 7,
    UseStoreUnavailable = 8,
}

/// <summary>Mutually exclusive result of validation or decision-token redemption.</summary>
public sealed record OccupantChannelCorrelationTokenValidation
{
    private OccupantChannelCorrelationTokenValidation(
        OccupantChannelCorrelationTokenClaims? claims,
        OccupantChannelCorrelationTokenFailure? failure)
    {
        if ((claims is null) == (failure is null))
        {
            throw new ArgumentException(
                "Correlation token validation must contain either claims or a failure.");
        }

        Claims = claims;
        Failure = failure;
    }

    public OccupantChannelCorrelationTokenClaims? Claims { get; }

    public OccupantChannelCorrelationTokenFailure? Failure { get; }

    public bool IsValid => Claims is not null;

    public static OccupantChannelCorrelationTokenValidation Valid(
        OccupantChannelCorrelationTokenClaims claims) =>
        new(claims ?? throw new ArgumentNullException(nameof(claims)), failure: null);

    public static OccupantChannelCorrelationTokenValidation Invalid(
        OccupantChannelCorrelationTokenFailure failure)
    {
        if (!Enum.IsDefined(failure))
        {
            throw new ArgumentOutOfRangeException(nameof(failure));
        }

        return new(claims: null, failure: failure);
    }
}
