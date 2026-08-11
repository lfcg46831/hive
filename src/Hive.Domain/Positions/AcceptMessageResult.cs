using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

public enum AcceptMessageDecision
{
    Accepted = 1,
    AlreadyAccepted = 2,
}

public static class AcceptMessageDecisionContract
{
    public static string ToWireValue(AcceptMessageDecision value) =>
        value switch
        {
            AcceptMessageDecision.Accepted => "accepted",
            AcceptMessageDecision.AlreadyAccepted => "already-accepted",
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
            default:
                result = default;
                return false;
        }
    }
}

/// <summary>
/// Explicit acknowledgement returned only after a position has durably accepted an inbound
/// message, or has proved that the same message identifier was accepted previously.
/// </summary>
public sealed record AcceptMessageResult
{
    public AcceptMessageResult(MessageId messageId, AcceptMessageDecision decision)
    {
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        Decision = Enum.IsDefined(decision)
            ? decision
            : throw new ArgumentOutOfRangeException(
                nameof(decision),
                decision,
                "Unknown message acceptance decision.");
    }

    public MessageId MessageId { get; }

    public AcceptMessageDecision Decision { get; }

    public bool IsAccepted =>
        Decision is AcceptMessageDecision.Accepted or AcceptMessageDecision.AlreadyAccepted;

    public static AcceptMessageResult Accepted(MessageId messageId) =>
        new(messageId, AcceptMessageDecision.Accepted);

    public static AcceptMessageResult AlreadyAccepted(MessageId messageId) =>
        new(messageId, AcceptMessageDecision.AlreadyAccepted);
}
