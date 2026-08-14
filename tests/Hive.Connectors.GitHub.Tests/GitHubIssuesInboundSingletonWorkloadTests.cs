using Akka.Actor;
using Akka.Cluster;
using Akka.Configuration;
using Hive.Domain.Identity;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Logging.Abstractions;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub.Tests;

[Collection(nameof(GitHubAkkaClusterCollection))]
public sealed class GitHubIssuesInboundSingletonWorkloadTests
{
    [Fact]
    public async Task Source_actor_runs_cycles_serially_without_overlapping_mailbox_triggers()
    {
        using var system = ActorSystem.Create($"hive-github-source-{Guid.NewGuid():N}");
        var poller = new BlockingPoller(expectedCalls: 4);
        var actor = system.ActorOf(GitHubIssuesInboundSourceActor.Props(
            poller,
            TimeProvider.System,
            NullLogger.Instance));
        actor.Tell(PollGitHubIssuesInbound.Instance);
        actor.Tell(PollGitHubIssuesInbound.Instance);
        actor.Tell(PollGitHubIssuesInbound.Instance);

        await poller.Completed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(4, poller.CallCount);
        Assert.Equal(1, poller.MaximumConcurrency);
        await system.Terminate();
    }

    [Fact]
    public async Task Source_actor_processes_staged_events_after_each_polling_cycle()
    {
        using var system = ActorSystem.Create($"hive-github-processing-{Guid.NewGuid():N}");
        var order = new List<string>();
        var poller = new OrderedPoller(order);
        var processor = new OrderedProcessor(order);
        system.ActorOf(GitHubIssuesInboundSourceActor.Props(
            poller,
            processor,
            TimeProvider.System,
            NullLogger.Instance));

        await processor.Completed.WaitAsync(TimeSpan.FromSeconds(10));

        Assert.Equal(["poll", "process"], order);
        await system.Terminate();
    }

