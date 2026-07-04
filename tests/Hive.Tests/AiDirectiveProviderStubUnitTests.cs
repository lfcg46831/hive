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

public sealed class AiDirectiveProviderStubUnitTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 4, 18, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly PositionId Engineer = PositionId.From("engineer");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001300"));
    private static readonly MessageId IncomingMessage =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001300"));
    private static readonly DirectiveId IncomingDirective =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001300"));

    [Fact]
    public async Task Provider_stub_records_context_prompt_interpretation_iteration_effects_and_audit()
    {
        var taskId = PositionTaskId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001300"));
        var request = Request(
            maxIterations: 2,
            openTasks:
            [
                new PersistedTask(
                    taskId,
                    Thread,
                    "Triage checkout regression",
                    Priority.High,
                    At,
                    causedBy: IncomingMessage),
            ]);
        var invoker = StubInvoker.FromOutput(ValidReportOutput());
        var system = ActorSystem.Create($"ai-agent-t13-happy-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(request.Occupant, invoker)),
                "agent");

            actor.Tell(request);

            var audit = await WaitForAuditAsync(actor, request.CorrelationId);
            var context = await actor.Ask<AiDirectiveExecutionContextQueryResult>(
                new GetAiDirectiveExecutionContext(request.CorrelationId),
                Timeout());
            var prompt = await actor.Ask<AiDirectiveInitialPromptQueryResult>(
                new GetAiDirectiveInitialPrompt(request.CorrelationId),
                Timeout());
            var gateway = await actor.Ask<AiDirectiveGatewayInvocationQueryResult>(
                new GetAiDirectiveGatewayInvocation(request.CorrelationId),
                Timeout());
            var interpretation = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult(request.CorrelationId),
                Timeout());
            var iteration = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot(request.CorrelationId),
                Timeout());
            var result = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                Timeout());
            var effects = await actor.Ask<AiDirectivePositionEffectsQueryResult>(
                new GetAiDirectivePositionEffects(request.CorrelationId),
                Timeout());

            Assert.True(context.Found);
            Assert.Equal("triage-v1", context.Context!.IdentityPromptRef);
            Assert.Equal("prompts/triage-v1.md", context.Context.IdentityPrompt!.Path);
            Assert.Equal(["bug.triage"], context.Context.Authority.CanDecide.Select(key => key.Value).ToArray());
            Assert.Equal(["jira"], context.Context.AuthorizedTools.Select(tool => tool.Connector).ToArray());
            Assert.Equal(2, context.Context.Limits.MaxIterations);

            Assert.True(prompt.Found);
            Assert.Same(prompt.Request, invoker.Invocation!.Request);
            Assert.Contains("Return JSON only", prompt.Request!.SystemInstruction, StringComparison.Ordinal);
            Assert.Contains("IdentityPromptRef: triage-v1", prompt.Request.Content, StringComparison.Ordinal);
            Assert.Contains("Objective: Triage checkout regression", prompt.Request.Content, StringComparison.Ordinal);
            Assert.Equal("2", prompt.Request.Metadata["max_iterations"]);
            Assert.True(prompt.Request.Policy!.HasAvailableBudget);

            Assert.True(gateway.Found);
            Assert.True(gateway.Result!.IsSuccess);
            Assert.Equal(1, invoker.CallCount);

            Assert.True(interpretation.Found);
            Assert.Equal(AiDirectiveInterpretationOutcomeKind.DecisionAccepted, interpretation.Result!.Outcome);
            var decision = Assert.IsType<AiDirectiveReportDecision>(interpretation.Result.Decision);
            Assert.Equal(ReportKind.Done, decision.Kind);

            Assert.True(iteration.Found);
            Assert.Equal(AiDirectiveIterationStopKind.Completed, iteration.Snapshot!.TerminalStopKind);
            Assert.Equal("completed", iteration.Snapshot.TerminalCode);

            Assert.True(result.Found);
            var report = Assert.IsType<Report>(result.Result!.Message);
            Assert.Equal(IncomingDirective, report.AboutDirectiveId);
            Assert.Equal(new PositionEndpointRef(Superior), report.To);

            Assert.True(effects.Found);
            Assert.Equal(
                ["UpdateShortMemory", "CompleteTask"],
                effects.Effects!.Commands.Select(command => command.GetType().Name).ToArray());

            Assert.True(audit.Found);
            Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, audit.Snapshot!.Status);
            Assert.Equal("result-emitted", audit.Snapshot.TerminalCode);
            Assert.Equal("completed", audit.Snapshot.IterationAudit!.TerminalCode);
            Assert.Equal("Report", audit.Snapshot.Decision!.DecisionKind);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Provider_stub_malformed_output_escalates_without_result_message_or_effects()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-t13-malformed-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    StubInvoker.FromOutput("{"))),
                "agent");

            actor.Tell(request);

            var audit = await WaitForAuditAsync(actor, request.CorrelationId);
            var interpretation = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult(request.CorrelationId),
                Timeout());
            var result = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                Timeout());
            var effects = await actor.Ask<AiDirectivePositionEffectsQueryResult>(
                new GetAiDirectivePositionEffects(request.CorrelationId),
                Timeout());

            Assert.Equal(AiDirectiveProcessingStatus.Escalated, audit.Snapshot!.Status);
            Assert.Equal("ai-output-invalid", audit.Snapshot.TerminalCode);
            Assert.True(interpretation.Found);
            Assert.Equal(AiDirectiveInterpretationOutcomeKind.EscalationRequired, interpretation.Result!.Outcome);
            Assert.Equal("ai-output-invalid", interpretation.Result.Failure!.Code);
            Assert.Contains(interpretation.Result.Failure.ParseErrors, error => error.Code == "invalid-json");
            Assert.False(result.Found);
            Assert.False(effects.Found);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Provider_stub_tool_call_response_records_max_iteration_stop_before_invalid_output_escalation()
    {
        var request = Request(
            maxIterations: 1,
            tools: [new ToolConfiguration("files", ["bugs/read"])]);
        var invoker = StubInvoker.FromResponse(invocation => AiGatewayResponse.Succeeded(
            invocation.Request.OrganizationId,
            invocation.Request.PositionId,
            invocation.Request.ThreadId,
            invocation.Request.MessageId,
            text: null,
            AiFinishReason.ToolCalls,
            invocation.Request.Provider,
            [new AiToolCall("call-1", "files")]));
        var system = ActorSystem.Create($"ai-agent-t13-max-iterations-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(request.Occupant, invoker)),
                "agent");

            actor.Tell(request);

            var audit = await WaitForAuditAsync(actor, request.CorrelationId);
            var iteration = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot(request.CorrelationId),
                Timeout());
            var interpretation = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult(request.CorrelationId),
                Timeout());

            Assert.True(iteration.Found);
            Assert.Equal(AiDirectiveIterationStopKind.MaxIterationsReached, iteration.Snapshot!.TerminalStopKind);
            Assert.Equal("max-iterations-reached", iteration.Snapshot.TerminalCode);
            Assert.Equal(AiDirectiveProcessingStatus.Escalated, audit.Snapshot!.Status);
            Assert.Equal("ai-output-invalid", audit.Snapshot.TerminalCode);
            Assert.Equal(AiDirectiveInterpretationOutcomeKind.EscalationRequired, interpretation.Result!.Outcome);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Provider_stub_budget_failure_records_structured_error_without_result_message()
    {
        var request = Request();
        var invoker = StubInvoker.FromResponse(invocation => AiGatewayResponse.Failed(
            new AiGatewayError(
                invocation.Request.OrganizationId,
                invocation.Request.PositionId,
                invocation.Request.ThreadId,
                invocation.Request.MessageId,
                AiGatewayErrorCode.BudgetInsufficient,
                "Budget is exhausted.",
                isRetryable: false,
                invocation.Request.Provider)));
        var system = ActorSystem.Create($"ai-agent-t13-budget-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(request.Occupant, invoker)),
                "agent");

            actor.Tell(request);

            var audit = await WaitForAuditAsync(actor, request.CorrelationId);
            var gateway = await actor.Ask<AiDirectiveGatewayInvocationQueryResult>(
                new GetAiDirectiveGatewayInvocation(request.CorrelationId),
                Timeout());
            var interpretation = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult(request.CorrelationId),
                Timeout());
            var result = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                Timeout());

            Assert.Equal(AiDirectiveProcessingStatus.Failed, audit.Snapshot!.Status);
            Assert.Equal("ai-gateway-failure", audit.Snapshot.TerminalCode);
            Assert.Equal("budget-insufficient", audit.Snapshot.Gateway.ErrorCode);
            Assert.True(gateway.Found);
            Assert.Equal(AiGatewayErrorCode.BudgetInsufficient, gateway.Result!.Response.Error!.Code);
            Assert.Equal(AiDirectiveInterpretationOutcomeKind.StructuredError, interpretation.Result!.Outcome);
            Assert.Equal("ai-gateway-failure", interpretation.Result.Failure!.Code);
            Assert.False(result.Found);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Provider_stub_timeout_cancels_invocation_and_records_timeout_audit()
    {
        var request = Request(timeout: TimeSpan.FromMilliseconds(50));
        var invoker = new WaitForCancellationInvoker();
        var system = ActorSystem.Create($"ai-agent-t13-timeout-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(request.Occupant, invoker)),
                "agent");

            actor.Tell(request);

            await invoker.Started.Task.WaitAsync(Timeout());
            var audit = await WaitForAuditAsync(actor, request.CorrelationId);
            var iteration = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot(request.CorrelationId),
                Timeout());
            var interpretation = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult(request.CorrelationId),
                Timeout());

            Assert.Equal(1, invoker.CallCount);
            Assert.True(invoker.CancellationToken.IsCancellationRequested);
            Assert.Equal(AiDirectiveProcessingStatus.Failed, audit.Snapshot!.Status);
            Assert.Equal("timeout", audit.Snapshot.TerminalCode);
            Assert.True(iteration.Found);
            Assert.Equal(AiDirectiveIterationStopKind.Timeout, iteration.Snapshot!.TerminalStopKind);
            Assert.Equal("timeout", iteration.Snapshot.TerminalCode);
            Assert.False(interpretation.Found);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Provider_stub_valid_output_escalates_when_result_gate_rejects_routing()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-t13-routing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    StubInvoker.FromOutput(ValidReportOutput()),
                    new RejectingResultMessageGate())),
                "agent");

            actor.Tell(request);

            var audit = await WaitForAuditAsync(actor, request.CorrelationId);
            var result = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                Timeout());
            var effects = await actor.Ask<AiDirectivePositionEffectsQueryResult>(
                new GetAiDirectivePositionEffects(request.CorrelationId),
                Timeout());

            Assert.Equal(AiDirectiveProcessingStatus.Escalated, audit.Snapshot!.Status);
            Assert.Equal("routing-rejected", audit.Snapshot.TerminalCode);
            Assert.True(result.Found);
            Assert.False(result.Result!.IsSuccess);
            Assert.Equal("routing-rejected", result.Result.Failure!.Code);
            Assert.True(effects.Found);
            Assert.True(effects.Effects!.IsFailure);
            Assert.Equal("routing-rejected", effects.Effects.Failure!.Code);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<AiDirectiveAuditSnapshotQueryResult> WaitForAuditAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveAuditSnapshotQueryResult>(
                new GetAiDirectiveAuditSnapshot(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive audit snapshot was not recorded.");
    }

    private static AiDirectiveProcessingRequest Request(
        TimeSpan? timeout = null,
        int? maxIterations = 2,
        IEnumerable<ToolConfiguration>? tools = null,
        IEnumerable<PersistedTask>? openTasks = null)
    {
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
        var entity = PositionEntityId.From(Organization, Position);
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(19, "sha256:t13"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                Superior,
                "Bug triage",
                "Europe/Lisbon",
                directSubordinates: [Engineer]),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                tools: tools ?? [new ToolConfiguration("jira", ["issues/read", "issues/comment"])],
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: timeout ?? TimeSpan.FromSeconds(15),
                    processingMode: AiProcessingMode.Batch,
                    maxIterations: maxIterations),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration(
                canDecide: ["bug.triage"],
                overrides:
                [
                    new PositionAuthorityOverrideRuntimeConfiguration(
                        "comms.external-official",
                        ActionDomainGate.HumanApproval,
                        "delivery-lead"),
                ]));
        var state = PositionState.Restore(new PositionSnapshot(
            At,
            openTasks: openTasks ?? [],
            shortMemory: new Dictionary<string, string>
            {
                ["last-report"] = "Customer reports checkout failures.",
            },
            recentHistory:
            [
                MessageId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000001300")),
            ],
            lastConfigurationStamp: new PositionConfigurationStamp(18, "sha256:t12")));

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            state,
            OccupantId.From("agent-13"),
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

    private sealed class StubInvoker(
        Func<AiAgentGatewayInvocation, AiGatewayResponse> responseFactory)
        : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public AiAgentGatewayInvocation? Invocation { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public static StubInvoker FromOutput(string output) =>
            FromResponse(invocation => AiGatewayResponse.Succeeded(
                invocation.Request.OrganizationId,
                invocation.Request.PositionId,
                invocation.Request.ThreadId,
                invocation.Request.MessageId,
                output,
                AiFinishReason.Stop,
                invocation.Request.Provider));

        public static StubInvoker FromResponse(
            Func<AiAgentGatewayInvocation, AiGatewayResponse> responseFactory) =>
            new(responseFactory);

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            Invocation = invocation;
            CancellationToken = cancellationToken;

            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                responseFactory(invocation)));
        }
    }

    private sealed class WaitForCancellationInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public TaskCompletionSource Started { get; } =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public async Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            CancellationToken = cancellationToken;
            Started.TrySetResult();

            await Task.Delay(System.Threading.Timeout.InfiniteTimeSpan, cancellationToken);
            throw new InvalidOperationException("The cancellation token should stop the wait.");
        }
    }

    private sealed class RejectingResultMessageGate : IAiDirectiveResultMessageGate
    {
        public ValueTask<AiDirectiveResultMessageGateResult> ValidateAsync(
            AiDirectiveExecutionContext context,
            OrgMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(message);
            cancellationToken.ThrowIfCancellationRequested();

            return ValueTask.FromResult(AiDirectiveResultMessageGateResult.Rejected(
                new AiDirectiveResultMessageFailure(
                    "routing-rejected",
                    "Routing gate rejected the provider-stub result message.")));
        }
    }
}
