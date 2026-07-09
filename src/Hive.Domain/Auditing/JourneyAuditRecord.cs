using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Domain.Auditing;

public sealed record JourneyAuditRecord
{
    public JourneyAuditRecord(
        Guid auditEventId,
        DateTimeOffset occurredAtUtc,
        JourneyAuditStage stage,
        JourneyAuditOutcome outcome,
        OrganizationId organizationId,
        ThreadId threadId,
        MessageId messageId,
        DirectiveId? directiveId = null,
        PositionId? positionId = null,
        string? reasonCode = null,
        string? messageType = null,
        AiProviderMetadata? provider = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        TimeSpan? latency = null,
        IReadOnlyDictionary<string, string>? payload = null,
        DateTimeOffset? persistedAtUtc = null)
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

        if (persistedAtUtc is { } persisted && persisted == default)
        {
            throw new ArgumentException(
                "Audit event persistence timestamp must be specified.",
                nameof(persistedAtUtc));
        }

        if (latency is { } latencyValue && latencyValue < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(latency),
                latency,
                "Audit event latency cannot be negative.");
        }

        AuditEventId = auditEventId;
        OccurredAtUtc = occurredAtUtc;
        PersistedAtUtc = persistedAtUtc ?? DateTimeOffset.UtcNow;
        Stage = RequireDefined(stage, nameof(stage));
        Outcome = RequireDefined(outcome, nameof(outcome));
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        MessageId = messageId ?? throw new ArgumentNullException(nameof(messageId));
        DirectiveId = directiveId;
        PositionId = positionId;
        ReasonCode = OptionalText(reasonCode, nameof(reasonCode));
        MessageType = OptionalText(messageType, nameof(messageType));
        Provider = provider;
        Usage = usage;
        Cost = cost;
        Latency = latency;
        Payload = SnapshotPayload(payload);
    }

    public Guid AuditEventId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public DateTimeOffset PersistedAtUtc { get; }

    public JourneyAuditStage Stage { get; }

    public JourneyAuditOutcome Outcome { get; }

    public OrganizationId OrganizationId { get; }

    public ThreadId ThreadId { get; }

    public DirectiveId? DirectiveId { get; }

    public MessageId MessageId { get; }

    public PositionId? PositionId { get; }

    public string? ReasonCode { get; }

    public string? MessageType { get; }

    public AiProviderMetadata? Provider { get; }

    public AiTokenUsage? Usage { get; }

    public AiCostMetadata? Cost { get; }

    public TimeSpan? Latency { get; }

    public IReadOnlyDictionary<string, string> Payload { get; }

    public static JourneyAuditRecord Create(
        JourneyAuditStage stage,
        JourneyAuditOutcome outcome,
        OrganizationId organizationId,
        ThreadId threadId,
        MessageId messageId,
        DirectiveId? directiveId = null,
        PositionId? positionId = null,
        string? reasonCode = null,
        string? messageType = null,
        AiProviderMetadata? provider = null,
        AiTokenUsage? usage = null,
        AiCostMetadata? cost = null,
        TimeSpan? latency = null,
        IReadOnlyDictionary<string, string>? payload = null,
        DateTimeOffset? occurredAtUtc = null,
        string? idempotencyDiscriminator = null)
    {
        var key = JourneyAuditIdempotencyKey.From(
            stage,
            outcome,
            organizationId,
            threadId,
            messageId,
            directiveId,
            positionId,
            idempotencyDiscriminator ?? messageType);

        return new JourneyAuditRecord(
            key.AuditEventId,
            occurredAtUtc ?? DateTimeOffset.UtcNow,
            stage,
            outcome,
            organizationId,
            threadId,
            messageId,
            directiveId,
            positionId,
            reasonCode,
            messageType,
            provider,
            usage,
            cost,
            latency,
            payload);
    }

    private static TEnum RequireDefined<TEnum>(TEnum value, string parameterName)
        where TEnum : struct, Enum
    {
        if (!Enum.IsDefined(value))
        {
            throw new ArgumentOutOfRangeException(parameterName, value, $"Unknown {typeof(TEnum).Name}.");
        }

        return value;
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
