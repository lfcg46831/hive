using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies the persisted event contract of the <c>PositionActor</c> (US-F0-06-T03): construction,
/// validation and value equality of the closed set of fact records. These are pure domain
/// contracts — they model the persisted shape, not how the reducer folds them into state.
/// </summary>
public sealed class PositionEventTests
{
    private static readonly DateTimeOffset At = DateTimeOffset.UnixEpoch.AddMinutes(5);

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
    public void Every_event_is_a_position_event()
    {
        Assert.IsAssignableFrom<PositionEvent>(new MessageReceived(SampleMessage(), At));
        Assert.IsAssignableFrom<PositionEvent>(
            new TaskCreated(PositionTaskId.New(), ThreadId.New(), "triage", Priority.Normal, At));
        Assert.IsAssignableFrom<PositionEvent>(new TaskUpdated(PositionTaskId.New(), "progress", At));
        Assert.IsAssignableFrom<PositionEvent>(new TaskCompleted(PositionTaskId.New(), At));
        Assert.IsAssignableFrom<PositionEvent>(new ShortMemoryUpdated("thread", "context", At));
        Assert.IsAssignableFrom<PositionEvent>(
            new OccupantChanged(OccupantId.From("alice"), OccupantType.Human, At));
        Assert.IsAssignableFrom<PositionEvent>(
            new MessageDispatched(MessageId.New(), ThreadId.New(), OccupantId.From("alice"), OccupantType.Human, At));
        Assert.IsAssignableFrom<PositionEvent>(new PositionPassivated(At));
        Assert.IsAssignableFrom<PositionEvent>(
            new PositionConfigurationApplied(new PositionConfigurationStamp(4, "sha256:v4"), At));
    }

    [Fact]
    public void Every_event_carries_the_occurred_at_instant()
    {
        Assert.Equal(At, new MessageReceived(SampleMessage(), At).OccurredAt);
        Assert.Equal(At, new PositionPassivated(At).OccurredAt);
    }

    [Fact]
    public void MessageReceived_carries_the_envelope()
    {
        var message = SampleMessage();

        var @event = new MessageReceived(message, At);

        Assert.Same(message, @event.Message);
    }

    [Fact]
    public void MessageReceived_rejects_null_message()
    {
        Assert.Throws<ArgumentNullException>(() => new MessageReceived(null!, At));
    }

    [Fact]
    public void TaskCreated_captures_all_fields()
    {
        var taskId = PositionTaskId.New();
        var thread = ThreadId.New();
        var causedBy = MessageId.New();
        var deadline = At.AddHours(2);

        var @event = new TaskCreated(taskId, thread, "triage incoming bug", Priority.High, At, deadline, causedBy);

        Assert.Equal(taskId, @event.TaskId);
        Assert.Equal(thread, @event.Thread);
        Assert.Equal("triage incoming bug", @event.Title);
        Assert.Equal(Priority.High, @event.Priority);
        Assert.Equal(deadline, @event.Deadline);
        Assert.Equal(causedBy, @event.CausedBy);
        Assert.Equal(At, @event.OccurredAt);
    }

    [Fact]
    public void TaskCreated_defaults_optional_fields_to_null()
    {
        var @event = new TaskCreated(PositionTaskId.New(), ThreadId.New(), "triage", Priority.Normal, At);

        Assert.Null(@event.Deadline);
        Assert.Null(@event.CausedBy);
    }

    [Theory]
    [InlineData("")]
    [InlineData("   ")]
    [InlineData(" leading")]
    [InlineData("trailing ")]
    public void TaskCreated_rejects_blank_or_padded_title(string title)
    {
        Assert.Throws<ArgumentException>(
            () => new TaskCreated(PositionTaskId.New(), ThreadId.New(), title, Priority.Normal, At));
    }

