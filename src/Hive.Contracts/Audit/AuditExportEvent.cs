using System.Text.Json.Serialization;

namespace Hive.Contracts.Audit;

public sealed record AuditExportEvent
{
    public AuditExportEvent(
        long sequence,
        Guid eventId,
        DateTimeOffset occurredAtUtc,
        DateTimeOffset persistedAtUtc,
        string stage,
        string outcome,
        Guid messageId,
        string? positionId = null,
        string? reasonCode = null,
        string? messageType = null,
        AuditExportProvider? provider = null,
        AuditExportUsage? usage = null,
        AuditExportCost? cost = null,
        long? latencyMilliseconds = null,
        IReadOnlyDictionary<string, string>? attributes = null)
    {
        if (sequence <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sequence),
                sequence,
                "Audit sequence must be positive.");
        }

        if (latencyMilliseconds is < 0)
        {
            throw new ArgumentOutOfRangeException(nameof(latencyMilliseconds));
        }

        Sequence = sequence;
        EventId = AuditExportContractGuards.Identifier(eventId, nameof(eventId));
        OccurredAtUtc = AuditExportContractGuards.UtcTimestamp(
            occurredAtUtc,
            nameof(occurredAtUtc));
        PersistedAtUtc = AuditExportContractGuards.UtcTimestamp(
            persistedAtUtc,
            nameof(persistedAtUtc));
        Stage = AuditExportContractGuards.Text(stage, nameof(stage));
        Outcome = AuditExportContractGuards.Text(outcome, nameof(outcome));
        MessageId = AuditExportContractGuards.Identifier(messageId, nameof(messageId));
        PositionId = AuditExportContractGuards.OptionalText(
            positionId,
            nameof(positionId));
        ReasonCode = AuditExportContractGuards.OptionalText(
            reasonCode,
            nameof(reasonCode));
        MessageType = AuditExportContractGuards.OptionalText(
            messageType,
            nameof(messageType));
        Provider = provider;
        Usage = usage;
        Cost = cost;
        LatencyMilliseconds = latencyMilliseconds;
        Attributes = AuditExportContractGuards.Attributes(
            attributes,
            nameof(attributes));
    }

    [JsonPropertyName("sequence")]
    public long Sequence { get; }

    [JsonPropertyName("event_id")]
    public Guid EventId { get; }

    [JsonPropertyName("occurred_at_utc")]
    public DateTimeOffset OccurredAtUtc { get; }

    [JsonPropertyName("persisted_at_utc")]
    public DateTimeOffset PersistedAtUtc { get; }

    [JsonPropertyName("stage")]
    public string Stage { get; }

    [JsonPropertyName("outcome")]
    public string Outcome { get; }

    [JsonPropertyName("message_id")]
    public Guid MessageId { get; }

    [JsonPropertyName("position_id")]
    public string? PositionId { get; }

    [JsonPropertyName("reason_code")]
    public string? ReasonCode { get; }

    [JsonPropertyName("message_type")]
    public string? MessageType { get; }

    [JsonPropertyName("provider")]
    public AuditExportProvider? Provider { get; }

    [JsonPropertyName("usage")]
    public AuditExportUsage? Usage { get; }

    [JsonPropertyName("cost")]
    public AuditExportCost? Cost { get; }

    [JsonPropertyName("latency_ms")]
    public long? LatencyMilliseconds { get; }

    [JsonPropertyName("attributes")]
    public IReadOnlyDictionary<string, string> Attributes { get; }
}
