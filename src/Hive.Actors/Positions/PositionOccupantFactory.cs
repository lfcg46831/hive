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

            if (result.IsFailure)
            {
                _directiveProcessingSnapshots[request.CorrelationId] =
                    gatewayRequested.AdvanceTo(
                        AiDirectiveProcessingStatus.Failed,
                        reason: GatewayFailureReason(result));
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

    private static string GatewayFailureReason(AiAgentGatewayInvocationResult result) =>
        result.FailureReason is { } error
            ? $"AI gateway request failed with '{error.Code}'."
            : "AI gateway request failed without a structured reason.";

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
