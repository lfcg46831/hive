using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Actors.Gateway;

/// <summary>
/// Routes the internal gateway API of US-F0-07-T12 to the provider entity introduced by
/// US-F1-05-T07. The signature the <c>AiAgentActor</c> consumes does not change: the same
/// correlation id, the same <see cref="AiGatewayResponse"/>, the same structured failures and the
/// same cancellation semantics — only the hop changes.
/// </summary>
/// <remarks>
/// When no route can be used — this node materialized none, or the cluster has no <c>gateway</c>
/// member up to host the provider entities — the call goes straight to the in-process gateway.
/// That is the colocated topology, where routing through the cluster would add a hop without
/// adding a boundary; it is a documented degradation, never a silent failure.
/// </remarks>
internal sealed class ShardedAiAgentGatewayInvoker : IAiAgentGatewayInvoker
{
    /// <summary>
    /// Transport-timeout default. It must exceed the worst case of the resilience pipeline —
    /// queue wait plus every retry, its backoff and the provider timeout — so a slow but healthy
    /// call is never turned into a transport failure.
    /// </summary>
    public static readonly TimeSpan DefaultAskTimeout = TimeSpan.FromMinutes(5);

    private readonly AiGatewayShardRegion _region;
    private readonly IAiAgentGatewayInvoker _colocated;
    private readonly TimeSpan _askTimeout;

    public ShardedAiAgentGatewayInvoker(
        AiGatewayShardRegion region,
        IAiGateway gateway,
        IOptions<HiveOptions> options)
    {
        _region = region ?? throw new ArgumentNullException(nameof(region));
        ArgumentNullException.ThrowIfNull(gateway);
        ArgumentNullException.ThrowIfNull(options);

        _colocated = new AiAgentGatewayInvoker(gateway);
        _askTimeout = options.Value.Gateway?.AskTimeout ?? DefaultAskTimeout;
    }

    internal ShardedAiAgentGatewayInvoker(
        AiGatewayShardRegion region,
        IAiAgentGatewayInvoker colocated,
        TimeSpan askTimeout)
    {
        _region = region ?? throw new ArgumentNullException(nameof(region));
        _colocated = colocated ?? throw new ArgumentNullException(nameof(colocated));
        _askTimeout = askTimeout;
    }

    public async Task<AiAgentGatewayInvocationResult> InvokeAsync(
        AiAgentGatewayInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);

        if (!_region.CanRoute || _region.Route is not { } route)
        {
            return await _colocated.InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        }

        var providerKey = AiGatewayEntityId.ForRequest(invocation.Request);
        var correlationId = invocation.CorrelationId;

        // The caller's token is the authority over cancellation, exactly as in US-F1-05-T04/T06.
        // Its wire form is an explicit cancel command to the very same entity, so the in-flight
        // provider call is canceled instead of being abandoned.
        using var registration = cancellationToken.Register(
            () => route.Tell(
                new AiGatewayEnvelope(providerKey, new CancelAiGatewayCall(correlationId)),
                ActorRefs.NoSender));

        try
        {
            var reply = await route
                .Ask<object>(
                    new AiGatewayEnvelope(
                        providerKey,
                        new CompleteAiGatewayCall(correlationId, invocation.Request)),
                    _askTimeout,
                    cancellationToken)
                .ConfigureAwait(false);

            return reply switch
            {
                AiGatewayCallCompleted completed when Correlates(completed.CorrelationId, correlationId) =>
                    AiAgentGatewayInvocationResult.FromResponse(correlationId, completed.Response),
                AiGatewayCallCanceled canceled when Correlates(canceled.CorrelationId, correlationId) =>
                    throw new OperationCanceledException(cancellationToken),
                _ => TransportFailure(invocation),
            };
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            // Ask timeout, a rebalancing entity or any other transport failure: sanitized and
            // retryable, never an address, CLR type or transport diagnostic.
            return TransportFailure(invocation);
        }
    }

    private static bool Correlates(string replyCorrelationId, string correlationId) =>
        string.Equals(replyCorrelationId, correlationId, StringComparison.Ordinal);

    private static AiAgentGatewayInvocationResult TransportFailure(
        AiAgentGatewayInvocation invocation)
    {
        var request = invocation.Request;
        return AiAgentGatewayInvocationResult.FromResponse(
            invocation.CorrelationId,
            AiGatewayResponse.Failed(new AiGatewayError(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                AiGatewayErrorCode.ProviderUnavailable,
                "AI gateway invocation failed before returning a structured response.",
                isRetryable: true,
                request.Provider)));
    }
}
