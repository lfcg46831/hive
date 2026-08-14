using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Tools.Singleton;
using Hive.Connectors.GitHub.PostgreSql;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Logging;
using Npgsql;

namespace Hive.Connectors.GitHub;

/// <summary>
/// Hosts the replay-safe GitHub Issues polling source exactly once across nodes with the
/// connectors role. PostgreSQL, rather than actor memory, is the recovery boundary on handover.
/// </summary>
internal sealed class GitHubIssuesInboundSingletonWorkload : IRoleWorkload
{
    public static readonly TimeSpan DefaultClusterUpTimeout = TimeSpan.FromSeconds(30);

    private readonly ActorSystem _system;
    private readonly GitHubIssuesConnectorConfigurationCatalog _catalog;
    private readonly IGitHubIssuesInboundPoller _poller;
    private readonly IGitHubIssuesInboundProcessor _processor;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<GitHubIssuesInboundSingletonWorkload> _logger;
    private readonly ILogger<GitHubIssuesInboundSourceActor> _sourceLogger;
    private readonly TimeSpan _clusterUpTimeout;
    private readonly Func<CancellationToken, Task> _migrate;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private IActorRef? _manager;
    private IActorRef? _proxy;

    public GitHubIssuesInboundSingletonWorkload(
        ActorSystem system,
        IConfiguration configuration,
        GitHubIssuesConnectorConfigurationCatalog catalog,
        IGitHubIssuesInboundPoller poller,
        IGitHubIssuesInboundProcessor processor,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory)
        : this(
            system,
            catalog,
            poller,
            processor,
            timeProvider,
            loggerFactory,
            DefaultClusterUpTimeout,
            cancellationToken => MigrateAsync(configuration, loggerFactory, cancellationToken))
    {
    }

    internal GitHubIssuesInboundSingletonWorkload(
        ActorSystem system,
        GitHubIssuesConnectorConfigurationCatalog catalog,
        IGitHubIssuesInboundPoller poller,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        TimeSpan clusterUpTimeout,
        Func<CancellationToken, Task> migrate)
        : this(
            system,
            catalog,
            poller,
            NoopGitHubIssuesInboundProcessor.Instance,
            timeProvider,
            loggerFactory,
            clusterUpTimeout,
            migrate)
    {
    }

