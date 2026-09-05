using System.Collections.Concurrent;
using Akka.Actor;
using Akka.Cluster;
using Hive.Actors;
using Hive.Actors.Gateway;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

[Collection(nameof(AkkaClusterCollection))]
public sealed class AiGatewayFallbackClusterTests
{
    [Fact]
    public async Task Direct_and_fallback_attempts_share_owner_limits_circuit_and_cancellation()
    {
        var firstPort = FreePort();
        var firstProvider = new Provider();
        var secondProvider = new Provider();
        var audit = new Audit();
        using var first = Host(firstPort, firstPort, firstProvider, audit);
        using var second = Host(FreePort(), firstPort, secondProvider, audit);
        using var deadline = new CancellationTokenSource(TimeSpan.FromSeconds(90));
        var token = deadline.Token;
        try
        {
            await first.StartAsync(token);
            // Allocate A before the second region joins, then B to the empty region.
            await Call(first, Request("a"), token);
            await second.StartAsync(token);
            await Until(() => Cluster.Get(first.Services.GetRequiredService<ActorSystem>())
                .State.Members.Count(m => m.Status == MemberStatus.Up) == 2, token);
            await Until(() => second.Services.GetRequiredService<AiGatewayShardingWorkload>().Region is not null, token);
            Assert.True((await Call(second, Request("b"), token)).IsSuccess);
            Assert.Contains(firstProvider.Requests, r => r.Provider!.ProviderId == "a");
            Assert.Contains(secondProvider.Requests, r => r.Provider!.ProviderId == "b");
            Assert.DoesNotContain(firstProvider.Requests, r => r.Provider!.ProviderId == "b");

            var block = secondProvider.BlockNext();
            var direct = Call(second, Request("b"), token);
            await block.Entered.Task.WaitAsync(token);
            var saturated = Request("a", "b");
            var rejected = await Call(first, saturated, token);
            Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, rejected.Error!.Code);
            Assert.Equal(AiGatewayErrorReason.FallbackExhausted, rejected.Error.Reason);
            var rejectedAttempts = audit.Attempts(saturated);
            Assert.Equal(2, rejectedAttempts.Length);
            Assert.Equal("b", rejectedAttempts[1].Provider!.ProviderId);
            Assert.False(rejectedAttempts[1].ReachedProvider);
            Assert.Null(rejectedAttempts[1].Cost);
            Assert.Single(audit.Journeys(saturated));
            block.Release.TrySetResult();
            Assert.True((await direct).IsSuccess);

            var successful = Request("a", "b");
            Assert.True((await Call(first, successful, token)).IsSuccess);
            var charged = audit.Attempts(successful);
            Assert.Equal(new[] { "0-1", "1-1" }, charged.Select(e => e.AttemptId));
            Assert.Equal(new[] { "a", "b" }, charged.Select(e => e.Provider!.ProviderId));
            Assert.Equal(0.25m, charged[1].Cost!.Amount);
            Assert.True(charged[1].ReachedProvider);
            Assert.Single(audit.Journeys(successful));

            // An unauthorized fallback is skipped before any attempt is sent to B.
            var restricted = Request("a", "b", authorizeFallback: false);
            var bBefore = secondProvider.Requests.Count;
            await Call(first, restricted, token);
            Assert.Single(audit.Attempts(restricted));
            Assert.Equal(bBefore, secondProvider.Requests.Count);

            // Cancel while the fallback is executing on the other node, and verify its lease
            // is released and its incomplete attempt produces no audit or charge.
            var cancelBlock = secondProvider.BlockNext();
            var canceledRequest = Request("a", "b");
            using var cancel = CancellationTokenSource.CreateLinkedTokenSource(token);
            var canceled = Call(first, canceledRequest, cancel.Token);
            await cancelBlock.Entered.Task.WaitAsync(token);
            cancel.Cancel();
            await Assert.ThrowsAnyAsync<OperationCanceledException>(() => canceled);
            await cancelBlock.Canceled.Task.WaitAsync(token);
            Assert.Single(audit.Attempts(canceledRequest));
            Assert.Empty(audit.Journeys(canceledRequest));
            Assert.True((await Call(second, Request("b"), token)).IsSuccess);

            // Two failed direct calls open B's circuit. Fallback must see that same circuit.
            secondProvider.Fail = true;
            await Call(second, Request("b"), token);
            await Call(second, Request("b"), token);
            var callsBeforeCircuit = secondProvider.Requests.Count;
            var circuitRequest = Request("a", "b");
            var circuit = await Call(first, circuitRequest, token);
            Assert.Equal("AI provider circuit is open.", circuit.Error!.Message);
            Assert.False(audit.Attempts(circuitRequest).Last().ReachedProvider);
            Assert.Equal(callsBeforeCircuit, secondProvider.Requests.Count);
            var directCircuit = await Call(second, Request("b"), token);
            Assert.Equal(AiGatewayErrorReason.CircuitOpen, directCircuit.Error!.Reason);
            Assert.DoesNotContain(firstProvider.Requests, r => r.Provider!.ProviderId == "b");

            // C admits only one call per window, even after its concurrent lease is released.
            Assert.True((await Call(second, Request("c"), token)).IsSuccess);
            var window = await Call(first, Request("a", "c"), token);
            Assert.Equal(AiGatewayErrorCode.GatewayOverloaded, window.Error!.Code);
            Assert.Equal(1, firstProvider.Requests.Concat(secondProvider.Requests)
                .Count(r => r.Provider!.ProviderId == "c"));
        }
        finally
        {
            await second.StopAsync();
            await first.StopAsync();
        }
    }

    private static async Task<AiGatewayResponse> Call(IHost host, AiGatewayRequest request, CancellationToken token)
    {
        var invoker = host.Services.GetRequiredService<Hive.Actors.Positions.IAiAgentGatewayInvoker>();
        var result = await invoker.InvokeAsync(new(Guid.NewGuid().ToString("N"), request), token);
        return result.Response;
    }

    private static AiGatewayRequest Request(string provider, string? fallback = null, bool authorizeFallback = true)
    {
        var primary = new AiProviderMetadata(provider, "model");
        var next = fallback is null ? Array.Empty<AiProviderMetadata>() : [new AiProviderMetadata(fallback, "model")];
        return new(OrganizationId.From("acme"), PositionId.From("lead"),
            ThreadId.From(Guid.NewGuid()), MessageId.From(Guid.NewGuid()), "test", provider: primary,
            policy: new AiGatewayPolicy(authorizeFallback ? new[] { primary }.Concat(next) : [primary],
                true, 100, TimeSpan.FromSeconds(10), null, null, next));
    }

    private static IHost Host(int port, int seed, Provider provider, Audit audit)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings { DisableDefaults = true });
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = port.ToString(),
            ["Hive:Cluster:SeedNodes:0"] = $"akka.tcp://hive@127.0.0.1:{seed}",
            ["Hive:Node:Roles:0"] = NodeRoleNames.Gateway,
            ["Hive:Gateway:AskTimeout"] = "00:00:20",
            ["Hive:OccupantChannels:CorrelationTokens:SigningKey"] = OccupantChannelCorrelationTokenTests.SigningKey(),
        });
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        builder.Services.AddSingleton<IAiGatewayProvider>(provider);
        builder.Services.AddSingleton<IAiGatewayAuditPublisher>(audit);
        builder.Services.AddSingleton<IAiProviderResiliencePolicyResolver>(new Policies());
        return builder.Build();
    }

    private sealed class Policies : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => new(
            new(1, provider?.ProviderId == "c" ? 1 : 100, TimeSpan.FromMinutes(5)),
            new(0, TimeSpan.Zero), new(1, TimeSpan.FromMilliseconds(1), TimeSpan.FromMilliseconds(1), 0),
            new(TimeSpan.FromMinutes(5), provider?.ProviderId == "b" ? 2 : 100,
                TimeSpan.FromMinutes(5), 1));
    }

    private sealed class Audit : IAiGatewayAuditPublisher
    {
        private readonly ConcurrentQueue<AiGatewayCostAuditEvent> _events = new();
        public void Publish(AiGatewayCostAuditEvent value) => _events.Enqueue(value);
        public AiGatewayCostAuditEvent[] Attempts(AiGatewayRequest r) => _events
            .Where(e => e.MessageId == r.MessageId && e.Scope == AiGatewayCostAuditScope.Attempt).ToArray();
        public AiGatewayCostAuditEvent[] Journeys(AiGatewayRequest r) => _events
            .Where(e => e.MessageId == r.MessageId && e.Scope == AiGatewayCostAuditScope.Journey).ToArray();
    }

    private sealed class Block
    {
        public TaskCompletionSource Entered { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Release { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
        public TaskCompletionSource Canceled { get; } = new(TaskCreationOptions.RunContinuationsAsynchronously);
    }

    private sealed class Provider : IAiGatewayProvider
    {
        public ConcurrentQueue<AiGatewayRequest> Requests { get; } = new();
        public volatile bool Fail;
        private Block? _block;
        public Block BlockNext() => _block = new();
        public async Task<AiGatewayResponse> CompleteAsync(AiGatewayRequest request, CancellationToken token)
        {
            Requests.Enqueue(request);
            if (request.Provider!.ProviderId == "b" && Interlocked.Exchange(ref _block, null) is { } block)
            {
                block.Entered.TrySetResult();
                try { await block.Release.Task.WaitAsync(token); }
                catch (OperationCanceledException) { block.Canceled.TrySetResult(); throw; }
            }

            if (request.Provider.ProviderId == "a" || (request.Provider.ProviderId == "b" && Fail))
                return AiGatewayResponse.Failed(new AiGatewayError(request.OrganizationId, request.PositionId,
                    request.ThreadId, request.MessageId, AiGatewayErrorCode.ProviderUnavailable,
                    "AI provider is unavailable.", true, request.Provider));
            return AiGatewayResponse.Succeeded(request.OrganizationId, request.PositionId, request.ThreadId,
                request.MessageId, "ok", AiFinishReason.Stop, request.Provider,
                cost: new AiCostMetadata(0.25m, "EUR", true));
        }
    }

    private static async Task Until(Func<bool> condition, CancellationToken token)
    {
        while (!condition()) await Task.Delay(50, token);
    }

    private static int FreePort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        var port = ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        listener.Stop();
        return port;
    }
}
