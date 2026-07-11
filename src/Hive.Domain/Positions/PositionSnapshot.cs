using System.Collections.Immutable;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Positions;

/// <summary>
/// The persisted point-in-time state of a <c>PositionActor</c> (US-F0-06-T03): the snapshot the
/// entity writes so recovery can skip replaying the whole journal. It fixes the durable shape only —
/// the pending <see cref="Inbox"/>, the <see cref="OpenTasks"/>, the <see cref="ShortMemory"/>, the
/// <see cref="RecentHistory"/>, the current occupant (<see cref="Occupant"/>/<see cref="OccupantType"/>)
/// the <see cref="ProcessedMessages"/> idempotency keys and the latest applied runtime
/// configuration stamp.
/// </summary>
/// <remarks>
/// <para>
/// This is a persisted DTO, not the live recoverable state: how events fold into state and how a
/// restore from this snapshot is reconciled with the events that follow it belong to the reducer
/// (US-F0-06-T06a); binding a versionable serializer to it belongs to US-F0-06-T05b. Collections are
/// never null — an absent collection is the empty collection — and the occupant pair is all-or-nothing
/// (a position with no occupant yet leaves both <see cref="Occupant"/> and <see cref="OccupantType"/>
/// null).
/// </para>
/// </remarks>
public sealed record PositionSnapshot
{
    public PositionSnapshot(
        DateTimeOffset takenAt,
        OccupantId? occupant = null,
        OccupantType? occupantType = null,
        IEnumerable<OrgMessage>? inbox = null,
        IEnumerable<PersistedTask>? openTasks = null,
        IReadOnlyDictionary<string, string>? shortMemory = null,
        IEnumerable<MessageId>? recentHistory = null,
        IEnumerable<MessageId>? processedMessages = null,
        PositionConfigurationStamp? lastConfigurationStamp = null,
        IEnumerable<PersistedRetainedAction>? retainedActions = null)
    {
        if (occupant is null != occupantType is null)
        {
            throw new ArgumentException(
                "Occupant and occupant type must both be set or both be null.",
                nameof(occupant));
        }

        if (occupantType is { } type && !Enum.IsDefined(type))
        {
            throw new ArgumentOutOfRangeException(
                nameof(occupantType),
                type,
                "Occupant type must be AiAgent or Human.");
        }

        TakenAt = takenAt;
        Occupant = occupant;
        OccupantType = occupantType;
        Inbox = ToValidatedArray(inbox, nameof(inbox));
        OpenTasks = ToValidatedArray(openTasks, nameof(openTasks));
        ShortMemory = ToValidatedMemory(shortMemory, nameof(shortMemory));
        RecentHistory = ToValidatedArray(recentHistory, nameof(recentHistory));
        ProcessedMessages = ToValidatedArray(processedMessages, nameof(processedMessages));
        LastConfigurationStamp = lastConfigurationStamp;
        RetainedActions = ToValidatedArray(retainedActions, nameof(retainedActions));
        if (RetainedActions.Select(action => action.Id).Distinct().Count() != RetainedActions.Length)
        {
            throw new ArgumentException("Retained action ids must be unique.", nameof(retainedActions));
        }

        if (RetainedActions.Select(action => action.CorrelationId).Distinct(StringComparer.Ordinal).Count()
            != RetainedActions.Length)
        {
            throw new ArgumentException("Retained action correlations must be unique.", nameof(retainedActions));
        }
    }

    /// <summary>When the snapshot was taken.</summary>
    public DateTimeOffset TakenAt { get; }

    /// <summary>The current occupant, or null when the position has none yet.</summary>
    public OccupantId? Occupant { get; }

    /// <summary>The current occupant's kind, or null when the position has no occupant yet.</summary>
    public OccupantType? OccupantType { get; }

    /// <summary>The messages still pending in the inbox.</summary>
    public ImmutableArray<OrgMessage> Inbox { get; }

    /// <summary>The tasks in progress.</summary>
    public ImmutableArray<PersistedTask> OpenTasks { get; }

    /// <summary>The short-term memory entries by key.</summary>
    public ImmutableDictionary<string, string> ShortMemory { get; }

    /// <summary>The recently handled message ids kept as history.</summary>
    public ImmutableArray<MessageId> RecentHistory { get; }

    /// <summary>The ids of messages already processed, used for idempotent acceptance.</summary>
    public ImmutableArray<MessageId> ProcessedMessages { get; }

    /// <summary>The latest runtime configuration stamp accepted by the position entity.</summary>
    public PositionConfigurationStamp? LastConfigurationStamp { get; }

    /// <summary>Actions durably stopped by the authority gate.</summary>
    public ImmutableArray<PersistedRetainedAction> RetainedActions { get; }

    private static ImmutableArray<T> ToValidatedArray<T>(IEnumerable<T>? source, string parameterName)
        where T : class
    {
        if (source is null)
        {
            return ImmutableArray<T>.Empty;
        }

        var builder = ImmutableArray.CreateBuilder<T>();
        foreach (var item in source)
        {
            if (item is null)
            {
                throw new ArgumentException("Collection cannot contain null items.", parameterName);
            }

            builder.Add(item);
        }

        return builder.ToImmutable();
    }

    private static ImmutableDictionary<string, string> ToValidatedMemory(
        IReadOnlyDictionary<string, string>? source,
        string parameterName)
    {
        if (source is null)
        {
            return ImmutableDictionary<string, string>.Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in source)
        {
            CommandText.RequireContent(key, parameterName);
            if (value is null)
            {
                throw new ArgumentException("Short-memory values cannot be null.", parameterName);
            }

            builder[key] = value;
        }

        return builder.ToImmutable();
    }
}
