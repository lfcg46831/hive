using System.Text.Json;
using Hive.Domain.Messaging;

namespace Hive.Infrastructure.Inbox.ReadModels.PostgreSql;

internal static class PostgreSqlInboxMessageContentJson
{
    public static string Serialize(
        InboxProjectionMessageType messageType,
        InboxProjectionMessageContent content)
    {
        ArgumentNullException.ThrowIfNull(content);
        if (content.MessageType != messageType)
        {
            throw new InvalidOperationException(
                $"Inbox content '{content.GetType().Name}' does not match message type '{messageType}'.");
        }

        return content switch
        {
            InboxProjectionDirectiveContent directive => JsonSerializer.Serialize(new
            {
                objective = directive.Objective,
                context = directive.Context,
            }),
            InboxProjectionReportContent report => JsonSerializer.Serialize(new
            {
                body = report.Body,
                kind = ReportKindContract.ToWireValue(report.Kind),
            }),
            InboxProjectionEscalationContent escalation => JsonSerializer.Serialize(new
            {
                issue = escalation.Issue,
                context = escalation.Context,
            }),
            InboxProjectionMemoContent memo => JsonSerializer.Serialize(new
            {
                body = memo.Body,
            }),
            InboxProjectionPeerRequestContent request => JsonSerializer.Serialize(new
            {
                ask = request.Ask,
            }),
            InboxProjectionPeerResponseContent response => JsonSerializer.Serialize(new
            {
                body = response.Body,
            }),
            InboxProjectionApprovalRequestContent request => JsonSerializer.Serialize(new
            {
                action = request.Action,
                justification = request.Justification,
            }),
            InboxProjectionApprovalDecisionContent { Reason: null } => "{}",
            InboxProjectionApprovalDecisionContent decision => JsonSerializer.Serialize(new
            {
                reason = decision.Reason,
            }),
            _ => throw new InvalidOperationException(
                $"Inbox content '{content.GetType().Name}' has no PostgreSQL JSON mapping."),
        };
    }

    public static InboxProjectionMessageContent Deserialize(
        InboxProjectionMessageType messageType,
        string json)
    {
        ArgumentNullException.ThrowIfNull(json);
        using var document = JsonDocument.Parse(json);
        var root = document.RootElement;
        if (root.ValueKind != JsonValueKind.Object)
        {
            throw new InvalidOperationException("Materialized inbox message content is not an object.");
        }

        return messageType switch
        {
            InboxProjectionMessageType.Directive => new InboxProjectionDirectiveContent(
                RequiredString(root, "objective", "context"),
                RequiredString(root, "context", "objective")),
            InboxProjectionMessageType.Report => new InboxProjectionReportContent(
                RequiredString(root, "body", "kind"),
                ReportKindContract.ParseWireValue(RequiredString(root, "kind", "body"))),
            InboxProjectionMessageType.Escalation => new InboxProjectionEscalationContent(
                RequiredString(root, "issue", "context"),
                RequiredString(root, "context", "issue")),
            InboxProjectionMessageType.Memo => new InboxProjectionMemoContent(
                RequiredString(root, "body")),
            InboxProjectionMessageType.PeerRequest => new InboxProjectionPeerRequestContent(
                RequiredString(root, "ask")),
            InboxProjectionMessageType.PeerResponse => new InboxProjectionPeerResponseContent(
                RequiredString(root, "body")),
            InboxProjectionMessageType.ApprovalRequest =>
                new InboxProjectionApprovalRequestContent(
                    RequiredString(root, "action", "justification"),
                    RequiredString(root, "justification", "action")),
            InboxProjectionMessageType.ApprovalDecision =>
                new InboxProjectionApprovalDecisionContent(OptionalReason(root)),
            _ => throw new InvalidOperationException(
                $"Inbox message type '{messageType}' has no PostgreSQL content mapping."),
        };
    }

    private static string RequiredString(
        JsonElement root,
        string propertyName,
        params string[] otherProperties)
    {
        RequireExactProperties(root, [propertyName, .. otherProperties]);
        if (!root.TryGetProperty(propertyName, out var property) ||
            property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                $"Materialized inbox message content has no string '{propertyName}'.");
        }

        return property.GetString()!;
    }

    private static string? OptionalReason(JsonElement root)
    {
        RequireExactProperties(
            root,
            root.TryGetProperty("reason", out _) ? ["reason"] : []);
        if (!root.TryGetProperty("reason", out var property))
        {
            return null;
        }

        if (property.ValueKind != JsonValueKind.String)
        {
            throw new InvalidOperationException(
                "Materialized inbox message content reason is not a string.");
        }

        return property.GetString();
    }

    private static void RequireExactProperties(
        JsonElement root,
        IReadOnlyCollection<string> expected)
    {
        var actual = root.EnumerateObject().Select(static property => property.Name).ToArray();
        if (actual.Length != expected.Count ||
            actual.Except(expected, StringComparer.Ordinal).Any())
        {
            throw new InvalidOperationException(
                "Materialized inbox message content does not match its closed schema.");
        }
    }
}
