using System.Collections.Immutable;
using System.Diagnostics;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Messaging;
using Hive.Domain.Outcomes;
using Hive.Infrastructure.Configuration;

namespace Hive.Actors.Positions;

internal interface IAiDirectiveOutcomeResolutionIntegrator
{
    bool RequiresStructuredProposal { get; }

    ValueTask<AiDirectiveOutcomeResolutionResult> ResolveAsync(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState iteration,
        AiDirectiveDecision decision,
        OutcomeProposal? proposal,
        AiDirectiveResultMessage proposedMessage,
        AiGatewayResponse gatewayResponse,
        bool hasAvailableBudget,
        IAiAgentActionGate actionGate,
        IAiDirectiveResultMessageGate routingGate,
        CancellationToken cancellationToken = default);
}

internal sealed record AiDirectiveOutcomeResolutionResult
{
    public AiDirectiveOutcomeResolutionResult(
        AiDirectiveResultMessage? resultMessage,
        AiAgentActionGateResult? actionGateResult,
        AiDirectiveResultMessageGateResult? routingGateResult,
        OutcomeProposal? proposal,
        OutcomeResolution? resolution,
        OutcomeResolutionMode mode,
        IEnumerable<OutcomeResolutionDiagnostic>? diagnostics = null,
        bool shouldContinue = false)
    {
        ResultMessage = resultMessage;
        ActionGateResult = actionGateResult;
        RoutingGateResult = routingGateResult;
        Proposal = proposal;
        Resolution = resolution;
        Mode = mode;
        Diagnostics = diagnostics?.Distinct().Order().ToImmutableArray() ?? [];
        ShouldContinue = shouldContinue;

        if (shouldContinue && resultMessage is not null)
        {
            throw new ArgumentException(
                "A ContinueWork outcome cannot carry an organization message.",
                nameof(resultMessage));
        }
    }

    public AiDirectiveResultMessage? ResultMessage { get; }

    public AiAgentActionGateResult? ActionGateResult { get; }

    public AiDirectiveResultMessageGateResult? RoutingGateResult { get; }

    public OutcomeProposal? Proposal { get; }

    public OutcomeResolution? Resolution { get; }

    public OutcomeResolutionMode Mode { get; }

    public ImmutableArray<OutcomeResolutionDiagnostic> Diagnostics { get; }

    public bool ShouldContinue { get; }

    public bool WasEvaluated => Proposal is not null && Resolution is not null;
}

internal sealed class PassthroughAiDirectiveOutcomeResolutionIntegrator
    : IAiDirectiveOutcomeResolutionIntegrator
{
    public static PassthroughAiDirectiveOutcomeResolutionIntegrator Instance { get; } = new();

    private PassthroughAiDirectiveOutcomeResolutionIntegrator()
    {
    }

    public bool RequiresStructuredProposal => false;

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
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        return ValueTask.FromResult(new AiDirectiveOutcomeResolutionResult(
            proposedMessage,
            actionGateResult: null,
            routingGateResult: null,
            proposal: null,
            resolution: null,
            OutcomeResolutionMode.Shadow));
    }
}

