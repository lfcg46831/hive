using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Requests that a position map a channel-correlated work response using the canonical closed
/// reply mapping. The expected thread is authenticated by the channel and checked against the
/// source message before emission.
/// </summary>
public sealed record EmitCorrelatedOccupantReply : PositionCommand
{
    public EmitCorrelatedOccupantReply(
        MessageId sourceMessageId,
        ThreadId sourceThreadId,
        MessageId replyMessageId,
        DirectiveId replyDirectiveId,
        OccupantReplyAuthor author,
        string body,
        ReportKind directiveReportKind)
    {
        SourceThreadId = sourceThreadId
            ?? throw new ArgumentNullException(nameof(sourceThreadId));
        ReplyDirectiveId = replyDirectiveId
            ?? throw new ArgumentNullException(nameof(replyDirectiveId));

        var validated = new EmitOccupantReply(
            sourceMessageId,
            replyMessageId,
            author,
            body,
            directiveReportKind,
            replyDirectiveId);
        SourceMessageId = validated.SourceMessageId;
        ReplyMessageId = validated.ReplyMessageId;
        Author = validated.Author;
        Body = validated.Body;
        DirectiveReportKind = validated.ReportKind!.Value;
    }

    public MessageId SourceMessageId { get; }

    public ThreadId SourceThreadId { get; }

    public MessageId ReplyMessageId { get; }

    public DirectiveId ReplyDirectiveId { get; }

    public OccupantReplyAuthor Author { get; }

    public string Body { get; }

    /// <summary>
    /// Report kind to use only when the correlated source is a <see cref="Directive"/>. Other
    /// source types select their mapping inside the position and ignore this value.
    /// </summary>
    public ReportKind DirectiveReportKind { get; }
}
