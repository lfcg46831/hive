using Akka.Actor;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Actors.Positions;

internal interface IPositionOccupantFactory
{
    Props Create(OccupantId occupant, OccupantType occupantType);
}

internal sealed class PositionOccupantFactory : IPositionOccupantFactory
{
    public static PositionOccupantFactory Instance { get; } = new();

    private readonly IAiAgentGatewayInvoker _aiGatewayInvoker;
    private readonly IAiDirectiveResultMessageGate _resultMessageGate;
    private readonly IJourneyAuditLog _auditLog;

    public PositionOccupantFactory()
        : this(UnavailableAiAgentGatewayInvoker.Instance)
    {
    }

    public PositionOccupantFactory(IAiAgentGatewayInvoker aiGatewayInvoker)
        : this(aiGatewayInvoker, AiDirectiveResultMessageEmissionGate.Instance)
    {
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IJourneyAuditLog auditLog)
    {
        _aiGatewayInvoker = aiGatewayInvoker
            ?? throw new ArgumentNullException(nameof(aiGatewayInvoker));
        _resultMessageGate = resultMessageGate
            ?? throw new ArgumentNullException(nameof(resultMessageGate));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate)
        : this(aiGatewayInvoker, resultMessageGate, NoopJourneyAuditLog.Instance)
    {
    }

    public Props Create(OccupantId occupant, OccupantType occupantType)
    {
        ArgumentNullException.ThrowIfNull(occupant);

        return occupantType switch
        {
            OccupantType.AiAgent => Props.Create(() => new AiAgentActor(
                occupant,
                _aiGatewayInvoker,
                _resultMessageGate,
                _auditLog)),
            OccupantType.Human => Props.Create(() => new HumanProxyActor(occupant)),
            _ => throw new ArgumentOutOfRangeException(
                nameof(occupantType),
                occupantType,
                "Occupant type must be AiAgent or Human."),
        };
    }
}

internal sealed class AiAgentActor : ReceiveActor
{
    private const string GatewayCallWithoutTerminalResultCode =
        "gateway-call-already-recorded-without-terminal-result";
    private const string TerminalResultAlreadyMaterializedReason =
        "terminal-result-already-materialized";
    private const string GatewayCallAlreadyMaterializedReason =
        "gateway-call-already-materialized";

    private readonly Dictionary<string, AiDirectiveProcessingSnapshot> _directiveProcessingSnapshots =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveExecutionContext> _directiveExecutionContexts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiGatewayRequest> _directiveInitialPrompts =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiAgentGatewayInvocationResult> _directiveGatewayInvocations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveInterpretationResult> _directiveInterpretations =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveResultMessage> _directiveResultMessages =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveIterationAuditTrail> _directiveIterationAudits =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectivePositionEffects> _directivePositionEffects =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveAuditSnapshot> _directiveAudits =
        new(StringComparer.Ordinal);
    private readonly IJourneyAuditLog _auditLog;

    public AiAgentActor(OccupantId occupant)
        : this(occupant, UnavailableAiAgentGatewayInvoker.Instance)
    {
    }

    public AiAgentActor(OccupantId occupant, IAiAgentGatewayInvoker gatewayInvoker)
        : this(occupant, gatewayInvoker, AiDirectiveResultMessageEmissionGate.Instance)
    {
    }

