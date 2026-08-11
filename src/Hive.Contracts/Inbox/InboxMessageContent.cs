using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hive.Contracts.Inbox;

/// <summary>
/// Canonical, untrusted message content exposed only by the principal-scoped inbox detail.
/// </summary>
[JsonPolymorphic(TypeDiscriminatorPropertyName = "type")]
[JsonDerivedType(typeof(InboxDirectiveMessageContent), nameof(InboxMessageType.Directive))]
[JsonDerivedType(typeof(InboxReportMessageContent), nameof(InboxMessageType.Report))]
[JsonDerivedType(typeof(InboxEscalationMessageContent), nameof(InboxMessageType.Escalation))]
[JsonDerivedType(typeof(InboxMemoMessageContent), nameof(InboxMessageType.Memo))]
[JsonDerivedType(typeof(InboxPeerRequestMessageContent), nameof(InboxMessageType.PeerRequest))]
[JsonDerivedType(typeof(InboxPeerResponseMessageContent), nameof(InboxMessageType.PeerResponse))]
[JsonDerivedType(typeof(InboxApprovalRequestMessageContent), nameof(InboxMessageType.ApprovalRequest))]
[JsonDerivedType(typeof(InboxApprovalDecisionMessageContent), nameof(InboxMessageType.ApprovalDecision))]
public abstract record InboxMessageContent
{
    internal abstract InboxMessageType MessageType { get; }

    protected static string Text(string value, string parameterName) =>
        value ?? throw new ArgumentNullException(parameterName);
}

public sealed record InboxDirectiveMessageContent : InboxMessageContent
{
    public InboxDirectiveMessageContent(string objective, string context)
    {
        Objective = Text(objective, nameof(objective));
        Context = Text(context, nameof(context));
    }

    internal override InboxMessageType MessageType => InboxMessageType.Directive;

    [JsonPropertyName("objective")]
    public string Objective { get; }

    [JsonPropertyName("context")]
    public string Context { get; }
}

public sealed record InboxReportMessageContent : InboxMessageContent
{
    public InboxReportMessageContent(string body, InboxReportKind kind)
    {
        Body = Text(body, nameof(body));
        Kind = InboxContractGuards.DefinedEnum(kind, nameof(kind));
    }

    internal override InboxMessageType MessageType => InboxMessageType.Report;

    [JsonPropertyName("body")]
    public string Body { get; }

    [JsonPropertyName("kind")]
    public InboxReportKind Kind { get; }
}

public sealed record InboxEscalationMessageContent : InboxMessageContent
{
    public InboxEscalationMessageContent(string issue, string context)
    {
        Issue = Text(issue, nameof(issue));
        Context = Text(context, nameof(context));
    }

    internal override InboxMessageType MessageType => InboxMessageType.Escalation;

    [JsonPropertyName("issue")]
    public string Issue { get; }

    [JsonPropertyName("context")]
    public string Context { get; }
}

public sealed record InboxMemoMessageContent : InboxMessageContent
{
    public InboxMemoMessageContent(string body)
    {
        Body = Text(body, nameof(body));
    }

    internal override InboxMessageType MessageType => InboxMessageType.Memo;

    [JsonPropertyName("body")]
    public string Body { get; }
}

public sealed record InboxPeerRequestMessageContent : InboxMessageContent
{
    public InboxPeerRequestMessageContent(string ask)
    {
        Ask = Text(ask, nameof(ask));
    }

    internal override InboxMessageType MessageType => InboxMessageType.PeerRequest;

    [JsonPropertyName("ask")]
    public string Ask { get; }
}

public sealed record InboxPeerResponseMessageContent : InboxMessageContent
{
    public InboxPeerResponseMessageContent(string body)
    {
        Body = Text(body, nameof(body));
    }

    internal override InboxMessageType MessageType => InboxMessageType.PeerResponse;

    [JsonPropertyName("body")]
    public string Body { get; }
}

public sealed record InboxApprovalRequestMessageContent : InboxMessageContent
{
    public InboxApprovalRequestMessageContent(string action, string justification)
    {
        Action = Text(action, nameof(action));
        Justification = Text(justification, nameof(justification));
    }

    internal override InboxMessageType MessageType => InboxMessageType.ApprovalRequest;

    [JsonPropertyName("action")]
    public string Action { get; }

    [JsonPropertyName("justification")]
    public string Justification { get; }
}

public sealed record InboxApprovalDecisionMessageContent : InboxMessageContent
{
    public InboxApprovalDecisionMessageContent(string? reason)
    {
        Reason = reason;
    }

    internal override InboxMessageType MessageType => InboxMessageType.ApprovalDecision;

    [JsonPropertyName("reason")]
    [JsonIgnore(Condition = JsonIgnoreCondition.WhenWritingNull)]
    public string? Reason { get; }
}

[JsonConverter(typeof(InboxReportKindJsonConverter))]
public enum InboxReportKind
{
    Progress = 1,
    Done = 2,
}

public sealed class InboxReportKindJsonConverter : JsonConverter<InboxReportKind>
{
    public override InboxReportKind Read(
        ref Utf8JsonReader reader,
        Type typeToConvert,
        JsonSerializerOptions options)
    {
        if (reader.TokenType != JsonTokenType.String)
        {
            throw new JsonException("Inbox report kind must be a string.");
        }

        return reader.GetString() switch
        {
            "progress" => InboxReportKind.Progress,
            "done" => InboxReportKind.Done,
            var value => throw new JsonException($"Unknown inbox report kind '{value}'."),
        };
    }

    public override void Write(
        Utf8JsonWriter writer,
        InboxReportKind value,
        JsonSerializerOptions options) =>
        writer.WriteStringValue(value switch
        {
            InboxReportKind.Progress => "progress",
            InboxReportKind.Done => "done",
            _ => throw new JsonException($"Unknown inbox report kind '{value}'."),
        });
}
