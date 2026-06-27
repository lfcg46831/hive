namespace Hive.Domain.Positions;

/// <summary>
/// The closed set of facts a <c>PositionActor</c> persists to its journal (US-F0-06-T03): the
/// durable record of what a successful command did — a message admitted into the inbox
/// (<see cref="MessageReceived"/>), a task created/updated/completed (<see cref="TaskCreated"/>,
/// <see cref="TaskUpdated"/>, <see cref="TaskCompleted"/>), short-term memory updated
/// (<see cref="ShortMemoryUpdated"/>), the occupant changed (<see cref="OccupantChanged"/>), an
/// accepted message dispatched to the occupant (<see cref="MessageDispatched"/>) and the position
/// passivated (<see cref="PositionPassivated"/>). The runtime-configuration gate extends this set
/// with <see cref="PositionConfigurationApplied"/> (US-F0-06-T08c).
/// </summary>
/// <remarks>
/// <para>
/// Events are <em>facts</em> in the past tense, distinct from the <see cref="PositionCommand"/>
/// intents of US-F0-06-T02: a command may be rejected, but an event is only ever produced by a
/// command that already succeeded, so events carry no validation verdict and are never rejected on
/// replay. They are pure domain contracts — they fix the persisted shape only. Folding events into
/// the recoverable state (inbox, open tasks, short memory, history, occupant, processed ids and the
/// latest applied configuration stamp) is the reducer's job (US-F0-06-T06a/US-F0-06-T08c); binding
/// versionable serializers to events and snapshots belongs to US-F0-06-T05b/US-F0-06-T08c.
/// </para>
/// <para>
/// Like the audit projection of US-F0-04-T10, every event carries the <see cref="OccurredAt"/>
/// instant supplied by the entity when it persisted the event, giving the journal a domain timestamp
/// independent of any storage-level sequence.
/// </para>
/// </remarks>
public abstract record PositionEvent
{
    private protected PositionEvent(DateTimeOffset occurredAt) => OccurredAt = occurredAt;

    /// <summary>When the fact occurred, as supplied by the persisting entity.</summary>
    public DateTimeOffset OccurredAt { get; }
}
