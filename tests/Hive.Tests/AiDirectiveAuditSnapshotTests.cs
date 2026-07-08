using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveAuditSnapshotTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 4, 17, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001200"));
    private static readonly MessageId IncomingMessage =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001200"));
    private static readonly DirectiveId IncomingDirective =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000001200"));

    [Fact]
    public async Task AiAgentActor_publishes_and_stores_redacted_audit_snapshot_for_accepted_report()
    {
        var taskId = PositionTaskId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001200"));
        var request = Request(openTasks:
        [
            new PersistedTask(
                taskId,
                Thread,
                "Existing task that should not leak",
                Priority.High,
                At,
                causedBy: IncomingMessage),
        ]);
        var system = ActorSystem.Create($"ai-agent-audit-success-{Guid.NewGuid():N}");
        var published = new TaskCompletionSource<AiDirectiveAuditSnapshot>(
            TaskCreationOptions.RunContinuationsAsynchronously);

        try
        {
            var subscriber = system.ActorOf(
                Props.Create(() => new AuditSubscriber(published)),
                "audit-subscriber");
            system.EventStream.Subscribe(subscriber, typeof(AiDirectiveAuditSnapshot));
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            actor.Tell(request);

            var stored = await WaitForAuditAsync(actor, request.CorrelationId);
            var emitted = await published.Task.WaitAsync(Timeout());
            var snapshot = stored.Snapshot!;

            Assert.True(stored.Found);
            Assert.Equal(request.CorrelationId, emitted.CorrelationId);
            Assert.Equal(request.CorrelationId, snapshot.CorrelationId);
            Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshot.Status);
            Assert.Equal("result-emitted", snapshot.TerminalCode);
            Assert.Equal(Organization, snapshot.Context.OrganizationId);
            Assert.Equal(Position, snapshot.Context.PositionId);
            Assert.Equal(request.Occupant, snapshot.Context.Occupant);
            Assert.Equal(Thread, snapshot.Context.ThreadId);
            Assert.Equal(IncomingDirective, snapshot.Context.DirectiveId);
            Assert.Equal(IncomingMessage, snapshot.Context.MessageId);
            Assert.Equal("triage-v1", snapshot.Context.IdentityPromptRef);
            Assert.Equal("prompts/triage-v1.md", snapshot.Context.IdentityPromptPath);
            Assert.Equal(1, snapshot.Context.ShortMemoryCount);
            Assert.Equal(1, snapshot.Context.OpenTaskCount);
            Assert.Equal(1, snapshot.Context.RecentHistoryCount);
            var tool = Assert.Single(snapshot.Context.AuthorizedTools);
            Assert.Equal("jira", tool.Connector);
            Assert.Equal(2, tool.ScopeCount);

            Assert.True(snapshot.Gateway.WasRequested);
            Assert.Equal("stub", snapshot.Gateway.ProviderId);
            Assert.Equal("triage", snapshot.Gateway.ModelId);
            Assert.Equal(AiProcessingMode.Batch, snapshot.Gateway.ProcessingMode);
            Assert.Equal(256, snapshot.Gateway.MaxOutputTokens);
            Assert.Equal(TimeSpan.FromSeconds(15), snapshot.Gateway.Timeout);
            Assert.Equal(AiGatewayCallResult.Succeeded, snapshot.Gateway.Result);
            Assert.Equal(AiFinishReason.Stop, snapshot.Gateway.FinishReason);

            Assert.Equal(AiDirectiveInterpretationOutcomeKind.DecisionAccepted, snapshot.Decision!.Outcome);
            Assert.Equal("Report", snapshot.Decision.DecisionKind);
            Assert.Equal("Report", snapshot.ResultMessage!.MessageType);
            Assert.Equal("UpdateShortMemory", snapshot.PositionEffects!.CommandTypes[0]);
            Assert.Equal("CompleteTask", snapshot.PositionEffects.CommandTypes[1]);
            Assert.True(snapshot.IterationAudit!.IsTerminal);
            Assert.Equal("completed", snapshot.IterationAudit.TerminalCode);

            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "context.directive.objective");
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "context.directive.context");
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "gateway.request.content");
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "gateway.response.text");
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "resultMessage.report.body");
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "positionEffects.commands[0].value");
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "positionEffects.commands[1].summary");

            var debugText = snapshot.ToString();
            Assert.DoesNotContain("Triage checkout regression", debugText, StringComparison.Ordinal);
            Assert.DoesNotContain("Customer reports checkout failures", debugText, StringComparison.Ordinal);
            Assert.DoesNotContain("user@example.com", debugText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret=abc123", debugText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("Bug triage is complete", debugText, StringComparison.Ordinal);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_records_journey_audit_for_decision_and_result_message()
    {
        var request = Request();
        var auditLog = new RecordingJourneyAuditLog();
        var system = ActorSystem.Create($"ai-agent-journey-audit-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()),
                    AiDirectiveResultMessageEmissionGate.Instance,
                    auditLog)),
                "agent");

            actor.Tell(request);

            await WaitForAuditAsync(actor, request.CorrelationId);

            Assert.Equal(
                [JourneyAuditStage.AgentDecided, JourneyAuditStage.ResultMessageCreated],
                auditLog.Records.Select(record => record.Stage));
            Assert.All(auditLog.Records, record =>
            {
                Assert.Equal(JourneyAuditOutcome.Succeeded, record.Outcome);
                Assert.Equal(Organization, record.OrganizationId);
                Assert.Equal(Position, record.PositionId);
                Assert.Equal(Thread, record.ThreadId);
                Assert.Equal(IncomingDirective, record.DirectiveId);
                Assert.Equal(IncomingMessage, record.MessageId);
            });
            Assert.Equal("Report", auditLog.Records[0].Payload["decisionKind"]);
            Assert.Equal("Report", auditLog.Records[1].MessageType);
            Assert.Equal("Report", auditLog.Records[1].Payload["resultMessageType"]);
            var payloadText = string.Join(" ", auditLog.Records.SelectMany(record => record.Payload.Values));
            Assert.Contains("resultMessage.report.body", payloadText, StringComparison.Ordinal);
            Assert.DoesNotContain("Bug triage is complete", payloadText, StringComparison.Ordinal);
            Assert.DoesNotContain("user@example.com", payloadText, StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret=abc123", payloadText, StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_records_invalid_output_audit_without_result_message_or_effects()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-audit-invalid-output-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker("{"))),
                "agent");

            actor.Tell(request);

            var stored = await WaitForAuditAsync(actor, request.CorrelationId);
            var snapshot = stored.Snapshot!;

            Assert.Equal(AiDirectiveProcessingStatus.Escalated, snapshot.Status);
            Assert.Equal("ai-output-invalid", snapshot.TerminalCode);
            Assert.Equal(AiGatewayCallResult.Succeeded, snapshot.Gateway.Result);
            Assert.Equal(AiDirectiveInterpretationOutcomeKind.EscalationRequired, snapshot.Decision!.Outcome);
            Assert.Equal("ai-output-invalid", snapshot.Decision.FailureCode);
            Assert.Equal(1, snapshot.Decision.ParseErrorCount);
            Assert.Null(snapshot.ResultMessage);
            Assert.Null(snapshot.PositionEffects);
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "gateway.response.text");
            Assert.DoesNotContain("Customer reports checkout failures", snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_records_gateway_failure_audit_without_position_effects()
    {
        var request = Request();
        var gatewayError = new AiGatewayError(
            Organization,
            Position,
            Thread,
            IncomingMessage,
            AiGatewayErrorCode.ProviderUnavailable,
            "Provider unavailable for user@example.com with secret=abc123.",
            isRetryable: true,
            new AiProviderMetadata("stub", "triage"));
        var system = ActorSystem.Create($"ai-agent-audit-gateway-failure-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new FailureResponseInvoker(gatewayError))),
                "agent");

            actor.Tell(request);

            var stored = await WaitForAuditAsync(actor, request.CorrelationId);
            var snapshot = stored.Snapshot!;

            Assert.Equal(AiDirectiveProcessingStatus.Failed, snapshot.Status);
            Assert.Equal("ai-gateway-failure", snapshot.TerminalCode);
            Assert.Equal(AiGatewayCallResult.Failed, snapshot.Gateway.Result);
            Assert.Equal("provider-unavailable", snapshot.Gateway.ErrorCode);
            Assert.Equal(AiDirectiveInterpretationOutcomeKind.StructuredError, snapshot.Decision!.Outcome);
            Assert.Equal("ai-gateway-failure", snapshot.Decision.FailureCode);
            Assert.True(snapshot.Decision.IsRetryable);
            Assert.Null(snapshot.ResultMessage);
            Assert.Null(snapshot.PositionEffects);
            Assert.Contains(
                snapshot.Redactions,
                redaction => redaction.Path == "gateway.error.message");
            Assert.DoesNotContain("user@example.com", snapshot.ToString(), StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain("secret=abc123", snapshot.ToString(), StringComparison.OrdinalIgnoreCase);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_records_identity_prompt_failure_before_gateway_request()
    {
        var request = Request(includeIdentityPrompt: false);
        var invoker = new ThrowingInvoker();
        var system = ActorSystem.Create($"ai-agent-audit-identity-prompt-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(request.Occupant, invoker)),
                "agent");

            actor.Tell(request);

            var stored = await WaitForAuditAsync(actor, request.CorrelationId);
            var snapshot = stored.Snapshot!;

            Assert.Equal(AiDirectiveProcessingStatus.Failed, snapshot.Status);
            Assert.Equal("identity-prompt-unresolved", snapshot.TerminalCode);
            Assert.False(snapshot.Gateway.WasRequested);
            Assert.Null(snapshot.Decision);
            Assert.Null(snapshot.ResultMessage);
            Assert.Null(snapshot.PositionEffects);
            Assert.Equal(0, invoker.CallCount);
            Assert.DoesNotContain("Customer reports checkout failures", snapshot.ToString(), StringComparison.Ordinal);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_returns_missing_audit_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-audit-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-12"),
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            var result = await actor.Ask<AiDirectiveAuditSnapshotQueryResult>(
                new GetAiDirectiveAuditSnapshot("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Snapshot);
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
        IEnumerable<PersistedTask>? openTasks = null,
        bool includeIdentityPrompt = true)
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
            objective: "Triage checkout regression for user@example.com",
            context: "Customer reports checkout failures with secret=abc123.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(18, "sha256:t12"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: Superior,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: includeIdentityPrompt ? "triage-v1" : null,
                tools: [new ToolConfiguration("jira", ["issues/read", "issues/comment"])],
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15),
                    processingMode: AiProcessingMode.Batch,
                    maxIterations: 3),
                identityPrompt: includeIdentityPrompt
                    ? new IdentityPromptRuntimeConfiguration(
                        "triage-v1",
                        "prompts/triage-v1.md",
                        "You are responsible for triaging incoming bugs.")
                    : null),
            new PositionAuthorityRuntimeConfiguration(canDecide: ["bug.triage"]));
        var state = PositionState.Restore(new PositionSnapshot(
            At,
            openTasks: openTasks ?? [],
            shortMemory: new Dictionary<string, string>
            {
                ["last-contact"] = "Customer user@example.com reported secret=abc123.",
            },
            recentHistory:
            [
                MessageId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000001200")),
            ]));

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            state,
            OccupantId.From("agent-12"),
            directive);
    }

    private static string ValidReportOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Done",
            "body": "Bug triage is complete for user@example.com with secret=abc123."
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

    private sealed class FailureResponseInvoker(AiGatewayError error) : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Failed(error)));
    }

    private sealed class ThrowingInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            throw new InvalidOperationException("Gateway must not be invoked.");
        }
    }

    private sealed class AuditSubscriber : ReceiveActor
    {
        public AuditSubscriber(TaskCompletionSource<AiDirectiveAuditSnapshot> published)
        {
            Receive<AiDirectiveAuditSnapshot>(snapshot => published.TrySetResult(snapshot));
        }
    }

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records => _records;

        public void Append(JourneyAuditRecord record)
        {
            _records.Add(record);
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) =>
            _records
                .Where(record => record.ThreadId == threadId &&
                    (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }
}
