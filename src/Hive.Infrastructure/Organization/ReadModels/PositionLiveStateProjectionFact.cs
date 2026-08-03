using System.Text.Json;
using Hive.Domain.Identity;

namespace Hive.Infrastructure.Organization.ReadModels;

public enum PositionLiveStateProjectionSource
{
    PositionEvent,
    OrganizationalMessage,
    AuditLog,
}

public enum PositionLiveStateProjectionSubscription
{
    PositionJournal,
    AuditLog,
}

public sealed record PositionLiveStateProjectionFact
{
    public PositionLiveStateProjectionFact(
        PositionLiveStateProjectionSource source,
        long sourceOffset,
        OrganizationId organizationId,
        string factType,
        DateTimeOffset occurredAtUtc,
        string payloadJson,
        PositionId? positionId = null,
        string? persistenceId = null,
        long? persistenceSequence = null,
        MessageId? messageId = null,
        ThreadId? threadId = null)
    {
        if (!Enum.IsDefined(source))
        {
            throw new ArgumentOutOfRangeException(nameof(source), source, "Unknown projection source.");
        }

        if (sourceOffset <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(sourceOffset),
                sourceOffset,
                "Projection source offset must be positive.");
        }

        ArgumentNullException.ThrowIfNull(organizationId);
        if (string.IsNullOrWhiteSpace(factType))
        {
            throw new ArgumentException("Projection fact type cannot be empty or whitespace.", nameof(factType));
        }

        if (occurredAtUtc == default || occurredAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                "Projection fact timestamp must be specified with the UTC offset.",
                nameof(occurredAtUtc));
        }

        if (string.IsNullOrWhiteSpace(payloadJson))
        {
            throw new ArgumentException("Projection fact payload cannot be empty or whitespace.", nameof(payloadJson));
        }

        using (var payload = JsonDocument.Parse(payloadJson))
        {
            if (payload.RootElement.ValueKind != JsonValueKind.Object)
            {
                throw new ArgumentException("Projection fact payload must be a JSON object.", nameof(payloadJson));
            }
        }

        if (source is PositionLiveStateProjectionSource.PositionEvent
            or PositionLiveStateProjectionSource.OrganizationalMessage)
        {
            ArgumentNullException.ThrowIfNull(positionId);
            if (string.IsNullOrWhiteSpace(persistenceId))
            {
                throw new ArgumentException(
                    "Journal projection facts require a persistence identifier.",
                    nameof(persistenceId));
            }

            if (persistenceSequence is null or <= 0)
            {
                throw new ArgumentOutOfRangeException(
                    nameof(persistenceSequence),
                    persistenceSequence,
                    "Journal projection facts require a positive persistence sequence.");
            }
        }

        if (source == PositionLiveStateProjectionSource.OrganizationalMessage)
        {
            ArgumentNullException.ThrowIfNull(messageId);
            ArgumentNullException.ThrowIfNull(threadId);
        }

        Source = source;
        SourceOffset = sourceOffset;
        OrganizationId = organizationId;
        PositionId = positionId;
        PersistenceId = persistenceId;
        PersistenceSequence = persistenceSequence;
        FactType = factType;
        MessageId = messageId;
        ThreadId = threadId;
        OccurredAtUtc = occurredAtUtc;
        PayloadJson = payloadJson;
    }

    public PositionLiveStateProjectionSource Source { get; }

    public long SourceOffset { get; }

    public OrganizationId OrganizationId { get; }

    public PositionId? PositionId { get; }

    public string? PersistenceId { get; }

    public long? PersistenceSequence { get; }

    public string FactType { get; }

    public MessageId? MessageId { get; }

    public ThreadId? ThreadId { get; }

    public DateTimeOffset OccurredAtUtc { get; }

    public string PayloadJson { get; }
}