    [Fact]
    public async Task Configured_workload_materializes_stable_connectors_singleton_and_polls_once()
    {
        var port = GetFreeTcpPort();
        var systemName = $"hive-github-singleton-{Guid.NewGuid():N}";
        var system = ActorSystem.Create(
            systemName,
            ConfigurationFactory.ParseString($$"""
                akka.actor.provider = cluster
                akka.remote.dot-netty.tcp.hostname = "127.0.0.1"
                akka.remote.dot-netty.tcp.port = {{port}}
                akka.cluster.roles = ["{{NodeRoleNames.Connectors}}"]
                """));
        try
        {
            var cluster = Cluster.Get(system);
            cluster.Join(cluster.SelfAddress);
            await WaitForAsync(
                () => cluster.SelfMember.Status == MemberStatus.Up,
                TimeSpan.FromSeconds(20));
            var poller = new CountingPoller();
            var migrations = 0;
            var workload = new GitHubIssuesInboundSingletonWorkload(
                system,
                CatalogWithOneInstance(),
                poller,
                TimeProvider.System,
                NullLoggerFactory.Instance,
                TimeSpan.FromSeconds(10),
                _ =>
                {
                    Interlocked.Increment(ref migrations);
                    return Task.CompletedTask;
                });

            await workload.StartAsync(CancellationToken.None);
            await WaitForAsync(() => poller.CallCount == 1, TimeSpan.FromSeconds(20));

            Assert.True(workload.IsEnabled);
            Assert.Equal(NodeRoleNames.Connectors, workload.Role);
            Assert.Equal(1, migrations);
            Assert.Equal(
                GitHubIssuesInboundSingletonIdentity.SingletonManagerName,
                workload.Manager!.Path.Name);
            Assert.Equal(
                GitHubIssuesInboundSingletonIdentity.ProxyName,
                workload.Proxy!.Path.Name);

            var manager = workload.Manager;
            var proxy = workload.Proxy;
            await workload.StartAsync(CancellationToken.None);

            Assert.Same(manager, workload.Manager);
            Assert.Same(proxy, workload.Proxy);
            Assert.Equal(1, migrations);
            Assert.Equal(1, poller.CallCount);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task Empty_catalog_is_inert_without_cluster_or_migration_access()
    {
        using var system = ActorSystem.Create($"hive-github-disabled-{Guid.NewGuid():N}");
        var migrations = 0;
        var poller = new CountingPoller();
        var workload = new GitHubIssuesInboundSingletonWorkload(
            system,
            EmptyCatalog(),
            poller,
            TimeProvider.System,
            NullLoggerFactory.Instance,
            TimeSpan.FromSeconds(1),
            _ =>
            {
                Interlocked.Increment(ref migrations);
                return Task.CompletedTask;
            });

        await workload.StartAsync(CancellationToken.None);

        Assert.False(workload.IsEnabled);
        Assert.Null(workload.Manager);
        Assert.Null(workload.Proxy);
        Assert.Equal(0, migrations);
        Assert.Equal(0, poller.CallCount);
        await system.Terminate();
    }

    private static GitHubIssuesConnectorConfigurationCatalog CatalogWithOneInstance() =>
        new(Options.Create(new GitHubIssuesConnectorOptions
        {
            Instances =
            [
                new GitHubIssuesConnectorInstanceOptions
                {
                    InstanceId = "acme-github",
                    OrganizationId = "acme",
                    Repositories = ["acme/payments"],
                    InboundDirectiveTarget = "bug-triage",
                    OutboundOperations = [],
                    Polling = new GitHubIssuesPollingOptions
                    {
                        Interval = "PT1H",
                        PageSize = 100,
                    },
                },
            ],
            Credentials =
            [
                new GitHubIssuesConnectorCredentialOptions
                {
                    InstanceId = "acme-github",
                    Token = "test-token",
                },
            ],
        }));

    private static GitHubIssuesConnectorConfigurationCatalog EmptyCatalog() =>
        new(Options.Create(new GitHubIssuesConnectorOptions()));

    private static async Task WaitForAsync(Func<bool> condition, TimeSpan timeout)
    {
        var deadline = DateTime.UtcNow + timeout;
        while (DateTime.UtcNow < deadline)
        {
            if (condition())
            {
                return;
            }

            await Task.Delay(100);
        }

        throw new TimeoutException("Condition was not met within the allotted time.");
    }

    private static int GetFreeTcpPort()
    {
        var listener = new System.Net.Sockets.TcpListener(
            System.Net.IPAddress.Loopback,
            0);
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

    private sealed class CountingPoller : IGitHubIssuesInboundPoller
    {
        private int _callCount;

        public int CallCount => Volatile.Read(ref _callCount);

        public Task<GitHubIssuesPollingCycleResult> PollDueRepositoriesAsync(
            CancellationToken cancellationToken = default)
        {
            Interlocked.Increment(ref _callCount);
            return Task.FromResult(new GitHubIssuesPollingCycleResult(
            [
                new GitHubIssuesRepositoryPollResult(
                    "acme-github",
                    "acme/payments",
                    GitHubIssuesRepositoryPollStatus.Deferred,
                    0,
                    0,
                    DateTimeOffset.UtcNow.AddHours(1)),
            ]));
        }
    }

    private sealed class BlockingPoller(int expectedCalls) : IGitHubIssuesInboundPoller
    {
        private int _callCount;
        private int _concurrency;
        private int _maximumConcurrency;
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public int CallCount => Volatile.Read(ref _callCount);

        public int MaximumConcurrency => Volatile.Read(ref _maximumConcurrency);

        public Task Completed => _completed.Task;

        public async Task<GitHubIssuesPollingCycleResult> PollDueRepositoriesAsync(
            CancellationToken cancellationToken = default)
        {
            var concurrent = Interlocked.Increment(ref _concurrency);
            UpdateMaximum(concurrent);
            try
            {
                await Task.Delay(30, cancellationToken);
                if (Interlocked.Increment(ref _callCount) == expectedCalls)
                {
                    _completed.TrySetResult();
                }

                return new GitHubIssuesPollingCycleResult([]);
            }
            finally
            {
                Interlocked.Decrement(ref _concurrency);
            }
        }

        private void UpdateMaximum(int current)
        {
            var observed = Volatile.Read(ref _maximumConcurrency);
            while (current > observed)
            {
                var previous = Interlocked.CompareExchange(
                    ref _maximumConcurrency,
                    current,
                    observed);
                if (previous == observed)
                {
                    return;
                }

                observed = previous;
            }
        }
    }

    private sealed class OrderedPoller(List<string> order) : IGitHubIssuesInboundPoller
    {
        public Task<GitHubIssuesPollingCycleResult> PollDueRepositoriesAsync(
            CancellationToken cancellationToken = default)
        {
            order.Add("poll");
            return Task.FromResult(new GitHubIssuesPollingCycleResult([]));
        }
    }

    private sealed class OrderedProcessor(List<string> order) : IGitHubIssuesInboundProcessor
    {
        private readonly TaskCompletionSource _completed =
            new(TaskCreationOptions.RunContinuationsAsynchronously);

        public Task Completed => _completed.Task;

        public Task<GitHubIssuesInboundProcessingCycleResult> ProcessPendingAsync(
            CancellationToken cancellationToken = default)
        {
            order.Add("process");
            _completed.TrySetResult();
            return Task.FromResult(new GitHubIssuesInboundProcessingCycleResult([]));
        }
    }
}

[CollectionDefinition(nameof(GitHubAkkaClusterCollection), DisableParallelization = true)]
public sealed class GitHubAkkaClusterCollection
{
}
