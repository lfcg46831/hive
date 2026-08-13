using Akka.Actor;
using Microsoft.Extensions.Logging;

namespace Hive.Connectors.GitHub;

internal sealed class GitHubIssuesInboundSourceActor : ReceiveActor
{
    private static readonly TimeSpan MinimumScheduleDelay = TimeSpan.FromMilliseconds(25);

    private readonly IGitHubIssuesInboundPoller _poller;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private ICancelable? _scheduledPoll;

    public GitHubIssuesInboundSourceActor(
        IGitHubIssuesInboundPoller poller,
        TimeProvider timeProvider,
        ILogger logger)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ReceiveAsync<PollGitHubIssuesInbound>(_ => PollAsync());
    }

    public static Props Props(
        IGitHubIssuesInboundPoller poller,
        TimeProvider timeProvider,
        ILogger logger) =>
        Akka.Actor.Props.Create(() => new GitHubIssuesInboundSourceActor(
            poller,
            timeProvider,
            logger));

    protected override void PreStart()
    {
        Self.Tell(PollGitHubIssuesInbound.Instance);
        base.PreStart();
    }

    protected override void PostStop()
    {
        _scheduledPoll?.Cancel();
        _stopping.Cancel();
        _stopping.Dispose();
        base.PostStop();
    }

    private async Task PollAsync()
    {
        GitHubIssuesPollingCycleResult cycle;
        try
        {
            cycle = await _poller
                .PollDueRepositoriesAsync(_stopping.Token)
                .ConfigureAwait(false);
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }

        foreach (var failed in cycle.Repositories.Where(result =>
                     result.Status is GitHubIssuesRepositoryPollStatus.Failed))
        {
            _logger.LogWarning(
                "GitHub Issues polling failed for connector instance {InstanceId} repository {Repository} with code {ErrorCode}; payload and transport diagnostics were omitted.",
                failed.InstanceId,
                failed.Repository,
                failed.ErrorCode);
        }

        if (cycle.NextPollAtUtc is { } nextPollAtUtc)
        {
            Schedule(nextPollAtUtc);
        }
    }

    private void Schedule(DateTimeOffset nextPollAtUtc)
    {
        _scheduledPoll?.Cancel();
        var delay = nextPollAtUtc - _timeProvider.GetUtcNow();
        if (delay < MinimumScheduleDelay)
        {
            delay = MinimumScheduleDelay;
        }

        _scheduledPoll = Context.System.Scheduler.ScheduleTellOnceCancelable(
            delay,
            Self,
            PollGitHubIssuesInbound.Instance,
            Self);
    }
}

internal sealed record PollGitHubIssuesInbound
{
    public static PollGitHubIssuesInbound Instance { get; } = new();

    private PollGitHubIssuesInbound()
    {
    }
}