    [Fact]
    public void TaskCreated_rejects_undefined_priority()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskCreated(PositionTaskId.New(), ThreadId.New(), "triage", (Priority)99, At));
    }

    [Fact]
    public void TaskUpdated_keeps_optional_revisions_null_by_default()
    {
        var @event = new TaskUpdated(PositionTaskId.New(), "progress", At);

        Assert.Equal("progress", @event.Note);
        Assert.Null(@event.Priority);
        Assert.Null(@event.Deadline);
    }

    [Fact]
    public void TaskUpdated_carries_present_revisions()
    {
        var deadline = At.AddDays(1);

        var @event = new TaskUpdated(PositionTaskId.New(), "progress", At, Priority.Critical, deadline);

        Assert.Equal(Priority.Critical, @event.Priority);
        Assert.Equal(deadline, @event.Deadline);
    }

    [Fact]
    public void TaskUpdated_rejects_undefined_priority()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new TaskUpdated(PositionTaskId.New(), "progress", At, (Priority)0));
    }

    [Fact]
    public void TaskCompleted_allows_optional_summary()
    {
        Assert.Null(new TaskCompleted(PositionTaskId.New(), At).Summary);
        Assert.Equal("done", new TaskCompleted(PositionTaskId.New(), At, "done").Summary);
    }

    [Fact]
    public void TaskCompleted_rejects_blank_summary_when_present()
    {
        Assert.Throws<ArgumentException>(() => new TaskCompleted(PositionTaskId.New(), At, "  "));
    }

    [Fact]
    public void ShortMemoryUpdated_allows_empty_value()
    {
        var @event = new ShortMemoryUpdated("thread", string.Empty, At);

        Assert.Equal("thread", @event.Key);
        Assert.Equal(string.Empty, @event.Value);
    }

    [Fact]
    public void ShortMemoryUpdated_requires_key_content_and_non_null_value()
    {
        Assert.Throws<ArgumentException>(() => new ShortMemoryUpdated("  ", "v", At));
        Assert.Throws<ArgumentNullException>(() => new ShortMemoryUpdated("k", null!, At));
    }

    [Fact]
    public void OccupantChanged_captures_occupant_and_type()
    {
        var occupant = OccupantId.From("agent-7");

        var @event = new OccupantChanged(occupant, OccupantType.AiAgent, At);

        Assert.Equal(occupant, @event.Occupant);
        Assert.Equal(OccupantType.AiAgent, @event.Type);
    }

    [Fact]
    public void OccupantChanged_rejects_undefined_type()
    {
        Assert.Throws<ArgumentOutOfRangeException>(
            () => new OccupantChanged(OccupantId.From("x"), (OccupantType)42, At));
    }

    [Fact]
    public void MessageDispatched_captures_all_fields()
    {
        var message = MessageId.New();
        var thread = ThreadId.New();
        var occupant = OccupantId.From("alice");

        var @event = new MessageDispatched(message, thread, occupant, OccupantType.Human, At);

        Assert.Equal(message, @event.Message);
        Assert.Equal(thread, @event.Thread);
        Assert.Equal(occupant, @event.Occupant);
        Assert.Equal(OccupantType.Human, @event.OccupantType);
    }

    [Fact]
    public void MessageDispatched_rejects_null_components()
    {
        Assert.Throws<ArgumentNullException>(
            () => new MessageDispatched(null!, ThreadId.New(), OccupantId.From("a"), OccupantType.Human, At));
        Assert.Throws<ArgumentNullException>(
            () => new MessageDispatched(MessageId.New(), null!, OccupantId.From("a"), OccupantType.Human, At));
        Assert.Throws<ArgumentNullException>(
            () => new MessageDispatched(MessageId.New(), ThreadId.New(), null!, OccupantType.Human, At));
    }

    [Fact]
    public void PositionPassivated_allows_optional_reason()
    {
        Assert.Null(new PositionPassivated(At).Reason);
        Assert.Equal("idle", new PositionPassivated(At, "idle").Reason);
    }

    [Fact]
    public void PositionPassivated_rejects_blank_reason_when_present()
    {
        Assert.Throws<ArgumentException>(() => new PositionPassivated(At, "   "));
    }

    [Fact]
    public void PositionConfigurationApplied_captures_the_accepted_stamp()
    {
        var stamp = new PositionConfigurationStamp(4, "sha256:v4");

        var @event = new PositionConfigurationApplied(stamp, At);

        Assert.Same(stamp, @event.Stamp);
        Assert.Equal(4, @event.Stamp.Version);
        Assert.Equal("sha256:v4", @event.Stamp.Fingerprint);
        Assert.Equal(At, @event.OccurredAt);
    }

    [Fact]
    public void PositionConfigurationApplied_rejects_null_stamp()
    {
        Assert.Throws<ArgumentNullException>(() => new PositionConfigurationApplied(null!, At));
    }

    [Fact]
    public void Events_have_value_equality()
    {
        var taskId = PositionTaskId.New();
        var thread = ThreadId.New();

        var left = new TaskCreated(taskId, thread, "triage", Priority.Normal, At);
        var right = new TaskCreated(taskId, thread, "triage", Priority.Normal, At);

        Assert.Equal(left, right);
        Assert.Equal(left.GetHashCode(), right.GetHashCode());
    }

    [Fact]
    public void Events_differing_in_occurred_at_are_not_equal()
    {
        var taskId = PositionTaskId.New();
        var thread = ThreadId.New();

        var left = new TaskCreated(taskId, thread, "triage", Priority.Normal, At);
        var right = new TaskCreated(taskId, thread, "triage", Priority.Normal, At.AddSeconds(1));

        Assert.NotEqual(left, right);
    }
}
