using System.Collections.Immutable;
using Hive.Application.Directives;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Directives;
using Hive.Domain.Outcomes;

namespace Hive.Actors.Positions;

/// <summary>
/// Actor adapter-facing execution trace. The application result is the authoritative terminal
/// contract; the remaining fields preserve the existing ephemeral query surfaces while they are
/// consumed by the characterization suite.
/// </summary>
internal sealed record AiDirectiveExecutionCoordinatorResult
{
    public AiDirectiveExecutionCoordinatorResult(
        DirectiveExecutionResult result,
        ExecutionBudget budget,
        AiDirectiveExecutionContext context,
        AiDirectiveProcessingSnapshot processing,
        AiDirectiveAuditSnapshot audit,
        AiGatewayRequest? initialPrompt = null,
        AiAgentGatewayInvocationResult? gatewayInvocation = null,
        AiDirectiveInterpretationResult? interpretation = null,
        AiDirectiveResultMessage? resultMessage = null,
        AiAgentActionGateResult? actionGateResult = null,
        AiDirectiveIterationAuditTrail? iterationAudit = null,
        AiDirectivePositionEffects? positionEffects = null,
        AiDirectiveOutcomeResolutionResult? outcomeResolution = null)
    {
        Result = result ?? throw new ArgumentNullException(nameof(result));
        Budget = budget ?? throw new ArgumentNullException(nameof(budget));
        Context = context ?? throw new ArgumentNullException(nameof(context));
        Processing = processing ?? throw new ArgumentNullException(nameof(processing));
        Audit = audit ?? throw new ArgumentNullException(nameof(audit));
        InitialPrompt = initialPrompt;
        GatewayInvocation = gatewayInvocation;
        Interpretation = interpretation;
        ResultMessage = resultMessage;
        ActionGateResult = actionGateResult;
        IterationAudit = iterationAudit;
        PositionEffects = positionEffects;
        OutcomeResolution = outcomeResolution;
    }

    public DirectiveExecutionResult Result { get; }

    public ExecutionBudget Budget { get; }

    public AiDirectiveExecutionContext Context { get; }

    public AiDirectiveProcessingSnapshot Processing { get; }

    public AiDirectiveAuditSnapshot Audit { get; }

    public AiGatewayRequest? InitialPrompt { get; }

    public AiAgentGatewayInvocationResult? GatewayInvocation { get; }

    public AiDirectiveInterpretationResult? Interpretation { get; }

    public AiDirectiveResultMessage? ResultMessage { get; }

    public AiAgentActionGateResult? ActionGateResult { get; }

    public AiDirectiveIterationAuditTrail? IterationAudit { get; }

    public AiDirectivePositionEffects? PositionEffects { get; }

    public AiDirectiveOutcomeResolutionResult? OutcomeResolution { get; }
}

/// <summary>
/// Owns the provider-neutral directive loop and composes ordered effects. It deliberately has no
/// actor references, mailbox state or event-stream access; the <see cref="AiAgentActor"/> is the
/// adapter that dispatches the returned effects.
/// </summary>
internal sealed class AiDirectiveExecutionCoordinator : IDirectiveExecutionCoordinator
{
    private readonly IAiAgentGatewayInvoker _gatewayInvoker;
    private readonly IAiDirectiveResultMessageGate _resultMessageGate;
    private readonly IAiAgentActionGate _actionGate;
    private readonly IAiDirectiveOutcomeResolutionIntegrator _outcomeResolutionIntegrator;
    private readonly Func<DateTimeOffset> _clock;

