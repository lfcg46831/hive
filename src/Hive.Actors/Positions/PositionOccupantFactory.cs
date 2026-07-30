using Akka.Actor;
using System.Text;
using Hive.Actors.Serialization;
using Hive.Application.Directives;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
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
    private readonly IAiAgentActionGate _actionGate;
    private readonly IJourneyAuditLog _auditLog;
    private readonly IDirectiveAuditExportResultSink _auditExportResultSink;
    private readonly IAiDirectiveOutcomeResolutionIntegrator _outcomeResolutionIntegrator;
    private readonly AiDirectiveExecutionCoordinator? _executionCoordinator;

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
        : this(
            aiGatewayInvoker,
            resultMessageGate,
            AllowingAiAgentActionGate.Instance,
            auditLog)
    {
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog)
        : this(
            aiGatewayInvoker,
            resultMessageGate,
            actionGate,
            auditLog,
            NoopDirectiveAuditExportStore.Instance)
    {
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog,
        IDirectiveAuditExportResultSink auditExportResultSink)
        : this(
            aiGatewayInvoker,
            resultMessageGate,
            actionGate,
            auditLog,
            auditExportResultSink,
            PassthroughAiDirectiveOutcomeResolutionIntegrator.Instance)
    {
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog,
        IDirectiveAuditExportResultSink auditExportResultSink,
        IAiDirectiveOutcomeResolutionIntegrator outcomeResolutionIntegrator)
        : this(
            aiGatewayInvoker,
            resultMessageGate,
            actionGate,
            auditLog,
            auditExportResultSink,
            outcomeResolutionIntegrator,
            executionCoordinator: null)
    {
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog,
        IDirectiveAuditExportResultSink auditExportResultSink,
        IAiDirectiveOutcomeResolutionIntegrator outcomeResolutionIntegrator,
        AiDirectiveExecutionCoordinator? executionCoordinator)
    {
        _aiGatewayInvoker = aiGatewayInvoker
            ?? throw new ArgumentNullException(nameof(aiGatewayInvoker));
        _resultMessageGate = resultMessageGate
            ?? throw new ArgumentNullException(nameof(resultMessageGate));
        _actionGate = actionGate ?? throw new ArgumentNullException(nameof(actionGate));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _auditExportResultSink = auditExportResultSink
            ?? throw new ArgumentNullException(nameof(auditExportResultSink));
        _outcomeResolutionIntegrator = outcomeResolutionIntegrator
            ?? throw new ArgumentNullException(nameof(outcomeResolutionIntegrator));
        _executionCoordinator = executionCoordinator;
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate)
        : this(aiGatewayInvoker, resultMessageGate, NoopJourneyAuditLog.Instance)
    {
    }

    public PositionOccupantFactory(
        IAiAgentGatewayInvoker aiGatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate)
        : this(aiGatewayInvoker, resultMessageGate, actionGate, NoopJourneyAuditLog.Instance)
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
                _actionGate,
                _auditLog,
                _auditExportResultSink,
                _outcomeResolutionIntegrator,
                _executionCoordinator)),
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
    private const string TerminalDecisionAlreadyMaterializedReason =
        "terminal-agent-decision-already-materialized";
    private const string RecoveredRejectedResultCode =
        "processing-escalated";

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
    private readonly Dictionary<string, AiAgentActionGateResult> _directiveActionGateResults =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveIterationAuditTrail> _directiveIterationAudits =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectivePositionEffects> _directivePositionEffects =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveAuditSnapshot> _directiveAudits =
        new(StringComparer.Ordinal);
    private readonly Dictionary<string, AiDirectiveOutcomeResolutionResult>
        _directiveOutcomeResolutions = new(StringComparer.Ordinal);
    private readonly IJourneyAuditLog _auditLog;
    private readonly IDirectiveAuditExportResultSink _auditExportResultSink;
    private readonly AiDirectiveExecutionCoordinator _executionCoordinator;

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
        : this(
            occupant,
            gatewayInvoker,
            resultMessageGate,
            AllowingAiAgentActionGate.Instance,
            auditLog)
    {
    }

    public AiAgentActor(
        OccupantId occupant,
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog)
        : this(
            occupant,
            gatewayInvoker,
            resultMessageGate,
            actionGate,
            auditLog,
            NoopDirectiveAuditExportStore.Instance)
    {
    }

    public AiAgentActor(
        OccupantId occupant,
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog,
        IDirectiveAuditExportResultSink auditExportResultSink)
        : this(
            occupant,
            gatewayInvoker,
            resultMessageGate,
            actionGate,
            auditLog,
            auditExportResultSink,
            PassthroughAiDirectiveOutcomeResolutionIntegrator.Instance)
    {
    }

    public AiAgentActor(
        OccupantId occupant,
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog,
        IDirectiveAuditExportResultSink auditExportResultSink,
        IAiDirectiveOutcomeResolutionIntegrator outcomeResolutionIntegrator)
        : this(
            occupant,
            gatewayInvoker,
            resultMessageGate,
            actionGate,
            auditLog,
            auditExportResultSink,
            outcomeResolutionIntegrator,
            executionCoordinator: null)
    {
    }

    public AiAgentActor(
        OccupantId occupant,
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate,
        IJourneyAuditLog auditLog,
        IDirectiveAuditExportResultSink auditExportResultSink,
        IAiDirectiveOutcomeResolutionIntegrator outcomeResolutionIntegrator,
        AiDirectiveExecutionCoordinator? executionCoordinator)
    {
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        GatewayInvoker = gatewayInvoker
            ?? throw new ArgumentNullException(nameof(gatewayInvoker));
        ResultMessageGate = resultMessageGate
            ?? throw new ArgumentNullException(nameof(resultMessageGate));
        ActionGate = actionGate ?? throw new ArgumentNullException(nameof(actionGate));
        _auditLog = auditLog ?? throw new ArgumentNullException(nameof(auditLog));
        _auditExportResultSink = auditExportResultSink
            ?? throw new ArgumentNullException(nameof(auditExportResultSink));
        var requiredOutcomeResolutionIntegrator = outcomeResolutionIntegrator
            ?? throw new ArgumentNullException(nameof(outcomeResolutionIntegrator));
        _executionCoordinator = executionCoordinator
            ?? new AiDirectiveExecutionCoordinator(
                GatewayInvoker,
                ResultMessageGate,
                ActionGate,
                requiredOutcomeResolutionIntegrator);

        ReceiveAsync<AiAgentGatewayInvocation>(async invocation =>
        {
            var replyTo = Sender;
            var result = await GatewayInvoker
                .InvokeAsync(invocation, CancellationToken.None)
                .ConfigureAwait(false);
            replyTo.Tell(result);
        });
        ReceiveAsync<AiDirectiveProcessingRequest>(HandleCoordinatedDirectiveProcessingAsync);
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
        Receive<GetAiAgentActionGateResult>(query =>
        {
            Sender.Tell(new AiAgentActionGateQueryResult(
                query.CorrelationId,
                _directiveActionGateResults.GetValueOrDefault(query.CorrelationId)));
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
        Receive<GetAiDirectiveOutcomeResolution>(query =>
        {
            Sender.Tell(new AiDirectiveOutcomeResolutionQueryResult(
                query.CorrelationId,
                _directiveOutcomeResolutions.GetValueOrDefault(query.CorrelationId)));
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

    public AiAgentActor(
        OccupantId occupant,
        IAiAgentGatewayInvoker gatewayInvoker,
        IAiDirectiveResultMessageGate resultMessageGate,
        IAiAgentActionGate actionGate)
        : this(
            occupant,
            gatewayInvoker,
            resultMessageGate,
            actionGate,
            NoopJourneyAuditLog.Instance)
    {
    }

    public OccupantId Occupant { get; }

    internal IAiAgentGatewayInvoker GatewayInvoker { get; }

    internal IAiDirectiveResultMessageGate ResultMessageGate { get; }

    internal IAiAgentActionGate ActionGate { get; }

    private sealed record AiDirectiveRecoveryDecision(
        AiDirectiveProcessingSnapshot Snapshot,
        DirectiveExecutionResult Result);

    private async Task HandleCoordinatedDirectiveProcessingAsync(
        AiDirectiveProcessingRequest request)
    {
        var parent = Context.Parent;
        Action<object> publishAudit = Context.System.EventStream.Publish;
        var context = _executionCoordinator.CreateContext(request);
        var received = AiDirectiveProcessingSnapshot.Received(request);
        if (TryRecoverJourney(request, received) is { } recovered)
        {
            _directiveExecutionContexts[request.CorrelationId] = context;
            _directiveProcessingSnapshots[request.CorrelationId] = recovered.Snapshot;
            ReturnCompletion(parent, recovered.Result);
            return;
        }

        var execution = await _executionCoordinator
            .ExecuteDetailedAsync(request, context, CancellationToken.None)
            .ConfigureAwait(false);
        StoreExecutionTrace(execution);

        foreach (var effect in execution.Result.Effects
            .Where(effect => effect is not DirectiveJourneyAuditEffect))
        {
            await DispatchEffectAsync(parent, effect).ConfigureAwait(false);
        }

        ReturnCompletion(parent, execution.Result);

        foreach (var auditEffect in execution.Result.Effects
            .OfType<DirectiveJourneyAuditEffect>())
        {
            _auditLog.Append(auditEffect.Record);
        }

        publishAudit(execution.Audit);
    }

    private void StoreExecutionTrace(AiDirectiveExecutionCoordinatorResult execution)
    {
        var correlationId = execution.Result.CorrelationId;
        _directiveExecutionContexts[correlationId] = execution.Context;
        _directiveProcessingSnapshots[correlationId] = execution.Processing;
        _directiveAudits[correlationId] = execution.Audit;

        if (execution.InitialPrompt is not null)
        {
            _directiveInitialPrompts[correlationId] = execution.InitialPrompt;
        }

        if (execution.GatewayInvocation is not null)
        {
            _directiveGatewayInvocations[correlationId] = execution.GatewayInvocation;
        }

        if (execution.Interpretation is not null)
        {
            _directiveInterpretations[correlationId] = execution.Interpretation;
        }

        if (execution.ResultMessage is not null)
        {
            _directiveResultMessages[correlationId] = execution.ResultMessage;
        }

        if (execution.ActionGateResult is not null)
        {
            _directiveActionGateResults[correlationId] = execution.ActionGateResult;
        }

        if (execution.IterationAudit is not null)
        {
            _directiveIterationAudits[correlationId] = execution.IterationAudit;
        }

        if (execution.PositionEffects is not null)
        {
            _directivePositionEffects[correlationId] = execution.PositionEffects;
        }

        if (execution.OutcomeResolution?.WasEvaluated == true)
        {
            _directiveOutcomeResolutions[correlationId] = execution.OutcomeResolution;
        }
    }

    private async ValueTask DispatchEffectAsync(
        IActorRef parent,
        DirectiveExecutionEffect effect)
    {
        switch (effect)
        {
            case DirectiveAuditExportResultEffect export:
                if (export.ResultMessage.From is not PositionEndpointRef source)
                {
                    throw new InvalidOperationException(
                        "Directive result export requires a position source.");
                }

                await _auditExportResultSink.StoreAsync(
                    new DirectiveAuditExportResultData(
                        export.ResultMessage.OrganizationId,
                        export.ResultMessage.Thread,
                        export.DirectiveId,
                        source.PositionId,
                        export.ResultMessage.GetType().Name,
                        export.ResultMessage.SchemaVersion,
                        Encoding.UTF8.GetString(
                            OrgMessageJsonFormat.Serialize(export.ResultMessage))),
                    CancellationToken.None).ConfigureAwait(false);
                break;
            case DirectivePositionCommandEffect position:
                parent.Tell(position.Command);
                break;
            case DirectiveMessageEffect message:
                parent.Tell(message.Message);
                break;
            default:
                throw new InvalidOperationException(
                    $"Unsupported directive execution effect '{effect.GetType().Name}'.");
        }
    }


    private AiDirectiveRecoveryDecision? TryRecoverJourney(
        AiDirectiveProcessingRequest request,
        AiDirectiveProcessingSnapshot received)
    {
        var records = _auditLog
            .ReadByThread(request.ThreadId, request.DirectiveId)
            .Where(record =>
                record.OrganizationId == request.OrganizationId
                && record.MessageId == request.MessageId
                && record.PositionId == request.PositionId)
            .ToArray();

        var resultMessage = records
            .LastOrDefault(record => record.Stage == JourneyAuditStage.ResultMessageCreated);
        if (resultMessage is not null)
        {
            RecordDuplicateSuppression(
                request,
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

            var result = resultMessage.Outcome == JourneyAuditOutcome.Succeeded
                ? DirectiveExecutionResult.Completed(request.ExecutionRequest)
                : DirectiveExecutionResult.Escalated(
                    request.ExecutionRequest,
                    RecoveredRejectedResultCode);

            return new AiDirectiveRecoveryDecision(snapshot, result);
        }

        var terminalDecision = records
            .LastOrDefault(record =>
                record.Stage == JourneyAuditStage.AgentDecided
                && record.Outcome is JourneyAuditOutcome.Failed or JourneyAuditOutcome.Rejected);
        if (terminalDecision is not null)
        {
            RecordDuplicateSuppression(
                request,
                terminalDecision,
                TerminalDecisionAlreadyMaterializedReason);

            var failureCode = TerminalCode(terminalDecision);
            var snapshot = RecoveredGatewayRequested(received)
                .AdvanceTo(
                    AiDirectiveProcessingStatus.Failed,
                    reason: failureCode);

            return new AiDirectiveRecoveryDecision(
                snapshot,
                DirectiveExecutionResult.Failed(
                    request.ExecutionRequest,
                    failureCode));
        }

        var gatewayCalled = records
            .LastOrDefault(record => record.Stage == JourneyAuditStage.GatewayCalled);
        var agentDecided = records.Any(record => record.Stage == JourneyAuditStage.AgentDecided);
        if (gatewayCalled is not null && !agentDecided)
        {
            RecordDuplicateSuppression(
                request,
                gatewayCalled,
                GatewayCallAlreadyMaterializedReason);

            var snapshot = RecoveredGatewayRequested(received)
                .AdvanceTo(
                    AiDirectiveProcessingStatus.Failed,
                    reason: GatewayCallWithoutTerminalResultCode);

            return new AiDirectiveRecoveryDecision(
                snapshot,
                DirectiveExecutionResult.Failed(
                    request.ExecutionRequest,
                    GatewayCallWithoutTerminalResultCode));
        }

        return null;
    }

    private static string TerminalCode(JourneyAuditRecord terminalDecision) =>
        terminalDecision.Payload.TryGetValue("terminalCode", out var terminalCode)
        && !string.IsNullOrWhiteSpace(terminalCode)
            ? terminalCode
            : terminalDecision.ReasonCode ?? "processing-failed";

    private void RecordDuplicateSuppression(
        AiDirectiveProcessingRequest request,
        JourneyAuditRecord suppressed,
        string reasonCode)
    {
        _auditLog.Append(JourneyAuditRecord.Create(
            JourneyAuditStage.DuplicateSuppressed,
            JourneyAuditOutcome.Rejected,
            request.OrganizationId,
            request.ThreadId,
            request.MessageId,
            request.DirectiveId,
            request.PositionId,
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
        DirectiveExecutionResult result)
    {
        ArgumentNullException.ThrowIfNull(parent);
        ArgumentNullException.ThrowIfNull(result);

        var status = result.Status switch
        {
            DirectiveExecutionStatus.Completed =>
                PositionOccupantProcessingStatus.Completed,
            DirectiveExecutionStatus.Escalated =>
                PositionOccupantProcessingStatus.Escalated,
            DirectiveExecutionStatus.Failed =>
                PositionOccupantProcessingStatus.Failed,
            _ => throw new ArgumentOutOfRangeException(
                nameof(result),
                result.Status,
                "Unknown directive execution status."),
        };
        parent.Tell(new PositionOccupantProcessingCompleted(
            result.CorrelationId,
            result.MessageId,
            result.ThreadId,
            result.DirectiveId,
            status,
            result.FailureCode));
    }

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
