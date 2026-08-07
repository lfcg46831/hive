using Hive.Domain.Identity;

namespace Hive.Infrastructure.Inbox.ReadModels;

public enum InboxInteractionReadState
{
    Unread,
    Read,
}

public enum InboxInteractionReplyState
{
    NotStarted,
    InProgress,
}

public enum InboxInteractionAction
{
    MarkRead,
    MarkUnread,
    StartReply,
    SaveDraft,
    ClearDraft,
}

/// <summary>
/// Durable UI-owned state for one person interacting with one projected inbox item. Organizational
/// facts such as a correlated response, an approval decision, expiry or delegation deliberately do
/// not belong to this model.
/// </summary>
public sealed record InboxInteractionState
{
    public InboxInteractionState(
        InboxProjectionItemKey itemKey,
        string personId,
        InboxInteractionReadState readState,
        InboxInteractionReplyState replyState,
        string? draftText,
        DateTimeOffset updatedAtUtc)
    {
        ItemKey = itemKey;
        PersonId = InboxInteractionGuards.PersonIdentifier(personId, nameof(personId));
        ReadState = InboxInteractionGuards.DefinedEnum(readState, nameof(readState));
        ReplyState = InboxInteractionGuards.DefinedEnum(replyState, nameof(replyState));
        UpdatedAtUtc = InboxInteractionGuards.UtcTimestamp(updatedAtUtc, nameof(updatedAtUtc));

        if (draftText is not null && replyState != InboxInteractionReplyState.InProgress)
        {
            throw new ArgumentException(
                "A draft can exist only while a reply is in progress.",
                nameof(draftText));
        }

        DraftText = draftText;
    }

    public InboxProjectionItemKey ItemKey { get; }

    public string PersonId { get; }

    public InboxInteractionReadState ReadState { get; }

    public InboxInteractionReplyState ReplyState { get; }

    public string? DraftText { get; }

    public DateTimeOffset UpdatedAtUtc { get; }
}

/// <summary>
/// One requested UI interaction. Applying it changes only UI-owned state and appends an audit row;
/// it never emits an organizational message.
/// </summary>
public sealed record InboxInteractionMutation
{
    public InboxInteractionMutation(
        InboxProjectionItemKey itemKey,
        string personId,
        InboxInteractionAction action,
        DateTimeOffset occurredAtUtc,
        string? draftText = null)
    {
        ItemKey = itemKey;
        PersonId = InboxInteractionGuards.PersonIdentifier(personId, nameof(personId));
        Action = InboxInteractionGuards.DefinedEnum(action, nameof(action));
        OccurredAtUtc = InboxInteractionGuards.UtcTimestamp(
            occurredAtUtc,
            nameof(occurredAtUtc));

        if ((action == InboxInteractionAction.SaveDraft) != (draftText is not null))
        {
            throw new ArgumentException(
                "Draft text is required only when saving a draft.",
                nameof(draftText));
        }

        DraftText = draftText;
    }

    public InboxProjectionItemKey ItemKey { get; }

    public string PersonId { get; }

    public InboxInteractionAction Action { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string? DraftText { get; }
}

/// <summary>
/// Sanitized audit evidence for a UI interaction. Draft content is intentionally not duplicated in
/// the audit trail; only its presence before and after the action is retained.
/// </summary>
public sealed record InboxInteractionAuditEntry
{
    public InboxInteractionAuditEntry(
        long sequence,
        InboxProjectionItemKey itemKey,
        string personId,
        InboxInteractionAction action,
        InboxInteractionReadState previousReadState,
        InboxInteractionReadState readState,
        InboxInteractionReplyState previousReplyState,
        InboxInteractionReplyState replyState,
        bool previousDraftPresent,
        bool draftPresent,
        DateTimeOffset occurredAtUtc)
    {
        if (sequence < 1)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Audit sequence must be positive.");
        }

        Sequence = sequence;
        ItemKey = itemKey;
        PersonId = InboxInteractionGuards.PersonIdentifier(personId, nameof(personId));
        Action = InboxInteractionGuards.DefinedEnum(action, nameof(action));
        PreviousReadState = InboxInteractionGuards.DefinedEnum(
            previousReadState,
            nameof(previousReadState));
        ReadState = InboxInteractionGuards.DefinedEnum(readState, nameof(readState));
        PreviousReplyState = InboxInteractionGuards.DefinedEnum(
            previousReplyState,
            nameof(previousReplyState));
        ReplyState = InboxInteractionGuards.DefinedEnum(replyState, nameof(replyState));
        PreviousDraftPresent = previousDraftPresent;
        DraftPresent = draftPresent;
        OccurredAtUtc = InboxInteractionGuards.UtcTimestamp(
            occurredAtUtc,
            nameof(occurredAtUtc));
    }

    public long Sequence { get; }

    public InboxProjectionItemKey ItemKey { get; }

    public string PersonId { get; }

    public InboxInteractionAction Action { get; }

    public InboxInteractionReadState PreviousReadState { get; }

    public InboxInteractionReadState ReadState { get; }

    public InboxInteractionReplyState PreviousReplyState { get; }

    public InboxInteractionReplyState ReplyState { get; }

    public bool PreviousDraftPresent { get; }

    public bool DraftPresent { get; }

    public DateTimeOffset OccurredAtUtc { get; }
}

internal static class InboxInteractionGuards
{
    public static string PersonIdentifier(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value) ||
            !string.Equals(value, value.Trim(), StringComparison.Ordinal) ||
            value.Length > 256)
        {
            throw new ArgumentException(
                "Person identifier must contain 1 to 256 characters without surrounding whitespace.",
                parameterName);
        }

        return value;
    }

    public static TEnum DefinedEnum<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, "Value is not defined.");
        }

        return value;
    }

    public static DateTimeOffset UtcTimestamp(DateTimeOffset value, string parameterName)
    {
        if (value.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Timestamp must use the UTC offset.", parameterName);
        }

        return value;
    }
}