    public AiDirectiveExecutionCoordinator(
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IAiDirectiveOutcomeResolutionIntegrator outcomeResolutionIntegrator,
        Func<DateTimeOffset>? clock = null)
    {
        _gatewayInvoker = gatewayInvoker
            ?? throw new ArgumentNullException(nameof(gatewayInvoker));
        _resultMessageGate = resultMessageGate
            ?? throw new ArgumentNullException(nameof(resultMessageGate));
        _actionGate = actionGate ?? throw new ArgumentNullException(nameof(actionGate));
        _outcomeResolutionIntegrator = outcomeResolutionIntegrator
            ?? throw new ArgumentNullException(nameof(outcomeResolutionIntegrator));
        _clock = clock ?? (() => DateTimeOffset.UtcNow);
    }

    public async ValueTask<DirectiveExecutionResult> ExecuteAsync(
        DirectiveExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        var processingRequest = AiDirectiveProcessingRequest.Create(
            request.PositionEntityId,
            request.RuntimeConfiguration,
            request.RecoveredState,
            request.Occupant,
            request.Directive);
        var execution = await ExecuteDetailedAsync(
            processingRequest,
            cancellationToken).ConfigureAwait(false);
        return execution.Result;
    }

    public ValueTask<AiDirectiveExecutionCoordinatorResult> ExecuteDetailedAsync(
        AiDirectiveProcessingRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        return ExecuteDetailedAsync(
            request,
            CreateContext(request),
            cancellationToken);
    }

