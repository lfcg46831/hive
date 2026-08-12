using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;

namespace Hive.Infrastructure.OccupantChannels;

internal enum InboundOccupantEmailContentTrust
{
    Untrusted = 1,
}

internal enum InboundOccupantEmailFailureCode
{
    MalformedMessage = 1,
    SenderMissing = 2,
    SenderAmbiguous = 3,
    PlainTextBodyMissing = 4,
    CorrelationTokenMissing = 5,
    CorrelationTokenAmbiguous = 6,
    PlainTextReplyMissing = 7,
    TokenMalformed = 8,
    TokenUnsupportedVersion = 9,
    TokenInvalidSignature = 10,
    TokenNotYetValid = 11,
    TokenExpired = 12,
    OccupationMissing = 13,
    OccupationRevoked = 14,
    BindingMissing = 15,
    BindingRevoked = 16,
    IdentityAmbiguous = 17,
    SenderMismatch = 18,
    DecisionTokenAlreadyUsed = 19,
    IdentityUnavailable = 20,
    DecisionTokenStoreUnavailable = 21,
}

internal static class InboundOccupantEmailFailureCodes
{
    public static string ToCode(this InboundOccupantEmailFailureCode value) => value switch
    {
        InboundOccupantEmailFailureCode.MalformedMessage => "malformed-message",
        InboundOccupantEmailFailureCode.SenderMissing => "sender-missing",
        InboundOccupantEmailFailureCode.SenderAmbiguous => "sender-ambiguous",
        InboundOccupantEmailFailureCode.PlainTextBodyMissing => "plain-text-body-missing",
        InboundOccupantEmailFailureCode.CorrelationTokenMissing => "correlation-token-missing",
        InboundOccupantEmailFailureCode.CorrelationTokenAmbiguous => "correlation-token-ambiguous",
        InboundOccupantEmailFailureCode.PlainTextReplyMissing => "plain-text-reply-missing",
        InboundOccupantEmailFailureCode.TokenMalformed => "token-malformed",
        InboundOccupantEmailFailureCode.TokenUnsupportedVersion => "token-unsupported-version",
        InboundOccupantEmailFailureCode.TokenInvalidSignature => "token-invalid-signature",
        InboundOccupantEmailFailureCode.TokenNotYetValid => "token-not-yet-valid",
        InboundOccupantEmailFailureCode.TokenExpired => "token-expired",
        InboundOccupantEmailFailureCode.OccupationMissing => "occupation-missing",
        InboundOccupantEmailFailureCode.OccupationRevoked => "occupation-revoked",
        InboundOccupantEmailFailureCode.BindingMissing => "binding-missing",
        InboundOccupantEmailFailureCode.BindingRevoked => "binding-revoked",
        InboundOccupantEmailFailureCode.IdentityAmbiguous => "identity-ambiguous",
        InboundOccupantEmailFailureCode.SenderMismatch => "sender-mismatch",
        InboundOccupantEmailFailureCode.DecisionTokenAlreadyUsed => "decision-token-already-used",
        InboundOccupantEmailFailureCode.IdentityUnavailable => "identity-unavailable",
        InboundOccupantEmailFailureCode.DecisionTokenStoreUnavailable =>
            "decision-token-store-unavailable",
        _ => throw new ArgumentOutOfRangeException(nameof(value), value, "Unknown inbound email failure."),
    };
}

internal sealed record InboundOccupantEmailAdmission(
    ImapInboundEmailEnvelope Envelope,
    OccupantChannelCorrelationTokenClaims Correlation,
    OccupantId OccupantId,
    UserId UserId,
    OccupantChannelBindingId BindingId,
    string PlainTextReply,
    InboundOccupantEmailContentTrust ContentTrust);

internal enum InboundOccupantEmailParseStatus
{
    Accepted = 1,
    Rejected = 2,
    RetryableFailure = 3,
}

internal sealed record InboundOccupantEmailParseResult
{
    private InboundOccupantEmailParseResult(
        InboundOccupantEmailParseStatus status,
        InboundOccupantEmailAdmission? admission,
        InboundOccupantEmailFailureCode? failure)
    {
        Status = status;
        Admission = admission;
        Failure = failure;
    }

    public InboundOccupantEmailParseStatus Status { get; }

    public InboundOccupantEmailAdmission? Admission { get; }

    public InboundOccupantEmailFailureCode? Failure { get; }

    public static InboundOccupantEmailParseResult Accepted(
        InboundOccupantEmailAdmission admission) =>
        new(
            InboundOccupantEmailParseStatus.Accepted,
            admission ?? throw new ArgumentNullException(nameof(admission)),
            failure: null);

    public static InboundOccupantEmailParseResult Rejected(
        InboundOccupantEmailFailureCode failure) =>
        new(InboundOccupantEmailParseStatus.Rejected, admission: null, RequireFailure(failure));

    public static InboundOccupantEmailParseResult Retryable(
        InboundOccupantEmailFailureCode failure) =>
        new(InboundOccupantEmailParseStatus.RetryableFailure, admission: null, RequireFailure(failure));

    private static InboundOccupantEmailFailureCode RequireFailure(
        InboundOccupantEmailFailureCode failure) => Enum.IsDefined(failure)
        ? failure
        : throw new ArgumentOutOfRangeException(nameof(failure));
}

internal interface IInboundOccupantEmailParser
{
    Task<InboundOccupantEmailParseResult> ParseAsync(
        ImapInboundEmailEnvelope envelope,
        CancellationToken cancellationToken = default);
}

internal sealed record InboundOccupantEmailProcessingResult(
    int PendingCount,
    int AcceptedCount,
    int RejectedCount,
    int RetryableCount,
    int AlreadyCompletedCount);

internal interface IInboundOccupantEmailProcessor
{
    Task<InboundOccupantEmailProcessingResult> ProcessPendingAsync(
        CancellationToken cancellationToken = default);
}

internal sealed record InboundOccupantEmailReplyProcessingResult(
    int PendingCount,
    int EmittedCount,
    int RejectedCount,
    int RetryableCount,
    int AlreadyCompletedCount);

internal interface IInboundOccupantEmailReplyProcessor
{
    Task<InboundOccupantEmailReplyProcessingResult> ProcessAcceptedAsync(
        CancellationToken cancellationToken = default);
}

internal sealed record InboundOccupantEmailDecisionProcessingResult(
    int PendingCount,
    int EmittedCount,
    int RejectedCount,
    int RetryableCount,
    int AlreadyCompletedCount);

internal interface IInboundOccupantEmailDecisionProcessor
{
    Task<InboundOccupantEmailDecisionProcessingResult> ProcessAcceptedAsync(
        CancellationToken cancellationToken = default);
}