    public AiAgentActor(
        OccupantId occupant,
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IJourneyAuditLog auditLog)
    {
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        GatewayInvoker = gatewayInvoker
            ?? throw new ArgumentNullException(nameof(gatewayInvoker));
        ResultMessageGate = resultMessageGate
            ?? throw new ArgumentNullException(nameof(resultMessageGate));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));

        ReceiveAsync<AiAgentGatewayInvocation>(async invocation =>
        {
            var replyTo = Sender;
            var result = await GatewayInvoker
                .InvokeAsync(invocation, CancellationToken.None)
                .ConfigureAwait(false);
            replyTo.Tell(result);
        });
        ReceiveAsync<AiDirectiveProcessingRequest>(HandleDirectiveProcessingRequestAsync);
        Receive<GetAiDirectiveProcessingSnapshot>(query =>
        {
            Sender.Tell(_directiveProcessingSnapshots.TryGetValue(
                query.CorrelationId,
                out var snapshot)
                ? AiDirectiveProcessingSnapshotQueryResult.FoundSnapshot(snapshot)
                : AiDirectiveProcessingSnapshotQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectiveExecutionContext>(query =>
        {
            Sender.Tell(_directiveExecutionContexts.TryGetValue(
                query.CorrelationId,
                out var context)
                ? AiDirectiveExecutionContextQueryResult.FoundContext(context)
                : AiDirectiveExecutionContextQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectiveInitialPrompt>(query =>
        {
            Sender.Tell(_directiveInitialPrompts.TryGetValue(
                query.CorrelationId,
                out var request)
                ? AiDirectiveInitialPromptQueryResult.FoundRequest(query.CorrelationId, request)
                : AiDirectiveInitialPromptQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectiveGatewayInvocation>(query =>
        {
            Sender.Tell(_directiveGatewayInvocations.TryGetValue(
                query.CorrelationId,
                out var result)
                ? AiDirectiveGatewayInvocationQueryResult.FoundResult(result)
                : AiDirectiveGatewayInvocationQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectiveInterpretationResult>(query =>
        {
            Sender.Tell(_directiveInterpretations.TryGetValue(
                query.CorrelationId,
                out var result)
                ? AiDirectiveInterpretationQueryResult.FoundResult(result)
                : AiDirectiveInterpretationQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectiveResultMessage>(query =>
        {
            Sender.Tell(_directiveResultMessages.TryGetValue(
                query.CorrelationId,
                out var result)
                ? AiDirectiveResultMessageQueryResult.FoundResult(result)
                : AiDirectiveResultMessageQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectiveIterationAuditSnapshot>(query =>
        {
            Sender.Tell(_directiveIterationAudits.TryGetValue(
                query.CorrelationId,
                out var snapshot)
                ? AiDirectiveIterationAuditSnapshotQueryResult.FoundSnapshot(snapshot)
                : AiDirectiveIterationAuditSnapshotQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectivePositionEffects>(query =>
        {
            Sender.Tell(_directivePositionEffects.TryGetValue(
                query.CorrelationId,
                out var effects)
                ? AiDirectivePositionEffectsQueryResult.FoundEffects(effects)
                : AiDirectivePositionEffectsQueryResult.Missing(query.CorrelationId));
        });
        Receive<GetAiDirectiveAuditSnapshot>(query =>
        {
            Sender.Tell(_directiveAudits.TryGetValue(
                query.CorrelationId,
                out var snapshot)
                ? AiDirectiveAuditSnapshotQueryResult.FoundSnapshot(snapshot)
                : AiDirectiveAuditSnapshotQueryResult.Missing(query.CorrelationId));
        });
        Receive<OrgMessage>(message =>
        {
            GenericMessageCompletion.Return(Context.Parent, message);
        });
    }

    public AiAgentActor(
        OccupantId occupant,
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate)
        : this(occupant, gatewayInvoker, resultMessageGate, NoopJourneyAuditLog.Instance)
    {
    }

    public OccupantId Occupant { get; }

    internal IAiAgentGatewayInvoker GatewayInvoker { get; }

    internal IAiDirectiveResultMessageGate ResultMessageGate { get; }

    private sealed record AiDirectiveRecoveryDecision(
        AiDirectiveProcessingSnapshot Snapshot,
        string? FailureCode = null);

    private async Task HandleDirectiveProcessingRequestAsync(AiDirectiveProcessingRequest request)
    {
        var parent = Context.Parent;
        Action<object> publishAudit = Context.System.EventStream.Publish;
        var context = AiDirectiveExecutionContext.From(request);
        var received = AiDirectiveProcessingSnapshot.Received(request);
        if (TryRecoverJourney(context, received) is { } recovered)
        {
            _directiveExecutionContexts[request.CorrelationId] = context;
            _directiveProcessingSnapshots[request.CorrelationId] = recovered.Snapshot;
            ReturnCompletion(parent, recovered.Snapshot, failureCodeOverride: recovered.FailureCode);
            return;
        }

        if (context.IdentityPrompt is null)
        {
            var failed = received.AdvanceTo(
                AiDirectiveProcessingStatus.Failed,
                reason: IdentityPromptFailureReason(context));
            _directiveExecutionContexts[request.CorrelationId] = context;
            _directiveProcessingSnapshots[request.CorrelationId] = failed;
            RecordAudit(publishAudit, context, failed);
            ReturnCompletion(parent, failed);
            return;
        }

        var prompt = AiDirectivePrompt.CreateInitialRequest(context);
        var contextAssembled = received.AdvanceTo(
            AiDirectiveProcessingStatus.ContextAssembled,
            reason: "execution context assembled");
        var gatewayRequested = contextAssembled.AdvanceTo(
            AiDirectiveProcessingStatus.GatewayRequested,
            reason: "AI gateway request submitted");

        _directiveExecutionContexts[request.CorrelationId] = context;
        _directiveInitialPrompts[request.CorrelationId] = prompt;
        _directiveProcessingSnapshots[request.CorrelationId] = gatewayRequested;

        var iterationStartedAt = DateTimeOffset.UtcNow;
        var iterationState = AiDirectiveIterationState.Start(context, iterationStartedAt);
        var iterationAudit = AiDirectiveIterationAuditTrail.Start(iterationState);

        using var timeout = CreateGatewayTimeout(prompt.Timeout);
        try
        {
            var result = await GatewayInvoker
                .InvokeAsync(
                    new AiAgentGatewayInvocation(request.CorrelationId, prompt),
                    timeout?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
            _directiveGatewayInvocations[request.CorrelationId] = result;
            var iterationAuditSnapshot = RecordInitialIterationAudit(
                iterationState,
                iterationAudit,
                result,
                prompt.Policy?.HasAvailableBudget ?? true,
                DateTimeOffset.UtcNow);
            _directiveIterationAudits[request.CorrelationId] = iterationAuditSnapshot;
            var interpretation = AiDirectiveDecisionInterpreter.Interpret(
                result,
                context.Authority.CanDecide);
            _directiveInterpretations[request.CorrelationId] = interpretation;

            if (interpretation.IsDecision)
            {
                var responseInterpreted = gatewayRequested.AdvanceTo(
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    reason: "AI gateway response interpreted");
                var resultMessage = AiDirectiveResultMessageFactory.Create(
                    context,
                    interpretation.Decision!);
                if (resultMessage.IsSuccess)
                {
                    var gateResult = await ResultMessageGate
                        .ValidateAsync(context, resultMessage.Message!)
                        .ConfigureAwait(false);

                    if (gateResult.IsRejected)
                    {
                        resultMessage = AiDirectiveResultMessage.Rejected(
                            request.CorrelationId,
                            gateResult.Failure!,
                            resultMessage.ActingUnder);
                    }
                }

                _directiveResultMessages[request.CorrelationId] = resultMessage;
                var positionEffects = AiDirectivePositionEffectFactory.Create(
                    context,
                    resultMessage);
                _directivePositionEffects[request.CorrelationId] = positionEffects;
                if (positionEffects.IsSuccess)
                {
                    foreach (var command in positionEffects.Commands)
                    {
                        parent.Tell(command);
                    }
                }

                var finalSnapshot =
                    resultMessage.IsSuccess
                        ? responseInterpreted.AdvanceTo(
                            AiDirectiveProcessingStatus.ResultEmitted,
                            reason: "AI directive result message materialized")
                        : responseInterpreted.AdvanceTo(
                            AiDirectiveProcessingStatus.Escalated,
                            reason: resultMessage.Failure!.AuditReason);
                _directiveProcessingSnapshots[request.CorrelationId] = finalSnapshot;
                ReturnCompletion(
                    parent,
                    finalSnapshot,
                    interpretation,
                    resultMessage,
                    iterationAuditSnapshot);
                RecordAudit(
                    publishAudit,
                    context,
                    finalSnapshot,
                    prompt,
                    result,
                    interpretation,
                    resultMessage,
                    iterationAuditSnapshot,
                    positionEffects);
            }
            else if (interpretation.IsStructuredError)
            {
                var finalSnapshot = gatewayRequested.AdvanceTo(
                    AiDirectiveProcessingStatus.Failed,
                    reason: interpretation.Failure!.AuditReason);
                _directiveProcessingSnapshots[request.CorrelationId] = finalSnapshot;
                ReturnCompletion(parent, finalSnapshot, interpretation);
                RecordAudit(
                    publishAudit,
                    context,
                    finalSnapshot,
                    prompt,
                    result,
                    interpretation,
                    resultMessage: null,
                    iterationAudit: iterationAuditSnapshot,
                    positionEffects: null);
            }
            else if (interpretation.RequiresEscalation)
            {
                var finalSnapshot = gatewayRequested.AdvanceTo(
                    AiDirectiveProcessingStatus.Escalated,
                    reason: interpretation.Failure!.AuditReason);
                _directiveProcessingSnapshots[request.CorrelationId] = finalSnapshot;
                ReturnCompletion(parent, finalSnapshot, interpretation);
                RecordAudit(
                    publishAudit,
                    context,
                    finalSnapshot,
                    prompt,
                    result,
                    interpretation,
                    resultMessage: null,
                    iterationAudit: iterationAuditSnapshot,
                    positionEffects: null);
            }
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
        {
            var timeoutAudit = iterationAudit.RecordDecision(
                iterationState,
                AiDirectiveIterationDecision.Stop(new AiDirectiveIterationStopReason(
                    AiDirectiveIterationStopKind.Timeout,
                    "timeout",
                    GatewayTimeoutReason(prompt.Timeout))),
                DateTimeOffset.UtcNow);
            _directiveIterationAudits[request.CorrelationId] = timeoutAudit;
            var finalSnapshot = gatewayRequested.AdvanceTo(
                AiDirectiveProcessingStatus.Failed,
                reason: GatewayTimeoutReason(prompt.Timeout));
            _directiveProcessingSnapshots[request.CorrelationId] = finalSnapshot;
            ReturnCompletion(parent, finalSnapshot, iterationAudit: timeoutAudit);
            RecordAudit(
                publishAudit,
                context,
                finalSnapshot,
                prompt,
                gatewayInvocation: null,
                interpretation: null,
                resultMessage: null,
                iterationAudit: timeoutAudit,
                positionEffects: null);
        }
        catch (OperationCanceledException)
        {
            var canceledAudit = iterationAudit.RecordExecution(
                iterationState,
                AiDirectiveIterationExecutionResult.Failed(
                    request.CorrelationId,
                    new AiDirectiveIterationExecutionFailure(
                        "iteration-canceled",
                        "AI directive iteration was canceled before a response was returned.")),
                DateTimeOffset.UtcNow);
            _directiveIterationAudits[request.CorrelationId] = canceledAudit;
            var finalSnapshot = gatewayRequested.AdvanceTo(
                AiDirectiveProcessingStatus.Failed,
                reason: "AI gateway request was canceled before a response was returned.");
            _directiveProcessingSnapshots[request.CorrelationId] = finalSnapshot;
            ReturnCompletion(parent, finalSnapshot, iterationAudit: canceledAudit);
            RecordAudit(
                publishAudit,
                context,
                finalSnapshot,
                prompt,
                gatewayInvocation: null,
                interpretation: null,
                resultMessage: null,
                iterationAudit: canceledAudit,
                positionEffects: null);
        }
    }

    private AiDirectiveRecoveryDecision? TryRecoverJourney(
        AiDirectiveExecutionContext context,
        AiDirectiveProcessingSnapshot received)
    {
        var records = _auditLog
            .ReadByThread(context.Directive.ThreadId, context.Directive.DirectiveId)
            .Where(record =>
                record.OrganizationId == context.OrganizationId
                && record.MessageId == context.Directive.MessageId
                && record.PositionId == context.PositionId)
            .ToArray();

        var resultMessage = records
            .LastOrDefault(record => record.Stage == JourneyAuditStage.ResultMessageCreated);
        if (resultMessage is not null)
        {
            RecordDuplicateSuppression(
                context,
                resultMessage,
                TerminalResultAlreadyMaterializedReason);

            var interpreted = RecoveredGatewayRequested(received)
                .AdvanceTo(
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    reason: "recovered terminal journey result");

            var snapshot = resultMessage.Outcome == JourneyAuditOutcome.Succeeded
                ? interpreted.AdvanceTo(
                    AiDirectiveProcessingStatus.ResultEmitted,
                    reason: "AI directive result message was already recorded.")
                : interpreted.AdvanceTo(
                    AiDirectiveProcessingStatus.Escalated,
                    reason: resultMessage.ReasonCode ?? "AI directive result message was already rejected.");

            return new AiDirectiveRecoveryDecision(snapshot);
        }

        var gatewayCalled = records
            .LastOrDefault(record => record.Stage == JourneyAuditStage.GatewayCalled);
        var agentDecided = records.Any(record => record.Stage == JourneyAuditStage.AgentDecided);
        if (gatewayCalled is not null && !agentDecided)
        {
            RecordDuplicateSuppression(
                context,
                gatewayCalled,
                GatewayCallAlreadyMaterializedReason);

            var snapshot = RecoveredGatewayRequested(received)
                .AdvanceTo(
                    AiDirectiveProcessingStatus.Failed,
                    reason: GatewayCallWithoutTerminalResultCode);

            return new AiDirectiveRecoveryDecision(
                snapshot,
                GatewayCallWithoutTerminalResultCode);
        }

        return null;
    }

    private void RecordDuplicateSuppression(
        AiDirectiveExecutionContext context,
        JourneyAuditRecord suppressed,
        string reasonCode)
    {
        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.DuplicateSuppressed,
            JourneyAuditOutcome.Rejected,
            context.OrganizationId,
            context.Directive.ThreadId,
            context.Directive.MessageId,
            context.Directive.DirectiveId,
            context.PositionId,
            reasonCode: reasonCode,
            messageType: suppressed.MessageType,
            payload: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["suppressedStage"] = suppressed.Stage.ToString(),
                ["suppressedOutcome"] = suppressed.Outcome.ToString(),
                ["reasonCode"] = reasonCode,
                ["redactions"] = "directive.objective,directive.context,gateway.request.content,gateway.response.text",
            },
            idempotencyDiscriminator: reasonCode));
    }

    private static AiDirectiveProcessingSnapshot RecoveredGatewayRequested(
        AiDirectiveProcessingSnapshot received) =>
        received
            .AdvanceTo(
                AiDirectiveProcessingStatus.ContextAssembled,
                reason: "recovered execution context")
            .AdvanceTo(
                AiDirectiveProcessingStatus.GatewayRequested,
                reason: "recovered gateway request");

    private static void ReturnCompletion(
        IActorRef parent,
        AiDirectiveProcessingSnapshot processing,
        AiDirectiveInterpretationResult? interpretation = null,
        AiDirectiveResultMessage? resultMessage = null,
        AiDirectiveIterationAuditTrail? iterationAudit = null,
        string? failureCodeOverride = null)
    {
        if (!processing.IsTerminal)
        {
            throw new ArgumentException(
                "Occupant processing completion requires a terminal processing snapshot.",
                nameof(processing));
        }

        var status = processing.Status switch
        {
            AiDirectiveProcessingStatus.ResultEmitted => PositionOccupantProcessingStatus.Completed,
            AiDirectiveProcessingStatus.Escalated => PositionOccupantProcessingStatus.Escalated,
            _ => PositionOccupantProcessingStatus.Failed,
        };

        parent.Tell(new PositionOccupantProcessingCompleted(
            processing.CorrelationId,
            processing.MessageId,
            processing.ThreadId,
            processing.DirectiveId,
            status,
            status == PositionOccupantProcessingStatus.Completed
                ? null
                : failureCodeOverride ?? AiDirectiveAuditSnapshotFactory.TerminalCode(
                    processing,
                    interpretation,
                    resultMessage,
                    iterationAudit)));
    }

    private void RecordAudit(
        Action<object> publishAudit,
        AiDirectiveExecutionContext context,
        AiDirectiveProcessingSnapshot processing,
        AiGatewayRequest? gatewayRequest = null,
        AiAgentGatewayInvocationResult? gatewayInvocation = null,
        AiDirectiveInterpretationResult? interpretation = null,
        AiDirectiveResultMessage? resultMessage = null,
        AiDirectiveIterationAuditTrail? iterationAudit = null,
        AiDirectivePositionEffects? positionEffects = null)
    {
        var snapshot = AiDirectiveAuditSnapshotFactory.Create(
            context,
            processing,
            gatewayRequest,
            gatewayInvocation,
            interpretation,
            resultMessage,
            iterationAudit,
            positionEffects);

        _directiveAudits[context.CorrelationId] = snapshot;
        RecordJourneyAudit(snapshot);
        publishAudit(snapshot);
    }

    private void RecordJourneyAudit(AiDirectiveAuditSnapshot snapshot)
    {
        if (snapshot.Decision is { } decision)
        {
            _auditLog.Append(JourneyAuditRecord.Create(
                JourneyAuditStage.AgentDecided,
                DecisionOutcome(decision),
                snapshot.Context.OrganizationId,
                snapshot.Context.ThreadId,
                snapshot.Context.MessageId,
                snapshot.Context.DirectiveId,
                snapshot.Context.PositionId,
                decision.FailureCode,
                payload: new Dictionary<string, string>(StringComparer.Ordinal)
                {
                    ["status"] = snapshot.Status.ToString(),
                    ["terminalCode"] = snapshot.TerminalCode,
                    ["decisionKind"] = decision.DecisionKind ?? "none",
                    ["actingUnderState"] = decision.ActingUnder?.State.ToString() ?? "none",
                    ["actingUnderCode"] = decision.ActingUnder?.Code ?? "none",
                    ["actingUnderKey"] = decision.ActingUnder?.Key?.Value ?? "none",
                    ["parseErrorCount"] = decision.ParseErrorCount.ToString(System.Globalization.CultureInfo.InvariantCulture),
                    ["redactions"] = RedactionPayload(snapshot),
                },
                occurredAtUtc: snapshot.RecordedAt,
                idempotencyDiscriminator: decision.DecisionKind ?? "none"));
        }

        if (snapshot.ResultMessage is { } resultMessage)
        {
            _auditLog.Append(JourneyAuditRecord.Create(
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
                    ["actingUnderState"] = resultMessage.ActingUnder?.State.ToString() ?? "none",
                    ["actingUnderCode"] = resultMessage.ActingUnder?.Code ?? "none",
                    ["actingUnderKey"] = resultMessage.ActingUnder?.Key?.Value ?? "none",
                    ["redactions"] = RedactionPayload(snapshot),
                },
                occurredAtUtc: snapshot.RecordedAt));
        }
    }

    private static JourneyAuditOutcome DecisionOutcome(AiDirectiveAuditDecisionSnapshot decision) =>
        decision.FailureCode is null
            ? JourneyAuditOutcome.Succeeded
            : JourneyAuditOutcome.Failed;

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

    private static CancellationTokenSource? CreateGatewayTimeout(TimeSpan? timeout) =>
        timeout is { } value ? new CancellationTokenSource(value) : null;

    private static string GatewayTimeoutReason(TimeSpan? timeout) =>
        timeout is { } value
            ? $"AI gateway timeout after {value}."
            : "AI gateway timeout.";
}

internal sealed class HumanProxyActor : ReceiveActor
{
    public HumanProxyActor(OccupantId occupant)
    {
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));

        Receive<OrgMessage>(message =>
        {
            GenericMessageCompletion.Return(Context.Parent, message);
        });
    }

    public OccupantId Occupant { get; }
}

internal static class GenericMessageCompletion
{
    public static void Return(IActorRef parent, OrgMessage message)
    {
        var directiveId = message is Hive.Domain.Messaging.Directive directive
            ? directive.DirectiveId
            : DirectiveId.From(message.Id.Value);

        parent.Tell(new PositionOccupantProcessingCompleted(
            $"message:{message.Id.Value:N}:delivery",
            message.Id,
            message.Thread,
            directiveId,
            PositionOccupantProcessingStatus.Completed));
    }
}
