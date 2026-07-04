using Akka.Actor;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;

namespace Hive.Actors.Positions;

internal interface IPositionOccupantFactory
{
    Props Create(OccupantId occupant, OccupantType occupantType);
}

internal sealed class PositionOccupantFactory : IPositionOccupantFactory
{
    public static PositionOccupantFactory Instance { get; } = new();

    private readonly IAiAgentGatewayInvoker _aiGatewayInvoker;

    public PositionOccupantFactory()
        : this(UnavailableAiAgentGatewayInvoker.Instance)
    {
    }

    public PositionOccupantFactory(IAiAgentGatewayInvoker aiGatewayInvoker)
    {
        _aiGatewayInvoker = aiGatewayInvoker
            ?? throw new ArgumentNullException(nameof(aiGatewayInvoker));
    }

    public Props Create(OccupantId occupant, OccupantType occupantType)
    {
        ArgumentNullException.ThrowIfNull(occupant);

        return occupantType switch
        {
            OccupantType.AiAgent => Props.Create(() => new AiAgentActor(
                occupant,
                _aiGatewayInvoker)),
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

    public AiAgentActor(OccupantId occupant)
        : this(occupant, UnavailableAiAgentGatewayInvoker.Instance)
    {
    }

    public AiAgentActor(OccupantId occupant, IAiAgentGatewayInvoker gatewayInvoker)
    {
        Occupant = occupant ?? throw new ArgumentNullException(nameof(occupant));
        GatewayInvoker = gatewayInvoker
            ?? throw new ArgumentNullException(nameof(gatewayInvoker));

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
        Receive<OrgMessage>(_ =>
        {
        });
    }

    public OccupantId Occupant { get; }

    internal IAiAgentGatewayInvoker GatewayInvoker { get; }

    private async Task HandleDirectiveProcessingRequestAsync(AiDirectiveProcessingRequest request)
    {
        var context = AiDirectiveExecutionContext.From(request);
        var received = AiDirectiveProcessingSnapshot.Received(request);
        if (context.IdentityPrompt is null)
        {
            _directiveExecutionContexts[request.CorrelationId] = context;
            _directiveProcessingSnapshots[request.CorrelationId] = received.AdvanceTo(
                AiDirectiveProcessingStatus.Failed,
                reason: IdentityPromptFailureReason(context));
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

        using var timeout = CreateGatewayTimeout(prompt.Timeout);
        try
        {
            var result = await GatewayInvoker
                .InvokeAsync(
                    new AiAgentGatewayInvocation(request.CorrelationId, prompt),
                    timeout?.Token ?? CancellationToken.None)
                .ConfigureAwait(false);
            _directiveGatewayInvocations[request.CorrelationId] = result;
            var interpretation = AiDirectiveDecisionInterpreter.Interpret(result);
            _directiveInterpretations[request.CorrelationId] = interpretation;

            if (interpretation.IsDecision)
            {
                var responseInterpreted = gatewayRequested.AdvanceTo(
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    reason: "AI gateway response interpreted");
                var resultMessage = AiDirectiveResultMessageFactory.Create(
                    context,
                    interpretation.Decision!);
                _directiveResultMessages[request.CorrelationId] = resultMessage;
                _directiveProcessingSnapshots[request.CorrelationId] =
                    resultMessage.IsSuccess
                        ? responseInterpreted.AdvanceTo(
                            AiDirectiveProcessingStatus.ResultEmitted,
                            reason: "AI directive result message materialized")
                        : responseInterpreted.AdvanceTo(
                            AiDirectiveProcessingStatus.Escalated,
                            reason: resultMessage.Failure!.AuditReason);
            }
            else if (interpretation.IsStructuredError)
            {
                _directiveProcessingSnapshots[request.CorrelationId] =
                    gatewayRequested.AdvanceTo(
                        AiDirectiveProcessingStatus.Failed,
                        reason: interpretation.Failure!.AuditReason);
            }
            else if (interpretation.RequiresEscalation)
            {
                _directiveProcessingSnapshots[request.CorrelationId] =
                    gatewayRequested.AdvanceTo(
                        AiDirectiveProcessingStatus.Escalated,
                        reason: interpretation.Failure!.AuditReason);
            }
        }
        catch (OperationCanceledException) when (timeout?.IsCancellationRequested == true)
        {
            _directiveProcessingSnapshots[request.CorrelationId] =
                gatewayRequested.AdvanceTo(
                    AiDirectiveProcessingStatus.Failed,
                    reason: GatewayTimeoutReason(prompt.Timeout));
        }
        catch (OperationCanceledException)
        {
            _directiveProcessingSnapshots[request.CorrelationId] =
                gatewayRequested.AdvanceTo(
                    AiDirectiveProcessingStatus.Failed,
                    reason: "AI gateway request was canceled before a response was returned.");
        }
    }

    private static string IdentityPromptFailureReason(AiDirectiveExecutionContext context) =>
        $"Identity prompt '{context.IdentityPromptRef ?? "<missing>"}' was not resolved; directive processing stopped before gateway request.";

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

        Receive<OrgMessage>(_ =>
        {
        });
    }

    public OccupantId Occupant { get; }
}
