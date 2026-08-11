using Hive.Actors.Positions;
using Hive.Application.Directives;
using Hive.Domain.Ai;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class AiDirectiveCheckpointCoordinatorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 1, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Progress_persists_checkpoint_before_materialized_effects_and_stops()
    {
        var clock = new MutableClock(At);
        var invoker = new SequencedInvoker(
            clock,
            [(TimeSpan.FromSeconds(86), ProgressOutput(
                completedIds: ["inspect"],
                nextSubtaskId: "verify",
                proposedIntent: "Report.Progress"))]);
        var coordinator = Coordinator(invoker, clock);

        var execution = await coordinator.ExecuteDetailedAsync(Request());

        Assert.Equal(DirectiveExecutionStatus.Completed, execution.Result.Status);
        Assert.Single(invoker.Requests);
        Assert.Equal(TimeSpan.FromSeconds(4), execution.Context.ExecutionPolicy.RemainingExecutionTime);
        Assert.Collection(
            execution.Result.Effects.Take(3),
            effect =>
            {
                var command = Assert.IsType<DirectivePositionCommandEffect>(effect);
                var persist = Assert.IsType<PersistDirectiveCheckpoint>(command.Command);
                Assert.Equal(1, persist.Checkpoint.Revision);
                Assert.Equal("verify", persist.Checkpoint.NextSubtaskId);
            },
            effect =>
            {
                var handoff = Assert.IsType<DirectiveMessageEffect>(effect);
                Assert.IsType<Report>(handoff.Message);
                Assert.NotEmpty(handoff.PositionCommands);
            },
            effect => Assert.IsType<DirectiveAuditExportResultEffect>(effect));
        Assert.IsType<Report>(execution.ResultMessage!.Message);
        Assert.Equal(ReportKind.Progress, ((Report)execution.ResultMessage.Message!).Kind);
        Assert.Equal(
            [ExecutionBudgetOperation.PrimaryInference],
            execution.Budget.ConsumedOperations);
    }

    [Fact]
    public async Task ContinueWork_uses_checkpoint_delta_then_reports_without_another_call()
    {
        var clock = new MutableClock(At);
        var invoker = new SequencedInvoker(
            clock,
            [
                (TimeSpan.FromSeconds(10), ProgressOutput(
                    completedIds: ["inspect"],
                    nextSubtaskId: "verify",
                    proposedIntent: "ContinueWork")),
                (TimeSpan.FromSeconds(76), ProgressOutput(
                    completedIds: ["inspect", "verify"],
                    nextSubtaskId: "summarize",
                    proposedIntent: "Report.Progress")),
            ]);
        var coordinator = Coordinator(invoker, clock);

        var execution = await coordinator.ExecuteDetailedAsync(Request());

        Assert.Equal(2, invoker.Requests.Count);
        Assert.Contains(
            "ResumeCheckpoint:",
            invoker.Requests[1].Content,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"next_subtask_id\":\"verify\"",
            invoker.Requests[1].Content,
            StringComparison.Ordinal);
        Assert.Equal(
            "directive-checkpoint-continuation",
            invoker.Requests[1].Metadata["hive.operation"]);
        Assert.Equal(
            [
                ExecutionBudgetOperation.PrimaryInference,
                ExecutionBudgetOperation.ContinuationInference,
            ],
            execution.Budget.ConsumedOperations);
        var persisted = Assert.IsType<PersistDirectiveCheckpoint>(
            Assert.IsType<DirectivePositionCommandEffect>(execution.Result.Effects[0]).Command);
        Assert.Equal(["inspect", "verify"], persisted.Checkpoint.CompletedSubtasks
            .Select(completed => completed.LocalId));
        Assert.Equal("summarize", persisted.Checkpoint.NextSubtaskId);
        Assert.Equal(ReportKind.Progress, Assert.IsType<Report>(execution.ResultMessage!.Message).Kind);
    }

    [Fact]
    public void Later_activation_selects_only_exact_thread_and_parent_lineage_checkpoint()
    {
        var parentDirectiveId = DirectiveId.From(
            Guid.Parse("cccccccc-0000-0000-0000-000000000901"));
        var checkpoint = Checkpoint(
            parentDirectiveId,
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000901")));
        var state = PositionState.Empty.Apply(new DirectiveCheckpointPersisted(checkpoint, At));
        var request = Request(
            state,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000902")),
            parentDirectiveId,
            checkpoint.Correlation.ThreadId);
        var context = AiDirectiveExecutionContext.From(request);

        var prompt = AiDirectivePrompt.CreateInitialRequest(context);

        Assert.Equal(checkpoint, context.ResumeCheckpoint);
        Assert.Contains("ResumeCheckpoint:", prompt.Content, StringComparison.Ordinal);
        Assert.Contains(parentDirectiveId.ToString(), prompt.Content, StringComparison.Ordinal);

        var unrelated = Request(
            state,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000903")),
            parentDirectiveId,
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000999")));
        var isolated = AiDirectiveExecutionContext.From(unrelated);
        Assert.Null(isolated.ResumeCheckpoint);
        Assert.Contains(
            "ResumeCheckpoint: <none>",
            AiDirectivePrompt.CreateInitialRequest(isolated).Content,
            StringComparison.Ordinal);
    }

    private static AiDirectiveExecutionCoordinator Coordinator(
        IAiAgentGatewayInvoker invoker,
        MutableClock clock) =>
        new(
            invoker,
            AiDirectiveResultMessageEmissionGate.Instance,
            AllowingAiAgentActionGate.Instance,
            StructuredPassthroughIntegrator.Instance,
            clock.Read);

    private static AiDirectiveProcessingRequest Request(
        PositionState? state = null,
        DirectiveId? directiveId = null,
        DirectiveId? parentDirectiveId = null,
        ThreadId? threadId = null)
    {
        var organization = OrganizationId.From("acme");
        var position = PositionId.From("bug-triage");
        var superior = PositionId.From("delivery-lead");
        var directive = new Directive(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000901")),
            organization,
            new PositionEndpointRef(superior),
            new PositionEndpointRef(position),
            threadId ?? ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000901")),
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddSeconds(90),
            directiveId ?? DirectiveId.From(
                Guid.Parse("cccccccc-0000-0000-0000-000000000901")),
            parentDirectiveId,
            "Triage checkout regression",
            "Customer reports checkout failures.",
            new DirectiveExecutionPolicyRequest(
                DirectiveExecutionPolicyContractVersions.V1,
                DirectiveExecutionMode.Checkpointable));
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(19, "sha256:f0-19-t04"),
            organization,
            position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: superior,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 1024),
                    timeout: TimeSpan.FromSeconds(90),
                    maxIterations: 3,
                    limitsVersion: AiPositionRuntimeConfiguration.CurrentLimitsVersion,
                    executionTimeout: TimeSpan.FromSeconds(90),
                    directiveExecutionPolicy: new DirectiveExecutionPolicyCapability(
                        DirectiveExecutionPolicyContractVersions.V1,
                        DirectiveExecutionMode.Checkpointable,
                        TimeSpan.FromSeconds(5))),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You triage incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration());
        return AiDirectiveProcessingRequest.Create(
            PositionEntityId.From(organization, position),
            configuration,
            state ?? PositionState.Empty,
            OccupantId.From("agent-19"),
            directive);
    }

    private static DirectiveCheckpoint Checkpoint(
        DirectiveId directiveId,
        ThreadId threadId) =>
        new(
            DirectiveCheckpointContractVersions.V1,
            revision: 1,
            Plan(),
            new DirectiveCheckpointCorrelation(
                OrganizationId.From("acme"),
                PositionId.From("bug-triage"),
                threadId,
                directiveId),
            [new CompletedDirectiveCheckpointSubtask(
                "inspect",
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.DirectiveInput,
                    "directive.context")])],
            blockers: [],
            nextSubtaskId: "verify");

    private static DirectiveCheckpointPlan Plan() =>
        new(
            DirectiveCheckpointContractVersions.V1,
            [
                new DirectiveCheckpointSubtask(
                    1,
                    "inspect",
                    "Inspect the supplied report",
                    ["Failure context is identified"],
                    TimeSpan.FromSeconds(20)),
                new DirectiveCheckpointSubtask(
                    2,
                    "verify",
                    "Verify the grounded finding",
                    ["Finding references directive input"],
                    TimeSpan.FromSeconds(20)),
                new DirectiveCheckpointSubtask(
                    3,
                    "summarize",
                    "Summarize the result",
                    ["Progress is ready to report"],
                    TimeSpan.FromSeconds(20)),
            ]);

    private static string ProgressOutput(
        IEnumerable<string> completedIds,
        string nextSubtaskId,
        string proposedIntent)
    {
        var plan = Plan();
        var checkpoint = new
        {
            contract_version = 1,
            plan = new
            {
                contract_version = 1,
                subtasks = plan.Subtasks.Select(subtask => new
                {
                    sequence = subtask.Sequence,
                    local_id = subtask.LocalId,
                    objective = subtask.Objective,
                    completion_criteria = subtask.CompletionCriteria,
                    estimated_duration_ms = (long)subtask.EstimatedDuration.TotalMilliseconds,
                }),
            },
            completed_subtasks = completedIds.Select(localId => new
            {
                local_id = localId,
                evidence_references = new[]
                {
                    new { source = "DirectiveInput", reference = "directive.context" },
                },
            }),
            blockers = Array.Empty<string>(),
            next_subtask_id = nextSubtaskId,
        };
        return System.Text.Json.JsonSerializer.Serialize(new
        {
            schema_version = 1,
            intent = "Report",
            report = new
            {
                kind = "Progress",
                body = "Grounded work is checkpointed.",
                checkpoint,
            },
            outcome_proposal = new
            {
                schema_version = 3,
                proposal = new
                {
                    proposed_intent = proposedIntent,
                    work_state = "InProgress",
                    required_intervention = "None",
                    blockers = Array.Empty<string>(),
                    next_action = $"Execute {nextSubtaskId}.",
                    evidence_references = new[]
                    {
                        new { source = "DirectiveInput", reference = "directive.context" },
                    },
                    information_gaps = Array.Empty<object>(),
                    authority_request = (object?)null,
                },
            },
        });
    }

    private sealed class MutableClock(DateTimeOffset value)
    {
        public DateTimeOffset Read() => value;

        public void Advance(TimeSpan elapsed) => value = value.Add(elapsed);
    }

    private sealed class SequencedInvoker(
        MutableClock clock,
        IReadOnlyList<(TimeSpan Elapsed, string Output)> responses)
        : IAiAgentGatewayInvoker
    {
        public List<AiGatewayRequest> Requests { get; } = [];

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(invocation.Request);
            var response = responses[Requests.Count - 1];
            clock.Advance(response.Elapsed);
            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    response.Output,
                    AiFinishReason.Stop)));
        }
    }

    private sealed class StructuredPassthroughIntegrator
        : IAiDirectiveOutcomeResolutionIntegrator
    {
        public static StructuredPassthroughIntegrator Instance { get; } = new();

        public bool RequiresStructuredProposal => true;

        public ValueTask<AiDirectiveOutcomeResolutionResult> ResolveAsync(
            AiDirectiveExecutionContext context,
            AiDirectiveIterationState iteration,
            AiDirectiveDecision decision,
            OutcomeProposal? proposal,
            AiDirectiveResultMessage proposedMessage,
            AiGatewayResponse gatewayResponse,
            bool hasAvailableBudget,
            IAiAgentActionGate actionGate,
            IAiDirectiveResultMessageGate routingGate,
            CancellationToken cancellationToken = default,
            DirectiveCheckpoint? verifiedCheckpoint = null) =>
            ValueTask.FromResult(new AiDirectiveOutcomeResolutionResult(
                proposedMessage,
                actionGateResult: null,
                routingGateResult: null,
                proposal,
                resolution: null,
                OutcomeResolutionMode.Shadow));
    }
}
