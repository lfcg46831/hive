using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies the internal command contract of the <c>PositionActor</c> (US-F0-06-T02): construction,
/// validation and value equality of the closed set of intent records. These are pure domain
/// contracts — they model the shape of the inputs, not their effect on entity state.
/// </summary>
public sealed class PositionCommandTests
{
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

    [Fact]
    public void Every_command_is_a_position_command()
    {
        Assert.IsAssignableFrom<PositionCommand>(new AcceptMessage(SampleMessage()));
        Assert.IsAssignableFrom<PositionCommand>(
            new OpenTask(PositionTaskId.New(), ThreadId.New(), "triage", Priority.Normal));
        Assert.IsAssignableFrom<PositionCommand>(new UpdateTask(PositionTaskId.New(), "progress"));
        Assert.IsAssignableFrom<PositionCommand>(new CompleteTask(PositionTaskId.New()));
        Assert.IsAssignableFrom<PositionCommand>(new UpdateShortMemory("thread", "context"));
        Assert.IsAssignableFrom<PositionCommand>(
            new ChangeOccupant(OccupantId.From("alice"), OccupantType.Human));
        Assert.IsAssignableFrom<PositionCommand>(new RequestPassivation());
    }

    [Fact]
    public void AcceptMessage_carries_the_envelope()
    {
        var message = SampleMessage();

        var command = new AcceptMessage(message);

        Assert.Same(message, command.Message);
    }

    [Fact]
    public void AcceptMessage_rejects_null_message()
    {
        Assert.Throws<ArgumentNullException>(() => new AcceptMessage(null!));
    }

    [Fact]
    public void OpenTask_captures_all_fields()
    {
        var taskId = PositionTaskId.New();
        var thread = ThreadId.New();
        var causedBy = MessageId.New();
        var deadline = DateTimeOffset.UnixEpoch.AddHours(2);

        var command = new OpenTask(taskId, thread, "triage incoming bug", Priority.High, deadline, causedBy);

        Assert.Equal(taskId, command.TaskId);
        Assert.Equal(thread, command.Thread);
        Assert.Equal("triage incoming bug", command.Title);
        Assert.Equal(Priority.High, command.Priority);
        Assert.Equal(deadline, command.Deadline);
        Assert.Equal(causedBy, command.CausedBy);
    }

    [Fact]
    public void OpenTask_defaults_optional_fields_to_null()
    {
        var command = new OpenTask(PositionTaskId.New(), ThreadId.New(), "triage", Priority.Normal);

        Assert.Null(command.Deadline);
        Assert.Null(command.CausedBy);
    }

    [Fact]
    public void OpenTask_rejects_null_required_references()
    {
        Assert.Throws<ArgumentNullException>(
            () => new OpenTask(null!, ThreadId.New(), "t", Priority.Normal));
        Assert.Throws<ArgumentNullException>(
            () => new OpenTask(PositionTaskId.New(), null!, "t", Priority.Normal));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void OpenTask_rejects_blank_or_padded_title(string? title)
    {
        Assert.ThrowsAny<ArgumentException>(
            () => new OpenTask(PositionTaskId.New(), ThreadId.New(), title!, Priority.Normal));
    }

    [Fact]
    public void OpenTask_rejects_undefined_priority()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OpenTask(PositionTaskId.New(), ThreadId.New(), "t", (Priority)99));
    }

    [Fact]
    public void UpdateTask_keeps_optional_revisions_null_by_default()
    {
        var command = new UpdateTask(PositionTaskId.New(), "made progress");

        Assert.Equal("made progress", command.Note);
        Assert.Null(command.Priority);
        Assert.Null(command.Deadline);
    }

    [Fact]
    public void UpdateTask_carries_revisions_when_present()
    {
        var deadline = DateTimeOffset.UnixEpoch.AddDays(1);

        var command = new UpdateTask(PositionTaskId.New(), "bumped", Priority.Critical, deadline);

        Assert.Equal(Priority.Critical, command.Priority);
        Assert.Equal(deadline, command.Deadline);
    }

    [Fact]
    public void UpdateTask_rejects_blank_note_and_undefined_priority()
    {
        Assert.ThrowsAny<ArgumentException>(() => new UpdateTask(PositionTaskId.New(), "  "));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new UpdateTask(PositionTaskId.New(), "n", (Priority)0));
    }

    [Fact]
    public void CompleteTask_summary_is_optional()
    {
        var taskId = PositionTaskId.New();

        Assert.Null(new CompleteTask(taskId).Summary);
        Assert.Equal("done", new CompleteTask(taskId, "done").Summary);
    }

    [Fact]
    public void CompleteTask_rejects_blank_summary_when_provided()
    {
        Assert.ThrowsAny<ArgumentException>(() => new CompleteTask(PositionTaskId.New(), "   "));
    }

    [Fact]
    public void CompleteTask_rejects_null_task()
    {
        Assert.Throws<ArgumentNullException>(() => new CompleteTask(null!));
    }

    [Fact]
    public void UpdateShortMemory_allows_empty_value_but_not_empty_key()
    {
        var command = new UpdateShortMemory("current-thread", "");

        Assert.Equal("current-thread", command.Key);
        Assert.Equal("", command.Value);
        Assert.ThrowsAny<ArgumentException>(() => new UpdateShortMemory("  ", "v"));
        Assert.Throws<ArgumentNullException>(() => new UpdateShortMemory("k", null!));
    }

    [Fact]
    public void ChangeOccupant_captures_occupant_and_type()
    {
        var command = new ChangeOccupant(OccupantId.From("agent-7"), OccupantType.AiAgent);

        Assert.Equal(OccupantId.From("agent-7"), command.Occupant);
        Assert.Equal(OccupantType.AiAgent, command.Type);
    }

    [Fact]
    public void ChangeOccupant_rejects_null_occupant_and_undefined_type()
    {
        Assert.Throws<ArgumentNullException>(() => new ChangeOccupant(null!, OccupantType.Human));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new ChangeOccupant(OccupantId.From("a"), (OccupantType)42));
    }

    [Fact]
    public void RequestPassivation_reason_is_optional()
    {
        Assert.Null(new RequestPassivation().Reason);
        Assert.Equal("idle", new RequestPassivation("idle").Reason);
        Assert.ThrowsAny<ArgumentException>(() => new RequestPassivation("  "));
    }

    [Fact]
    public void Commands_have_value_equality()
    {
        var taskId = PositionTaskId.New();
        var thread = ThreadId.New();

        var a = new OpenTask(taskId, thread, "t", Priority.Normal);
        var b = new OpenTask(taskId, thread, "t", Priority.Normal);
        var different = new OpenTask(taskId, thread, "t", Priority.High);

        Assert.Equal(a, b);
        Assert.Equal(a.GetHashCode(), b.GetHashCode());
        Assert.NotEqual(a, different);
    }
}
