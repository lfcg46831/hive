using Akka.Actor;
using Hive.Domain.Ai;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Actors.Gateway;

/// <summary>Every attempt, including retries and fallback, uses its effective provider's owner.</summary>
internal sealed class ShardedAiGatewayAttemptExecutor(
    AiGatewayShardRegion region,
    LocalAiGatewayAttemptExecutor local,
    IOptions<HiveOptions> options) : IAiGatewayAttemptExecutor
{
    public async Task<AiGatewayAttemptResult> ExecuteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (!region.CanRoute || region.Route is not { } route)
        {
            return await local.ExecuteAsync(request, cancellationToken).ConfigureAwait(false);
        }

        var providerKey = AiGatewayEntityId.ForRequest(request);
        // Distinct from the journey correlation and from every other attempt, including retries.
        var correlationId = Guid.NewGuid().ToString("N");
        var command = new AiGatewayEnvelope(providerKey, new ExecuteAiGatewayAttempt(correlationId, request));
        // Send before registering cancellation: a token already canceled during dispatch still
        // sends cancel after execute, using the same sender to preserve remote ordering.
        IActorRef? sender = ActorRefs.NoSender;
        var pending = route.Ask<object>(new Func<IActorRef, object>(replyTo =>
            {
                sender = replyTo;
                return command;
            }),
            options.Value.Gateway?.AskTimeout ?? ShardedAiAgentGatewayInvoker.DefaultAskTimeout,
            CancellationToken.None);
        using var registration = cancellationToken.Register(() => route.Tell(
            new AiGatewayEnvelope(providerKey, new CancelAiGatewayCall(correlationId)), sender));

        try
        {
            var reply = await pending.WaitAsync(cancellationToken).ConfigureAwait(false);
            cancellationToken.ThrowIfCancellationRequested();
            return reply switch
            {
                AiGatewayAttemptCompleted completed when completed.CorrelationId == correlationId =>
                    completed.Result,
                AiGatewayCallCanceled canceled when canceled.CorrelationId == correlationId =>
                    throw new OperationCanceledException(cancellationToken),
                _ => throw new InvalidOperationException("AI gateway attempt failed before returning a structured response."),
            };
        }
        catch
        {
            route.Tell(new AiGatewayEnvelope(providerKey, new CancelAiGatewayCall(correlationId)), sender);
            throw;
        }
    }
}
