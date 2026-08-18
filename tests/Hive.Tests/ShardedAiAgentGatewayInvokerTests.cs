using Akka.Actor;
using Hive.Actors.Gateway;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F1-05-T07: the internal gateway API of US-F0-07-T12 keeps its contract while routing
/// to the provider entity — same correlation, same structured response, same cancellation
/// semantics — and falls back to the in-process gateway when no route can be used.
/// </summary>
public sealed class ShardedAiAgentGatewayInvokerTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread = ThreadId.From(Guid.NewGuid());
    private static readonly MessageId Message = MessageId.From(Guid.NewGuid());

    [Fact]
    public async Task Call_uses_the_in_process_gateway_when_no_route_was_materialized()
    {
        var colocated = new RecordingInvoker(Succeeded());
        var invoker = new ShardedAiAgentGatewayInvoker(
            new AiGatewayShardRegion(),
            colocated,
            TimeSpan.FromSeconds(1));

        var result = await invoker.InvokeAsync(Invocation());

        Assert.True(result.IsSuccess);
        Assert.Equal("corr-1", result.CorrelationId);
        Assert.Equal(1, colocated.Calls);
    }

    [Fact]
    public async Task Call_uses_the_in_process_gateway_while_no_gateway_member_is_up()
    {
        using var system = ActorSystem.Create($"ai-gateway-route-{Guid.NewGuid():N}");
        var route = system.ActorOf(RouteActor.Props(new AiGatewayCallCompleted("corr-1", Succeeded())));
        var region = new AiGatewayShardRegion();
        region.Publish(route, hasGatewayMember: () => false);
        var colocated = new RecordingInvoker(Succeeded());
        var invoker = new ShardedAiAgentGatewayInvoker(region, colocated, TimeSpan.FromSeconds(1));

        var result = await invoker.InvokeAsync(Invocation());

        Assert.True(result.IsSuccess);
        Assert.Equal(1, colocated.Calls);
        Assert.True(region.IsRouted);
        Assert.False(region.CanRoute);
    }

    [Fact]
    public async Task Routed_call_returns_the_entity_response_and_preserves_correlation()
    {
        using var system = ActorSystem.Create($"ai-gateway-route-{Guid.NewGuid():N}");
        var response = Succeeded();
        var route = system.ActorOf(RouteActor.Props(new AiGatewayCallCompleted("corr-1", response)));
        var colocated = new RecordingInvoker(Succeeded());
        var invoker = new ShardedAiAgentGatewayInvoker(
            Routed(region: new AiGatewayShardRegion(), route),
            colocated,
            TimeSpan.FromSeconds(5));

        var result = await invoker.InvokeAsync(Invocation());

        Assert.Equal("corr-1", result.CorrelationId);
        Assert.Same(response, result.Response);
        Assert.Equal(0, colocated.Calls);
    }

    [Fact]
    public async Task Routed_call_addresses_the_entity_of_the_effective_provider()
    {
        using var system = ActorSystem.Create($"ai-gateway-route-{Guid.NewGuid():N}");
        var route = system.ActorOf(RouteActor.Props(new AiGatewayCallCompleted("corr-1", Succeeded())));
        var invoker = new ShardedAiAgentGatewayInvoker(
            Routed(new AiGatewayShardRegion(), route),
            new RecordingInvoker(Succeeded()),
            TimeSpan.FromSeconds(5));

        await invoker.InvokeAsync(Invocation());
        var envelopes = await RouteActor.EnvelopesOf(route);

        var envelope = Assert.Single(envelopes);
        Assert.Equal("openai", envelope.ProviderKey);
        var command = Assert.IsType<CompleteAiGatewayCall>(envelope.Command);
        Assert.Equal("corr-1", command.CorrelationId);
        Assert.Equal(Message, command.Request.MessageId);
    }

    [Fact]
    public async Task Transport_failure_is_sanitized_and_retryable()
    {
        using var system = ActorSystem.Create($"ai-gateway-route-{Guid.NewGuid():N}");
        var route = system.ActorOf(RouteActor.Props(reply: null));
        var invoker = new ShardedAiAgentGatewayInvoker(
            Routed(new AiGatewayShardRegion(), route),
            new RecordingInvoker(Succeeded()),
            TimeSpan.FromMilliseconds(250));

        var result = await invoker.InvokeAsync(Invocation());

        var error = result.FailureReason!;
        Assert.True(result.IsFailure);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, error.Code);
        Assert.True(error.IsRetryable);
        Assert.Equal("corr-1", result.CorrelationId);
        Assert.DoesNotContain("akka", error.Message, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Caller_cancellation_cancels_the_entity_and_propagates()
    {
        using var system = ActorSystem.Create($"ai-gateway-route-{Guid.NewGuid():N}");
        var route = system.ActorOf(RouteActor.Props(reply: null));
        var invoker = new ShardedAiAgentGatewayInvoker(
            Routed(new AiGatewayShardRegion(), route),
            new RecordingInvoker(Succeeded()),
            TimeSpan.FromSeconds(30));
        using var cancellation = new CancellationTokenSource();

        var pending = invoker.InvokeAsync(Invocation(), cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);

        // The cancel command and the canceled ask race by construction, so wait for the entity to
        // observe it rather than assuming an ordering the runtime does not guarantee.
        Assert.True(await RouteActor.EventuallyReceivedAsync(
            route,
            envelope => envelope.Command is CancelAiGatewayCall));
    }

    private static AiGatewayShardRegion Routed(AiGatewayShardRegion region, IActorRef route)
    {
        region.Publish(route, hasGatewayMember: () => true);
        return region;
    }

    private static AiAgentGatewayInvocation Invocation() =>
        new(
            "corr-1",
            new AiGatewayRequest(
                Organization,
                Position,
                Thread,
                Message,
                "Classify the incoming directive.",
                provider: new AiProviderMetadata("openai", "gpt-5.6-luna")));

    private static AiGatewayResponse Succeeded() =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "classified",
            AiFinishReason.Stop);

    private sealed class RecordingInvoker : IAiAgentGatewayInvoker
    {
        private readonly AiGatewayResponse _response;

        public RecordingInvoker(AiGatewayResponse response)
        {
            _response = response;
        }

        public int Calls { get; private set; }

        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(
                AiAgentGatewayInvocationResult.FromResponse(invocation.CorrelationId, _response));
        }
    }

    /// <summary>Stands in for the shard region: records envelopes and optionally answers.</summary>
    private sealed class RouteActor : ReceiveActor
    {
        private readonly List<AiGatewayEnvelope> _envelopes = new();

        public RouteActor(object? reply)
        {
            Receive<AiGatewayEnvelope>(envelope =>
            {
                _envelopes.Add(envelope);
                if (reply is not null && envelope.Command is CompleteAiGatewayCall)
                {
                    Sender.Tell(reply, Self);
                }
            });
            Receive<GetEnvelopes>(_ => Sender.Tell(_envelopes.ToArray(), Self));
        }

        public static Props Props(object? reply) =>
            Akka.Actor.Props.Create(() => new RouteActor(reply));

        public static async Task<IReadOnlyList<AiGatewayEnvelope>> EnvelopesOf(IActorRef route) =>
            await route.Ask<AiGatewayEnvelope[]>(new GetEnvelopes(), TimeSpan.FromSeconds(5));

        public static async Task<bool> EventuallyReceivedAsync(
            IActorRef route,
            Func<AiGatewayEnvelope, bool> predicate)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if ((await EnvelopesOf(route)).Any(predicate))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            return false;
        }

        public sealed record GetEnvelopes;
    }
}