    internal async ValueTask<AiDirectiveExecutionCoordinatorResult> ExecuteDetailedAsync(
        AiDirectiveProcessingRequest request,
        AiDirectiveExecutionContext context,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(context);
        if (!string.Equals(
            request.CorrelationId,
            context.CorrelationId,
            StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Directive execution context correlation must match the processing request.",
                nameof(context));
        }

        cancellationToken.ThrowIfCancellationRequested();

        var received = AiDirectiveProcessingSnapshot.Received(request);
        var hasAvailableBudget = true;
        var startedAt = _clock();
        var budget = ExecutionBudget.Start(
            request.CorrelationId,
            startedAt,
            context.Limits.ExecutionTimeout,
            context.Directive.Deadline,
            context.Limits.MaxIterations,
            hasAvailableBudget);
        var effectiveExecutionBudget = budget.RemainingTime(startedAt);
        context = context with
        {
            ExecutionPolicy = DirectiveExecutionPolicyComposer.ComposeV1(
                context.Directive.ExecutionPolicy,
                request.RuntimeContext.OccupantConfiguration.AiGateway
                    ?.DirectiveExecutionPolicy,
                context.Limits.LimitsVersion ==
                    AiPositionRuntimeConfiguration.CurrentLimitsVersion
                    ? effectiveExecutionBudget
                    : null,
                context.Limits.LimitsVersion ==
                    AiPositionRuntimeConfiguration.CurrentLimitsVersion
                    ? effectiveExecutionBudget
                    : null),
        };

        if (context.IdentityPrompt is null)
        {
            var failed = received.AdvanceTo(
                AiDirectiveProcessingStatus.Failed,
                reason: IdentityPromptFailureReason(context));
            return CreateTrace(
                request,
                budget,
                context,
                failed);
        }

        var prompt = AiDirectivePrompt.CreateInitialRequest(context);
        TimeSpan? initialTimeout = null;
        if (context.Limits.LimitsVersion == AiPositionRuntimeConfiguration.CurrentLimitsVersion &&
            !budget.TryGetEffectiveTimeout(
                context.Limits.PerCallTimeout,
                startedAt,
                out initialTimeout))
        {
            var failed = received.AdvanceTo(
                AiDirectiveProcessingStatus.Failed,
                reason: "Directive execution budget was exhausted before the first gateway request.");
            return CreateTrace(
                request,
                budget,
                context,
                failed);
        }

        if (context.Limits.LimitsVersion == AiPositionRuntimeConfiguration.CurrentLimitsVersion)
        {
            prompt = WithTimeout(prompt, initialTimeout);
        }
        hasAvailableBudget = prompt.Policy?.HasAvailableBudget ?? true;
        if (!hasAvailableBudget)
        {
            budget = budget.MarkCostBudgetUnavailable();
        }

        var contextAssembled = received.AdvanceTo(
            AiDirectiveProcessingStatus.ContextAssembled,
            reason: "execution context assembled");
        var gatewayRequested = contextAssembled.AdvanceTo(
            AiDirectiveProcessingStatus.GatewayRequested,
            reason: "AI gateway request submitted");
        var iterationState = AiDirectiveIterationState.Start(context, startedAt);
        var iterationAudit = AiDirectiveIterationAuditTrail.Start(iterationState);

        try
        {
            ConsumeIfAvailable(
                ref budget,
                ExecutionBudgetOperation.PrimaryInference,
                startedAt);

            // The provider adapter owns the functional request deadline. Passing a second token
            // scheduled for the same timeout would race that deadline and bypass structured
            // gateway audit by surfacing caller cancellation first.
            var result = await _gatewayInvoker
                .InvokeAsync(
                    new AiAgentGatewayInvocation(request.CorrelationId, prompt),
                    cancellationToken)
                .ConfigureAwait(false);
            var continuationExecutor = new AiDirectiveIterationExecutor(
                _gatewayInvoker,
                UnavailableAiDirectiveConnectorToolExecutor.Instance,
                _actionGate,
                _clock);
            var outcomeProposalEvidenceContext =
                context.RequiresStructuredOutcomeProposal
                    ? AiDirectiveOutcomeEvidenceContext.CreateProposalContext(context)
                    : null;
            var outcomeProposalCorrectionAttempted = false;

            while (true)
            {
                cancellationToken.ThrowIfCancellationRequested();
                var interpretation = AiDirectiveDecisionInterpreter.Interpret(
                    result,
                    context.Authority.CanDecide,
                    requireOutcomeProposal: context.RequiresStructuredOutcomeProposal,
                    outcomeProposalEvidenceContext: outcomeProposalEvidenceContext,
                    allowProgressReports: context.ExecutionPolicy.AllowsProgressReports);

                if (interpretation.IsDecision)
                {
                    var responseInterpreted = gatewayRequested.AdvanceTo(
                        AiDirectiveProcessingStatus.ResponseInterpreted,
                        reason: "AI gateway response interpreted");
                    var resultMessage = AiDirectiveResultMessageFactory.Create(
                        context,
                        interpretation.Decision!);
                    var outcomeResolution = await _outcomeResolutionIntegrator.ResolveAsync(
                        context,
                        iterationState,
                        interpretation.Decision!,
                        interpretation.Proposal,
                        resultMessage,
                        result.Response,
                        hasAvailableBudget,
                        _actionGate,
                        _resultMessageGate,
                        cancellationToken).ConfigureAwait(false);

                    if (outcomeResolution.Resolution?.VerifierInvoked == true)
                    {
                        ConsumeIfAvailable(
                            ref budget,
                            ExecutionBudgetOperation.OutcomeVerifier,
                            _clock());
                    }

                    var iterationAuditSnapshot = RecordInitialIterationAudit(
                        iterationState,
                        iterationAudit,
                        result,
                        hasAvailableBudget,
                        _clock());
                    resultMessage = outcomeResolution.ResultMessage ?? resultMessage;
                    AiAgentActionGateResult? actionGateResult =
                        outcomeResolution.ActionGateResult;
                    if (resultMessage.IsSuccess)
                    {
                        actionGateResult ??= await _actionGate
                            .EvaluateAsync(
                                context,
                                AiAgentActionCandidate.ForMessage(
                                    resultMessage.Message!,
                                    resultMessage.ActingUnder),
                                cancellationToken)
                            .ConfigureAwait(false);

                        if (actionGateResult.IsRetained)
                        {
                            resultMessage = AiDirectiveResultMessage.Rejected(
                                request.CorrelationId,
                                new AiDirectiveResultMessageFailure(
                                    actionGateResult.Code,
                                    $"AI action was retained by the authority gate with code '{actionGateResult.Code}'."),
                                resultMessage.ActingUnder);
                        }
                        else
                        {
                            var gateResult = outcomeResolution.RoutingGateResult
                                ?? await _resultMessageGate
                                    .ValidateAsync(
                                        context,
                                        resultMessage.Message!,
                                        cancellationToken)
                                    .ConfigureAwait(false);

                            if (gateResult.IsRejected)
                            {
                                resultMessage = AiDirectiveResultMessage.Rejected(
                                    request.CorrelationId,
                                    gateResult.Failure!,
                                    resultMessage.ActingUnder);
                            }
                        }
                    }

                    var positionEffects = AiDirectivePositionEffectFactory.Create(
                        context,
                        resultMessage);
                    var finalSnapshot =
                        resultMessage.IsSuccess
                            ? responseInterpreted.AdvanceTo(
                                AiDirectiveProcessingStatus.ResultEmitted,
                                reason: "AI directive result message materialized")
                            : responseInterpreted.AdvanceTo(
                                AiDirectiveProcessingStatus.Escalated,
                                reason: resultMessage.Failure!.AuditReason);
                    var effects = ComposeDispatchEffects(
                        context,
                        resultMessage,
                        actionGateResult,
                        positionEffects);

                    return CreateTrace(
                        request,
                        budget,
                        context,
                        finalSnapshot,
                        prompt,
                        result,
                        interpretation,
                        resultMessage,
                        iterationAuditSnapshot,
                        positionEffects,
                        actionGateResult,
                        outcomeResolution,
                        effects);
                }

                if (interpretation.IsStructuredError)
                {
                    var iterationAuditSnapshot = RecordInitialIterationAudit(
                        iterationState,
                        iterationAudit,
                        result,
                        hasAvailableBudget,
                        _clock());
                    var finalSnapshot = gatewayRequested.AdvanceTo(
                        AiDirectiveProcessingStatus.Failed,
                        reason: interpretation.Failure!.AuditReason);
                    return CreateTrace(
                        request,
                        budget,
                        context,
                        finalSnapshot,
                        prompt,
                        result,
                        interpretation,
                        resultMessage: null,
                        iterationAuditSnapshot);
                }

                if (interpretation.RequiresEscalation)
                {
                    if (!outcomeProposalCorrectionAttempted &&
                        context.RequiresStructuredOutcomeProposal &&
                        AiDirectiveOutcomeProposalCorrection.IsEligible(interpretation))
                    {
                        outcomeProposalCorrectionAttempted = true;
                        var correctionObservedAt = _clock();
                        var correctionDecision = iterationState.EvaluateOutcomeProposalCorrection(
                            correctionObservedAt,
                            hasAvailableBudget,
                            interpretation.Failure!.ParseErrors,
                            interpretation.Failure.AcceptedDecision,
                            interpretation.Failure.AcceptedProposal);
                        iterationAudit = iterationAudit.RecordDecision(
                            iterationState,
                            correctionDecision,
                            correctionObservedAt);
                        if (correctionDecision.CanContinue)
                        {
                            ConsumeContinuation(
                                ref budget,
                                correctionDecision,
                                correctionObservedAt);
                            var correctionResult = await continuationExecutor.ExecuteAsync(
                                context,
                                iterationState,
                                correctionDecision,
                                hasAvailableBudget,
                                cancellationToken)
                                .ConfigureAwait(false);
                            iterationAudit = iterationAudit.RecordExecution(
                                iterationState,
                                correctionResult,
                                _clock());
                            if (correctionResult.IsSuccess)
                            {
                                iterationState = iterationState.Advance(
                                    correctionDecision,
                                    correctionObservedAt);
                                result = correctionResult.InferenceResult!;
                                continue;
                            }
                        }
                    }

                    var iterationAuditSnapshot = iterationAudit.IsTerminal
                        ? iterationAudit
                        : RecordInitialIterationAudit(
                            iterationState,
                            iterationAudit,
                            result,
                            hasAvailableBudget,
                            _clock());
                    var finalSnapshot = gatewayRequested.AdvanceTo(
                        AiDirectiveProcessingStatus.Escalated,
                        reason: interpretation.Failure!.AuditReason);
                    return CreateTrace(
                        request,
                        budget,
                        context,
                        finalSnapshot,
                        prompt,
                        result,
                        interpretation,
                        resultMessage: null,
                        iterationAuditSnapshot);
                }
            }
        }
        catch (OperationCanceledException) when (!cancellationToken.IsCancellationRequested)
        {
            var canceledAudit = iterationAudit.RecordExecution(
                iterationState,
                AiDirectiveIterationExecutionResult.Failed(
                    request.CorrelationId,
                    new AiDirectiveIterationExecutionFailure(
                        "iteration-canceled",
                        "AI directive iteration was canceled before a response was returned.")),
                _clock());
            var finalSnapshot = gatewayRequested.AdvanceTo(
                AiDirectiveProcessingStatus.Failed,
                reason: "AI gateway request was canceled before a response was returned.");
            return CreateTrace(
                request,
                budget,
                context,
                finalSnapshot,
                prompt,
                gatewayInvocation: null,
                interpretation: null,
                resultMessage: null,
                canceledAudit);
        }
    }

