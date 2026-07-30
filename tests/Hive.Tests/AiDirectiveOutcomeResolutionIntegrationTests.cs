using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveOutcomeResolutionIntegrationTests
{
    private static readonly DateTimeOffset At =
        new(2030, 7, 20, 12, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly PositionId FollowUpPosition =
        PositionId.From("follow-up-coordination");
    private static readonly PositionId Superior = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000001317"));
    private static readonly MessageId IncomingMessage =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000001317"));
    private static readonly DirectiveId IncomingDirective =
        DirectiveId.From(Guid.Parse("dddddddd-0000-0000-0000-000000001317"));
    private const string FollowUpIdentityPrompt =
        "Coordinate operational follow-up using only the supplied business context.";

    [Fact]
    public async Task Shadow_audits_policy_override_but_preserves_the_proposed_message()
    {
        var audit = new RecordingJourneyAuditLog();
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Shadow,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()),
            clockAt: At.AddSeconds(5));
        var input = Input(new AiDirectiveReportDecision(ReportKind.Done, "Complete."));

        var result = await ResolveAsync(integrator, input);

        Assert.IsType<Report>(result.ResultMessage!.Message);
        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.True(result.Resolution.ProposalOverridden);
        Assert.True(result.Resolution.VerifierInvoked);
        var record = Assert.Single(audit.Records);
        Assert.Equal(JourneyAuditStage.OutcomeResolved, record.Stage);
        Assert.Equal("shadow", record.Payload["mode"]);
        Assert.Equal("Report.Done", record.Payload["proposedIntent"]);
        Assert.Equal("Escalation", record.Payload["resolvedOutcome"]);
        Assert.Equal("Unavailable", record.Payload["verifierStatus"]);
        Assert.Equal("true", record.Payload["semanticCompletionCandidate"]);
        Assert.Equal("0", record.Payload["semanticCompletionIneligibilityReasonCount"]);
        Assert.Equal("10000", record.Payload["deadlineRemainingMilliseconds"]);
        Assert.False(record.Payload.ContainsKey("verifierClassification"));
        Assert.DoesNotContain("Complete.", string.Join('|', record.Payload.Values));
        Assert.Equal(
            "prompt,chain-of-thought,provider-output,rejected-values,next-action,verification-artifact,evidence-references",
            record.Payload["redactions"]);
    }

    [Fact]
    public async Task Audit_records_only_closed_semantic_ineligibility_reasons()
    {
        var audit = new RecordingJourneyAuditLog();
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            new StaticVerifier(
                OutcomeVerifierResult.Classified(
                    OutcomeVerifierClassification.Undetermined)));
        var input = Input(new AiDirectiveReportDecision(ReportKind.Done, "Complete.")) with
        {
            Proposal = new OutcomeProposal(
                OutcomeProposedIntent.ReportDone,
                OutcomeWorkState.Completed,
                OutcomeRequiredIntervention.None,
                blockers: [],
                nextAction: null,
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.RuntimeFact,
                    "model-proposal:report-done")]),
        };

        var result = await ResolveAsync(integrator, input);

        Assert.False(result.Resolution!.SemanticCompletionCandidate);
        var record = Assert.Single(audit.Records);
        Assert.Equal("2", record.Payload["semanticCompletionIneligibilityReasonCount"]);
        Assert.Equal(
            "evidence-source-not-directive-input",
            record.Payload["semanticCompletionIneligibilityReason.0"]);
        Assert.Equal(
            "evidence-reference-not-in-context",
            record.Payload["semanticCompletionIneligibilityReason.1"]);
        Assert.DoesNotContain(
            "model-proposal:report-done",
            string.Join('|', record.Payload.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task Audit_clamps_exhausted_deadline_remaining_time_to_zero()
    {
        var audit = new RecordingJourneyAuditLog();
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()));
        var input = Input(
            new AiDirectiveReportDecision(ReportKind.Done, "Complete."),
            deadline: At.AddSeconds(30));

        await ResolveAsync(integrator, input);

        Assert.Equal(
            "0",
            Assert.Single(audit.Records).Payload["deadlineRemainingMilliseconds"]);
    }

    [Fact]
    public async Task Audit_omits_remaining_time_when_no_directive_or_processing_deadline_exists()
    {
        var audit = new RecordingJourneyAuditLog();
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()));
        var input = Input(
            new AiDirectiveReportDecision(ReportKind.Done, "Complete."),
            withoutDeadline: true);

        await ResolveAsync(integrator, input);

        Assert.False(
            Assert.Single(audit.Records).Payload.ContainsKey(
                "deadlineRemainingMilliseconds"));
    }

    [Fact]
    public async Task Enforcement_materializes_fail_safe_escalation_before_final_gates()
    {
        var actingUnder = ActingUnderDeclaration.Declared(
            AuthorityKey.From("delivery.bug-triage"));
        var audit = new RecordingJourneyAuditLog();
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()));
        var input = Input(
            new AiDirectiveReportDecision(
                ReportKind.Done,
                "Complete.",
                actingUnder));

        var result = await ResolveAsync(integrator, input);

        var escalation = Assert.IsType<Escalation>(result.ResultMessage!.Message);
        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.Contains("verifier-unavailable", escalation.Context, StringComparison.Ordinal);
        Assert.Null(result.ActionGateResult);
        Assert.Null(result.RoutingGateResult);
        Assert.Empty(result.Diagnostics);
        Assert.DoesNotContain("Complete.", escalation.Context, StringComparison.Ordinal);
        Assert.Equal(actingUnder, result.ResultMessage.ActingUnder);
    }

    [Fact]
    public async Task Triage_006_done_report_closes_after_grounded_semantic_verification()
    {
        var verifier = new StaticVerifier(
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var audit = new RecordingJourneyAuditLog();
        var integrator = CreateIntegrator(OutcomeResolutionMode.Enforcement, audit, verifier);
        var input = Input(
            new AiDirectiveReportDecision(ReportKind.Done, "Triage completed."));

        var result = await ResolveAsync(integrator, input);

        var report = Assert.IsType<Report>(result.ResultMessage!.Message);
        Assert.Equal(ReportKind.Done, report.Kind);
        Assert.Equal(OutcomeKind.ReportDone, result.Resolution!.Outcome);
        Assert.Equal(
            [
                OutcomeResolutionReason.SemanticCompletionVerified,
                OutcomeResolutionReason.VerifierConfirmed,
            ],
            result.Resolution.Reasons);
        Assert.Equal(1, verifier.CallCount);
        var artifact = Assert.IsType<OutcomeVerificationArtifact>(
            verifier.LastRequest!.Artifact);
        Assert.Equal(OutcomeKind.ReportDone, artifact.Kind);
        Assert.Equal(
            "Triage completed.",
            Assert.Single(artifact.Entries).Value);
        Assert.DoesNotContain(
            artifact.Entries,
            entry => entry.Value.Contains("hive-evaluation-v1", StringComparison.Ordinal));
        var auditRecord = Assert.Single(audit.Records);
        Assert.Equal("Classified", auditRecord.Payload["verifierStatus"]);
        Assert.Equal("Report.Done", auditRecord.Payload["verifierClassification"]);
        Assert.Equal("true", auditRecord.Payload["semanticCompletionCandidate"]);
    }

    [Fact]
    public async Task Configured_verifier_timeout_is_capped_by_position_timeout()
    {
        var verifier = new StaticVerifier(
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            new RecordingJourneyAuditLog(),
            verifier,
            verifierTimeout: TimeSpan.FromSeconds(30));
        var input = Input(new AiDirectiveReportDecision(ReportKind.Done, "Complete."));

        await ResolveAsync(integrator, input);

        Assert.Equal(TimeSpan.FromSeconds(15), verifier.LastRequest!.Context.Timeout);
    }

    [Fact]
    public async Task Verifier_timeout_is_capped_by_remaining_directive_deadline()
    {
        var verifier = new StaticVerifier(
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            new RecordingJourneyAuditLog(),
            verifier,
            verifierTimeout: TimeSpan.FromSeconds(30));
        var input = Input(
            new AiDirectiveReportDecision(ReportKind.Done, "Complete."),
            deadline: At.AddSeconds(65));

        await ResolveAsync(integrator, input);

        Assert.Equal(TimeSpan.FromSeconds(5), verifier.LastRequest!.Context.Timeout);
    }

    [Fact]
    public async Task Enforcement_continues_unfinished_authorized_work_without_a_message()
    {
        var audit = new RecordingJourneyAuditLog();
        var verifier = new StaticVerifier(OutcomeVerifierResult.Unavailable());
        var integrator = CreateIntegrator(OutcomeResolutionMode.Enforcement, audit, verifier);
        var input = Input(new AiDirectiveReportDecision(ReportKind.Progress, "Partial."));

        var result = await ResolveAsync(integrator, input);

        Assert.True(result.ShouldContinue);
        Assert.Null(result.ResultMessage);
        Assert.Equal(OutcomeKind.ContinueWork, result.Resolution!.Outcome);
        Assert.Equal(0, verifier.CallCount);
        Assert.Equal("ContinueWork", Assert.Single(audit.Records).Payload["resolvedOutcome"]);
    }

    [Fact]
    public async Task Routing_rejection_is_an_authoritative_fact_that_cannot_open_a_report()
    {
        var audit = new RecordingJourneyAuditLog();
        var verifier = new StaticVerifier(OutcomeVerifierResult.Unavailable());
        var integrator = CreateIntegrator(OutcomeResolutionMode.Enforcement, audit, verifier);
        var input = Input(new AiDirectiveReportDecision(ReportKind.Progress, "Partial."));

        var result = await integrator.ResolveAsync(
            input.Context,
            input.Iteration,
            input.Decision,
            input.Proposal,
            input.ProposedMessage,
            input.Response,
            hasAvailableBudget: true,
            AllowingAiAgentActionGate.Instance,
            RejectingRoutingGate.Instance);

        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.Contains(OutcomeResolutionReason.RoutingUnavailable, result.Resolution.Reasons);
        Assert.IsType<Escalation>(result.ResultMessage!.Message);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Exhausted_prompt_budget_is_authoritative_and_fails_closed()
    {
        var audit = new RecordingJourneyAuditLog();
        var verifier = new StaticVerifier(OutcomeVerifierResult.Unavailable());
        var integrator = CreateIntegrator(OutcomeResolutionMode.Enforcement, audit, verifier);
        var input = Input(new AiDirectiveReportDecision(ReportKind.Progress, "Partial."));

        var result = await integrator.ResolveAsync(
            input.Context,
            input.Iteration,
            input.Decision,
            input.Proposal,
            input.ProposedMessage,
            input.Response,
            hasAvailableBudget: false,
            AllowingAiAgentActionGate.Instance,
            AiDirectiveResultMessageEmissionGate.Instance);

        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.Contains(OutcomeResolutionReason.BudgetExhausted, result.Resolution.Reasons);
        Assert.IsType<Escalation>(result.ResultMessage!.Message);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Expired_deadline_is_authoritative_and_fails_closed()
    {
        var audit = new RecordingJourneyAuditLog();
        var verifier = new StaticVerifier(OutcomeVerifierResult.Unavailable());
        var integrator = CreateIntegrator(OutcomeResolutionMode.Enforcement, audit, verifier);
        var input = Input(
            new AiDirectiveReportDecision(ReportKind.Progress, "Partial."),
            deadline: At.AddSeconds(30));

        var result = await ResolveAsync(integrator, input);

        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.Contains(OutcomeResolutionReason.DeadlineExceeded, result.Resolution.Reasons);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Human_approval_outcome_reuses_the_existing_governance_retention()
    {
        var audit = new RecordingJourneyAuditLog();
        var verifier = new StaticVerifier(OutcomeVerifierResult.Unavailable());
        var integrator = CreateIntegrator(OutcomeResolutionMode.Enforcement, audit, verifier);
        var input = Input(new AiDirectiveReportDecision(ReportKind.Progress, "Partial."));

        var result = await integrator.ResolveAsync(
            input.Context,
            input.Iteration,
            input.Decision,
            input.Proposal,
            input.ProposedMessage,
            input.Response,
            hasAvailableBudget: true,
            HumanApprovalActionGate.Instance,
            AiDirectiveResultMessageEmissionGate.Instance);

        Assert.Equal(OutcomeKind.ApprovalRequired, result.Resolution!.Outcome);
        Assert.Equal(
            AiAgentActionGateOutcome.RetainedForHumanApproval,
            result.ActionGateResult!.Outcome);
        Assert.IsType<Report>(result.ResultMessage!.Message);
        Assert.IsType<ApprovalRequest>(
            Assert.Single(result.ActionGateResult.Retention!.GovernanceMessages));
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Retry_limit_from_runtime_attempt_state_fails_closed()
    {
        var audit = new RecordingJourneyAuditLog();
        var verifier = new StaticVerifier(OutcomeVerifierResult.Unavailable());
        var policy = OutcomePolicyComposer.ComposeV1(
            1,
            "sha256:registry",
            organizationOverlay: null,
            positionOverlay: new OutcomePolicyOverlay(maximumRetries: 1));
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            verifier,
            policy);
        var input = Input(new AiDirectiveReportDecision(ReportKind.Progress, "Partial."));
        var retryableFailure = AiGatewayResponse.Failed(new AiGatewayError(
            Organization,
            Position,
            Thread,
            IncomingMessage,
            AiGatewayErrorCode.ProviderUnavailable,
            "Provider unavailable.",
            isRetryable: true));

        var result = await ResolveAsync(integrator, input with { Response = retryableFailure });

        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.Contains(OutcomeResolutionReason.RetryLimitReached, result.Resolution.Reasons);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Incompatible_policy_fails_closed_and_emits_only_closed_diagnostics()
    {
        var audit = new RecordingJourneyAuditLog();
        var incompatiblePolicy = new OutcomePolicySnapshot(
            "outcome-policy-v2/registry-1",
            "sha256:future",
            8,
            3,
            verifierEnabled: true);
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()),
            incompatiblePolicy);
        var input = Input(new AiDirectiveReportDecision(ReportKind.Done, "Complete."));

        var result = await ResolveAsync(integrator, input);

        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.Equal([OutcomeResolutionDiagnostic.PolicyIncompatible], result.Diagnostics);
        var record = Assert.Single(audit.Records);
        Assert.Equal("policy-incompatible", record.ReasonCode);
        Assert.Equal("policy-incompatible", record.Payload["diagnostic.0"]);
        Assert.DoesNotContain("future", record.Payload.Values.Where(value =>
            !string.Equals(value, "sha256:future", StringComparison.Ordinal)));
    }

    [Theory]
    [InlineData(OutcomeResolutionDiagnostic.FactsUnavailable)]
    [InlineData(OutcomeResolutionDiagnostic.PolicyUnavailable)]
    [InlineData(OutcomeResolutionDiagnostic.ResolutionUnavailable)]
    public async Task Boundary_failures_fail_closed_without_dynamic_diagnostics(
        OutcomeResolutionDiagnostic expectedDiagnostic)
    {
        var audit = new RecordingJourneyAuditLog();
        var policy = OutcomePolicyComposer.ComposeV1(
            1,
            "sha256:registry",
            organizationOverlay: null,
            positionOverlay: null);
        IExecutionFactsMaterializer factsMaterializer =
            expectedDiagnostic == OutcomeResolutionDiagnostic.FactsUnavailable
                ? new ThrowingFactsMaterializer()
                : new ExecutionFactsMaterializer();
        IOutcomePolicyProvider policyProvider =
            expectedDiagnostic == OutcomeResolutionDiagnostic.PolicyUnavailable
                ? new ThrowingPolicyProvider()
                : new StaticPolicyProvider(policy);
        IOrganizationalOutcomeOrchestrator orchestrator =
            expectedDiagnostic == OutcomeResolutionDiagnostic.ResolutionUnavailable
                ? new ThrowingOrchestrator()
                : new OrganizationalOutcomeOrchestrator(
                    new OrganizationalOutcomeResolver(),
                    new StaticVerifier(OutcomeVerifierResult.Unavailable()));
        var integrator = new AiDirectiveOutcomeResolutionIntegrator(
            factsMaterializer,
            policyProvider,
            orchestrator,
            audit,
            OutcomeResolutionMode.Enforcement,
            () => At.AddMinutes(1));
        var input = Input(new AiDirectiveReportDecision(ReportKind.Done, "Secret body."));

        var result = await ResolveAsync(integrator, input);

        Assert.Equal(OutcomeKind.Escalation, result.Resolution!.Outcome);
        Assert.Equal([expectedDiagnostic], result.Diagnostics);
        var record = Assert.Single(audit.Records);
        Assert.Equal(
            OutcomeResolutionDiagnosticContract.ToWireValue(expectedDiagnostic),
            record.ReasonCode);
        Assert.Equal(JourneyAuditOutcome.Succeeded, record.Outcome);
        Assert.DoesNotContain("Secret body.", string.Join('|', record.Payload.Values));
    }

    [Fact]
    public async Task Audit_identity_is_stable_for_the_same_correlation_and_iteration()
    {
        var audit = new RecordingJourneyAuditLog();
        var compatibleIntegrator = CreateIntegrator(
            OutcomeResolutionMode.Shadow,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()));
        var incompatibleIntegrator = CreateIntegrator(
            OutcomeResolutionMode.Shadow,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()),
            new OutcomePolicySnapshot(
                "outcome-policy-v2/registry-1",
                "sha256:future",
                8,
                3,
                verifierEnabled: true));
        var input = Input(new AiDirectiveReportDecision(ReportKind.Done, "Complete."));

        await ResolveAsync(compatibleIntegrator, input);
        await ResolveAsync(incompatibleIntegrator, input);

        Assert.Equal(2, audit.Records.Count);
        Assert.NotEqual(audit.Records[0].ReasonCode, audit.Records[1].ReasonCode);
        Assert.Equal(audit.Records[0].AuditEventId, audit.Records[1].AuditEventId);
    }

    [Fact]
    [Trait(
        DirectiveExecutionCharacterization.CategoryTrait,
        DirectiveExecutionCharacterization.Category)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.Outcomes)]
    public async Task Ai_agent_enforcement_emits_only_the_resolved_message_through_the_existing_flow()
    {
        var request = Request();
        var audit = new RecordingJourneyAuditLog();
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()));
        var system = ActorSystem.Create($"outcome-enforcement-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(),
                    AiDirectiveResultMessageEmissionGate.Instance,
                    AllowingAiAgentActionGate.Instance,
                    audit,
                    NoopDirectiveAuditExportStore.Instance,
                    integrator)),
                "agent");

            actor.Tell(request);

            var message = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            var resolution = await actor.Ask<AiDirectiveOutcomeResolutionQueryResult>(
                new GetAiDirectiveOutcomeResolution(request.CorrelationId),
                TimeSpan.FromSeconds(10));

            Assert.True(message.Found);
            Assert.IsType<Escalation>(message.Result!.Message);
            Assert.True(resolution.Found);
            Assert.Equal(OutcomeKind.Escalation, resolution.Result!.Resolution!.Outcome);
            Assert.Contains(
                audit.Records,
                record => record.Stage == JourneyAuditStage.OutcomeResolved);
            Assert.DoesNotContain(
                audit.Records,
                record => record.Stage == JourneyAuditStage.ResultMessageCreated &&
                    record.MessageType == nameof(Report));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    [Trait(
        DirectiveExecutionCharacterization.CategoryTrait,
        DirectiveExecutionCharacterization.Category)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.Iterations)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.Outcomes)]
    public async Task Ai_agent_continue_work_runs_the_next_inference_before_emitting_a_message()
    {
        var request = Request();
        var audit = new RecordingJourneyAuditLog();
        var invoker = new SequencedResponseInvoker();
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            new StaticVerifier(OutcomeVerifierResult.Unavailable()));
        var system = ActorSystem.Create($"outcome-continue-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    invoker,
                    AiDirectiveResultMessageEmissionGate.Instance,
                    AllowingAiAgentActionGate.Instance,
                    audit,
                    NoopDirectiveAuditExportStore.Instance,
                    integrator)),
                "agent");

            actor.Tell(request);

            var message = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            var iterationAudit = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));

            Assert.Equal(2, invoker.CallCount);
            Assert.True(message.Found);
            Assert.IsType<Escalation>(message.Result!.Message);
            Assert.True(iterationAudit.Found);
            Assert.Equal([1, 1, 2], iterationAudit.Snapshot!.Entries.Select(entry =>
                entry.Iteration));
            Assert.Equal(
                ["continue", "inference-succeeded", "completed"],
                iterationAudit.Snapshot.Entries.Select(entry => entry.Code));
            var outcomeRecords = audit.Records
                .Where(record => record.Stage == JourneyAuditStage.OutcomeResolved)
                .ToArray();
            Assert.Equal(2, outcomeRecords.Length);
            Assert.Equal(["1", "2"], outcomeRecords.Select(record =>
                record.Payload["iteration"]));
            Assert.Equal("ContinueWork", outcomeRecords[0].Payload["resolvedOutcome"]);
            Assert.Equal("Escalation", outcomeRecords[1].Payload["resolvedOutcome"]);
            Assert.DoesNotContain(
                audit.Records,
                record => record.Stage == JourneyAuditStage.ResultMessageCreated &&
                    record.MessageType == nameof(Report));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Ai_agent_requests_one_bounded_correction_before_verifying_grounded_evidence()
    {
        var request = Request(
            positionId: FollowUpPosition,
            identityPromptRef: "follow-up-coordination-v1",
            identityPrompt: FollowUpIdentityPrompt,
            positionName: "Follow-up Coordinator",
            objective: "Coordinate the next operational follow-up",
            context: "The owner and requested response window are supplied.");
        var audit = new RecordingJourneyAuditLog();
        var invoker = new EvidenceCorrectionInvoker(correctionSucceeds: true);
        var verifier = new StaticVerifier(
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            verifier);
        var system = ActorSystem.Create($"outcome-evidence-correction-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    invoker,
                    AiDirectiveResultMessageEmissionGate.Instance,
                    AllowingAiAgentActionGate.Instance,
                    audit,
                    NoopDirectiveAuditExportStore.Instance,
                    integrator)),
                "agent");

            actor.Tell(request);

            var message = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            var iterationAudit = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            var auditSnapshot = await actor.Ask<AiDirectiveAuditSnapshotQueryResult>(
                new GetAiDirectiveAuditSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));

            Assert.Equal(2, invoker.Requests.Count);
            Assert.Equal(1, verifier.CallCount);
            Assert.True(message.Found);
            Assert.True(auditSnapshot.Found);
            Assert.Equal(ReportKind.Done, Assert.IsType<Report>(message.Result!.Message).Kind);
            Assert.Equal(
                ["continue", "inference-succeeded", "completed"],
                iterationAudit.Snapshot!.Entries.Select(entry => entry.Code));

            var initial = invoker.Requests[0];
            var correction = invoker.Requests[1];
            Assert.Equal(
                "outcome-proposal-evidence",
                correction.Metadata["hive.correction"]);
            Assert.Equal(initial.SystemInstruction, correction.SystemInstruction);
            Assert.Contains(
                FollowUpIdentityPrompt,
                correction.SystemInstruction,
                StringComparison.Ordinal);
            Assert.StartsWith(
                $"{initial.Content}{Environment.NewLine}{Environment.NewLine}",
                correction.Content,
                StringComparison.Ordinal);
            Assert.Contains(
                "outcome_proposal.proposal.evidence_references.item.source:invalid-vocabulary",
                correction.Content,
                StringComparison.Ordinal);
            Assert.Contains("\"directive.context\"", correction.Content, StringComparison.Ordinal);
            Assert.Contains("\"directive.objective\"", correction.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("runtime.fabricated", correction.Content, StringComparison.Ordinal);
            Assert.DoesNotContain(
                "runtime.fabricated",
                string.Join('|', audit.Records.SelectMany(record => record.Payload.Values)),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "runtime.fabricated",
                auditSnapshot.Snapshot!.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                auditSnapshot.Snapshot.Redactions,
                redaction => redaction.Path == "gateway.response.text");
            Assert.DoesNotContain(
                "bug-triage",
                correction.SystemInstruction + correction.Content,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "missing-information",
                correction.SystemInstruction + correction.Content,
                StringComparison.OrdinalIgnoreCase);
            Assert.DoesNotContain(
                "severity",
                correction.SystemInstruction + correction.Content,
                StringComparison.OrdinalIgnoreCase);

            var evidenceProperties = correction.OutputConstraint!.JsonSchema
                .GetProperty("properties")
                .GetProperty(AiDirectiveOutcomeProposalEnvelope.PropertyName)
                .GetProperty("properties")
                .GetProperty(OutcomeProposalConstraint.ProposalProperty)
                .GetProperty("anyOf")[0]
                .GetProperty("properties")
                .GetProperty(OutcomeProposalConstraint.EvidenceReferencesProperty)
                .GetProperty("items")
                .GetProperty("properties");
            Assert.Equal(
                ["DirectiveInput"],
                evidenceProperties.GetProperty(OutcomeProposalConstraint.EvidenceSourceProperty)
                    .GetProperty("enum")
                    .EnumerateArray()
                    .Select(item => item.GetString()));
            Assert.Equal(
                ["directive.context", "directive.objective"],
                evidenceProperties.GetProperty(OutcomeProposalConstraint.EvidenceReferenceProperty)
                    .GetProperty("enum")
                    .EnumerateArray()
                    .Select(item => item.GetString()));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Ai_agent_does_not_retry_a_second_invalid_evidence_proposal()
    {
        var request = Request(
            positionId: FollowUpPosition,
            identityPromptRef: "follow-up-coordination-v1",
            identityPrompt: FollowUpIdentityPrompt,
            positionName: "Follow-up Coordinator",
            objective: "Coordinate the next operational follow-up",
            context: "The owner and requested response window are supplied.");
        var audit = new RecordingJourneyAuditLog();
        var invoker = new EvidenceCorrectionInvoker(correctionSucceeds: false);
        var verifier = new StaticVerifier(
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            verifier);
        var system = ActorSystem.Create($"outcome-evidence-correction-invalid-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    invoker,
                    AiDirectiveResultMessageEmissionGate.Instance,
                    AllowingAiAgentActionGate.Instance,
                    audit,
                    NoopDirectiveAuditExportStore.Instance,
                    integrator)),
                "agent");

            actor.Tell(request);

            var snapshot = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            var message = await actor.Ask<AiDirectiveResultMessageQueryResult>(
                new GetAiDirectiveResultMessage(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            var auditSnapshot = await actor.Ask<AiDirectiveAuditSnapshotQueryResult>(
                new GetAiDirectiveAuditSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));

            Assert.Equal(2, invoker.Requests.Count);
            Assert.Equal(0, verifier.CallCount);
            Assert.True(snapshot.Found);
            Assert.False(message.Found);
            Assert.True(auditSnapshot.Found);
            Assert.Equal(AiDirectiveProcessingStatus.Escalated, snapshot.Snapshot!.Status);
            Assert.Contains(
                "ai-output-invalid",
                snapshot.Snapshot.TerminalReason,
                StringComparison.Ordinal);
            Assert.False(invoker.Requests[0].Metadata.ContainsKey("hive.correction"));
            Assert.Equal(
                "outcome-proposal-evidence",
                invoker.Requests[1].Metadata["hive.correction"]);
            Assert.Equal(
                invoker.Requests[0].SystemInstruction,
                invoker.Requests[1].SystemInstruction);
            Assert.Contains(
                FollowUpIdentityPrompt,
                invoker.Requests[1].SystemInstruction,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "runtime.fabricated",
                invoker.Requests[1].Content,
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "runtime.fabricated",
                string.Join('|', audit.Records.SelectMany(record => record.Payload.Values)),
                StringComparison.Ordinal);
            Assert.DoesNotContain(
                "runtime.fabricated",
                auditSnapshot.Snapshot!.ToString(),
                StringComparison.Ordinal);
            Assert.Contains(
                auditSnapshot.Snapshot.Redactions,
                redaction => redaction.Path == "gateway.response.text");
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Ai_agent_does_not_request_correction_when_the_iteration_limit_is_exhausted()
    {
        var request = Request(maxIterations: 1);
        var audit = new RecordingJourneyAuditLog();
        var invoker = new EvidenceCorrectionInvoker(correctionSucceeds: true);
        var verifier = new StaticVerifier(
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var integrator = CreateIntegrator(
            OutcomeResolutionMode.Enforcement,
            audit,
            verifier);
        var system = ActorSystem.Create($"outcome-evidence-correction-limit-{Guid.NewGuid():N}");
        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    invoker,
                    AiDirectiveResultMessageEmissionGate.Instance,
                    AllowingAiAgentActionGate.Instance,
                    audit,
                    NoopDirectiveAuditExportStore.Instance,
                    integrator)),
                "agent");

            actor.Tell(request);

            var snapshot = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));
            var iterationAudit = await actor.Ask<AiDirectiveIterationAuditSnapshotQueryResult>(
                new GetAiDirectiveIterationAuditSnapshot(request.CorrelationId),
                TimeSpan.FromSeconds(10));

            Assert.Single(invoker.Requests);
            Assert.Equal(0, verifier.CallCount);
            Assert.Equal(AiDirectiveProcessingStatus.Escalated, snapshot.Snapshot!.Status);
            Assert.Equal("max-iterations-reached", iterationAudit.Snapshot!.TerminalCode);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static AiDirectiveOutcomeResolutionIntegrator CreateIntegrator(
        OutcomeResolutionMode mode,
        RecordingJourneyAuditLog audit,
        StaticVerifier verifier,
        OutcomePolicySnapshot? policy = null,
        TimeSpan? verifierTimeout = null,
        DateTimeOffset? clockAt = null) =>
        new(
            new ExecutionFactsMaterializer(),
            new StaticPolicyProvider(policy ?? OutcomePolicyComposer.ComposeV1(
                1,
                "sha256:registry",
                organizationOverlay: null,
                positionOverlay: null)),
            new OrganizationalOutcomeOrchestrator(
                new OrganizationalOutcomeResolver(),
            verifier),
            audit,
            mode,
            () => clockAt ?? At.AddMinutes(1),
            verifierTimeout);

    private static ValueTask<AiDirectiveOutcomeResolutionResult> ResolveAsync(
        AiDirectiveOutcomeResolutionIntegrator integrator,
        ResolutionInput input) =>
        integrator.ResolveAsync(
            input.Context,
            input.Iteration,
            input.Decision,
            input.Proposal,
            input.ProposedMessage,
            input.Response,
            hasAvailableBudget: true,
            AllowingAiAgentActionGate.Instance,
            AiDirectiveResultMessageEmissionGate.Instance);

    private static ResolutionInput Input(
        AiDirectiveDecision decision,
        DateTimeOffset? deadline = null,
        bool withoutDeadline = false)
    {
        var request = Request(deadline, withoutDeadline);
        var context = AiDirectiveExecutionContext.From(request);
        var iteration = AiDirectiveIterationState.Start(context, At);
        var proposedMessage = AiDirectiveResultMessageFactory.Create(
            context,
            decision,
            clock: () => At.AddMinutes(1));
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            IncomingMessage,
            "redacted-provider-output",
            AiFinishReason.Stop,
            new AiProviderMetadata("stub", "model-v1"),
            usage: new AiTokenUsage(10, 4, 14, isEstimated: false),
            cost: new AiCostMetadata(0.001m, "USD", isEstimated: false));
        return new ResolutionInput(
            context,
            iteration,
            decision,
            ProposalFor(decision),
            proposedMessage,
            response);
    }

    private static OutcomeProposal ProposalFor(AiDirectiveDecision decision) =>
        decision switch
        {
            AiDirectiveReportDecision { Kind: ReportKind.Progress } => new OutcomeProposal(
                OutcomeProposedIntent.ReportProgress,
                OutcomeWorkState.InProgress,
                OutcomeRequiredIntervention.None,
                blockers: [],
                nextAction: "Continue the current directive.",
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.DirectiveInput,
                    "directive.context")]),
            AiDirectiveReportDecision { Kind: ReportKind.Done } => new OutcomeProposal(
                OutcomeProposedIntent.ReportDone,
                OutcomeWorkState.Completed,
                OutcomeRequiredIntervention.None,
                blockers: [],
                nextAction: null,
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.DirectiveInput,
                    "directive.context")]),
            AiDirectiveEscalationDecision => new OutcomeProposal(
                OutcomeProposedIntent.Escalation,
                OutcomeWorkState.Blocked,
                OutcomeRequiredIntervention.SuperiorDecision,
                [OutcomeBlocker.SuperiorDecision],
                nextAction: null,
                evidenceReferences: []),
            AiDirectiveChildDirectiveDecision child => new OutcomeProposal(
                OutcomeProposedIntent.Directive,
                OutcomeWorkState.InProgress,
                OutcomeRequiredIntervention.Delegation,
                blockers: [],
                nextAction: $"Delegate to position {child.TargetPositionId.Value}.",
                evidenceReferences: []),
            _ => throw new InvalidOperationException("Unknown test decision type."),
        };

    private static AiDirectiveProcessingRequest Request(
        DateTimeOffset? deadline = null,
        bool withoutDeadline = false,
        int? maxIterations = null,
        PositionId? positionId = null,
        string identityPromptRef = "coordinator-v1",
        string identityPrompt = "Coordinate the assigned organizational work.",
        string positionName = "Coordinator",
        string objective = "Classify incoming work",
        string context = "A bounded business context.")
    {
        var effectivePosition = positionId ?? Position;
        var entity = PositionEntityId.From(Organization, effectivePosition);
        var occupant = OccupantId.From("agent-17");
        var directive = new OrgDirective(
            IncomingMessage,
            Organization,
            new PositionEndpointRef(Superior),
            new PositionEndpointRef(effectivePosition),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: withoutDeadline ? null : deadline ?? At.AddHours(2),
            IncomingDirective,
            parentDirectiveId: null,
            objective,
            context);
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(17, "sha256:t17e"),
            Organization,
            effectivePosition,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: Superior,
                name: positionName,
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef,
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "model-v1"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: withoutDeadline ? null : TimeSpan.FromSeconds(15),
                    maxIterations: maxIterations),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    identityPromptRef,
                    $"prompts/{identityPromptRef}.md",
                    identityPrompt)),
            new PositionAuthorityRuntimeConfiguration());
        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            occupant,
            directive);
    }

    private sealed record ResolutionInput(
        AiDirectiveExecutionContext Context,
        AiDirectiveIterationState Iteration,
        AiDirectiveDecision Decision,
        OutcomeProposal Proposal,
        AiDirectiveResultMessage ProposedMessage,
        AiGatewayResponse Response);

    private sealed class StaticPolicyProvider(OutcomePolicySnapshot policy)
        : IOutcomePolicyProvider
    {
        public ValueTask<OutcomePolicySnapshot> GetPolicyAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(policy);
    }

    private sealed class StaticVerifier(OutcomeVerifierResult result) : IOutcomeVerifier
    {
        public int CallCount { get; private set; }

        public OutcomeVerificationRequest? LastRequest { get; private set; }

        public Task<OutcomeVerifierResult> VerifyAsync(
            OutcomeVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            LastRequest = request;
            return Task.FromResult(result);
        }
    }

    private sealed class ThrowingFactsMaterializer : IExecutionFactsMaterializer
    {
        public ExecutionFacts Materialize(
            OutcomeRuntimeSnapshot runtime,
            DirectiveExecutionContract directive) =>
            throw new InvalidOperationException("dynamic facts failure");
    }

    private sealed class ThrowingPolicyProvider : IOutcomePolicyProvider
    {
        public ValueTask<OutcomePolicySnapshot> GetPolicyAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromException<OutcomePolicySnapshot>(
                new InvalidOperationException("dynamic policy failure"));
    }

    private sealed class ThrowingOrchestrator : IOrganizationalOutcomeOrchestrator
    {
        public Task<OutcomeResolution> ResolveAsync(
            OutcomeVerificationRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromException<OutcomeResolution>(
                new InvalidOperationException("dynamic resolution failure"));
    }

    private sealed class StaticResponseInvoker : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) => Task.FromResult(
                AiAgentGatewayInvocationResult.FromResponse(
                    invocation.CorrelationId,
                    AiGatewayResponse.Succeeded(
                        invocation.Request.OrganizationId,
                        invocation.Request.PositionId,
                        invocation.Request.ThreadId,
                        invocation.Request.MessageId,
                        "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Done\",\"body\":\"Complete.\"},\"outcome_proposal\":{\"schema_version\":2,\"proposal\":{\"proposed_intent\":\"Report.Done\",\"work_state\":\"Completed\",\"required_intervention\":\"None\",\"blockers\":[],\"next_action\":null,\"evidence_references\":[{\"source\":\"DirectiveInput\",\"reference\":\"directive.context\"}]}}}",
                        AiFinishReason.Stop,
                        new AiProviderMetadata("stub", "model-v1"))));
    }

    private sealed class SequencedResponseInvoker : IAiAgentGatewayInvoker
    {
        public int CallCount { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            CallCount++;
            var text = CallCount == 1
                ? "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Progress\",\"body\":\"Partial.\"},\"outcome_proposal\":{\"schema_version\":2,\"proposal\":{\"proposed_intent\":\"Report.Progress\",\"work_state\":\"InProgress\",\"required_intervention\":\"None\",\"blockers\":[],\"next_action\":\"Continue triage.\",\"evidence_references\":[{\"source\":\"DirectiveInput\",\"reference\":\"directive.context\"}]}}}"
                : "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Done\",\"body\":\"Complete.\"},\"outcome_proposal\":{\"schema_version\":2,\"proposal\":{\"proposed_intent\":\"Report.Done\",\"work_state\":\"Completed\",\"required_intervention\":\"None\",\"blockers\":[],\"next_action\":null,\"evidence_references\":[{\"source\":\"DirectiveInput\",\"reference\":\"directive.context\"}]}}}";
            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    text,
                    AiFinishReason.Stop,
                    new AiProviderMetadata("stub", "model-v1"))));
        }
    }

    private sealed class EvidenceCorrectionInvoker(bool correctionSucceeds)
        : IAiAgentGatewayInvoker
    {
        public List<AiGatewayRequest> Requests { get; } = [];

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Requests.Add(invocation.Request);
            var text = Requests.Count == 1 || !correctionSucceeds
                ? InvalidEvidenceResponse()
                : GroundedEvidenceResponse();
            return Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    text,
                    AiFinishReason.Stop,
                    new AiProviderMetadata("stub", "model-v1"))));
        }

        private static string InvalidEvidenceResponse() =>
            "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Done\",\"body\":\"Complete.\"},\"outcome_proposal\":{\"schema_version\":2,\"proposal\":{\"proposed_intent\":\"Report.Done\",\"work_state\":\"Completed\",\"required_intervention\":\"None\",\"blockers\":[],\"next_action\":null,\"evidence_references\":[{\"source\":\"RuntimeFact\",\"reference\":\"runtime.fabricated\"}]}}}";

        private static string GroundedEvidenceResponse() =>
            "{\"schema_version\":1,\"intent\":\"Report\",\"report\":{\"kind\":\"Done\",\"body\":\"Complete.\"},\"outcome_proposal\":{\"schema_version\":2,\"proposal\":{\"proposed_intent\":\"Report.Done\",\"work_state\":\"Completed\",\"required_intervention\":\"None\",\"blockers\":[],\"next_action\":null,\"evidence_references\":[{\"source\":\"DirectiveInput\",\"reference\":\"directive.context\"}]}}}";
    }

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        public List<JourneyAuditRecord> Records { get; } = [];

        public void Append(JourneyAuditRecord record) => Records.Add(record);

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null) => Records
                .Where(record => record.ThreadId == threadId &&
                    (directiveId is null || record.DirectiveId == directiveId))
                .ToArray();
    }

    private sealed class RejectingRoutingGate : IAiDirectiveResultMessageGate
    {
        public static RejectingRoutingGate Instance { get; } = new();

        public ValueTask<AiDirectiveResultMessageGateResult> ValidateAsync(
            AiDirectiveExecutionContext context,
            OrgMessage message,
            CancellationToken cancellationToken = default) => ValueTask.FromResult(
                AiDirectiveResultMessageGateResult.Rejected(
                    new AiDirectiveResultMessageFailure(
                        "routing-rejected",
                        "Routing is unavailable.")));
    }

    private sealed class HumanApprovalActionGate : IAiAgentActionGate
    {
        public static HumanApprovalActionGate Instance { get; } = new();

        public ValueTask<AiAgentActionGateResult> EvaluateAsync(
            AiDirectiveExecutionContext context,
            AiAgentActionCandidate candidate,
            CancellationToken cancellationToken = default)
        {
            var request = new ApprovalRequest(
                MessageId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000001317")),
                context.OrganizationId,
                new PositionEndpointRef(context.PositionId),
                new PositionEndpointRef(Superior),
                context.Directive.ThreadId,
                context.Directive.Priority,
                schemaVersion: 1,
                sentAt: At.AddMinutes(1),
                deadline: context.Directive.Deadline,
                action: "Release retained organizational message",
                justification: "Authority policy requires approval.",
                policy: ApprovalPolicyRef.From("outcome-approval"));
            var retention = new AiAgentActionRetentionIntent(
                candidate,
                context.CorrelationId,
                context.OrganizationId,
                context.PositionId,
                context.Directive.ThreadId,
                context.Directive.MessageId,
                context.Directive.DirectiveId,
                context.Directive.ParentDirectiveId,
                "action-gate-failure",
                [request]);
            return ValueTask.FromResult(AiAgentActionGateResult.Retained(
                AiAgentActionGateOutcome.RetainedForHumanApproval,
                candidate,
                facts: null,
                resolution: null,
                "action-gate-failure",
                retention));
        }
    }
}
