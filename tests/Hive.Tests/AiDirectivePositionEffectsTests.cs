using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectivePositionEffectsTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 4, 16, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly PositionId Engineer = PositionId.From("engineer");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001110"));
    private static readonly MessageId IncomingMessage =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001110"));
    private static readonly DirectiveId IncomingDirective =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001110"));

    [Fact]
    public void Create_generates_memory_and_completes_matching_task_for_done_report()
    {
        var taskId = PositionTaskId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001110"));
        var context = AiDirectiveExecutionContext.From(Request(openTasks:
        [
            new PersistedTask(
                taskId,
                Thread,
                "Triage checkout regression",
                Priority.High,
                At,
                causedBy: IncomingMessage),
        ]));
        var result = AcceptedResult(
            context,
            new AiDirectiveReportDecision(ReportKind.Done, "Bug triage is complete."));

        var effects = AiDirectivePositionEffectFactory.Create(context, result);

        Assert.True(effects.IsSuccess, effects.Failure?.AuditReason);
        Assert.Equal(context.CorrelationId, effects.CorrelationId);
        Assert.Equal(2, effects.Commands.Count);
        var memory = Assert.IsType<UpdateShortMemory>(effects.Commands[0]);
        Assert.Equal($"directive:{IncomingDirective.Value:N}:result", memory.Key);
        Assert.Equal("Report Done: Bug triage is complete.", memory.Value);
        var complete = Assert.IsType<CompleteTask>(effects.Commands[1]);
        Assert.Equal(taskId, complete.TaskId);
        Assert.Equal("Bug triage is complete.", complete.Summary);
    }

    [Fact]
    public void Create_generates_memory_and_opens_followup_task_for_child_directive()
    {
        var context = AiDirectiveExecutionContext.From(Request(directSubordinates: [Engineer]));
        var result = AcceptedResult(
            context,
            new AiDirectiveChildDirectiveDecision(
                Engineer,
                "Investigate checkout callback failures.",
                "Focus on payment callback logs."));
        var generatedTaskId = PositionTaskId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000001110"));

        var effects = AiDirectivePositionEffectFactory.Create(
            context,
            result,
            () => generatedTaskId);

        Assert.True(effects.IsSuccess, effects.Failure?.AuditReason);
        Assert.Equal(2, effects.Commands.Count);
        var memory = Assert.IsType<UpdateShortMemory>(effects.Commands[0]);
        Assert.Equal($"directive:{IncomingDirective.Value:N}:result", memory.Key);
        Assert.Equal(
            "Delegated directive to engineer: Investigate checkout callback failures.",
            memory.Value);
        var openTask = Assert.IsType<OpenTask>(effects.Commands[1]);
        Assert.Equal(generatedTaskId, openTask.TaskId);
        Assert.Equal(Thread, openTask.Thread);
        Assert.Equal("Follow delegated directive to engineer", openTask.Title);
        Assert.Equal(Priority.High, openTask.Priority);
        Assert.Equal(context.Directive.Deadline, openTask.Deadline);
        Assert.Equal(IncomingMessage, openTask.CausedBy);
    }

    [Fact]
    public void Create_returns_no_commands_for_rejected_result()
    {
        var context = AiDirectiveExecutionContext.From(Request());
        var rejected = AiDirectiveResultMessage.Rejected(
            context.CorrelationId,
            new AiDirectiveResultMessageFailure(
                "routing-rejected",
                "Routing gate rejected the result."));

        var effects = AiDirectivePositionEffectFactory.Create(context, rejected);

        Assert.True(effects.IsFailure);
        Assert.Empty(effects.Commands);
        Assert.Equal("routing-rejected", effects.Failure!.Code);
    }

    [Fact]
    public async Task AiAgentActor_stores_position_effects_and_sends_commands_to_parent()
    {
        var taskId = PositionTaskId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001111"));
        var request = Request(openTasks:
        [
            new PersistedTask(
                taskId,
                Thread,
                "Triage checkout regression",
                Priority.High,
                At,
                causedBy: IncomingMessage),
        ]);
        var system = ActorSystem.Create($"ai-agent-position-effects-{Guid.NewGuid():N}");
        var commands = new TaskCompletionSource<IReadOnlyList<PositionCommand>>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var parent = system.ActorOf(
                Props.Create(() => new CapturingCommandParentActor(request, commands)),
                "parent");

            parent.Tell(StartProcessing.Instance);

            var captured = await commands.Task.WaitAsync(Timeout());
            Assert.Collection(
                captured,
                command => Assert.IsType<UpdateShortMemory>(command),
                command => Assert.IsType<CompleteTask>(command));

            var effects = await parent.Ask<AiDirectivePositionEffectsQueryResult>(
                new ForwardPositionEffectsQuery(request.CorrelationId),
                Timeout());

            Assert.True(effects.Found);
            Assert.Equal(request.CorrelationId, effects.CorrelationId);
            Assert.Equal(2, effects.Effects!.Commands.Count);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_correlated_processing_result_to_parent()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-position-result-{Guid.NewGuid():N}");
        var completed = new TaskCompletionSource<PositionOccupantProcessingCompleted>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var parent = system.ActorOf(
                Props.Create(() => new CapturingParentActor(request, completed)),
                "parent");

            parent.Tell(StartProcessing.Instance);

            var result = await completed.Task.WaitAsync(Timeout());

            Assert.Equal(request.CorrelationId, result.CorrelationId);
            Assert.Equal(request.ThreadId, result.ThreadId);
            Assert.Equal(request.DirectiveId, result.DirectiveId);
            Assert.Equal(request.MessageId, result.MessageId);
            Assert.Equal(PositionOccupantProcessingStatus.Completed, result.Status);
            Assert.Null(result.FailureCode);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static AiDirectiveResultMessage AcceptedResult(
        AiDirectiveExecutionContext context,
        AiDirectiveDecision decision)
    {
        var result = AiDirectiveResultMessageFactory.Create(
            context,
            decision,
            () => MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000001110")),
            () => DirectiveId.From(Guid.Parse("20000000-0000-0000-0000-000000001110")),
            () => At.AddMinutes(1));

        Assert.True(result.IsSuccess, result.Failure?.AuditReason);
        return result;
    }

    private static AiDirectiveProcessingRequest Request(
        IEnumerable<PersistedTask>? openTasks = null,
        IEnumerable<PositionId>? directSubordinates = null)
    {
        var entity = PositionEntityId.From(Organization, Position);
        var directive = new OrgDirective(
            IncomingMessage,
            Organization,
            new PositionEndpointRef(Superior),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddHours(2),
            IncomingDirective,
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(17, "sha256:t11"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: Superior,
                name: "Bug triage",
                timezone: "Europe/Lisbon",
                directSubordinates: directSubordinates),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15)),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration());
        var state = PositionState.Restore(new PositionSnapshot(
            At,
            openTasks: openTasks ?? []));

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            state,
            OccupantId.From("agent-7"),
            directive);
    }

    private static string ValidReportOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Done",
            "body": "Bug triage is complete."
          }
        }
        """;

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class StaticResponseInvoker(string output) : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    output,
                    AiFinishReason.Stop)));
    }

    private sealed class CapturingCommandParentActor : ReceiveActor
    {
        private readonly IActorRef _child;
        private readonly List<PositionCommand> _commands = [];
        private readonly TaskCompletionSource<IReadOnlyList<PositionCommand>> _capture;

        public CapturingCommandParentActor(
            AiDirectiveProcessingRequest request,
            TaskCompletionSource<IReadOnlyList<PositionCommand>> capture)
        {
            _capture = capture;
            _child = Context.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            Receive<StartProcessing>(_ => _child.Tell(request));
            Receive<PositionCommand>(command =>
            {
                _commands.Add(command);
                if (_commands.Count == 2)
                {
                    _capture.TrySetResult(_commands.ToArray());
                }
            });
            Receive<ForwardPositionEffectsQuery>(query => _child.Forward(
                new GetAiDirectivePositionEffects(query.CorrelationId)));
        }
    }

    private sealed class CapturingParentActor : ReceiveActor
    {
        private readonly IActorRef _child;

        public CapturingParentActor(
            AiDirectiveProcessingRequest request,
            TaskCompletionSource<PositionOccupantProcessingCompleted> capture)
        {
            _child = Context.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            Receive<StartProcessing>(_ => _child.Tell(request));
            Receive<PositionOccupantProcessingCompleted>(capture.TrySetResult);
            Receive<PositionCommand>(_ =>
            {
            });
        }
    }

    private sealed record ForwardPositionEffectsQuery(string CorrelationId);

    private sealed record StartProcessing
    {
        public static StartProcessing Instance { get; } = new();

        private StartProcessing()
        {
        }
    }
}
