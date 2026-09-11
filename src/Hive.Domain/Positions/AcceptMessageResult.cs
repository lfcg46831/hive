using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

public enum AcceptMessageDecision
{
    Accepted = 1,
    AlreadyAccepted = 2,
    Rejected = 3,
}

public static class AcceptMessageDecisionContract
{
    public static string ToWireValue(AcceptMessageDecision value) =>
        value switch
        {
            AcceptMessageDecision.Accepted => "accepted",
            AcceptMessageDecision.AlreadyAccepted => "already-accepted",
            AcceptMessageDecision.Rejected => "rejected",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unknown message acceptance decision."),
        };

    public static bool TryParseWireValue(
        string? value,
        out AcceptMessageDecision result)
    {
        switch (value)
        {
            case "accepted":
                result = AcceptMessageDecision.Accepted;
                return true;
            case "already-accepted":
                result = AcceptMessageDecision.AlreadyAccepted;
                return true;
            case "rejected":
                result = AcceptMessageDecision.Rejected;
                return true;
            default:
                result = default;
                return false;
        }
    }
}

/// <summary>
/// Recipient admission outcome: durable acceptance, an already accepted id, or a rejection
/// exposing only its coarse public reason. Rejected ids remain eligible for retry.
/// </summary>
public sealed record AcceptMessageResult
{
    public AcceptMessageResult(MessageId messageId, AcceptMessageDecision decision, RejectionReason? reason = null)
    {
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        Decision = Enum.IsDefined(decision)
            ? decision
            : throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "Unknown message acceptance decision.");
        if ((decision == AcceptMessageDecision.Rejected) != (reason is not null))
            throw new ArgumentException("Only rejected admissions require a reason.", nameof(reason));
        Reason = reason is { } value ? RejectionReasonContract.RequireDefined(value, nameof(reason)) : null;
    }

    public MessageId MessageId { get; }

    public AcceptMessageDecision Decision { get; }

    [System.Text.Json.Serialization.JsonIgnore(Condition = System.Text.Json.Serialization.JsonIgnoreCondition.WhenWritingNull)]
    public RejectionReason? Reason { get; }

    public bool IsAccepted =>
        Decision is AcceptMessageDecision.Accepted or AcceptMessageDecision.AlreadyAccepted;

    public static AcceptMessageResult Accepted(MessageId messageId) =>
        new(messageId, AcceptMessageDecision.Accepted);

    public static AcceptMessageResult AlreadyAccepted(MessageId messageId) =>
        new(messageId, AcceptMessageDecision.AlreadyAccepted);

    public static AcceptMessageResult Rejected(MessageId messageId, RejectionReason reason) =>
        new(messageId, AcceptMessageDecision.Rejected, reason);
}