    internal GitHubIssuesInboundSingletonWorkload(
        ActorSystem system,
        GitHubIssuesConnectorConfigurationCatalog catalog,
        IGitHubIssuesInboundPoller poller,
        IGitHubIssuesInboundProcessor processor,
        TimeProvider timeProvider,
        ILoggerFactory loggerFactory,
        TimeSpan clusterUpTimeout,
        Func<CancellationToken, Task> migrate)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _catalog = catalog ?? throw new ArgumentNullException(nameof(catalog));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        ArgumentNullException.ThrowIfNull(loggerFactory);
        if (clusterUpTimeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(clusterUpTimeout),
                "Cluster-up timeout must be positive.");
        }

        _clusterUpTimeout = clusterUpTimeout;
        _migrate = migrate ?? throw new ArgumentNullException(nameof(migrate));
        _logger = loggerFactory.CreateLogger<GitHubIssuesInboundSingletonWorkload>();
        _sourceLogger = loggerFactory.CreateLogger<GitHubIssuesInboundSourceActor>();
    }

    public string Role => NodeRoleNames.Connectors;

    public bool IsEnabled => _catalog.Instances.Count > 0;

    public IActorRef? Manager => _manager;

    public IActorRef? Proxy => _proxy;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!IsEnabled || _proxy is not null)
            {
                return;
            }

            await _migrate(cancellationToken).ConfigureAwait(false);
            await WaitForClusterUpAsync(cancellationToken).ConfigureAwait(false);
            var sourceProps = GitHubIssuesInboundSourceActor.Props(
                _poller,
                _processor,
                _timeProvider,
                _sourceLogger);
            var managerSettings = ClusterSingletonManagerSettings.Create(_system)
                .WithRole(NodeRoleNames.Connectors)
                .WithSingletonName(GitHubIssuesInboundSingletonIdentity.SingletonName);
            _manager = _system.ActorOf(
                ClusterSingletonManager.Props(
                    sourceProps,
                    PoisonPill.Instance,
                    managerSettings),
                GitHubIssuesInboundSingletonIdentity.SingletonManagerName);

            var localSingleton = await TryResolveLocalSingletonWhenThisNodeIsTheOnlyConnectorAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (localSingleton is not null)
            {
                _proxy = _system.ActorOf(
                    GitHubIssuesInboundLocalProxy.Props(localSingleton),
                    GitHubIssuesInboundSingletonIdentity.ProxyName);
            }
            else
            {
                var proxySettings = ClusterSingletonProxySettings.Create(_system)
                    .WithRole(NodeRoleNames.Connectors)
                    .WithSingletonName(GitHubIssuesInboundSingletonIdentity.SingletonName);
                _proxy = _system.ActorOf(
                    ClusterSingletonProxy.Props(
                        GitHubIssuesInboundSingletonIdentity.SingletonManagerPath,
                        proxySettings),
                    GitHubIssuesInboundSingletonIdentity.ProxyName);
            }

            _logger.LogInformation(
                "GitHub Issues inbound singleton materialized on role {Role} for {InstanceCount} connector instances.",
                NodeRoleNames.Connectors,
                _catalog.Instances.Count);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // The manager, singleton and proxy are ActorSystem-owned and participate in coordinated
        // shutdown. The source actor cancels an in-flight cycle from PostStop.
        return Task.CompletedTask;
    }

    private static async Task MigrateAsync(
        IConfiguration configuration,
        ILoggerFactory loggerFactory,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(configuration);
        ArgumentNullException.ThrowIfNull(loggerFactory);
        var logger = loggerFactory.CreateLogger<GitHubIssuesInboundSingletonWorkload>();
        var connectionString = configuration.GetConnectionString(ConnectionStringNames.PostgreSql);
        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new InvalidOperationException(
                $"ConnectionStrings:{ConnectionStringNames.PostgreSql} is required for GitHub Issues polling.");
        }

        logger.LogInformation("Applying GitHub connector PostgreSQL migrations.");
        await using var dataSource = NpgsqlDataSource.Create(connectionString);
        await new PostgreSqlGitHubIssuesInboundMigrator(dataSource)
            .MigrateAsync(cancellationToken)
            .ConfigureAwait(false);
        logger.LogInformation("GitHub connector PostgreSQL migrations are current.");
    }

    private async Task<IActorRef?> TryResolveLocalSingletonWhenThisNodeIsTheOnlyConnectorAsync(
        CancellationToken cancellationToken)
    {
        var cluster = Cluster.Get(_system);
        if (cluster.SelfMember.Status != MemberStatus.Up
            || !cluster.SelfMember.Roles.Contains(NodeRoleNames.Connectors))
        {
            return null;
        }

        var otherUpConnector = cluster.State.Members.Any(member =>
            member.Address != cluster.SelfAddress
            && member.Status == MemberStatus.Up
            && member.Roles.Contains(NodeRoleNames.Connectors));
        if (otherUpConnector)
        {
            return null;
        }

        var selection = _system.ActorSelection(
            $"{GitHubIssuesInboundSingletonIdentity.SingletonManagerPath}/{GitHubIssuesInboundSingletonIdentity.SingletonName}");
        var deadline = DateTimeOffset.UtcNow + _clusterUpTimeout;
        while (DateTimeOffset.UtcNow < deadline)
        {
            cancellationToken.ThrowIfCancellationRequested();
            try
            {
                return await selection.ResolveOne(TimeSpan.FromMilliseconds(100))
                    .ConfigureAwait(false);
            }
            catch (ActorNotFoundException)
            {
            }
            catch (AskTimeoutException)
            {
            }

            await Task.Delay(TimeSpan.FromMilliseconds(25), cancellationToken)
                .ConfigureAwait(false);
        }

        return null;
    }

    private async Task WaitForClusterUpAsync(CancellationToken cancellationToken)
    {
        var cluster = Cluster.Get(_system);
        var memberUp = new TaskCompletionSource(TaskCreationOptions.RunContinuationsAsynchronously);
        cluster.RegisterOnMemberUp(() => memberUp.TrySetResult());

        using var timeoutCts = new CancellationTokenSource(_clusterUpTimeout);
        using var linkedCts = CancellationTokenSource.CreateLinkedTokenSource(
            cancellationToken,
            timeoutCts.Token);
        var timeoutOrCancellation = Task.Delay(Timeout.Infinite, linkedCts.Token);
        var completed = await Task.WhenAny(memberUp.Task, timeoutOrCancellation)
            .ConfigureAwait(false);
        if (completed == memberUp.Task)
        {
            return;
        }

        cancellationToken.ThrowIfCancellationRequested();
        var lastStatus = cluster.SelfMember.Status;
        _logger.LogError(
            "GitHub Issues singleton for role {Role} was not materialized because the ActorSystem did not reach cluster Up within {ClusterUpTimeout}; last status was {MemberStatus}.",
            NodeRoleNames.Connectors,
            _clusterUpTimeout,
            lastStatus);
        throw new TimeoutException(
            $"ActorSystem did not reach cluster Up for role '{NodeRoleNames.Connectors}' within {_clusterUpTimeout}.");
    }
}

internal sealed class GitHubIssuesInboundLocalProxy : ReceiveActor
{
    public GitHubIssuesInboundLocalProxy(IActorRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ReceiveAny(target.Forward);
    }

    public static Props Props(IActorRef target) =>
        Akka.Actor.Props.Create(() => new GitHubIssuesInboundLocalProxy(target));
}
