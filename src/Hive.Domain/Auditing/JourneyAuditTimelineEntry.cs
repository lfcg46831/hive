using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public sealed record JourneyAuditTimelineEntry
{
    public JourneyAuditTimelineEntry(
        Guid auditEventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset persistedAtUtc,
        JourneyAuditStage stage,
        JourneyAuditOutcome outcome,
        MessageId messageId,
        DirectiveId? directiveId = null,
        PositionId? positionId = null,
        string? reasonCode = null,
        string? messageType = null,
        AiProviderMetadata? provider = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        TimeSpan? latency = null,
        IReadOnlyDictionary<string, string>? redactedPayload = null)
    {
        if (auditEventId == Guid.Empty)
        {
            throw new ArgumentException(
                "Audit event id cannot be empty.",
                nameof(auditEventId));
        }

        if (occurredAtUtc == default)
        {
            throw new ArgumentException(
                "Audit event occurrence timestamp must be specified.",
                nameof(occurredAtUtc));
        }

        if (persistedAtUtc == default)
        {
            throw new ArgumentException(
                "Audit event persistence timestamp must be specified.",
                nameof(persistedAtUtc));
        }

        if (!Enum.IsDefined(stage))
        {
            throw new ArgumentOutOfRangeException(nameof(stage), stage, "Unknown journey audit stage.");
        }

        if (!Enum.IsDefined(outcome))
        {
            throw new ArgumentOutOfRangeException(nameof(outcome), outcome, "Unknown journey audit outcome.");
        }

        if (latency is { } latencyValue && latencyValue < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latency),
                latency,
                "Journey audit latency cannot be negative.");
        }

        AuditEventId = auditEventId;
        OccurredAtUtc = occurredAtUtc;
        PersistedAtUtc = persistedAtUtc;
        Stage = stage;
        Outcome = outcome;
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        DirectiveId = directiveId;
        PositionId = positionId;
        ReasonCode = OptionalText(reasonCode, nameof(reasonCode));
        MessageType = OptionalText(messageType, nameof(messageType));
        Provider = provider;
        Usage = usage;
        Cost = cost;
        Latency = latency;
        RedactedPayload = SnapshotPayload(redactedPayload);
    }

    public Guid AuditEventId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset PersistedAtUtc { get; }

    public JourneyAuditStage Stage { get; }

    public JourneyAuditOutcome Outcome { get; }

    public MessageId MessageId { get; }

    public DirectiveId? DirectiveId { get; }

    public PositionId? PositionId { get; }

    public string? ReasonCode { get; }

    public string? MessageType { get; }

    public AiProviderMetadata? Provider { get; }

    public AiTokenUsage? Usage { get; }

    public AiCostMetadata? Cost { get; }

    public TimeSpan? Latency { get; }

    public IReadOnlyDictionary<string, string> RedactedPayload { get; }

    public static JourneyAuditTimelineEntry FromRecord(JourneyAuditRecord record)
    {
        ArgumentNullException.ThrowIfNull(record);

        return new JourneyAuditTimelineEntry(
            record.AuditEventId,
            record.OccurredAtUtc,
            record.PersistedAtUtc,
            record.Stage,
            record.Outcome,
            record.MessageId,
            record.DirectiveId,
            record.PositionId,
            record.ReasonCode,
            record.MessageType,
            record.Provider,
            record.Usage,
            record.Cost,
            record.Latency,
            record.Payload);
    }

    private static string? OptionalText(string? value, string parameterName)
    {
        if (value is null)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException("Value cannot contain leading or trailing whitespace.", parameterName);
        }

        return value;
    }

    private static IReadOnlyDictionary<string, string> SnapshotPayload(
        IReadOnlyDictionary<string, string>? payload)
    {
        if (payload is null || payload.Count == 0)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in payload)
        {
            builder[RequireText(key, nameof(payload))] = value
                ?? throw new ArgumentException("Payload values cannot be null.", nameof(payload));
        }

        return builder.ToImmutable();
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        return value;
    }
}
