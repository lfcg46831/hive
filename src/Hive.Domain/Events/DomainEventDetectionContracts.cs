using System.Collections.Immutable;
using Hive.Domain.Identity;

namespace Hive.Domain.Events;

public sealed record DomainEventDetectorId
{
    public DomainEventDetectorId(OrganizationId organizationId, OrganizationEventType eventType)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        OrganizationEventTypeContract.RequireDefined(eventType, nameof(eventType));
        OrganizationId = organizationId;
        EventType = eventType;
    }

    public OrganizationId OrganizationId { get; }
    public OrganizationEventType EventType { get; }
}

/// <summary>Durable progress. Cursor is an adapter-versioned safe prefix, never a business timestamp.</summary>
public sealed record DomainEventDetectionCheckpoint
{
    public DomainEventDetectionCheckpoint(DomainEventDetectorId detectorId, long revision,
        string? cursor, DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(detectorId);
        ArgumentOutOfRangeException.ThrowIfLessThanOrEqual(revision, 0);
        if (cursor is not null) ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        DetectorId = detectorId;
        Revision = revision;
        Cursor = cursor;
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
    }

    public DomainEventDetectorId DetectorId { get; }
    public long Revision { get; }
    public string? Cursor { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
}

public sealed class DomainEventDetectionContext
{
    public DomainEventDetectionContext(DomainEventDetectorId detectorId,
        EventSubscriptionsSnapshot subscriptions, DomainEventDetectionCheckpoint? checkpoint,
        DateTimeOffset evaluatedAtUtc)
    {
        ArgumentNullException.ThrowIfNull(detectorId);
        ArgumentNullException.ThrowIfNull(subscriptions);
        if (subscriptions.OrganizationId != detectorId.OrganizationId
            || (checkpoint is not null && checkpoint.DetectorId != detectorId))
            throw new ArgumentException("Detection inputs must belong to the same detector partition.");
        if (checkpoint is not null && evaluatedAtUtc < checkpoint.EvaluatedAtUtc)
            throw new ArgumentException("Evaluation cannot move backwards.", nameof(evaluatedAtUtc));
        DetectorId = detectorId;
        Subscribers = subscriptions.Resolve(detectorId.EventType);
        Checkpoint = checkpoint;
        EvaluatedAtUtc = evaluatedAtUtc.ToUniversalTime();
    }

    public DomainEventDetectorId DetectorId { get; }
    public ImmutableArray<EventSubscriber> Subscribers { get; }
    public DomainEventDetectionCheckpoint? Checkpoint { get; }
    public DateTimeOffset EvaluatedAtUtc { get; }
}

/// <summary>Complete evaluation at one instant; includes current candidates even without new input facts.</summary>
public sealed class DomainEventDetectionResult
{
    public DomainEventDetectionResult(string? cursor, IEnumerable<DomainEventOccurrence> occurrences)
    {
        if (cursor is not null) ArgumentException.ThrowIfNullOrWhiteSpace(cursor);
        ArgumentNullException.ThrowIfNull(occurrences);
        Cursor = cursor;
        Occurrences = occurrences.ToImmutableArray();
        foreach (var occurrence in Occurrences) ArgumentNullException.ThrowIfNull(occurrence);
        if (Occurrences.Select(item => item.Key).Distinct().Count() != Occurrences.Length)
            throw new ArgumentException("Duplicate occurrence keys in one evaluation.", nameof(occurrences));
    }

    public string? Cursor { get; }
    public ImmutableArray<DomainEventOccurrence> Occurrences { get; }
}

/// <summary>Validated atomic write request. Revision zero denotes confirmed checkpoint absence.</summary>
public sealed class DomainEventDetectionCommit
{
    public DomainEventDetectionCommit(DomainEventDetectionContext context, DomainEventDetectionResult result)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(result);
        foreach (var occurrence in result.Occurrences)
        {
            if (occurrence.OrganizationId != context.DetectorId.OrganizationId
                || occurrence.Payload.EventType != context.DetectorId.EventType
                || occurrence.Payload.ObservedAtUtc != context.EvaluatedAtUtc
                || !context.Subscribers.Contains(occurrence.Subscriber))
                throw new ArgumentException("Occurrence does not match the evaluation context.", nameof(result));
        }
        ExpectedRevision = context.Checkpoint?.Revision ?? 0;
        Checkpoint = new(context.DetectorId, checked(ExpectedRevision + 1), result.Cursor, context.EvaluatedAtUtc);
        Occurrences = result.Occurrences.OrderBy(item => item.Key.Value, StringComparer.Ordinal).ToImmutableArray();
    }

    public long ExpectedRevision { get; }
    public DomainEventDetectionCheckpoint Checkpoint { get; }
    public ImmutableArray<DomainEventOccurrence> Occurrences { get; }
}

/// <summary>
/// Reads persisted facts only: inbox envelopes/correlations for deadlines, operational projection
/// history for blocked periods, and attempt-scoped GatewayCostRecorded audit facts for budgets.
/// Implementations own typed source adapters and safe cursors. They must revisit current persisted
/// candidates on every call, reconstruct aggregates after restart, and exclude obsolete occurrences.
/// No actor queries or delivery side effects. Technical failures and cancellation propagate.
/// </summary>
public interface IDomainEventDetector
{
    OrganizationEventType EventType { get; }
    ValueTask<DomainEventDetectionResult> EvaluateAsync(DomainEventDetectionContext context,
        CancellationToken cancellationToken = default);
}

/// <summary>Durable checkpoint and pending-occurrence boundary; no in-memory production fallback.</summary>
public interface IDomainEventDetectionStore
{
    /// <summary>Null means confirmed absence. Failure or cancellation must never become absence.</summary>
    ValueTask<DomainEventDetectionCheckpoint?> ReadCheckpointAsync(DomainEventDetectorId detectorId,
        CancellationToken cancellationToken = default);

    /// <summary>
    /// Atomically compares ExpectedRevision and saves the next checkpoint with pending occurrences.
    /// Deduplicates by Key, preserving the first payload and recoverable pending work. False means
    /// a revision conflict with no writes. Failure/cancellation cannot produce partial progress;
    /// an uncertain commit outcome is recovered by rereading. Empty batches still advance progress.
    /// PostgreSQL implementation and pending delivery recovery belong to T10.
    /// </summary>
    ValueTask<bool> TryCommitAsync(DomainEventDetectionCommit commit,
        CancellationToken cancellationToken = default);
}
