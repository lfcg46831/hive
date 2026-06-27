using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies the persisted snapshot contract of the <c>PositionActor</c> (US-F0-06-T03): the durable
/// shape of the point-in-time state, its collection normalization and its consistency guards. The
/// reducer that produces or restores from a snapshot belongs to US-F0-06-T06a.
/// </summary>
public sealed class PositionSnapshotTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddMinutes(10);

    private static Memo SampleMessage() => new(
        MessageId.New(),
        OrganizationId.From("acme"),
        new PositionEndpointRef(PositionId.From("eng-lead")),
        new PositionEndpointRef(PositionId.From("bug-triage")),
        ThreadId.New(),
        Priority.Normal,
        schemaVersion: 1,
        sentAt: DateTimeOffset.UnixEpoch,
        deadline: null,
        body: "ping");

    private static PersistedTask SampleTask() =>
        new(PositionTaskId.New(), ThreadId.New(), "triage", Priority.High, At);

    [Fact]
    public void Empty_snapshot_normalizes_collections_to_empty()
    {
        var snapshot = new PositionSnapshot(At);

        Assert.Equal(At, snapshot.TakenAt);
        Assert.Null(snapshot.Occupant);
        Assert.Null(snapshot.OccupantType);
        Assert.Empty(snapshot.Inbox);
        Assert.Empty(snapshot.OpenTasks);
        Assert.Empty(snapshot.ShortMemory);
        Assert.Empty(snapshot.RecentHistory);
        Assert.Empty(snapshot.ProcessedMessages);
        Assert.Null(snapshot.LastConfigurationStamp);
    }

    [Fact]
    public void Snapshot_captures_all_state()
    {
        var occupant = OccupantId.From("agent-7");
        var inbox = new[] { SampleMessage() };
        var tasks = new[] { SampleTask() };
        var memory = new Dictionary<string, string> { ["thread"] = "context", ["scratch"] = string.Empty };
        var history = new[] { MessageId.New() };
        var processed = new[] { MessageId.New(), MessageId.New() };
        var stamp = new PositionConfigurationStamp(9, "sha256:v9");

        var snapshot = new PositionSnapshot(
            At, occupant, OccupantType.AiAgent, inbox, tasks, memory, history, processed, stamp);

        Assert.Equal(occupant, snapshot.Occupant);
        Assert.Equal(OccupantType.AiAgent, snapshot.OccupantType);
        Assert.Single(snapshot.Inbox);
        Assert.Single(snapshot.OpenTasks);
        Assert.Equal("context", snapshot.ShortMemory["thread"]);
        Assert.Equal(string.Empty, snapshot.ShortMemory["scratch"]);
        Assert.Single(snapshot.RecentHistory);
        Assert.Equal(2, snapshot.ProcessedMessages.Length);
        Assert.Equal(stamp, snapshot.LastConfigurationStamp);
    }

    [Fact]
    public void Snapshot_rejects_occupant_without_type()
    {
        Assert.Throws<ArgumentException>(
            () => new PositionSnapshot(At, OccupantId.From("alice")));
    }

    [Fact]
    public void Snapshot_rejects_type_without_occupant()
    {
        Assert.Throws<ArgumentException>(
            () => new PositionSnapshot(At, occupantType: OccupantType.Human));
    }

    [Fact]
    public void Snapshot_rejects_undefined_occupant_type()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PositionSnapshot(At, OccupantId.From("alice"), (OccupantType)55));
    }

    [Fact]
    public void Snapshot_rejects_null_items_in_collections()
    {
        Assert.Throws<ArgumentException>(
            () => new PositionSnapshot(At, inbox: new OrgMessage[] { null! }));
        Assert.Throws<ArgumentException>(
            () => new PositionSnapshot(At, openTasks: new PersistedTask[] { null! }));
        Assert.Throws<ArgumentException>(
            () => new PositionSnapshot(At, processedMessages: new MessageId[] { null! }));
    }

    [Fact]
    public void Snapshot_rejects_blank_memory_key()
    {
        var memory = new Dictionary<string, string> { ["  "] = "v" };

        Assert.Throws<ArgumentException>(() => new PositionSnapshot(At, shortMemory: memory));
    }

    [Fact]
    public void PersistedTask_captures_durable_attributes()
    {
        var taskId = PositionTaskId.New();
        var thread = ThreadId.New();
        var causedBy = MessageId.New();
        var deadline = At.AddHours(3);

        var task = new PersistedTask(taskId, thread, "triage bug", Priority.Critical, At, deadline, causedBy);

        Assert.Equal(taskId, task.TaskId);
        Assert.Equal(thread, task.Thread);
        Assert.Equal("triage bug", task.Title);
        Assert.Equal(Priority.Critical, task.Priority);
        Assert.Equal(At, task.OpenedAt);
        Assert.Equal(deadline, task.Deadline);
        Assert.Equal(causedBy, task.CausedBy);
    }

    [Fact]
    public void PersistedTask_rejects_blank_title_and_undefined_priority()
    {
        Assert.Throws<ArgumentException>(
            () => new PersistedTask(PositionTaskId.New(), ThreadId.New(), " ", Priority.Normal, At));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new PersistedTask(PositionTaskId.New(), ThreadId.New(), "t", (Priority)0, At));
    }
}
