using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Domain.Positions;

/// <summary>
/// Requests that a position emit a canonical organizational response on behalf of an authenticated
/// occupant principal. The position remains the sender and the principal is retained only as
/// authorship audit evidence.
/// </summary>
public sealed record EmitOccupantReply : PositionCommand
{
    public const int MaximumBodyLength = 4_096;

    public EmitOccupantReply(
        MessageId sourceMessageId,
        MessageId replyMessageId,
        OccupantReplyAuthor author,
        string body,
        ReportKind? reportKind = null,
        DirectiveId? replyDirectiveId = null)
    {
        SourceMessageId = sourceMessageId
            ?? throw new ArgumentNullException(nameof(sourceMessageId));
        ReplyMessageId = replyMessageId
            ?? throw new ArgumentNullException(nameof(replyMessageId));
        if (SourceMessageId == ReplyMessageId)
        {
            throw new ArgumentException(
                "An occupant reply must use a message identifier different from its source message.",
                nameof(replyMessageId));
        }

        Author = author ?? throw new ArgumentNullException(nameof(author));
        Body = CommandText.RequireContent(body, nameof(body));
        if (Body.Length > MaximumBodyLength)
        {
            throw new ArgumentOutOfRangeException(
                nameof(body),
                Body.Length,
                $"Occupant reply text cannot exceed {MaximumBodyLength} characters.");
        }

        if (reportKind is { } kind && !Enum.IsDefined(kind))
        {
            throw new ArgumentOutOfRangeException(
                nameof(reportKind),
                kind,
                "Occupant reply report kind must be progress or done.");
        }

        ReportKind = reportKind;
        ReplyDirectiveId = replyDirectiveId;
    }

    public MessageId SourceMessageId { get; }

    public MessageId ReplyMessageId { get; }

    public OccupantReplyAuthor Author { get; }

    public string Body { get; }

    public ReportKind? ReportKind { get; }

    public DirectiveId? ReplyDirectiveId { get; }
}

/// <summary>The authenticated principal that supplied an occupant reply.</summary>
public sealed record OccupantReplyAuthor
{
    public const int MaximumSubjectIdLength = 256;
    public const int MaximumChannelLength = 128;

    public OccupantReplyAuthor(
        OccupantReplyAuthorKind kind,
        string subjectId,
        string channel)
    {
        Kind = OccupantReplyAuthorKindContract.RequireDefined(kind, nameof(kind));
        SubjectId = RequireValue(
            subjectId,
            MaximumSubjectIdLength,
            nameof(subjectId),
            "Occupant reply author subject identifier");
        Channel = RequireValue(
            channel,
            MaximumChannelLength,
            nameof(channel),
            "Occupant reply author channel");
    }

    public OccupantReplyAuthorKind Kind { get; }

    public string SubjectId { get; }

    public string Channel { get; }

    public static OccupantReplyAuthor HumanUser(string subjectId, string channel) =>
        new(OccupantReplyAuthorKind.HumanUser, subjectId, channel);

    public static OccupantReplyAuthor ExternalOccupant(string subjectId, string channel) =>
        new(OccupantReplyAuthorKind.ExternalOccupant, subjectId, channel);

    public static OccupantReplyAuthor AiAgent(OccupantId occupant) =>
        new(
            OccupantReplyAuthorKind.AiAgent,
            (occupant ?? throw new ArgumentNullException(nameof(occupant))).Value,
            "runtime");

    private static string RequireValue(
        string value,
        int maximumLength,
        string parameterName,
        string displayName)
    {
        var required = CommandText.RequireContent(value, parameterName);
        if (required.Length > maximumLength)
        {
            throw new ArgumentOutOfRangeException(
                parameterName,
                required.Length,
                $"{displayName} cannot exceed {maximumLength} characters.");
        }

        return required;
    }
}

public enum OccupantReplyAuthorKind
{
    HumanUser,
    ExternalOccupant,
    AiAgent,
}

public static class OccupantReplyAuthorKindContract
{
    public static string ToWireValue(OccupantReplyAuthorKind value) =>
        value switch
        {
            OccupantReplyAuthorKind.HumanUser => "human-user",
            OccupantReplyAuthorKind.ExternalOccupant => "external-occupant",
            OccupantReplyAuthorKind.AiAgent => "ai-agent",
            _ => throw new ArgumentOutOfRangeException(
                nameof(value),
                value,
                "Unknown occupant reply author kind."),
        };

    public static bool TryParseWireValue(string? value, out OccupantReplyAuthorKind result)
    {
        switch (value)
        {
            case "human-user":
                result = OccupantReplyAuthorKind.HumanUser;
                return true;
            case "external-occupant":
                result = OccupantReplyAuthorKind.ExternalOccupant;
                return true;
            case "ai-agent":
                result = OccupantReplyAuthorKind.AiAgent;
                return true;
            default:
                result = default;
                return false;
        }
    }

    public static OccupantReplyAuthorKind RequireDefined(
        OccupantReplyAuthorKind value,
        string parameterName) =>
        Enum.IsDefined(value)
            ? value
            : throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Unknown occupant reply author kind.");
}

public sealed record OccupantReplyEmissionError
{
    public OccupantReplyEmissionError(string code, string path, RejectionReason reason)
    {
        Code = CommandText.RequireContent(code, nameof(code));
        Path = CommandText.RequireContent(path, nameof(path));
        Reason = RejectionReasonContract.RequireDefined(reason, nameof(reason));
    }

    public string Code { get; }

    public string Path { get; }

    public RejectionReason Reason { get; }
}

/// <summary>The reply returned across the sharding boundary to the public API.</summary>
public sealed record OccupantReplyEmissionResult
{
    public OccupantReplyEmissionResult(
        MessageId sourceMessageId,
        OrgMessage? message,
        ImmutableArray<OccupantReplyEmissionError> errors)
    {
        SourceMessageId = sourceMessageId
            ?? throw new ArgumentNullException(nameof(sourceMessageId));
        if (errors.IsDefault)
        {
            throw new ArgumentException("Occupant reply errors must be initialized.", nameof(errors));
        }

        Errors = errors;
        if (Errors.Any(static error => error is null))
        {
            throw new ArgumentException("Occupant reply errors cannot contain null entries.", nameof(errors));
        }

        if ((message is null) == Errors.IsEmpty)
        {
            throw new ArgumentException(
                "An accepted occupant reply requires a message and a rejected reply requires errors.",
                nameof(errors));
        }

        Message = message;
    }

    public MessageId SourceMessageId { get; }

    public OrgMessage? Message { get; }

    public ImmutableArray<OccupantReplyEmissionError> Errors { get; }

    public bool IsAccepted => Message is not null;

    public static OccupantReplyEmissionResult Accepted(
        MessageId sourceMessageId,
        OrgMessage message)
    {
        ArgumentNullException.ThrowIfNull(message);
        return new OccupantReplyEmissionResult(
            sourceMessageId,
            message,
            ImmutableArray<OccupantReplyEmissionError>.Empty);
    }

    public static OccupantReplyEmissionResult Rejected(
        MessageId sourceMessageId,
        params OccupantReplyEmissionError[] errors) =>
        new(sourceMessageId, message: null, errors.ToImmutableArray());
}
