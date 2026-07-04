using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveExecutionContextTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 1, 15, 0, 0, TimeSpan.Zero);

    [Fact]
    public void From_rejects_null_request()
    {
        Assert.Throws<ArgumentNullException>(() =>
            AiDirectiveExecutionContext.From(null!));
    }

    [Fact]
    public void From_preserves_directive_runtime_policy_tools_and_persisted_context_deterministically()
    {
        var request = Request(includeOptionalContext: true);

        var context = AiDirectiveExecutionContext.From(request);

        Assert.Equal(request.CorrelationId, context.CorrelationId);
        Assert.Equal(request.OrganizationId, context.OrganizationId);
        Assert.Equal(request.PositionId, context.PositionId);
        Assert.Equal(request.Occupant, context.Occupant);
        Assert.Equal("triage-v1", context.IdentityPromptRef);
        Assert.NotNull(context.IdentityPrompt);
        Assert.Equal("triage-v1", context.IdentityPrompt.Id);
        Assert.Equal("prompts/triage-v1.md", context.IdentityPrompt.Path);
        Assert.Equal("You are responsible for triaging incoming bugs.", context.IdentityPrompt.Content);
        Assert.Equal(request.Limits, context.Limits);
        Assert.Equal(request.PersistedContext.LastConfigurationStamp, context.LastConfigurationStamp);

        Assert.Equal(request.Directive.Id, context.Directive.MessageId);
        Assert.Equal(request.Directive.Thread, context.Directive.ThreadId);
        Assert.Equal(request.Directive.DirectiveId, context.Directive.DirectiveId);
        Assert.Equal(request.Directive.ParentDirectiveId, context.Directive.ParentDirectiveId);
        Assert.Equal(request.Directive.From, context.Directive.From);
        Assert.Equal(request.Directive.To, context.Directive.To);
        Assert.Equal(request.Directive.Priority, context.Directive.Priority);
        Assert.Equal(request.Directive.SentAt, context.Directive.SentAt);
        Assert.Equal(request.Directive.Deadline, context.Directive.Deadline);
        Assert.Equal("Triage checkout regression", context.Directive.Objective);
        Assert.Equal("Customer reports checkout failures.", context.Directive.Context);

        Assert.Equal(UnitId.From("engineering"), context.Relation.Unit);
        Assert.Equal(PositionId.From("delivery-lead"), context.Relation.ReportsTo);
        Assert.Equal(
            ["bug.triage"],
            context.Authority.CanDecide.Select(key => key.Value).ToArray());
        var authorityOverride = Assert.Single(context.Authority.Overrides);
        Assert.Equal("comms.external-official", authorityOverride.Key.Value);
        Assert.Equal(ActionDomainGate.HumanApproval, authorityOverride.Gate);
        Assert.Equal("delivery-lead", authorityOverride.Approver);

        Assert.Equal(["alpha", "zeta"], context.ShortMemory.Select(entry => entry.Key).ToArray());
        Assert.Equal(["first", "last"], context.ShortMemory.Select(entry => entry.Value).ToArray());
        Assert.Equal(
            [
                PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000002")),
            ],
            context.OpenTasks.Select(task => task.TaskId).ToArray());
        Assert.Equal(
            [
                MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000002")),
                MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000001")),
            ],
            context.RecentHistory.Select(message => message).ToArray());

        var tool = Assert.Single(context.AuthorizedTools);
        Assert.Equal("jira", tool.Connector);
        Assert.Equal(["issues/read", "issues/comment"], tool.Scope.Select(scope => scope).ToArray());
    }

    [Fact]
    public void From_preserves_absent_optionals_explicitly()
    {
        var request = Request(includeOptionalContext: false);

        var context = AiDirectiveExecutionContext.From(request);

        Assert.Null(context.IdentityPromptRef);
        Assert.Null(context.Relation.ReportsTo);
        Assert.Empty(context.ShortMemory);
        Assert.Empty(context.OpenTasks);
        Assert.Empty(context.RecentHistory);
        Assert.Empty(context.Authority.CanDecide);
        Assert.Empty(context.Authority.Overrides);
        Assert.Empty(context.AuthorizedTools);
    }

    [Fact]
    public async Task AiAgentActor_assembles_context_before_advancing_snapshot_to_result_emitted()
    {
        var request = Request(includeOptionalContext: true);
        var system = ActorSystem.Create($"ai-agent-context-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new RecordingInvoker())),
                "agent");

            actor.Tell(request);

            var contextResult = await WaitForContextAsync(actor, request.CorrelationId);
            var snapshotResult = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                Timeout());

            Assert.True(contextResult.Found);
            Assert.Equal(request.CorrelationId, contextResult.CorrelationId);
            Assert.Equal(request.DirectiveId, contextResult.Context!.Directive.DirectiveId);
            Assert.True(snapshotResult.Found);
            Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshotResult.Snapshot!.Status);
            Assert.Equal(
                [
                    AiDirectiveProcessingStatus.Received,
                    AiDirectiveProcessingStatus.ContextAssembled,
                    AiDirectiveProcessingStatus.GatewayRequested,
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    AiDirectiveProcessingStatus.ResultEmitted,
                ],
                snapshotResult.Snapshot.History.Select(transition => transition.Status).ToArray());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_missing_context_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-context-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-7"),
                    new RecordingInvoker())),
                "agent");

            var result = await actor.Ask<AiDirectiveExecutionContextQueryResult>(
                new GetAiDirectiveExecutionContext("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Context);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<AiDirectiveExecutionContextQueryResult> WaitForContextAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveExecutionContextQueryResult>(
                new GetAiDirectiveExecutionContext(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive execution context was not assembled.");
    }

    private static AiDirectiveProcessingRequest Request(bool includeOptionalContext)
    {
        var entity = PositionEntityId.From(
            OrganizationId.From("acme"),
            PositionId.From("triage-agent"));
        var occupant = OccupantId.From("agent-7");
        var stamp = new PositionConfigurationStamp(10, "sha256:t04");
        var parentDirective = includeOptionalContext
            ? DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000099"))
            : null;
        var directive = new OrgDirective(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000904")),
            entity.Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(entity.Position),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000904")),
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: includeOptionalContext ? At.AddHours(2) : null,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000904")),
            parentDirective,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");

        var configuration = new PositionRuntimeConfiguration(
            stamp,
            entity.Organization,
            entity.Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: includeOptionalContext ? PositionId.From("delivery-lead") : null,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: includeOptionalContext ? "triage-v1" : null,
                tools: includeOptionalContext
                    ? [new ToolConfiguration("jira", ["issues/read", "issues/comment"])]
                    : null,
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15)),
                identityPrompt: includeOptionalContext
                    ? new IdentityPromptRuntimeConfiguration(
                        "triage-v1",
                        "prompts/triage-v1.md",
                        "You are responsible for triaging incoming bugs.")
                    : null),
            includeOptionalContext
                ? new PositionAuthorityRuntimeConfiguration(
                    canDecide: ["bug.triage"],
                    overrides:
                    [
                        new PositionAuthorityOverrideRuntimeConfiguration(
                            "comms.external-official",
                            ActionDomainGate.HumanApproval,
                            "delivery-lead"),
                    ])
                : new PositionAuthorityRuntimeConfiguration());

        var state = includeOptionalContext
            ? PositionState.Restore(new PositionSnapshot(
                At,
                occupant,
                OccupantType.AiAgent,
                openTasks:
                [
                    new PersistedTask(
                        PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000002")),
                        directive.Thread,
                        "Second task",
                        Priority.Normal,
                        At),
                    new PersistedTask(
                        PositionTaskId.From(Guid.Parse("00000000-0000-0000-0000-000000000001")),
                        directive.Thread,
                        "First task",
                        Priority.High,
                        At),
                ],
                shortMemory: new Dictionary<string, string>
                {
                    ["zeta"] = "last",
                    ["alpha"] = "first",
                },
                recentHistory:
                [
                    MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000002")),
                    MessageId.From(Guid.Parse("dddddddd-0000-0000-0000-000000000001")),
                ],
                lastConfigurationStamp: stamp))
            : PositionState.Restore(new PositionSnapshot(At, lastConfigurationStamp: stamp));

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            state,
            occupant,
            directive);
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
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
                    "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Working.\"}}",
                    AiFinishReason.Stop)));
    }
}
