using Akka.Actor;
using Hive.Domain.Ai;
using Microsoft.Extensions.Logging;

namespace Hive.Actors.Gateway;

/// <summary>
/// The thin AI gateway actor reserved by US-F0-07-T02/T12 and introduced by US-F1-05-T07. One
/// entity per provider is materialized by Cluster Sharding on nodes with the <c>gateway</c> role,
/// which makes the queue, rate limiter and circuit breaker of that provider unique in the cluster.
/// </summary>
/// <remarks>
/// <para>
/// The actor is deliberately thin: it receives a request already assembled by the caller, delegates
/// it exactly once to <see cref="IAiGateway"/> and returns the response unchanged. Pre-call policy,
/// normalization, adapters, the fallback chain, cost attribution and auditing stay in the gateway
/// and are never duplicated here.
/// </para>
/// <para>
/// The provider call runs outside the actor's message loop, so a single provider entity still
/// admits the concurrency its resilience policy allows; only the admission decision is serialized
/// by the mailbox. Each in-flight call keeps a cancellation source keyed by correlation id, so a
/// <see cref="CancelAiGatewayCall"/> — the wire form of the caller's token — cancels exactly that
/// call and answers <see cref="AiGatewayCallCanceled"/> without producing a response or audit.
/// </para>
/// </remarks>
public sealed class AiGatewayActor : ReceiveActor
{
    private readonly IAiGateway _gateway;
    private readonly ILogger? _logger;
    private readonly Dictionary<string, CancellationTokenSource> _inFlight =
        new(StringComparer.Ordinal);

    public AiGatewayActor(IAiGateway gateway, ILogger? logger = null)
    {
        _gateway = gateway ?? throw new ArgumentNullException(nameof(gateway));
        _logger = logger;

        Receive<CompleteAiGatewayCall>(Handle);
        Receive<CancelAiGatewayCall>(Handle);
        Receive<CallSettled>(Handle);
    }

    public static Props Props(IAiGateway gateway, ILogger? logger = null)
    {
        ArgumentNullException.ThrowIfNull(gateway);
        return Akka.Actor.Props.Create(() => new AiGatewayActor(gateway, logger));
    }

    private void Handle(CompleteAiGatewayCall call)
    {
        var replyTo = Sender;
        var correlationId = call.CorrelationId;

        if (_inFlight.ContainsKey(correlationId))
        {
            // A duplicate correlation id would make cancellation ambiguous. Fail it structurally
            // instead of silently attaching to the call already in flight.
            replyTo.Tell(
                new AiGatewayCallCompleted(correlationId, TransportFailure(call.Request)),
                Self);
            return;
        }

        var cancellation = new CancellationTokenSource();
        _inFlight[correlationId] = cancellation;

        var self = Self;
        Invoke(_gateway, call.Request, cancellation.Token)
            .ContinueWith(
                task => Settle(correlationId, replyTo, call.Request, task),
                CancellationToken.None,
                TaskContinuationOptions.ExecuteSynchronously,
                TaskScheduler.Default)
            .PipeTo(self);
    }

    /// <summary>
    /// Calls the gateway defensively: a provider seam that throws synchronously, or hands back no
    /// task at all, must surface as the same sanitized terminal instead of restarting the entity
    /// and leaving the caller waiting for a reply that will never come.
    /// </summary>
    private static Task<AiGatewayResponse> Invoke(
        IAiGateway gateway,
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        try
        {
            return gateway.CompleteAsync(request, cancellationToken)
                ?? Task.FromResult<AiGatewayResponse>(null!);
        }
        catch (Exception failure)
        {
            return Task.FromException<AiGatewayResponse>(failure);
        }
    }

    private void Handle(CancelAiGatewayCall cancel)
    {
        if (_inFlight.TryGetValue(cancel.CorrelationId, out var cancellation))
        {
            cancellation.Cancel();
        }
    }

    private void Handle(CallSettled settled)
    {
        if (_inFlight.Remove(settled.CorrelationId, out var cancellation))
        {
            cancellation.Dispose();
        }

        settled.ReplyTo.Tell(settled.Reply, Self);
    }

    private static CallSettled Settle(
        string correlationId,
        IActorRef replyTo,
        AiGatewayRequest request,
        Task<AiGatewayResponse> task)
    {
        if (task.IsCanceled)
        {
            return new CallSettled(correlationId, replyTo, new AiGatewayCallCanceled(correlationId));
        }

        if (task.IsFaulted)
        {
            var failure = task.Exception?.GetBaseException();
            var reply = failure is OperationCanceledException
                ? new AiGatewayCallCanceled(correlationId)
                : (object)new AiGatewayCallCompleted(correlationId, TransportFailure(request));
            return new CallSettled(correlationId, replyTo, reply);
        }

        var response = task.Result ?? TransportFailure(request);
        return new CallSettled(
            correlationId,
            replyTo,
            new AiGatewayCallCompleted(correlationId, response));
    }

    /// <summary>
    /// The single sanitized terminal for a call that never produced a structured response. It
    /// mirrors the invoker's pre-T07 wording, so callers see no behavioural drift, and it exposes
    /// no address, CLR type or transport diagnostic.
    /// </summary>
    private static AiGatewayResponse TransportFailure(AiGatewayRequest request) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.ProviderUnavailable,
            "AI gateway invocation failed before returning a structured response.",
            isRetryable: true,
            request.Provider));

    protected override void PostStop()
    {
        foreach (var cancellation in _inFlight.Values)
        {
            cancellation.Cancel();
            cancellation.Dispose();
        }

        _inFlight.Clear();
        _logger?.LogDebug("AI gateway entity stopped; in-flight calls were canceled.");
        base.PostStop();
    }

    private sealed record CallSettled(string CorrelationId, IActorRef ReplyTo, object Reply);
}
