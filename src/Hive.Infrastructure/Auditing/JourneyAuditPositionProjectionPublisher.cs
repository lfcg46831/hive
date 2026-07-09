using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Infrastructure.Auditing;

public sealed class JourneyAuditPositionProjectionPublisher : IPositionProjectionPublisher
{
    private readonly IJourneyAuditLog _auditLog;
    private readonly IPositionProjectionPublisher? _inner;
    private readonly Dictionary<MessageId, DirectiveId> _directiveByMessage = new();
    private readonly Dictionary<MessageId, string> _messageTypeByMessage = new();

    public JourneyAuditPositionProjectionPublisher(
        IJourneyAuditLog auditLog,
        IPositionProjectionPublisher? inner = null)
    {
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _inner = inner;
    }

    public void Publish(PositionProjectionEvent @event)
    {
        ArgumentNullException.ThrowIfNull(@event);

        if (@event is PositionEventCommitted committed)
        {
            PublishCommitted(committed);
        }

        _inner?.Publish(@event);
    }

    private void PublishCommitted(PositionEventCommitted committed)
    {
        switch (committed.Event)
        {
            case MessageReceived received:
                Remember(received.Message);
                _auditLog.Append(Record(
                    JourneyAuditStage.PositionAccepted,
                    committed.EntityId.Position,
                    received.Message,
                    committed.OccurredAt));
                break;

            case MessageDispatched dispatched:
                _auditLog.Append(JourneyAuditRecord.Create(
                    JourneyAuditStage.PositionDispatched,
                    JourneyAuditOutcome.Accepted,
                    committed.EntityId.Organization,
                    dispatched.Thread,
                    dispatched.Message,
                    directiveId: DirectiveFor(dispatched.Message),
                    positionId: committed.EntityId.Position,
                    messageType: MessageTypeFor(dispatched.Message),
                    payload: new Dictionary<string, string>(StringComparer.Ordinal)
                    {
                        ["source"] = nameof(PositionEventCommitted),
                        ["occupantType"] = dispatched.OccupantType.ToString(),
                        ["redactions"] = "message.payload",
                    },
                    occurredAtUtc: committed.OccurredAt));
                break;
        }
    }

    private void Remember(OrgMessage message)
    {
        _messageTypeByMessage[message.Id] = message.GetType().Name;
        if (message is Directive directive)
        {
            _directiveByMessage[message.Id] = directive.DirectiveId;
        }
        else if (message is Report report)
        {
            _directiveByMessage[message.Id] = report.AboutDirectiveId;
        }
    }

    private JourneyAuditRecord Record(
        JourneyAuditStage stage,
        PositionId positionId,
        OrgMessage message,
        DateTimeOffset occurredAt) =>
        JourneyAuditRecord.Create(
            stage,
            JourneyAuditOutcome.Accepted,
            message.OrganizationId,
            message.Thread,
            message.Id,
            directiveId: DirectiveFor(message.Id),
            positionId: positionId,
            messageType: message.GetType().Name,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["source"] = nameof(PositionEventCommitted),
                ["channel"] = message.Channel.ToString(),
                ["priority"] = message.Priority.ToString(),
                ["redactions"] = "message.payload",
            },
            occurredAtUtc: occurredAt);

    private DirectiveId? DirectiveFor(MessageId message) =>
        _directiveByMessage.TryGetValue(message, out var directiveId)
            ? directiveId
            : null;

    private string? MessageTypeFor(MessageId message) =>
        _messageTypeByMessage.TryGetValue(message, out var messageType)
            ? messageType
            : null;
}