    internal AiDirectiveExecutionContext CreateContext(AiDirectiveProcessingRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);
        return AiDirectiveExecutionContext.From(
            request,
            _outcomeResolutionIntegrator.RequiresStructuredProposal);
    }

    private AiDirectiveExecutionCoordinatorResult CreateTrace(
        AiDirectiveProcessingRequest request,
        ExecutionBudget budget,
        AiDirectiveExecutionContext context,
        AiDirectiveProcessingSnapshot processing,
        AiGatewayRequest? gatewayRequest = null,
        AiAgentGatewayInvocationResult? gatewayInvocation = null,
        AiDirectiveInterpretationResult? interpretation = null,
        AiDirectiveResultMessage? resultMessage = null,
        AiDirectiveIterationAuditTrail? iterationAudit = null,
        AiDirectivePositionEffects? positionEffects = null,
        AiAgentActionGateResult? actionGateResult = null,
        AiDirectiveOutcomeResolutionResult? outcomeResolution = null,
        IEnumerable<DirectiveExecutionEffect>? dispatchEffects = null)
    {
        var audit = AiDirectiveAuditSnapshotFactory.Create(
            context,
            processing,
            gatewayRequest,
            gatewayInvocation,
            interpretation,
            resultMessage,
            iterationAudit,
            positionEffects);
        var effects = ImmutableArray.CreateBuilder<DirectiveExecutionEffect>();
        if (dispatchEffects is not null)
        {
            effects.AddRange(dispatchEffects);
        }

        effects.AddRange(CreateJourneyAuditRecords(audit)
            .Select(record => new DirectiveJourneyAuditEffect(record)));

        var failureCode = processing.Status == AiDirectiveProcessingStatus.ResultEmitted
            ? null
            : AiDirectiveAuditSnapshotFactory.TerminalCode(
                processing,
                interpretation,
                resultMessage,
                iterationAudit);
        var result = processing.Status switch
        {
            AiDirectiveProcessingStatus.ResultEmitted =>
                DirectiveExecutionResult.Completed(request.ExecutionRequest, effects),
            AiDirectiveProcessingStatus.Escalated =>
                DirectiveExecutionResult.Escalated(
                    request.ExecutionRequest,
                    failureCode!,
                    effects),
            _ => DirectiveExecutionResult.Failed(
                request.ExecutionRequest,
                failureCode!,
                effects),
        };

        return new AiDirectiveExecutionCoordinatorResult(
            result,
            budget,
            context,
            processing,
            audit,
            gatewayRequest,
            gatewayInvocation,
            interpretation,
            resultMessage,
            actionGateResult,
            iterationAudit,
            positionEffects,
            outcomeResolution);
    }

    private IEnumerable<DirectiveExecutionEffect> ComposeDispatchEffects(
        AiDirectiveExecutionContext context,
        AiDirectiveResultMessage resultMessage,
        AiAgentActionGateResult? actionGateResult,
        AiDirectivePositionEffects positionEffects)
    {
        if (resultMessage.IsSuccess)
        {
            yield return new DirectiveAuditExportResultEffect(
                context.Directive.DirectiveId,
                resultMessage.Message!);
        }

        if (actionGateResult?.IsRetained == true)
        {
            yield return new DirectivePositionCommandEffect(
                AiAgentRetainedActionFactory.Create(actionGateResult, _clock()));
        }

        if (positionEffects.IsSuccess)
        {
            foreach (var command in positionEffects.Commands)
            {
                yield return new DirectivePositionCommandEffect(command);
            }
        }
    }

    private static IEnumerable<JourneyAuditRecord> CreateJourneyAuditRecords(
        AiDirectiveAuditSnapshot snapshot)
    {
        if (snapshot.Decision is { } decision)
        {
            yield return JourneyAuditRecord.Create(
                JourneyAuditStage.AgentDecided,
                decision.FailureCode is null
                    ? JourneyAuditOutcome.Succeeded
                    : JourneyAuditOutcome.Failed,
                snapshot.Context.OrganizationId,
                snapshot.Context.ThreadId,
                snapshot.Context.MessageId,
                snapshot.Context.DirectiveId,
                snapshot.Context.PositionId,
                decision.FailureCode,
                payload: DecisionPayload(snapshot, decision),
                occurredAtUtc: snapshot.RecordedAt,
                idempotencyDiscriminator: decision.DecisionKind ?? "none");
        }

        if (snapshot.ResultMessage is { } resultMessage)
        {
            yield return JourneyAuditRecord.Create(
                JourneyAuditStage.ResultMessageCreated,
                resultMessage.FailureCode is null
                    ? JourneyAuditOutcome.Succeeded
                    : JourneyAuditOutcome.Rejected,
                snapshot.Context.OrganizationId,
                snapshot.Context.ThreadId,
                snapshot.Context.MessageId,
                snapshot.Context.DirectiveId,
                snapshot.Context.PositionId,
                resultMessage.FailureCode,
                resultMessage.MessageType,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = snapshot.Status.ToString(),
                    ["terminalCode"] = snapshot.TerminalCode,
                    ["resultMessageType"] = resultMessage.MessageType ?? "none",
                    ["actingUnderState"] =
                        resultMessage.ActingUnder?.State.ToString() ?? "none",
                    ["actingUnderCode"] = resultMessage.ActingUnder?.Code ?? "none",
                    ["actingUnderKey"] =
                        resultMessage.ActingUnder?.Key?.Value ?? "none",
                    ["redactions"] = RedactionPayload(snapshot),
                },
                occurredAtUtc: snapshot.RecordedAt);
        }
    }

    private static IReadOnlyDictionary<string, string> DecisionPayload(
        AiDirectiveAuditSnapshot snapshot,
        AiDirectiveAuditDecisionSnapshot decision)
    {
        var payload = new Dictionary<string, string>(StringComparer.Ordinal)
        {
            ["status"] = snapshot.Status.ToString(),
            ["terminalCode"] = snapshot.TerminalCode,
            ["decisionKind"] = decision.DecisionKind ?? "none",
            ["actingUnderState"] = decision.ActingUnder?.State.ToString() ?? "none",
            ["actingUnderCode"] = decision.ActingUnder?.Code ?? "none",
            ["actingUnderKey"] = decision.ActingUnder?.Key?.Value ?? "none",
            ["parseErrorContractVersion"] = decision.ParseErrorContractVersion.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["parseErrorCount"] = decision.ParseErrorCount.ToString(
                System.Globalization.CultureInfo.InvariantCulture),
            ["redactions"] = RedactionPayload(snapshot),
        };
        for (var index = 0; index < decision.ParseErrors.Length; index++)
        {
            var diagnostic = decision.ParseErrors[index];
            payload[$"parseError.{index}.path"] = diagnostic.Path;
            payload[$"parseError.{index}.code"] = diagnostic.Code;
        }

        return payload;
    }

    private static string RedactionPayload(AiDirectiveAuditSnapshot snapshot) =>
        string.Join(
            ",",
            snapshot.Redactions.Select(redaction => $"{redaction.Path}:{redaction.Reason}"));

    private static string IdentityPromptFailureReason(AiDirectiveExecutionContext context) =>
        $"Identity prompt '{context.IdentityPromptRef ?? "<missing>"}' was not resolved; directive processing stopped before gateway request.";

    private static AiDirectiveIterationAuditTrail RecordInitialIterationAudit(
        AiDirectiveIterationState state,
        AiDirectiveIterationAuditTrail audit,
        AiAgentGatewayInvocationResult result,
        bool hasAvailableBudget,
        DateTimeOffset observedAt)
    {
        if (result.IsSuccess)
        {
            return audit.RecordDecision(
                state,
                state.Evaluate(result.Response, observedAt, hasAvailableBudget),
                observedAt);
        }

        var failure = result.FailureReason;
        if (failure?.Code == AiGatewayErrorCode.Timeout)
        {
            return audit.RecordDecision(
                state,
                AiDirectiveIterationDecision.Stop(new AiDirectiveIterationStopReason(
                    AiDirectiveIterationStopKind.Timeout,
                    "timeout",
                    GatewayTimeoutReason(state.Deadline - state.StartedAt))),
                observedAt);
        }

        var reason = failure is null
            ? new AiDirectiveIterationExecutionFailure(
                "ai-gateway-failure",
                "AI gateway iteration failed without a structured reason.")
            : new AiDirectiveIterationExecutionFailure(
                "ai-gateway-" + AiGatewayErrorCodeContract.ToWireValue(failure.Code),
                $"AI gateway iteration failed with '{AiGatewayErrorCodeContract.ToWireValue(failure.Code)}'.");

        return audit.RecordExecution(
            state,
            AiDirectiveIterationExecutionResult.Failed(state.CorrelationId, reason),
            observedAt);
    }

    private static string GatewayTimeoutReason(TimeSpan? timeout) =>
        timeout is { } value
            ? $"AI gateway timeout after {value}."
            : "AI gateway timeout.";

    private static AiGatewayRequest WithTimeout(
        AiGatewayRequest request,
        TimeSpan? timeout) =>
        new(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            request.Content,
            request.SystemInstruction,
            request.ContextMessages,
            request.Tools,
            request.ModelParameters,
            request.Metadata,
            request.Provider,
            request.ProcessingMode,
            timeout,
            request.Policy,
            request.OutputConstraint);

    private static void ConsumeContinuation(
        ref ExecutionBudget budget,
        AiDirectiveIterationDecision decision,
        DateTimeOffset observedAt)
    {
        var operation = decision.Continuations.Single().Kind switch
        {
            AiDirectiveIterationContinuationKind.OutcomeProposalCorrection =>
                ExecutionBudgetOperation.ContinuationInference,
            AiDirectiveIterationContinuationKind.ConnectorTool =>
                ExecutionBudgetOperation.ConnectorTool,
            _ => throw new InvalidOperationException(
                "Unknown directive continuation kind."),
        };
        ConsumeIfAvailable(ref budget, operation, observedAt);
    }

    private static void ConsumeIfAvailable(
        ref ExecutionBudget budget,
        ExecutionBudgetOperation operation,
        DateTimeOffset observedAt)
    {
        if (budget.TryConsume(
            operation,
            observedAt,
            out var remaining,
            out _))
        {
            budget = remaining;
        }
    }
}
