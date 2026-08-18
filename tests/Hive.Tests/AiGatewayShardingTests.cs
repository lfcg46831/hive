using Akka.Actor;
using Hive.Actors.Gateway;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

/// <summary>
/// Verifies the placement contract of US-F1-05-T07: the AI gateway entity is addressed by the
/// effective ProviderId, hosted only on the gateway role, and reached from agents nodes through a
/// region proxy.
/// </summary>
public sealed class AiGatewayShardingTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("delivery-lead");
    private static readonly ThreadId Thread = ThreadId.From(Guid.NewGuid());
    private static readonly MessageId Message = MessageId.From(Guid.NewGuid());

    [Fact]
    public void Entity_id_is_the_effective_provider_id()
    {
        Assert.Equal(
            "openai",
            AiGatewayEntityId.ForRequest(Request(new AiProviderMetadata("openai", "gpt-5.6-luna"))));
        Assert.Equal(
            "openai",
            AiGatewayEntityId.ForRequest(Request(new AiProviderMetadata("openai", "gpt-5.4-mini"))));
        Assert.Equal(
            "anthropic",
            AiGatewayEntityId.ForRequest(Request(new AiProviderMetadata("anthropic", "claude"))));
    }

    [Fact]
    public void Requests_without_an_effective_provider_share_the_local_bucket()
    {
        Assert.Equal(AiGatewayEntityId.LocalProviderKey, AiGatewayEntityId.ForRequest(Request()));
        Assert.Equal(AiGatewayEntityId.LocalProviderKey, AiGatewayEntityId.ForProvider(null));
        Assert.Equal("local-bucket", AiGatewayEntityId.LocalProviderKey);
    }

    [Fact]
    public void Extractor_routes_envelopes_and_unwraps_the_command()
    {
        var extractor = new AiGatewayMessageExtractor();
        var command = new CancelAiGatewayCall("corr-1");
        var envelope = new AiGatewayEnvelope("openai", command);

        Assert.Equal("openai", extractor.EntityId(envelope));
        Assert.Same(command, extractor.EntityMessage(envelope));
        Assert.Equal(
            extractor.EntityId(new AiGatewayEnvelope("openai", new CancelAiGatewayCall("corr-2"))),
            extractor.EntityId(envelope));
        Assert.NotEqual(
            extractor.EntityId(new AiGatewayEnvelope("anthropic", command)),
            extractor.EntityId(envelope));
    }

    [Fact]
    public void Extractor_drops_unaddressed_messages()
    {
        var extractor = new AiGatewayMessageExtractor();

        Assert.Null(extractor.EntityId("not an envelope"));
        Assert.Equal("not an envelope", extractor.EntityMessage("not an envelope"));
    }

    [Fact]
    public void Extractor_rejects_a_non_positive_shard_count()
    {
        Assert.Throws<ArgumentOutOfRangeException>(() => new AiGatewayMessageExtractor(0));
        Assert.Equal(12, AiGatewayMessageExtractor.DefaultNumberOfShards);
    }

    [Fact]
    public void Region_is_hosted_on_the_gateway_role_and_proxied_from_agents()
    {
        using var system = ActorSystem.Create($"ai-gateway-roles-{Guid.NewGuid():N}");
        var region = new AiGatewayShardRegion();
        var options = Options.Create(new HiveOptions());

        var hosting = new AiGatewayShardingWorkload(
            system,
            new StubGateway(),
            region,
            options,
            NullLoggerFor<AiGatewayShardingWorkload>());
        var proxy = new AiGatewayShardProxyWorkload(
            system,
            region,
            new ActiveNodeRoles(Options.Create(new HiveOptions
            {
                Node = new NodeOptions { Roles = [NodeRoleNames.Agents] },
            })),
            options,
            NullLoggerFor<AiGatewayShardProxyWorkload>());

        Assert.Equal(NodeRoleNames.Gateway, hosting.Role);
        Assert.Equal(NodeRoleNames.Agents, proxy.Role);
        Assert.Equal(AiGatewayMessageExtractor.DefaultNumberOfShards, hosting.NumberOfShards);
        Assert.Equal(AiGatewayShardingWorkload.DefaultClusterUpTimeout, hosting.ClusterUpTimeout);
        Assert.Null(hosting.Region);
        Assert.Null(proxy.Proxy);
    }

    [Fact]
    public async Task Proxy_is_not_started_when_the_node_already_hosts_the_region()
    {
        using var system = ActorSystem.Create($"ai-gateway-colocated-{Guid.NewGuid():N}");
        var region = new AiGatewayShardRegion();
        var proxy = new AiGatewayShardProxyWorkload(
            system,
            region,
            new ActiveNodeRoles(Options.Create(new HiveOptions
            {
                Node = new NodeOptions
                {
                    Roles = [NodeRoleNames.Agents, NodeRoleNames.Gateway],
                },
            })),
            Options.Create(new HiveOptions()),
            NullLoggerFor<AiGatewayShardProxyWorkload>());

        // An all-in-one node must not build a second route: the hosted region already is one.
        await proxy.StartAsync(CancellationToken.None);

        Assert.Null(proxy.Proxy);
        Assert.False(region.IsRouted);
    }

    [Fact]
    public void Configured_placement_overrides_the_defaults()
    {
        using var system = ActorSystem.Create($"ai-gateway-config-{Guid.NewGuid():N}");
        var options = Options.Create(new HiveOptions
        {
            Gateway = new GatewayNodeOptions
            {
                NumberOfShards = 3,
                ClusterUpTimeout = TimeSpan.FromSeconds(7),
                AskTimeout = TimeSpan.FromSeconds(11),
            },
        });

        var hosting = new AiGatewayShardingWorkload(
            system,
            new StubGateway(),
            new AiGatewayShardRegion(),
            options,
            NullLoggerFor<AiGatewayShardingWorkload>());

        Assert.Equal(3, hosting.NumberOfShards);
        Assert.Equal(TimeSpan.FromSeconds(7), hosting.ClusterUpTimeout);
    }

    [Fact]
    public void Non_positive_gateway_placement_values_fail_validation()
    {
        var validator = new HiveOptionsValidator();
        var options = new HiveOptions
        {
            Node = new NodeOptions { Roles = [NodeRoleNames.Gateway] },
            Gateway = new GatewayNodeOptions
            {
                NumberOfShards = 0,
                ClusterUpTimeout = TimeSpan.Zero,
                AskTimeout = TimeSpan.FromSeconds(-1),
            },
        };

        var result = validator.Validate(name: null, options);

        Assert.True(result.Failed);
        Assert.Contains(result.Failures!, failure => failure.Contains("Hive:Gateway:NumberOfShards"));
        Assert.Contains(result.Failures!, failure => failure.Contains("Hive:Gateway:ClusterUpTimeout"));
        Assert.Contains(result.Failures!, failure => failure.Contains("Hive:Gateway:AskTimeout"));
    }

    private static Microsoft.Extensions.Logging.ILogger<T> NullLoggerFor<T>() =>
        Microsoft.Extensions.Logging.Abstractions.NullLogger<T>.Instance;

    private static AiGatewayRequest Request(AiProviderMetadata? provider = null) =>
        new(Organization, Position, Thread, Message, "Classify.", provider: provider);

    private sealed class StubGateway : IAiGateway
    {
        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiGatewayResponse.Succeeded(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                "ok",
                AiFinishReason.Stop));
    }
}
