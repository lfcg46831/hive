using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies the recoverable state and event reducer contract of the PositionActor (US-F0-06-T06a).
/// </summary>
public sealed class PositionStateTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddMinutes(20);

    [Fact]
    public void Empty_state_starts_without_recovered_data()
    {
        var state = PositionState.Empty;

        Assert.Empty(state.Inbox);
        Assert.Empty(state.OpenTasks);
        Assert.Empty(state.ShortMemory);
        Assert.Empty(state.RecentHistory);
        Assert.Empty(state.ProcessedMessages);
        Assert.Null(state.Occupant);
        Assert.Null(state.OccupantType);
    }

    [Fact]
    public void Applying_events_reconstructs_inbox_tasks_memory_occupant_history_and_processed_messages()
    {
        var message = SampleMessage();
        var taskId = PositionTaskId.New();
        var occupant = OccupantId.From("agent-7");
        var deadline = At.AddHours(4);
        var revisedDeadline = At.AddHours(8);

        var state = PositionState.Empty
            .Apply(new MessageReceived(message, At))
            .Apply(new TaskCreated(taskId, message.Thread, "triage incoming bug", Priority.High, At.AddMinutes(1), deadline, message.Id))
            .Apply(new TaskUpdated(taskId, "investigating", At.AddMinutes(2), Priority.Critical, revisedDeadline))
            .Apply(new ShortMemoryUpdated("thread", "customer is blocked", At.AddMinutes(3)))
            .Apply(new OccupantChanged(occupant, OccupantType.AiAgent, At.AddMinutes(4)))
            .Apply(new MessageDispatched(message.Id, message.Thread, occupant, OccupantType.AiAgent, At.AddMinutes(5)));

        Assert.Empty(state.Inbox);
        Assert.Contains(message.Id, state.ProcessedMessages);
        Assert.Equal(new[] { message.Id }, state.RecentHistory);
        Assert.Equal("customer is blocked", state.ShortMemory["thread"]);
        Assert.Equal(occupant, state.Occupant);
        Assert.Equal(OccupantType.AiAgent, state.OccupantType);

        var task = Assert.Single(state.OpenTasks).Value;
        Assert.Equal(taskId, task.TaskId);
        Assert.Equal(message.Thread, task.Thread);
        Assert.Equal("triage incoming bug", task.Title);
        Assert.Equal(Priority.Critical, task.Priority);
        Assert.Equal(revisedDeadline, task.Deadline);
        Assert.Equal(message.Id, task.CausedBy);
    }

    [Fact]
    public void Completing_a_task_removes_it_from_open_tasks()
    {
        var taskId = PositionTaskId.New();

        var state = PositionState.Empty
            .Apply(new TaskCreated(taskId, ThreadId.New(), "triage", Priority.Normal, At))
            .Apply(new TaskCompleted(taskId, At.AddMinutes(1), "done"));

        Assert.Empty(state.OpenTasks);
    }

    [Fact]
    public void Snapshot_restore_and_snapshot_export_are_deterministic()
    {
        var message = SampleMessage();
        var task = new PersistedTask(PositionTaskId.New(), message.Thread, "triage", Priority.High, At, causedBy: message.Id);
        var occupant = OccupantId.From("alice");
        var history = new[] { MessageId.New() };
        var processed = new[] { message.Id };
        var snapshot = new PositionSnapshot(
            At,
            occupant,
            OccupantType.Human,
            new[] { message },
            new[] { task },
            new Dictionary<string, string> { ["thread"] = "context" },
            history,
            processed);

        var state = PositionState.Restore(snapshot);
        var exported = state.ToSnapshot(At.AddMinutes(10));

        Assert.Equal(At.AddMinutes(10), exported.TakenAt);
        Assert.Equal(occupant, exported.Occupant);
        Assert.Equal(OccupantType.Human, exported.OccupantType);
        Assert.Equal(new[] { message }, exported.Inbox);
        Assert.Equal(new[] { task }, exported.OpenTasks);
        Assert.Equal("context", exported.ShortMemory["thread"]);
        Assert.Equal(history, exported.RecentHistory);
        Assert.Equal(processed, exported.ProcessedMessages);
    }

    [Fact]
    public void Updating_or_completing_unknown_task_does_not_reject_replay()
    {
        var taskId = PositionTaskId.New();

        var state = PositionState.Empty
            .Apply(new TaskUpdated(taskId, "progress", At))
            .Apply(new TaskCompleted(taskId, At.AddMinutes(1)));

        Assert.Empty(state.OpenTasks);
    }

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
}
