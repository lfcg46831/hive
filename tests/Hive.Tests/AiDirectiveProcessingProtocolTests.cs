using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class AiDirectiveProcessingProtocolTests
{
    private static readonly DateTimeOffset At = new(2026, 7, 1, 9, 30, 0, TimeSpan.Zero);

    [Fact]
    public void Create_preserves_directive_identity_limits_and_persisted_context_snapshot()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));
        var occupant = OccupantId.From("agent-7");
        var stamp = new PositionConfigurationStamp(3, "sha256:runtime");
        var directive = Directive(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000801")),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000801")),
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000801")));
        var previousMessage = MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000801"));
        var task = new PersistedTask(
            PositionTaskId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000000801")),
            directive.Thread,
            "Investigate checkout regression",
            Priority.High,
            At,
            causedBy: previousMessage);
        var state = PositionState.Restore(new PositionSnapshot(
            At,
            occupant,
            OccupantType.AiAgent,
            openTasks: [task],
            shortMemory: new Dictionary<string, string>
            {
                ["last-customer"] = "contoso",
            },
            recentHistory: [previousMessage],
            processedMessages: [directive.Id],
            lastConfigurationStamp: stamp));
        var configuration = RuntimeConfiguration(
            entity,
            stamp,
            new AiPositionRuntimeConfiguration(
                new AiProviderMetadata("stub", "triage"),
                new AiModelParameters(maxOutputTokens: 512),
                timeout: TimeSpan.FromSeconds(20),
                costLimits: new AiCostLimits(maxCallsPerHour: 12)));

        var request = AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            state,
            occupant,
            directive);

        Assert.Equal(entity.Organization, request.OrganizationId);
        Assert.Equal(entity.Position, request.PositionId);
        Assert.Equal(occupant, request.Occupant);
        Assert.Same(directive, request.Directive);
        Assert.Equal(directive.Thread, request.ThreadId);
        Assert.Equal(directive.DirectiveId, request.DirectiveId);
        Assert.Equal(directive.Id, request.MessageId);
        Assert.Equal(
            "directive:cccccccc000000000000000000000801:message:aaaaaaaa000000000000000000000801",
            request.CorrelationId);
        Assert.Equal(TimeSpan.FromSeconds(20), request.Limits.Timeout);
        Assert.Equal(512, request.Limits.MaxOutputTokens);
        Assert.Null(request.Limits.MaxIterations);
        Assert.Equal(12, request.Limits.CostLimits!.MaxCallsPerHour);
        Assert.Equal(stamp, request.PersistedContext.LastConfigurationStamp);
        Assert.Equal("contoso", request.PersistedContext.ShortMemory["last-customer"]);
        Assert.Equal(new[] { task }, request.PersistedContext.OpenTasks);
        Assert.Equal(new[] { previousMessage }, request.PersistedContext.RecentHistory);
        Assert.Equal(AiDirectiveTaskStateStatus.None, request.TaskState.Status);
        Assert.Null(request.TaskState.Task);
        Assert.Empty(request.TaskState.Matches);
    }

    [Fact]
    public void Create_exposes_open_task_state_for_single_task_caused_by_directive_message()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));
        var occupant = OccupantId.From("agent-7");
        var stamp = new PositionConfigurationStamp(4, "sha256:runtime");
        var directive = Directive(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000811")),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000811")),
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000811")));
        var deadline = At.AddHours(2);
        var task = new PersistedTask(
            PositionTaskId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000000811")),
            directive.Thread,
            "Triage checkout regression",
            Priority.High,
            At,
            deadline,
            directive.Id);

        var request = AiDirectiveProcessingRequest.Create(
            entity,
            RuntimeConfiguration(entity, stamp),
            StateWithTasks(stamp, occupant, task),
            occupant,
            directive);

        Assert.Equal(AiDirectiveTaskStateStatus.Open, request.TaskState.Status);
        Assert.Same(task, request.TaskState.Task);
        Assert.Equal(new[] { task }, request.TaskState.Matches);
        Assert.Equal(task.TaskId, request.TaskState.Task!.TaskId);
        Assert.Equal("Triage checkout regression", request.TaskState.Task.Title);
        Assert.Equal(Priority.High, request.TaskState.Task.Priority);
        Assert.Equal(deadline, request.TaskState.Task.Deadline);
    }

    [Fact]
    public void Create_exposes_ambiguous_task_state_for_multiple_tasks_caused_by_directive_message()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));
        var occupant = OccupantId.From("agent-7");
        var stamp = new PositionConfigurationStamp(5, "sha256:runtime");
        var directive = Directive(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000812")),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000812")),
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000812")));
        var second = new PersistedTask(
            PositionTaskId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000000812")),
            directive.Thread,
            "Second matching task",
            Priority.Normal,
            At,
            causedBy: directive.Id);
        var first = new PersistedTask(
            PositionTaskId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000812")),
            directive.Thread,
            "First matching task",
            Priority.High,
            At,
            causedBy: directive.Id);

        var request = AiDirectiveProcessingRequest.Create(
            entity,
            RuntimeConfiguration(entity, stamp),
            StateWithTasks(stamp, occupant, second, first),
            occupant,
            directive);

        Assert.Equal(AiDirectiveTaskStateStatus.Ambiguous, request.TaskState.Status);
        Assert.Null(request.TaskState.Task);
        Assert.Equal(new[] { first, second }, request.TaskState.Matches);
    }

    [Fact]
    public void Runtime_context_rejects_non_ai_occupant()
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));

        Assert.Throws<ArgumentException>(() => new AiDirectiveRuntimeContext(
            entity,
            Descriptor(),
            OccupantId.From("person-1"),
            new OccupantRuntimeConfiguration(OccupantType.Human),
            new PositionAuthorityRuntimeConfiguration()));
    }

    [Theory]
    [InlineData(0, null, null)]
    [InlineData(null, 0, null)]
    [InlineData(null, null, 0)]
    public void Limits_reject_non_positive_values(
        int? timeoutSeconds,
        int? maxOutputTokens,
        int? maxIterations)
    {
        var timeout = timeoutSeconds is null
            ? (TimeSpan?)null
            : TimeSpan.FromSeconds(timeoutSeconds.Value);

        Assert.Throws<ArgumentOutOfRangeException>(() => new AiDirectiveProcessingLimits(
            timeout,
            maxOutputTokens,
            maxIterations));
    }

    private static Directive Directive(
        MessageId message,
        ThreadId thread,
        DirectiveId directive) =>
        new(
            message,
            OrganizationId.From("acme"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("triage-agent")),
            thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            directive,
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        PositionEntityId entity,
        PositionConfigurationStamp stamp) =>
        RuntimeConfiguration(
            entity,
            stamp,
            new AiPositionRuntimeConfiguration(
                new AiProviderMetadata("stub", "triage"),
                new AiModelParameters(maxOutputTokens: 512),
                timeout: TimeSpan.FromSeconds(20),
                costLimits: new AiCostLimits(maxCallsPerHour: 12)));

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        PositionEntityId entity,
        PositionConfigurationStamp stamp,
        AiPositionRuntimeConfiguration aiGateway) =>
        new(
            stamp,
            entity.Organization,
            entity.Position,
            Descriptor(),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                tools: [new ToolConfiguration("files", ["bugs/read"])],
                aiGateway: aiGateway),
            new PositionAuthorityRuntimeConfiguration(
                canDecide: ["bug.triage"],
                overrides:
                [
                    new PositionAuthorityOverrideRuntimeConfiguration(
                        "customer.data-request",
                        ActionDomainGate.Escalate,
                        "delivery-lead"),
                ]));

    private static PositionRuntimeDescriptor Descriptor() =>
        new(
            UnitId.From("engineering"),
            reportsTo: PositionId.From("delivery-lead"),
            name: "Bug triage",
            timezone: "Europe/Lisbon");

    private static PositionState StateWithTasks(
        PositionConfigurationStamp stamp,
        OccupantId occupant,
        params PersistedTask[] tasks) =>
        PositionState.Restore(new PositionSnapshot(
            At,
            occupant,
            OccupantType.AiAgent,
            openTasks: tasks,
            lastConfigurationStamp: stamp));
}
