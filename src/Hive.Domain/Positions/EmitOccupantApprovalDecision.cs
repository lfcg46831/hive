using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Requests that a position decide one correlated approval request on behalf of an authenticated
/// occupant principal. The position remains the organizational sender and the principal is retained
/// as authorship audit evidence.
/// </summary>
public sealed record EmitOccupantApprovalDecision : PositionCommand
{
    public const int MaximumReasonLength = 4_096;

    public EmitOccupantApprovalDecision(
        MessageId requestId,
        MessageId decisionMessageId,
        OccupantReplyAuthor author,
        bool approved,
        string? reason = null)
    {
        RequestId = requestId ?? throw new ArgumentNullException(nameof(requestId));
        DecisionMessageId = decisionMessageId
            ?? throw new ArgumentNullException(nameof(decisionMessageId));
        if (RequestId == DecisionMessageId)
        {
            throw new ArgumentException(
                "An approval decision must use a message identifier different from its request.",
                nameof(decisionMessageId));
        }

        Author = author ?? throw new ArgumentNullException(nameof(author));
        Approved = approved;
        Reason = reason is null ? null : CommandText.RequireContent(reason, nameof(reason));
        if (Reason?.Length > MaximumReasonLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(reason),
                Reason.Length,
                $"Approval decision reason cannot exceed {MaximumReasonLength} characters.");
        }
    }

    public MessageId RequestId { get; }

    public MessageId DecisionMessageId { get; }

    public OccupantReplyAuthor Author { get; }

    public bool Approved { get; }

    public string? Reason { get; }
}
