using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests.Serialization;

/// <summary>
/// Deterministic persisted PositionActor protocol values for US-F0-06-T13b. The set covers the base
/// PositionEvent journal facts and one persisted snapshot; runtime-configuration stamp fixtures are
/// intentionally left to US-F0-06-T08c.
/// </summary>
internal static class CanonicalPositionProtocolFixtures
{
    public static readonly DateTimeOffset OccurredAt = new(2026, 6, 26, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset SnapshotTakenAt = new(2026, 6, 26, 10, 5, 0, TimeSpan.Zero);

    public static IReadOnlyList<(string Manifest, object Value)> All { get; } =
    [
        ("message-received", new MessageReceived(Message(), OccurredAt)),
        ("task-created", new TaskCreated(TaskId(), ThreadId(), "triage incoming regression", Priority.High, OccurredAt, OccurredAt.AddHours(3), MessageId())),
        ("task-updated", new TaskUpdated(TaskId(), "reproduced on staging", OccurredAt.AddMinutes(5), Priority.Critical, OccurredAt.AddHours(1))),
        ("task-completed", new TaskCompleted(TaskId(), OccurredAt.AddMinutes(15), "hotfix shipped")),
        ("short-memory-updated", new ShortMemoryUpdated("current-thread", "customer-impact", OccurredAt.AddMinutes(20))),
        ("occupant-changed", new OccupantChanged(OccupantId.From("agent-7"), OccupantType.AiAgent, OccurredAt.AddMinutes(25))),
        ("message-dispatched", new MessageDispatched(MessageId(), ThreadId(), OccupantId.From("agent-7"), OccupantType.AiAgent, OccurredAt.AddMinutes(30))),
        ("position-passivated", new PositionPassivated(OccurredAt.AddMinutes(45), "idle")),
        ("position-snapshot", Snapshot()),
    ];

    public static IReadOnlyList<(string Manifest, PositionEvent Event)> BaseEvents { get; } =
        All.Where(entry => entry.Value is PositionEvent)
            .Select(entry => (entry.Manifest, Event: (PositionEvent)entry.Value))
            .ToArray();

    private static PositionSnapshot Snapshot() =>
        new(
            SnapshotTakenAt,
            OccupantId.From("agent-7"),
            OccupantType.AiAgent,
            new[] { Message() },
            new[]
            {
                new PersistedTask(TaskId(), ThreadId(), "triage incoming regression", Priority.Critical, OccurredAt, OccurredAt.AddHours(1), MessageId()),
            },
            new Dictionary<string, string> { ["current-thread"] = "customer-impact" },
            new[] { MessageId() },
            new[] { MessageId() },
            lastConfigurationStamp: null);

    private static OrgMessage Message() =>
        CanonicalMessageFixtures.All.Single(entry => entry.Manifest == "memo").Message;

    private static MessageId MessageId() =>
        Hive.Domain.Identity.MessageId.From(new Guid("d3000000-0000-0000-0000-000000000001"));

    private static ThreadId ThreadId() =>
        Hive.Domain.Identity.ThreadId.From(new Guid("d3000000-0000-0000-0000-0000000000a1"));

    private static PositionTaskId TaskId() =>
        PositionTaskId.From(new Guid("c6000000-0000-0000-0000-000000000001"));
}
