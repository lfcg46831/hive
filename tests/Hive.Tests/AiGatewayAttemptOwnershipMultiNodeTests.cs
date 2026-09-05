using System.Globalization;
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

/// <summary>
/// Verifies BUG-023 with two gateway nodes and stub providers: a direct call and a fallback that
/// reach the same provider share one admission limit and one circuit in the cluster, the attempt
/// audit stays with the coordinator of each journey, and cancellation keeps its owner.
/// </summary>
[Collection(nameof(AkkaClusterCollection))]
public sealed class AiGatewayAttemptOwnershipMultiNodeTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("77777777-7777-7777-7777-777777777777"));
    private static readonly AiProviderMetadata Primary = new("openai", "gpt-5-mini");
    private static readonly AiProviderMetadata Secondary = new("anthropic", "claude-sonnet");

    [Fact]
    public async Task A_direct_call_and_a_fallback_share_one_admission_limit_in_the_cluster()
    {
        var probe = new SharedProviderProbe();
        var resolver = Resolver(maxConcurrentCalls: 1);
        var firstAudit = new RecordingAuditPublisher();
        var secondAudit = new RecordingAuditPublisher();
        var seedPort = GetFreeTcpPort();
        using var first = BuildHost(
            seedPort,
            seedPort,
            new ProbeProvider("node-1", probe, FailPrimarySucceedSecondary, Secondary.ProviderId),
            resolver,
            firstAudit);
        using var second = BuildHost(
            GetFreeTcpPort(),
            seedPort,
            new ProbeProvider("node-2", probe, FailPrimarySucceedSecondary, Secondary.ProviderId),
            resolver,
            secondAudit);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        try
        {
            await StartRoutableClusterAsync([first, second], timeout.Token);

            // One journey enters directly on the first node and holds the provider.
            var direct = first.Services.GetRequiredService<IAiGateway>().CompleteAsync(
                Request(Secondary, policy: null),
                timeout.Token);
            Assert.True(await probe.WaitForEntriesAsync(Secondary.ProviderId, 1));

            // A second journey enters on the other node and falls back to the same provider.
            var fallback = second.Services.GetRequiredService<IAiGateway>().CompleteAsync(
                Request(Primary, Policy(Secondary)),
                timeout.Token);
            Assert.True(await probe.WaitForEntriesAsync(Primary.ProviderId, 1));
            await Task.Delay(TimeSpan.FromSeconds(2), timeout.Token);

            // Without a shared owner the fallback would run against a second local bucket.
            Assert.Single(probe.Entries(Secondary.ProviderId));

            probe.Release();
            var responses = await Task.WhenAll(direct, fallback);

            Assert.All(responses, response => Assert.True(response.IsSuccess));
            var secondary = probe.Entries(Secondary.ProviderId);
            Assert.Equal(2, secondary.Count);
            Assert.Single(secondary.Select(entry => entry.NodeName).Distinct(StringComparer.Ordinal));
            Assert.Equal(1, probe.MaxConcurrency(Secondary.ProviderId));
        }
        finally
        {
            await StopAsync(second, first);
        }
    }

    [Fact]
    public async Task An_open_circuit_at_the_owner_short_circuits_a_call_entering_from_another_node()
    {
        var probe = new SharedProviderProbe();
        var resolver = Resolver(maxConcurrentCalls: 4, failureThreshold: 2);
        var firstAudit = new RecordingAuditPublisher();
        var secondAudit = new RecordingAuditPublisher();
        var seedPort = GetFreeTcpPort();
        using var first = BuildHost(
            seedPort,
            seedPort,
            new ProbeProvider("node-1", probe, FailEveryProvider),
            resolver,
            firstAudit);
        using var second = BuildHost(
            GetFreeTcpPort(),
            seedPort,
            new ProbeProvider("node-2", probe, FailEveryProvider),
            resolver,
            secondAudit);
        using var timeout = new CancellationTokenSource(TimeSpan.FromSeconds(120));

        try
        {
            await StartRoutableClusterAsync([first, second], timeout.Token);
            var firstGateway = first.Services.GetRequiredService<IAiGateway>();

            for (var journey = 0; journey < 2; journey++)
            {
                var exhausted = await firstGateway.CompleteAsync(
                    Request(Primary, Policy(Secondary)),
                    timeout.Token);
                Assert.True(exhausted.IsFailure);
            }

            var opened = probe.Entries(Secondary.ProviderId);
            Assert.Equal(2, opened.Count);
            Assert.Single(opened.Select(entry => entry.NodeName).Distinct(StringComparer.Ordinal));

            var direct = await second.Services.GetRequiredService<IAiGateway>().CompleteAsync(
                Request(Secondary, policy: null),
                timeout.Token);

            Assert.True(direct.IsFailure);
            Assert.Equal(AiGatewayErrorReason.CircuitOpen, direct.Error!.Reason);
            Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, direct.Error.Code);
            // The short-circuited attempt never reached the adapter on any node.
            Assert.Equal(2, probe.Entries(Secondary.ProviderId).Count);

            // Auditing belongs to the coordinator of each journey and is never duplicated.
            Assert.Equal(4, Attempts(firstAudit).Count);
            Assert.Equal(2, Journeys(firstAudit).Count);
            Assert.Single(Attempts(secondAudit));
            Assert.Single(Journeys(secondAudit));
            Assert.False(Attempts(secondAudit)[0].ReachedProvider);
        }
        finally
        {
            await StopAsync(second, first);
        }
    }

    private static IReadOnlyList<AiGatewayCostAuditEvent> Attempts(RecordingAuditPublisher audit) =>
        audit.Events
            .Where(@event => @event.Scope == AiGatewayCostAuditScope.Attempt)
            .ToArray();

    private static IReadOnlyList<AiGatewayCostAuditEvent> Journeys(RecordingAuditPublisher audit) =>
        audit.Events
            .Where(@event => @event.Scope == AiGatewayCostAuditScope.Journey)
            .ToArray();

    private static async Task StartRoutableClusterAsync(
        IReadOnlyList<IHost> hosts,
        CancellationToken cancellationToken)
    {
        foreach (var host in hosts)
        {
            await host.StartAsync(cancellationToken);
        }

        foreach (var host in hosts)
        {
            var system = host.Services.GetRequiredService<ActorSystem>();
            var region = host.Services.GetRequiredService<AiGatewayShardRegion>();
            while (Cluster.Get(system).State.Members.Count(
                       member => member.Status == MemberStatus.Up) != hosts.Count
                   || !region.CanRoute)
            {
                await Task.Delay(100, cancellationToken);
            }
        }
    }

    private static async Task StopAsync(params IHost[] hosts)
    {
        foreach (var host in hosts)
        {
            await host.StopAsync();
        }
    }

    private static IHost BuildHost(
        int port,
        int seedPort,
        IAiGatewayProvider provider,
        IAiProviderResiliencePolicyResolver resolver,
        IAiGatewayAuditPublisher audit)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });

        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Cluster:Hostname"] = "127.0.0.1",
            ["Hive:Cluster:Port"] = port.ToString(CultureInfo.InvariantCulture),
            ["Hive:Node:Roles:0"] = NodeRoleNames.Gateway,
            ["Hive:Cluster:SeedNodes:0"] = $"akka.tcp://hive@127.0.0.1:{seedPort}",
            ["Hive:OccupantChannels:CorrelationTokens:SigningKey"] =
                OccupantChannelCorrelationTokenTests.SigningKey(),
        });

        builder.AddHiveBootstrap();
        builder.Services.AddSingleton<IAiGatewayProvider>(provider);
        builder.Services.AddSingleton<IAiProviderResiliencePolicyResolver>(resolver);
        builder.Services.AddSingleton<IAiGatewayAuditPublisher>(audit);
        builder.Services.AddSingleton<IAiGatewayDetailedAuditPublisher>(
            new DiscardingDetailedAuditPublisher());
        builder.AddHiveActorSystem();
        return builder.Build();
    }

    private static IAiProviderResiliencePolicyResolver Resolver(
        int maxConcurrentCalls,
        int failureThreshold = 5) =>
        new FixedPolicyResolver(new AiProviderResiliencePolicy(
            new AiProviderRateLimitPolicy(
                maxConcurrentCalls,
                maxCallsPerWindow: 100,
                TimeSpan.FromMinutes(5)),
            new AiProviderQueuePolicy(maxDepth: 16, TimeSpan.FromSeconds(60)),
            new AiProviderRetryPolicy(
                maxAttempts: 1,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(10),
                jitterRatio: 0m),
            new AiProviderCircuitBreakerPolicy(
                TimeSpan.FromMinutes(5),
                failureThreshold,
                TimeSpan.FromMinutes(5),
                halfOpenMaxConcurrentProbes: 1)));

    private static AiGatewayPolicy Policy(params AiProviderMetadata[] fallback) =>
        new(
            new[] { Primary }.Concat(fallback),
            hasAvailableBudget: true,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback: fallback);

    private static AiGatewayRequest Request(AiProviderMetadata provider, AiGatewayPolicy? policy) =>
        new(
            Organization,
            Position,
            Thread,
            MessageId.From(Guid.NewGuid()),
            "Classify this bug.",
            provider: provider,
            policy: policy);

    private static AiGatewayResponse FailPrimarySucceedSecondary(AiGatewayRequest request) =>
        string.Equals(request.Provider!.ProviderId, Primary.ProviderId, StringComparison.Ordinal)
            ? Failure(request)
            : Success(request);

    private static AiGatewayResponse FailEveryProvider(AiGatewayRequest request) => Failure(request);

    private static AiGatewayResponse Success(AiGatewayRequest request) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            "Done.",
            AiFinishReason.Stop,
            request.Provider);

    private static AiGatewayResponse Failure(AiGatewayRequest request) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.Timeout,
            "Provider failed.",
            isRetryable: true,
            request.Provider));

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(System.Net.IPAddress.Loopback, 0);
        listener.Start();
        try
        {
            return ((System.Net.IPEndPoint)listener.LocalEndpoint).Port;
        }
        finally
        {
            listener.Stop();
        }
    }

    private sealed class FixedPolicyResolver(AiProviderResiliencePolicy policy)
        : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => policy;
    }

    /// <summary>The journey envelope is not what this test observes; keep it out of the way.</summary>
    private sealed class DiscardingDetailedAuditPublisher : IAiGatewayDetailedAuditPublisher
    {
        public void Publish(AiGatewayAuditEnvelope envelope)
        {
        }
    }

    private sealed class RecordingAuditPublisher : IAiGatewayAuditPublisher
    {
        private readonly object _gate = new();
        private readonly List<AiGatewayCostAuditEvent> _events = [];

        public IReadOnlyList<AiGatewayCostAuditEvent> Events
        {
            get
            {
                lock (_gate)
                {
                    return _events.ToArray();
                }
            }
        }

        public void Publish(AiGatewayCostAuditEvent auditEvent)
        {
            lock (_gate)
            {
                _events.Add(auditEvent);
            }
        }
    }

    /// <summary>
    /// The adapter of both nodes, sharing one probe so the test can tell which node actually
    /// executed each attempt and how many of them ran at the same time.
    /// </summary>
    private sealed class ProbeProvider(
        string nodeName,
        SharedProviderProbe probe,
        Func<AiGatewayRequest, AiGatewayResponse> respond,
        string? heldProviderId = null) : IAiGatewayProvider
    {
        public async Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            var providerId = request.Provider!.ProviderId;
            probe.Enter(nodeName, providerId);
            try
            {
                if (string.Equals(providerId, heldProviderId, StringComparison.Ordinal))
                {
                    await probe.Released.WaitAsync(cancellationToken).ConfigureAwait(false);
                }

                return respond(request);
            }
            finally
            {
                probe.Exit(providerId);
            }
        }
    }

    private sealed class SharedProviderProbe
    {
        private readonly object _gate = new();
        private readonly List<ProviderEntry> _entries = [];
        private readonly Dictionary<string, int> _inFlight = new(StringComparer.Ordinal);
        private readonly Dictionary<string, int> _peak = new(StringComparer.Ordinal);
        private readonly TaskCompletionSource _release =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Released => _release.Task;

        public void Release() => _release.TrySetResult();

        public void Enter(string nodeName, string providerId)
        {
            lock (_gate)
            {
                _entries.Add(new ProviderEntry(nodeName, providerId));
                var inFlight = (_inFlight.TryGetValue(providerId, out var current) ? current : 0) + 1;
                _inFlight[providerId] = inFlight;
                _peak[providerId] = Math.Max(
                    _peak.TryGetValue(providerId, out var peak) ? peak : 0,
                    inFlight);
            }
        }

        public void Exit(string providerId)
        {
            lock (_gate)
            {
                _inFlight[providerId] = _inFlight[providerId] - 1;
            }
        }

        public IReadOnlyList<ProviderEntry> Entries(string providerId)
        {
            lock (_gate)
            {
                return _entries
                    .Where(entry => string.Equals(
                        entry.ProviderId,
                        providerId,
                        StringComparison.Ordinal))
                    .ToArray();
            }
        }

        public int MaxConcurrency(string providerId)
        {
            lock (_gate)
            {
                return _peak.TryGetValue(providerId, out var peak) ? peak : 0;
            }
        }

        public async Task<bool> WaitForEntriesAsync(string providerId, int count)
        {
            for (var attempt = 0; attempt < 300; attempt++)
            {
                if (Entries(providerId).Count >= count)
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(100));
            }

            return false;
        }
    }

    private sealed record ProviderEntry(string NodeName, string ProviderId);
}
