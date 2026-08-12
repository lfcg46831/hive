using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Tools.Singleton;
using Hive.Actors.Sharding;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Hosting;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Options;

namespace Hive.Actors.OccupantChannels;

/// <summary>
/// Hosts the transport-only IMAP source exactly once across nodes with the connectors role. The
/// shared PostgreSQL checkpoint remains the recovery boundary across singleton handover.
/// </summary>
internal sealed class ImapInboundEmailSingletonWorkload : IRoleWorkload
{
    private readonly ActorSystem _system;
    private readonly IImapInboundEmailPoller _poller;
    private readonly IInboundOccupantEmailProcessor _processor;
    private readonly IInboundOccupantEmailReplyProcessor _replyProcessor;
    private readonly IInboundOccupantEmailDecisionProcessor _decisionProcessor;
    private readonly ImapInboundEmailOptions _options;
    private readonly ILogger<ImapInboundEmailSingletonWorkload> _logger;
    private readonly SemaphoreSlim _startGate = new(1, 1);
    private IActorRef? _manager;
    private IActorRef? _proxy;

    public ImapInboundEmailSingletonWorkload(
        ActorSystem system,
        IImapInboundEmailPoller poller,
        IInboundOccupantEmailProcessor processor,
        IInboundOccupantEmailReplyProcessor replyProcessor,
        IInboundOccupantEmailDecisionProcessor decisionProcessor,
        IOptions<ImapInboundEmailOptions> options,
        ILogger<ImapInboundEmailSingletonWorkload> logger)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _replyProcessor = replyProcessor ?? throw new ArgumentNullException(nameof(replyProcessor));
        _decisionProcessor = decisionProcessor
            ?? throw new ArgumentNullException(nameof(decisionProcessor));
        ArgumentNullException.ThrowIfNull(options);
        _options = options.Value;
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    public string Role => NodeRoleNames.Connectors;

    public IActorRef? Manager => _manager;

    public IActorRef? Proxy => _proxy;

    public bool IsEnabled => _options.Enabled;

    public async Task StartAsync(CancellationToken cancellationToken)
    {
        await _startGate.WaitAsync(cancellationToken).ConfigureAwait(false);
        try
        {
            if (!_options.Enabled || _proxy is not null)
            {
                return;
            }

            await WaitForClusterUpAsync(cancellationToken).ConfigureAwait(false);
            var sourceProps = ImapInboundEmailSourceActor.Props(
                _poller,
                _processor,
                _replyProcessor,
                _decisionProcessor,
                _options.PollInterval,
                _options.SourceId,
                _options.Mailbox,
                _logger);
            var managerSettings = ClusterSingletonManagerSettings.Create(_system)
                .WithRole(NodeRoleNames.Connectors)
                .WithSingletonName(ImapInboundEmailSingletonIdentity.SingletonName);
            _manager = _system.ActorOf(
                ClusterSingletonManager.Props(
                    sourceProps,
                    PoisonPill.Instance,
                    managerSettings),
                ImapInboundEmailSingletonIdentity.SingletonManagerName);

            var localSingleton = await TryResolveLocalSingletonWhenThisNodeIsTheOnlyConnectorAsync(
                    cancellationToken)
                .ConfigureAwait(false);
            if (localSingleton is not null)
            {
                _proxy = _system.ActorOf(
                    ImapInboundEmailLocalProxy.Props(localSingleton),
                    ImapInboundEmailSingletonIdentity.ProxyName);
            }
            else
            {
                var proxySettings = ClusterSingletonProxySettings.Create(_system)
                    .WithRole(NodeRoleNames.Connectors)
                    .WithSingletonName(ImapInboundEmailSingletonIdentity.SingletonName);
                _proxy = _system.ActorOf(
                    ClusterSingletonProxy.Props(
                        ImapInboundEmailSingletonIdentity.SingletonManagerPath,
                        proxySettings),
                    ImapInboundEmailSingletonIdentity.ProxyName);
            }

            _logger.LogInformation(
                "IMAP occupant-email singleton materialized on role {Role} for source {SourceId} mailbox {Mailbox}.",
                NodeRoleNames.Connectors,
                _options.SourceId,
                _options.Mailbox);
        }
        finally
        {
            _startGate.Release();
        }
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        // Cluster Singleton and its proxy are owned by the ActorSystem and participate in Akka's
        // coordinated shutdown. The source actor cancels an in-flight poll in PostStop.
        return Task.CompletedTask;
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
            $"{ImapInboundEmailSingletonIdentity.SingletonManagerPath}/{ImapInboundEmailSingletonIdentity.SingletonName}");
        var deadline = DateTimeOffset.UtcNow + _options.ClusterUpTimeout;
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

        using var timeoutCts = new CancellationTokenSource(_options.ClusterUpTimeout);
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
            "IMAP singleton for role {Role} was not materialized because the ActorSystem did not reach cluster Up within {ClusterUpTimeout}.",
            NodeRoleNames.Connectors,
            _options.ClusterUpTimeout);
        throw new ClusterStartupTimeoutException(
            NodeRoleNames.Connectors,
            _options.ClusterUpTimeout,
            lastStatus);
    }
}

internal sealed class ImapInboundEmailLocalProxy : ReceiveActor
{
    public ImapInboundEmailLocalProxy(IActorRef target)
    {
        ArgumentNullException.ThrowIfNull(target);
        ReceiveAny(target.Forward);
    }

    public static Props Props(IActorRef target) =>
        Akka.Actor.Props.Create(() => new ImapInboundEmailLocalProxy(target));
}
