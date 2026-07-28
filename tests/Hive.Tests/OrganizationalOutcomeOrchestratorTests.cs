using Hive.Domain.Identity;
using Hive.Domain.Outcomes;

namespace Hive.Tests;

public sealed class OrganizationalOutcomeOrchestratorTests
{
    [Fact]
    public async Task Deterministically_resolved_outcome_never_invokes_the_verifier()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.Escalation));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(
            facts: Facts(autonomousActionAvailable: true, pendingActions: true)));

        Assert.Equal(OutcomeKind.ContinueWork, resolution.Outcome);
        Assert.False(resolution.VerifierInvoked);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Undetermined_outcome_invokes_verifier_once_and_repeats_the_resolver()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);
        var request = Request(facts: Facts(
            autonomousActionAvailable: false,
            pendingActions: false,
            responsibilityRetained: false,
            completionState: OutcomeCompletionState.Satisfied));

        var resolution = await orchestrator.ResolveAsync(request);

        Assert.Equal(OutcomeKind.ReportDone, resolution.Outcome);
        Assert.Equal(
            [
                OutcomeResolutionReason.CompletionCriteriaSatisfied,
                OutcomeResolutionReason.VerifierConfirmed,
            ],
            resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
        Assert.True(resolution.VerifierInvoked);
        Assert.Equal(OutcomeVerifierResultStatus.Classified, resolution.VerifierStatus);
        Assert.Equal(
            OutcomeVerifierClassification.ReportDone,
            resolution.VerifierClassification);
        Assert.Equal(1, verifier.CallCount);
        Assert.Same(request, verifier.LastRequest);
    }

    [Fact]
    public async Task Verifier_can_tighten_an_ambiguous_case_to_escalation()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.Escalation));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request());

        Assert.Equal(OutcomeKind.Escalation, resolution.Outcome);
        Assert.Equal(
            [
                OutcomeResolutionReason.ProposalEscalation,
                OutcomeResolutionReason.VerifierConfirmed,
            ],
            resolution.Reasons);
        Assert.True(resolution.VerifierInvoked);
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public async Task Disabled_verifier_closes_undetermined_to_escalation_without_calling_it()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(
            policy: Policy(verifierEnabled: false)));

        AssertFailSafe(
            resolution,
            OutcomeResolutionReason.VerifierUnavailable,
            verifierInvoked: false);
        Assert.Equal(0, verifier.CallCount);
    }

    [Theory]
    [InlineData(true, false, OutcomeResolutionReason.BudgetExhausted)]
    [InlineData(false, true, OutcomeResolutionReason.DeadlineExceeded)]
    public async Task Budget_and_deadline_gates_never_invoke_verifier(
        bool budgetExhausted,
        bool deadlineExceeded,
        OutcomeResolutionReason expectedReason)
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(facts: Facts(
            budgetExhausted: budgetExhausted,
            deadlineExceeded: deadlineExceeded)));

        Assert.Equal(OutcomeKind.Escalation, resolution.Outcome);
        Assert.Equal([expectedReason], resolution.Reasons);
        Assert.False(resolution.VerifierInvoked);
        Assert.Equal(0, verifier.CallCount);
    }

    [Theory]
    [InlineData(OutcomeVerifierResultStatus.Unavailable, OutcomeResolutionReason.VerifierUnavailable)]
    [InlineData(OutcomeVerifierResultStatus.TimedOut, OutcomeResolutionReason.VerifierTimedOut)]
    [InlineData(OutcomeVerifierResultStatus.InvalidOutput, OutcomeResolutionReason.VerifierOutputInvalid)]
    public async Task Closed_verifier_failures_escalate_with_auditable_reason(
        OutcomeVerifierResultStatus status,
        OutcomeResolutionReason expectedReason)
    {
        var verifier = new RecordingVerifier(_ => status switch
        {
            OutcomeVerifierResultStatus.Unavailable => OutcomeVerifierResult.Unavailable(),
            OutcomeVerifierResultStatus.TimedOut => OutcomeVerifierResult.TimedOut(),
            OutcomeVerifierResultStatus.InvalidOutput => OutcomeVerifierResult.InvalidOutput(),
            _ => throw new ArgumentOutOfRangeException(nameof(status), status, null),
        });
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request());

        AssertFailSafe(resolution, expectedReason, verifierInvoked: true);
        Assert.Equal(status, resolution.VerifierStatus);
        Assert.Null(resolution.VerifierClassification);
        Assert.Equal(1, verifier.CallCount);
    }

    [Theory]
    [InlineData(typeof(TimeoutException), OutcomeResolutionReason.VerifierTimedOut)]
    [InlineData(typeof(InvalidOperationException), OutcomeResolutionReason.VerifierUnavailable)]
    public async Task Verifier_boundary_exceptions_fail_closed(
        Type exceptionType,
        OutcomeResolutionReason expectedReason)
    {
        var verifier = new ThrowingVerifier(exceptionType);
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request());

        AssertFailSafe(resolution, expectedReason, verifierInvoked: true);
    }

    [Fact]
    public async Task Classification_that_contradicts_facts_escalates_instead_of_opening_a_report()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(facts: Facts(
            autonomousActionAvailable: false,
            pendingActions: false,
            completionState: OutcomeCompletionState.NotSatisfied)));

        AssertFailSafe(
            resolution,
            OutcomeResolutionReason.VerifierContradictedFacts,
            verifierInvoked: true);
    }

    [Fact]
    public async Task Grounded_verifier_confirmation_closes_done_when_no_structured_criteria_exist()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(
            facts: Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                completionState: OutcomeCompletionState.NotDeclared),
            directive: new DirectiveExecutionContract(),
            proposal: DoneProposal("directive.objective")));

        Assert.Equal(OutcomeKind.ReportDone, resolution.Outcome);
        Assert.Equal(
            [
                OutcomeResolutionReason.SemanticCompletionVerified,
                OutcomeResolutionReason.VerifierConfirmed,
            ],
            resolution.Reasons);
        Assert.True(resolution.VerifierInvoked);
        Assert.True(resolution.SemanticCompletionCandidate);
        Assert.Empty(resolution.SemanticCompletionIneligibilityReasons!);
        Assert.Equal(
            OutcomeVerifierClassification.ReportDone,
            resolution.VerifierClassification);
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public async Task Ungrounded_model_evidence_cannot_open_a_done_report()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(
            facts: Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                completionState: OutcomeCompletionState.NotDeclared),
            directive: new DirectiveExecutionContract(),
            proposal: DoneProposal("invented.runtime.fact")));

        AssertFailSafe(
            resolution,
            OutcomeResolutionReason.VerifierDisagreement,
            verifierInvoked: true);
        Assert.Equal(
            [
                OutcomeSemanticCompletionIneligibilityReason
                    .EvidenceReferenceNotInContext,
            ],
            resolution.SemanticCompletionIneligibilityReasons);
    }

    [Theory]
    [InlineData(OutcomeVerifierClassification.ContinueWork)]
    [InlineData(OutcomeVerifierClassification.ReportProgress)]
    public async Task Active_work_classification_without_progress_or_next_action_fails_closed(
        OutcomeVerifierClassification classification)
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(classification));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(
            facts: Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                completionState: OutcomeCompletionState.NotDeclared),
            directive: new DirectiveExecutionContract(),
            proposal: DoneProposal("directive.objective")));

        AssertFailSafe(
            resolution,
            OutcomeResolutionReason.VerifierDisagreement,
            verifierInvoked: true);
    }

    [Theory]
    [InlineData(OutcomeCompletionState.NotSatisfied, OutcomeResolutionReason.VerifierContradictedFacts)]
    [InlineData(OutcomeCompletionState.Unknown, OutcomeResolutionReason.VerifierDisagreement)]
    public async Task Semantic_verification_never_bypasses_structured_completion_facts(
        OutcomeCompletionState completionState,
        OutcomeResolutionReason expectedReason)
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(
            facts: Facts(
                autonomousActionAvailable: false,
                pendingActions: false,
                completionState: completionState),
            proposal: DoneProposal("directive.objective")));

        AssertFailSafe(resolution, expectedReason, verifierInvoked: true);
    }

    [Fact]
    public async Task Classification_that_remains_undetermined_escalates_as_disagreement()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(facts: Facts(
            autonomousActionAvailable: false,
            pendingActions: false,
            completionState: OutcomeCompletionState.Unknown)));

        AssertFailSafe(
            resolution,
            OutcomeResolutionReason.VerifierDisagreement,
            verifierInvoked: true);
    }

    [Fact]
    public async Task Verifier_can_declare_low_confidence_without_guessing_an_outcome()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.Undetermined));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request());

        AssertFailSafe(
            resolution,
            OutcomeResolutionReason.VerifierDisagreement,
            verifierInvoked: true);
        Assert.Equal(OutcomeVerifierResultStatus.Classified, resolution.VerifierStatus);
        Assert.Equal(
            OutcomeVerifierClassification.Undetermined,
            resolution.VerifierClassification);
        Assert.Equal(1, verifier.CallCount);
    }

    [Fact]
    public async Task Missing_bounded_artifact_fails_closed_without_calling_verifier()
    {
        var verifier = new RecordingVerifier(_ =>
            OutcomeVerifierResult.Classified(OutcomeVerifierClassification.ReportDone));
        var orchestrator = CreateOrchestrator(verifier);

        var resolution = await orchestrator.ResolveAsync(Request(includeArtifact: false));

        AssertFailSafe(
            resolution,
            OutcomeResolutionReason.VerifierUnavailable,
            verifierInvoked: false);
        Assert.Equal(OutcomeVerifierResultStatus.Unavailable, resolution.VerifierStatus);
        Assert.Null(resolution.VerifierClassification);
        Assert.Equal(0, verifier.CallCount);
    }

    [Fact]
    public async Task Caller_cancellation_is_propagated_and_not_relabelled_as_verifier_timeout()
    {
        var verifier = new RecordingVerifier(_ => OutcomeVerifierResult.Unavailable());
        var orchestrator = CreateOrchestrator(verifier);
        using var source = new CancellationTokenSource();
        source.Cancel();

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() =>
            orchestrator.ResolveAsync(Request(), source.Token));
        Assert.Equal(0, verifier.CallCount);
    }

    private static IOrganizationalOutcomeOrchestrator CreateOrchestrator(IOutcomeVerifier verifier) =>
        new OrganizationalOutcomeOrchestrator(new OrganizationalOutcomeResolver(), verifier);

    private static OutcomeVerificationRequest Request(
        ExecutionFacts? facts = null,
        OutcomePolicySnapshot? policy = null,
        DirectiveExecutionContract? directive = null,
        OutcomeProposal? proposal = null,
        bool includeArtifact = true)
    {
        var selectedProposal = proposal ?? ContinueProposal();
        return new OutcomeVerificationRequest(
            new OutcomeVerificationContext(
                OrganizationId.From("org-verifier"),
                PositionId.From("delivery-lead"),
                ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                TimeSpan.FromSeconds(5),
                [new OutcomeVerificationContextEntry(
                    "directive.objective",
                    "Assess the supplied work item.")]),
            facts ?? Facts(),
            directive ?? new DirectiveExecutionContract(
                requiredInputs: [new("input.work", "The work input is present.")],
                completionCriteria: [new("criterion.complete", "The work is complete.")]),
            selectedProposal,
            policy ?? Policy(),
            includeArtifact ? ArtifactFor(selectedProposal) : null);
    }

    private static ExecutionFacts Facts(
        bool deadlineExceeded = false,
        bool budgetExhausted = false,
        bool autonomousActionAvailable = false,
        bool pendingActions = false,
        bool responsibilityRetained = true,
        OutcomeCompletionState completionState = OutcomeCompletionState.NotDeclared) =>
        new(
            iterationCount: 0,
            retryCount: 0,
            deadlineExceeded,
            budgetExhausted,
            humanApprovalRequired: false,
            approvalPending: false,
            OutcomeDependencyState.Available,
            OutcomeAuthorityState.Authorized,
            OutcomeRoutingState.Available,
            autonomousActionAvailable,
            delegationRequired: false,
            pendingActions,
            externalInterventionRequired: false,
            verifiableProgress: false,
            responsibilityRetained,
            completionState);

    private static OutcomePolicySnapshot Policy(bool verifierEnabled = true) =>
        new(
            "outcome-policy-v1",
            "sha256:orchestrator",
            maximumIterations: 8,
            maximumRetries: 3,
            verifierEnabled);

    private static OutcomeProposal ContinueProposal() =>
        new(
            OutcomeProposedIntent.ContinueWork,
            OutcomeWorkState.InProgress,
            OutcomeRequiredIntervention.None,
            blockers: [],
            "Continue the current work.",
            evidenceReferences: []);

    private static OutcomeProposal DoneProposal(string evidenceReference) =>
        new(
            OutcomeProposedIntent.ReportDone,
            OutcomeWorkState.Completed,
            OutcomeRequiredIntervention.None,
            blockers: [],
            nextAction: null,
            [new OutcomeEvidenceReference(
                OutcomeEvidenceSource.DirectiveInput,
                evidenceReference)]);

    private static OutcomeVerificationArtifact ArtifactFor(OutcomeProposal proposal) =>
        proposal.ProposedIntent switch
        {
            OutcomeProposedIntent.ReportDone => new OutcomeVerificationArtifact(
                OutcomeKind.ReportDone,
                [new("report.body", "The requested assessment is complete.")]),
            OutcomeProposedIntent.Escalation or OutcomeProposedIntent.ApprovalRequired =>
                new OutcomeVerificationArtifact(
                    OutcomeKind.Escalation,
                    [
                        new("escalation.issue", "A superior decision is required."),
                        new("escalation.context", "The bounded assessment requires intervention."),
                    ]),
            OutcomeProposedIntent.Directive => new OutcomeVerificationArtifact(
                OutcomeKind.Directive,
                [
                    new("directive.objective", "Delegate the bounded next action."),
                    new("directive.context", "The delegation remains within authority."),
                ]),
            _ => new OutcomeVerificationArtifact(
                OutcomeKind.ReportProgress,
                [new("report.body", "The requested assessment is still in progress.")]),
        };

    private static void AssertFailSafe(
        OutcomeResolution resolution,
        OutcomeResolutionReason reason,
        bool verifierInvoked)
    {
        Assert.Equal(OutcomeKind.Escalation, resolution.Outcome);
        Assert.Equal([reason], resolution.Reasons);
        Assert.True(resolution.ProposalOverridden);
        Assert.Equal(verifierInvoked, resolution.VerifierInvoked);
    }

    private sealed class RecordingVerifier(
        Func<OutcomeVerificationRequest, OutcomeVerifierResult> resultFactory)
        : IOutcomeVerifier
    {
        public int CallCount { get; private set; }

        public OutcomeVerificationRequest? LastRequest { get; private set; }

        public Task<OutcomeVerifierResult> VerifyAsync(
            OutcomeVerificationRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(resultFactory(request));
        }
    }

    private sealed class ThrowingVerifier(Type exceptionType) : IOutcomeVerifier
    {
        public Task<OutcomeVerifierResult> VerifyAsync(
            OutcomeVerificationRequest request,
            CancellationToken cancellationToken = default) =>
            exceptionType == typeof(TimeoutException)
                ? Task.FromException<OutcomeVerifierResult>(new TimeoutException())
                : Task.FromException<OutcomeVerifierResult>(new InvalidOperationException());
    }
}
