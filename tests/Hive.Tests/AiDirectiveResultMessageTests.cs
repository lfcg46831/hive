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

public sealed class AiDirectiveResultMessageTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 3, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly PositionId Engineer = PositionId.From("engineer");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000908"));
    private static readonly MessageId IncomingMessage =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000908"));
    private static readonly DirectiveId IncomingDirective =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000908"));

    [Fact]
    public void Create_materializes_report_decision_as_canonical_report()
    {
        var context = AiDirectiveExecutionContext.From(Request());
        var messageId = MessageId.From(Guid.Parse("10000000-0000-0000-0000-000000000908"));

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(ReportKind.Done, "Bug triage is complete."),
            () => messageId,
            DirectiveId.New,
            () => At.AddMinutes(5));

        Assert.True(result.IsSuccess, result.Failure?.AuditReason);
        var report = Assert.IsType<Report>(result.Message);
        Assert.Equal(context.CorrelationId, result.CorrelationId);
        Assert.Equal(messageId, report.Id);
        Assert.Equal(Organization, report.OrganizationId);
        Assert.Equal(new PositionEndpointRef(Position), report.From);
        Assert.Equal(new PositionEndpointRef(Superior), report.To);
        Assert.Equal(Thread, report.Thread);
        Assert.Equal(Priority.High, report.Priority);
        Assert.Equal(1, report.SchemaVersion);
        Assert.Equal(At.AddMinutes(5), report.SentAt);
        Assert.Equal(context.Directive.Deadline, report.Deadline);
        Assert.Equal(IncomingDirective, report.AboutDirectiveId);
        Assert.Equal(ReportKind.Done, report.Kind);
        Assert.Equal("Bug triage is complete.", report.Body);
        Assert.Equal(ActingUnderDeclarationState.Missing, result.ActingUnder.State);
    }

    [Fact]
    public void Create_keeps_acting_under_on_candidate_without_changing_org_message()
    {
        var context = AiDirectiveExecutionContext.From(Request());
        var actingUnder = ActingUnderDeclaration.Declared(
            AuthorityKey.From("delivery.bug-triage"));

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(
                ReportKind.Done,
                "Bug triage is complete.",
                actingUnder),
            clock: () => At.AddMinutes(5));

        Assert.True(result.IsSuccess, result.Failure?.AuditReason);
        Assert.Same(actingUnder, result.ActingUnder);
        var report = Assert.IsType<Report>(result.Message);
        Assert.Null(typeof(OrgMessage).GetProperty("ActingUnder"));
        Assert.Null(report.GetType().GetProperty("ActingUnder"));
    }

    [Fact]
    public void Create_uses_deterministic_report_message_id_for_same_directive()
    {
        var context = AiDirectiveExecutionContext.From(Request());

        var first = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(ReportKind.Done, "Bug triage is complete."),
            clock: () => At.AddMinutes(5));
        var second = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(ReportKind.Done, "Bug triage is complete."),
            clock: () => At.AddMinutes(6));

        var firstReport = Assert.IsType<Report>(first.Message);
        var secondReport = Assert.IsType<Report>(second.Message);
        Assert.Equal(firstReport.Id, secondReport.Id);
    }

    [Fact]
    public void Create_materializes_escalation_decision_to_direct_superior()
    {
        var context = AiDirectiveExecutionContext.From(Request());
        var messageId = MessageId.From(Guid.Parse("20000000-0000-0000-0000-000000000908"));

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveEscalationDecision(
                "Need product decision.",
                "The directive asks for production release approval.",
                ["Ask release owner", "Wait for approval policy"]),
            () => messageId,
            DirectiveId.New,
            () => At.AddMinutes(6));

        Assert.True(result.IsSuccess, result.Failure?.AuditReason);
        var escalation = Assert.IsType<Escalation>(result.Message);
        Assert.Equal(messageId, escalation.Id);
        Assert.Equal(Organization, escalation.OrganizationId);
        Assert.Equal(new PositionEndpointRef(Position), escalation.From);
        Assert.Equal(new PositionEndpointRef(Superior), escalation.To);
        Assert.Equal(Thread, escalation.Thread);
        Assert.Equal(Priority.High, escalation.Priority);
        Assert.Equal(At.AddMinutes(6), escalation.SentAt);
        Assert.Equal(context.Directive.Deadline, escalation.Deadline);
        Assert.Equal("Need product decision.", escalation.Issue);
        Assert.Equal("The directive asks for production release approval.", escalation.Context);
        Assert.Equal(["Ask release owner", "Wait for approval policy"], escalation.OptionsConsidered);
    }

    [Fact]
    public void Create_materializes_root_escalation_to_organization_owner()
    {
        var context = AiDirectiveExecutionContext.From(Request(hasSuperior: false));
        var messageId = MessageId.From(Guid.Parse("21000000-0000-0000-0000-000000000908"));

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveEscalationDecision(
                "Need owner decision.",
                "The root leadership cannot decide this safely.",
                ["Escalate to owner"]),
            () => messageId,
            DirectiveId.New,
            () => At.AddMinutes(6));

        Assert.True(result.IsSuccess, result.Failure?.AuditReason);
        var escalation = Assert.IsType<Escalation>(result.Message);
        Assert.Equal(messageId, escalation.Id);
        Assert.Equal(new PositionEndpointRef(Position), escalation.From);
        Assert.IsType<OrganizationOwnerEndpointRef>(escalation.To);
        Assert.Equal("Need owner decision.", escalation.Issue);
        Assert.Equal(["Escalate to owner"], escalation.OptionsConsidered);
    }

    [Fact]
    public void Create_materializes_child_directive_only_for_permitted_direct_subordinate()
    {
        var context = AiDirectiveExecutionContext.From(Request(directSubordinates: [Engineer]));
        var messageId = MessageId.From(Guid.Parse("30000000-0000-0000-0000-000000000908"));
        var directiveId = DirectiveId.From(Guid.Parse("40000000-0000-0000-0000-000000000908"));

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveChildDirectiveDecision(
                Engineer,
                "Investigate checkout regression.",
                "Focus on payment callback failures."),
            () => messageId,
            () => directiveId,
            () => At.AddMinutes(7));

        Assert.True(result.IsSuccess, result.Failure?.AuditReason);
        var directive = Assert.IsType<OrgDirective>(result.Message);
        Assert.Equal(messageId, directive.Id);
        Assert.Equal(Organization, directive.OrganizationId);
        Assert.Equal(new PositionEndpointRef(Position), directive.From);
        Assert.Equal(new PositionEndpointRef(Engineer), directive.To);
        Assert.Equal(Thread, directive.Thread);
        Assert.Equal(Priority.High, directive.Priority);
        Assert.Equal(At.AddMinutes(7), directive.SentAt);
        Assert.Equal(context.Directive.Deadline, directive.Deadline);
        Assert.Equal(directiveId, directive.DirectiveId);
        Assert.Equal(IncomingDirective, directive.ParentDirectiveId);
        Assert.Equal("Investigate checkout regression.", directive.Objective);
        Assert.Equal("Focus on payment callback failures.", directive.Context);
    }

    [Fact]
    public void Create_uses_deterministic_child_directive_ids_for_same_directive_and_target()
    {
        var context = AiDirectiveExecutionContext.From(Request(directSubordinates: [Engineer]));

        var first = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveChildDirectiveDecision(
                Engineer,
                "Investigate checkout regression.",
                "Focus on payment callback failures."),
            clock: () => At.AddMinutes(7));
        var second = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveChildDirectiveDecision(
                Engineer,
                "Investigate checkout regression.",
                "Focus on payment callback failures."),
            clock: () => At.AddMinutes(8));

        var firstDirective = Assert.IsType<OrgDirective>(first.Message);
        var secondDirective = Assert.IsType<OrgDirective>(second.Message);
        Assert.Equal(firstDirective.Id, secondDirective.Id);
        Assert.Equal(firstDirective.DirectiveId, secondDirective.DirectiveId);
    }

    [Fact]
    public void Create_rejects_child_directive_when_target_is_not_permitted_subordinate()
    {
        var context = AiDirectiveExecutionContext.From(Request(directSubordinates: [Engineer]));
        var qa = PositionId.From("qa");

        var result = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveChildDirectiveDecision(
                qa,
                "Investigate checkout regression.",
                "Focus on payment callback failures."),
            MessageId.New,
            DirectiveId.New,
            () => At.AddMinutes(8));

        Assert.False(result.IsSuccess);
        Assert.Null(result.Message);
        var failure = Assert.IsType<AiDirectiveResultMessageFailure>(result.Failure);
        Assert.Equal("child-directive-target-not-permitted", failure.Code);
        Assert.Contains("qa", failure.AuditReason, StringComparison.Ordinal);
        Assert.Equal(ActingUnderDeclarationState.Missing, result.ActingUnder.State);
    }

    [Fact]
    public async Task Emission_gate_allows_materialized_report_after_routing_validation()
    {
        var context = AiDirectiveExecutionContext.From(Request());
        var messageId = MessageId.From(Guid.Parse("50000000-0000-0000-0000-000000000908"));
        var materialized = AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveReportDecision(ReportKind.Progress, "Bug triage is in progress."),
            () => messageId,
            DirectiveId.New,
            () => At.AddMinutes(9));

        var verdict = await AiDirectiveResultMessageEmissionGate.Instance.ValidateAsync(
            context,
            materialized.Message!);

        Assert.True(verdict.IsAllowed, verdict.Failure?.AuditReason);
        Assert.Null(verdict.Failure);
    }

    [Fact]
    public async Task Emission_gate_rejects_result_message_to_unknown_route_before_emission()
    {
        var context = AiDirectiveExecutionContext.From(Request());
        var ceo = PositionId.From("ceo");
        var report = new Report(
            MessageId.From(Guid.Parse("51000000-0000-0000-0000-000000000908")),
            Organization,
            new PositionEndpointRef(Position),
            new PositionEndpointRef(ceo),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At.AddMinutes(10),
            deadline: context.Directive.Deadline,
            IncomingDirective,
            ReportKind.Progress,
            "Skipping the direct superior must not be emitted.");

        var verdict = await AiDirectiveResultMessageEmissionGate.Instance.ValidateAsync(
            context,
            report);

        Assert.False(verdict.IsAllowed);
        var failure = Assert.IsType<AiDirectiveResultMessageFailure>(verdict.Failure);
        Assert.Equal("routing-rejected", failure.Code);
        var rejection = Assert.IsType<RoutingRejection>(failure.RoutingRejection);
        Assert.Equal(
            [new ValidationError("invalid-route", "$", RejectionReason.InvalidRoute)],
            rejection.PublicResult.Errors);
        Assert.Null(verdict.Message);
    }

    [Fact]
    public async Task Emission_gate_rejects_implicit_approval_request_before_admission()
    {
        var context = AiDirectiveExecutionContext.From(Request());
        var approval = new ApprovalRequest(
            MessageId.From(Guid.Parse("52000000-0000-0000-0000-000000000908")),
            Organization,
            new PositionEndpointRef(Position),
            new PositionEndpointRef(Superior),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At.AddMinutes(11),
            deadline: context.Directive.Deadline,
            action: "approve-result",
            justification: "The AI agent cannot imply approval for its own result.",
            ApprovalPolicyRef.From("requires-human-approval"));

        var verdict = await AiDirectiveResultMessageEmissionGate.Instance.ValidateAsync(
            context,
            approval);

        Assert.False(verdict.IsAllowed);
        var failure = Assert.IsType<AiDirectiveResultMessageFailure>(verdict.Failure);
        Assert.Equal("implicit-approval-not-authorized", failure.Code);
        Assert.Null(failure.RoutingRejection);
        Assert.Null(verdict.Message);
    }

    [Fact]
    public async Task AiAgentActor_stores_emitted_result_message_and_advances_to_result_emitted()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-result-message-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            actor.Tell(request);

            var result = await WaitForResultMessageAsync(actor, request.CorrelationId);
            var snapshot = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                Timeout());

            Assert.True(result.Found);
            Assert.Equal(request.CorrelationId, result.CorrelationId);
            var report = Assert.IsType<Report>(result.Result!.Message);
            Assert.Equal(request.DirectiveId, report.AboutDirectiveId);
            Assert.Equal(new PositionEndpointRef(request.PositionId), report.From);
            Assert.Equal(new PositionEndpointRef(Superior), report.To);
            Assert.True(snapshot.Found);
            Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshot.Snapshot!.Status);
            Assert.Equal(
                [
                    AiDirectiveProcessingStatus.Received,
                    AiDirectiveProcessingStatus.ContextAssembled,
                    AiDirectiveProcessingStatus.GatewayRequested,
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    AiDirectiveProcessingStatus.ResultEmitted,
                ],
                snapshot.Snapshot.History.Select(transition => transition.Status).ToArray());
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_stores_gate_failure_and_advances_to_escalated()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-result-gate-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()),
                    new RejectingResultMessageGate())),
                "agent");

            actor.Tell(request);

            var result = await WaitForResultMessageAsync(actor, request.CorrelationId);
            var snapshot = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                Timeout());

            Assert.True(result.Found);
            Assert.False(result.Result!.IsSuccess);
            Assert.Null(result.Result.Message);
            Assert.Equal("routing-rejected", result.Result.Failure!.Code);
            Assert.True(snapshot.Found);
            Assert.Equal(AiDirectiveProcessingStatus.Escalated, snapshot.Snapshot!.Status);
            Assert.Equal("Routing gate rejected the candidate result message.", snapshot.Snapshot.TerminalReason);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static async Task<AiDirectiveResultMessageQueryResult> WaitForResultMessageAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive result message was not recorded.");
    }

    private static AiDirectiveProcessingRequest Request(
        IEnumerable<PositionId>? directSubordinates = null,
        bool hasSuperior = true)
    {
        var entity = PositionEntityId.From(Organization, Position);
        var occupant = OccupantId.From("agent-8");
        var reportsTo = hasSuperior ? Superior : null;
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
            new PositionConfigurationStamp(13, "sha256:t08"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: reportsTo,
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

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            occupant,
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

    private sealed class RejectingResultMessageGate : IAiDirectiveResultMessageGate
    {
        public ValueTask<AiDirectiveResultMessageGateResult> ValidateAsync(
            AiDirectiveExecutionContext context,
            OrgMessage message,
            CancellationToken cancellationToken = default)
        {
            ArgumentNullException.ThrowIfNull(context);
            ArgumentNullException.ThrowIfNull(message);

            return new ValueTask<AiDirectiveResultMessageGateResult>(
                AiDirectiveResultMessageGateResult.Rejected(
                    new AiDirectiveResultMessageFailure(
                        "routing-rejected",
                        "Routing gate rejected the candidate result message.")));
        }
    }
}