/// <summary>
/// Integrates the provider-neutral resolver before an organizational message can leave the AI
/// occupant. Candidate messages are used only to obtain authoritative action/routing facts; only
/// the resolved message is returned to the caller for effects and emission.
/// </summary>
internal sealed class AiDirectiveOutcomeResolutionIntegrator
    : IAiDirectiveOutcomeResolutionIntegrator
{
    private const string UnavailablePolicyVersion = "outcome-policy-unavailable";
    private const string UnavailablePolicyFingerprint = "unavailable";

    private readonly IExecutionFactsMaterializer _factsMaterializer;
    private readonly IOutcomePolicyProvider _policyProvider;
    private readonly IOrganizationalOutcomeOrchestrator _orchestrator;
    private readonly IJourneyAuditLog _auditLog;
    private readonly OutcomeResolutionMode _mode;
    private readonly Func<DateTimeOffset> _clock;
    private readonly TimeSpan _verifierTimeout;

    public bool RequiresStructuredProposal => true;

    public AiDirectiveOutcomeResolutionIntegrator(
        IExecutionFactsMaterializer factsMaterializer,
        IOutcomePolicyProvider policyProvider,
        IOrganizationalOutcomeOrchestrator orchestrator,
        IJourneyAuditLog auditLog,
        OutcomeResolutionMode mode,
        Func<DateTimeOffset>? clock = null,
        TimeSpan? verifierTimeout = null)
    {
        _factsMaterializer = factsMaterializer
            ?? throw new ArgumentNullException(nameof(factsMaterializer));
        _policyProvider = policyProvider ?? throw new ArgumentNullException(nameof(policyProvider));
        _orchestrator = orchestrator ?? throw new ArgumentNullException(nameof(orchestrator));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _mode = Enum.IsDefined(mode)
            ? mode
            : throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown outcome mode.");
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
        _verifierTimeout = verifierTimeout ?? OutcomeResolutionOptions.DefaultVerifierTimeout;
        if (_verifierTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(verifierTimeout),
                _verifierTimeout,
                "Outcome verifier timeout must be greater than zero.");
        }
    }

    public async ValueTask<AiDirectiveOutcomeResolutionResult> ResolveAsync(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState iteration,
        AiDirectiveDecision decision,
        OutcomeProposal? proposal,
        AiDirectiveResultMessage proposedMessage,
        AiGatewayResponse gatewayResponse,
        bool hasAvailableBudget,
        IAiAgentActionGate actionGate,
        IAiDirectiveResultMessageGate routingGate,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(context);
        ArgumentNullException.ThrowIfNull(iteration);
        ArgumentNullException.ThrowIfNull(decision);
        ArgumentNullException.ThrowIfNull(proposal);
        if (!AiDirectiveOutcomeProposalEnvelope.IsCompatible(decision, proposal))
        {
            throw new ArgumentException(
                "The outcome proposal contradicts the organizational message decision.",
                nameof(proposal));
        }
        ArgumentNullException.ThrowIfNull(proposedMessage);
        ArgumentNullException.ThrowIfNull(gatewayResponse);
        ArgumentNullException.ThrowIfNull(actionGate);
        ArgumentNullException.ThrowIfNull(routingGate);
        cancellationToken.ThrowIfCancellationRequested();

        var startedAt = Stopwatch.GetTimestamp();
        var diagnostics = new List<OutcomeResolutionDiagnostic>();

        AiAgentActionGateResult? proposedActionGate = null;
        AiDirectiveResultMessageGateResult? proposedRoutingGate = null;
        if (proposedMessage.IsSuccess)
        {
            proposedActionGate = await actionGate.EvaluateAsync(
                context,
                AiAgentActionCandidate.ForMessage(
                    proposedMessage.Message!,
                    proposedMessage.ActingUnder),
                cancellationToken).ConfigureAwait(false);
            if (proposedActionGate.IsAllowed)
            {
                proposedRoutingGate = await routingGate.ValidateAsync(
                    context,
                    proposedMessage.Message!,
                    cancellationToken).ConfigureAwait(false);
            }
        }

        var directive = new DirectiveExecutionContract();
        ExecutionFacts facts;
        try
        {
            facts = _factsMaterializer.Materialize(
                CreateRuntimeSnapshot(
                    context,
                    iteration,
                    proposal,
                    gatewayResponse,
                    hasAvailableBudget,
                    proposedMessage,
                    proposedActionGate,
                    proposedRoutingGate),
                directive);
        }
        catch (Exception) when (!cancellationToken.IsCancellationRequested)
        {
            diagnostics.Add(OutcomeResolutionDiagnostic.FactsUnavailable);
            return CompleteFailSafe(
                context,
                iteration,
                proposal,
                gatewayResponse,
                proposedMessage,
                proposedActionGate,
                proposedRoutingGate,
                diagnostics,
                OutcomeResolutionReason.FactsUnavailable,
                startedAt);
        }

        OutcomePolicySnapshot policy;
        try
        {
            policy = await _policyProvider.GetPolicyAsync(
                context.OrganizationId,
                context.PositionId,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics.Add(OutcomeResolutionDiagnostic.PolicyUnavailable);
            return CompleteFailSafe(
                context,
                iteration,
                proposal,
                gatewayResponse,
                proposedMessage,
                proposedActionGate,
                proposedRoutingGate,
                diagnostics,
                OutcomeResolutionReason.PolicyUnavailable,
                startedAt);
        }

        if (!IsCompatible(policy))
        {
            diagnostics.Add(OutcomeResolutionDiagnostic.PolicyIncompatible);
            var incompatible = FailSafe(
                proposal,
                policy,
                OutcomeResolutionReason.PolicyIncompatible);
            return Complete(
                context,
                iteration,
                proposal,
                gatewayResponse,
                proposedMessage,
                proposedActionGate,
                proposedRoutingGate,
                incompatible,
                diagnostics,
                startedAt);
        }

        OutcomeResolution resolution;
        try
        {
            var verificationRequest = new OutcomeVerificationRequest(
                CreateVerificationContext(context),
                facts,
                directive,
                proposal,
                policy,
                CreateVerificationArtifact(proposedMessage));
            resolution = await _orchestrator.ResolveAsync(
                verificationRequest,
                cancellationToken).ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
        {
            throw;
        }
        catch (Exception)
        {
            diagnostics.Add(OutcomeResolutionDiagnostic.ResolutionUnavailable);
            resolution = FailSafe(
                proposal,
                policy,
                OutcomeResolutionReason.FactsUnavailable);
        }

        return Complete(
            context,
            iteration,
            proposal,
            gatewayResponse,
            proposedMessage,
            proposedActionGate,
            proposedRoutingGate,
            resolution,
            diagnostics,
            startedAt);
    }

    private AiDirectiveOutcomeResolutionResult CompleteFailSafe(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState iteration,
        OutcomeProposal proposal,
        AiGatewayResponse gatewayResponse,
        AiDirectiveResultMessage proposedMessage,
        AiAgentActionGateResult? proposedActionGate,
        AiDirectiveResultMessageGateResult? proposedRoutingGate,
        List<OutcomeResolutionDiagnostic> diagnostics,
        OutcomeResolutionReason reason,
        long startedAt)
    {
        var resolution = new OutcomeResolution(
            OutcomeKind.Escalation,
            [reason],
            UnavailablePolicyVersion,
            UnavailablePolicyFingerprint,
            proposalOverridden: proposal.ProposedIntent != OutcomeProposedIntent.Escalation,
            verifierInvoked: false);
        return Complete(
            context,
            iteration,
            proposal,
            gatewayResponse,
            proposedMessage,
            proposedActionGate,
            proposedRoutingGate,
            resolution,
            diagnostics,
            startedAt);
    }

    private AiDirectiveOutcomeResolutionResult Complete(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState iteration,
        OutcomeProposal proposal,
        AiGatewayResponse gatewayResponse,
        AiDirectiveResultMessage proposedMessage,
        AiAgentActionGateResult? proposedActionGate,
        AiDirectiveResultMessageGateResult? proposedRoutingGate,
        OutcomeResolution resolution,
        List<OutcomeResolutionDiagnostic> diagnostics,
        long startedAt)
    {
        var elapsed = Stopwatch.GetElapsedTime(startedAt);
        var result = Materialize(
            context,
            proposal,
            proposedMessage,
            proposedActionGate,
            proposedRoutingGate,
            resolution,
            diagnostics);
        RecordAudit(
            context,
            iteration,
            proposal,
            resolution,
            gatewayResponse,
            diagnostics,
            elapsed);
        return result;
    }

    private AiDirectiveOutcomeResolutionResult Materialize(
        AiDirectiveExecutionContext context,
        OutcomeProposal proposal,
        AiDirectiveResultMessage proposedMessage,
        AiAgentActionGateResult? proposedActionGate,
        AiDirectiveResultMessageGateResult? proposedRoutingGate,
        OutcomeResolution resolution,
        List<OutcomeResolutionDiagnostic> diagnostics)
    {
        if (_mode == OutcomeResolutionMode.Shadow)
        {
            return new AiDirectiveOutcomeResolutionResult(
                proposedMessage,
                proposedActionGate,
                proposedRoutingGate,
                proposal,
                resolution,
                _mode,
                diagnostics);
        }

        if (resolution.Outcome == OutcomeKind.ContinueWork)
        {
            return new AiDirectiveOutcomeResolutionResult(
                resultMessage: null,
                actionGateResult: null,
                routingGateResult: null,
                proposal,
                resolution,
                _mode,
                diagnostics,
                shouldContinue: true);
        }

        if (resolution.Outcome == OutcomeKind.ApprovalRequired &&
            proposedActionGate?.Outcome == AiAgentActionGateOutcome.RetainedForHumanApproval)
        {
            return new AiDirectiveOutcomeResolutionResult(
                proposedMessage,
                proposedActionGate,
                proposedRoutingGate,
                proposal,
                resolution,
                _mode,
                diagnostics);
        }

        if (Matches(resolution.Outcome, proposedMessage.Message))
        {
            return new AiDirectiveOutcomeResolutionResult(
                proposedMessage,
                proposedActionGate,
                proposedRoutingGate,
                proposal,
                resolution,
                _mode,
                diagnostics);
        }


        if (resolution.Outcome == OutcomeKind.Escalation)
        {
            return new AiDirectiveOutcomeResolutionResult(
                CreateFailSafeEscalation(context, resolution, proposedMessage),
                actionGateResult: null,
                routingGateResult: null,
                proposal,
                resolution,
                _mode,
                diagnostics);
        }

        diagnostics.Add(OutcomeResolutionDiagnostic.MaterializationIncompatible);
        var escalation = CreateFailSafeEscalation(context, resolution, proposedMessage);
        return new AiDirectiveOutcomeResolutionResult(
            escalation,
            actionGateResult: null,
            routingGateResult: null,
            proposal,
            resolution,
            _mode,
            diagnostics);
    }

    private void RecordAudit(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState iteration,
        OutcomeProposal proposal,
        OutcomeResolution resolution,
        AiGatewayResponse gatewayResponse,
        IEnumerable<OutcomeResolutionDiagnostic> diagnostics,
        TimeSpan latency)
    {
        var diagnosticSnapshot = diagnostics.Distinct().Order().ToArray();
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["mode"] = OutcomeResolutionModeContract.ToWireValue(_mode),
            ["iteration"] = iteration.CurrentIteration.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["proposedIntent"] = OutcomeProposedIntentContract.ToWireValue(proposal.ProposedIntent),
            ["workState"] = OutcomeWorkStateContract.ToWireValue(proposal.WorkState),
            ["requiredIntervention"] = OutcomeRequiredInterventionContract.ToWireValue(
                proposal.RequiredIntervention),
            ["resolvedOutcome"] = OutcomeKindContract.ToWireValue(resolution.Outcome),
            ["policyVersion"] = resolution.PolicyVersion,
            ["policyFingerprint"] = resolution.PolicyFingerprint,
            ["proposalOverridden"] = resolution.ProposalOverridden ? "true" : "false",
            ["verifierInvoked"] = resolution.VerifierInvoked ? "true" : "false",
            ["semanticCompletionCandidate"] =
                resolution.SemanticCompletionCandidate ? "true" : "false",
            ["reasonCount"] = resolution.Reasons.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["diagnosticCount"] = diagnosticSnapshot.Length.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["redactions"] =
                "prompt,chain-of-thought,provider-output,rejected-values,next-action,verification-artifact,evidence-references",
        };
        if (DeadlineRemainingMilliseconds(context, iteration) is { } remainingMilliseconds)
        {
            payload["deadlineRemainingMilliseconds"] = remainingMilliseconds.ToString(
                System.Globalization.CultureInfo.InvariantCulture);
        }

        if (resolution.SemanticCompletionIneligibilityReasons is { } ineligibilityReasons)
        {
            payload["semanticCompletionIneligibilityReasonCount"] =
                ineligibilityReasons.Length.ToString(
                    System.Globalization.CultureInfo.InvariantCulture);
            for (var index = 0; index < ineligibilityReasons.Length; index++)
            {
                payload[$"semanticCompletionIneligibilityReason.{index}"] =
                    OutcomeSemanticCompletionIneligibilityReasonContract.ToWireValue(
                        ineligibilityReasons[index]);
            }
        }

        if (resolution.VerifierStatus is { } verifierStatus)
        {
            payload["verifierStatus"] =
                OutcomeVerifierResultStatusContract.ToWireValue(verifierStatus);
        }

        if (resolution.VerifierClassification is { } verifierClassification)
        {
            payload["verifierClassification"] =
                OutcomeVerifierClassificationContract.ToWireValue(verifierClassification);
        }

        for (var index = 0; index < resolution.Reasons.Length; index++)
        {
            payload[$"reason.{index}"] = OutcomeResolutionReasonContract.ToWireValue(
                resolution.Reasons[index]);
        }

        for (var index = 0; index < diagnosticSnapshot.Length; index++)
        {
            payload[$"diagnostic.{index}"] = OutcomeResolutionDiagnosticContract.ToWireValue(
                diagnosticSnapshot[index]);
        }

        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.OutcomeResolved,
            JourneyAuditOutcome.Succeeded,
            context.OrganizationId,
            context.Directive.ThreadId,
            context.Directive.MessageId,
            context.Directive.DirectiveId,
            context.PositionId,
            reasonCode: diagnosticSnapshot.Length == 0
                ? null
                : OutcomeResolutionDiagnosticContract.ToWireValue(diagnosticSnapshot[0]),
            messageType: OutcomeKindContract.ToWireValue(resolution.Outcome),
            provider: gatewayResponse.Provider ?? gatewayResponse.Error?.Provider,
            usage: gatewayResponse.Usage,
            cost: gatewayResponse.Cost,
            latency,
            payload,
            idempotencyDiscriminator:
                $"{context.CorrelationId}:{iteration.CurrentIteration}"));
    }

    private long? DeadlineRemainingMilliseconds(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState iteration)
    {
        var deadline = (context.Directive.Deadline, iteration.Deadline) switch
        {
            ({ } directiveDeadline, { } iterationDeadline) =>
                directiveDeadline <= iterationDeadline
                    ? directiveDeadline
                    : iterationDeadline,
            ({ } directiveDeadline, null) => directiveDeadline,
            (null, { } iterationDeadline) => iterationDeadline,
            _ => (DateTimeOffset?)null,
        };
        if (deadline is null)
        {
            return null;
        }

        var remaining = deadline.Value - _clock();
        return remaining <= TimeSpan.Zero
            ? 0L
            : Convert.ToInt64(
                Math.Floor(remaining.TotalMilliseconds),
                System.Globalization.CultureInfo.InvariantCulture);
    }

    private OutcomeRuntimeSnapshot CreateRuntimeSnapshot(
        AiDirectiveExecutionContext context,
        AiDirectiveIterationState iteration,
        OutcomeProposal proposal,
        AiGatewayResponse gatewayResponse,
        bool hasAvailableBudget,
        AiDirectiveResultMessage proposedMessage,
        AiAgentActionGateResult? actionGate,
        AiDirectiveResultMessageGateResult? routingGate)
    {
        var actionGateState = actionGate?.Outcome switch
        {
            AiAgentActionGateOutcome.Allowed => OutcomeActionGateState.Authorized,
            AiAgentActionGateOutcome.RetainedForHumanApproval =>
                OutcomeActionGateState.HumanApprovalRequired,
            AiAgentActionGateOutcome.RetainedForEscalation => OutcomeActionGateState.Denied,
            _ => OutcomeActionGateState.Unknown,
        };
        var routingState = proposedMessage.IsFailure || routingGate?.IsRejected == true
            ? OutcomeRoutingState.Unavailable
            : routingGate?.IsAllowed == true
                ? OutcomeRoutingState.Available
                : OutcomeRoutingState.Unknown;
        var pendingActions = proposal.ProposedIntent is
            OutcomeProposedIntent.ContinueWork or
            OutcomeProposedIntent.ReportProgress or
            OutcomeProposedIntent.Directive or
            OutcomeProposedIntent.ApprovalRequired;

        return new OutcomeRuntimeSnapshot(
            iteration.CurrentIteration,
            retryCount: gatewayResponse.Error?.IsRetryable == true ? 1 : 0,
            _clock(),
            context.Directive.Deadline ?? iteration.Deadline,
            hasAvailableBudget:
                hasAvailableBudget &&
                gatewayResponse.Error?.Code != AiGatewayErrorCode.BudgetInsufficient,
            actionGateState,
            approvalPending: false,
            routingState,
            autonomousActionAvailable:
                (proposal.ProposedIntent is
                    OutcomeProposedIntent.ContinueWork or
                    OutcomeProposedIntent.ReportProgress) &&
                actionGateState == OutcomeActionGateState.Authorized &&
                routingState is OutcomeRoutingState.Available or OutcomeRoutingState.NotRequired,
            delegationRequired:
                proposal.ProposedIntent == OutcomeProposedIntent.Directive &&
                proposedMessage.Message is Directive,
            pendingActions,
            externalInterventionRequired:
                proposal.RequiredIntervention is not OutcomeRequiredIntervention.None ||
                actionGateState is OutcomeActionGateState.HumanApprovalRequired or
                    OutcomeActionGateState.Denied,
            verifiableProgress: false,
            responsibilityRetained: proposal.ProposedIntent != OutcomeProposedIntent.Directive);
    }

    private OutcomeVerificationContext CreateVerificationContext(
        AiDirectiveExecutionContext context)
    {
        var timeout = EffectiveVerifierTimeout(context);
        return new OutcomeVerificationContext(
            context.OrganizationId,
            context.PositionId,
            context.Directive.ThreadId,
            context.Directive.MessageId,
            context.Directive.DirectiveId,
            timeout,
            AiDirectiveOutcomeEvidenceContext.CreateVerificationEntries(context));
    }

    private TimeSpan EffectiveVerifierTimeout(AiDirectiveExecutionContext context)
    {
        var timeout = _verifierTimeout;
        if (context.Limits.Timeout is { } positionTimeout && positionTimeout < timeout)
        {
            timeout = positionTimeout;
        }

        if (context.Directive.Deadline is { } deadline)
        {
            var remaining = deadline - _clock();
            if (remaining <= TimeSpan.Zero)
            {
                return TimeSpan.FromTicks(1);
            }

            if (remaining < timeout)
            {
                timeout = remaining;
            }
        }

        return timeout;
    }

    private static OutcomeVerificationArtifact? CreateVerificationArtifact(
        AiDirectiveResultMessage proposedMessage)
    {
        try
        {
            return proposedMessage.Message switch
            {
                Report report => new OutcomeVerificationArtifact(
                    report.Kind == ReportKind.Progress
                        ? OutcomeKind.ReportProgress
                        : OutcomeKind.ReportDone,
                    [
                        new OutcomeVerificationArtifactEntry(
                            "report.body",
                            AiDirectiveEvaluationEnvelope.RemoveEnvelopeLines(report.Body)),
                    ]),
                Escalation escalation => new OutcomeVerificationArtifact(
                    OutcomeKind.Escalation,
                    new[]
                    {
                        new OutcomeVerificationArtifactEntry(
                            "escalation.issue",
                            escalation.Issue),
                        new OutcomeVerificationArtifactEntry(
                            "escalation.context",
                            AiDirectiveEvaluationEnvelope.RemoveEnvelopeLines(
                                escalation.Context)),
                    }.Concat(escalation.OptionsConsidered.Select(
                        (option, index) => new OutcomeVerificationArtifactEntry(
                            $"escalation.options.{index:D2}",
                            option)))),
                Directive directive => new OutcomeVerificationArtifact(
                    OutcomeKind.Directive,
                    [
                        new OutcomeVerificationArtifactEntry(
                            "directive.objective",
                            directive.Objective),
                        new OutcomeVerificationArtifactEntry(
                            "directive.context",
                            directive.Context),
                    ]),
                ApprovalRequest approval => new OutcomeVerificationArtifact(
                    OutcomeKind.ApprovalRequired,
                    [
                        new OutcomeVerificationArtifactEntry(
                            "approval.action",
                            approval.Action),
                        new OutcomeVerificationArtifactEntry(
                            "approval.justification",
                            approval.Justification),
                    ]),
                _ => null,
            };
        }
        catch (ArgumentException)
        {
            // Oversized or invalid message fields are never truncated. A missing artifact makes
            // the limited verifier unavailable and the orchestrator retains fail-safe behavior.
            return null;
        }
    }

    private static bool IsCompatible(OutcomePolicySnapshot policy) =>
        policy.ContractVersion == OrganizationalOutcomeContractVersions.PolicySnapshot &&
        (string.Equals(policy.Version, OutcomeSystemPolicy.Version, StringComparison.Ordinal) ||
         policy.Version.StartsWith(OutcomeSystemPolicy.Version + "/", StringComparison.Ordinal));

    private static OutcomeResolution FailSafe(
        OutcomeProposal proposal,
        OutcomePolicySnapshot policy,
        OutcomeResolutionReason reason) =>
        new(
            policy.FailSafeOutcome,
            [reason],
            policy.Version,
            policy.Fingerprint,
            proposalOverridden: proposal.ProposedIntent != OutcomeProposedIntent.Escalation,
            verifierInvoked: false);

    private static bool Matches(OutcomeKind outcome, OrgMessage? message) =>
        (outcome, message) switch
        {
            (OutcomeKind.ReportProgress, Report { Kind: ReportKind.Progress }) => true,
            (OutcomeKind.ReportDone, Report { Kind: ReportKind.Done }) => true,
            (OutcomeKind.Escalation, Escalation) => true,
            (OutcomeKind.Directive, Directive) => true,
            _ => false,
        };

    private static AiDirectiveResultMessage CreateFailSafeEscalation(
        AiDirectiveExecutionContext context,
        OutcomeResolution resolution,
        AiDirectiveResultMessage proposedMessage)
    {
        var reasonCodes = string.Join(
            ",",
            resolution.Reasons.Select(OutcomeResolutionReasonContract.ToWireValue));
        return AiDirectiveResultMessageFactory.Create(
            context,
            new AiDirectiveEscalationDecision(
                "Outcome policy requires escalation",
                $"The authoritative outcome resolver closed this execution as escalation ({reasonCodes}).",
                ["Review the authoritative execution facts and applied outcome policy."],
                proposedMessage.ActingUnder),
            evaluationEnvelopeJson: proposedMessage.EvaluationEnvelopeJson);
    }
}

internal sealed record GetAiDirectiveOutcomeResolution
{
    public GetAiDirectiveOutcomeResolution(string correlationId)
    {
        CorrelationId = AiAgentGatewayText.Require(correlationId, nameof(correlationId));
    }

    public string CorrelationId { get; }
}

internal sealed record AiDirectiveOutcomeResolutionQueryResult(
    string CorrelationId,
    AiDirectiveOutcomeResolutionResult? Result)
{
    public bool Found => Result is not null;
}
