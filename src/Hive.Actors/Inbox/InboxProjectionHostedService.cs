using Hive.Infrastructure.Inbox.ReadModels;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Actors.Inbox;

internal sealed class InboxProjectionHostedService : BackgroundService
{
    private static readonly TimeSpan IdleDelay = TimeSpan.FromMilliseconds(250);
    private static readonly TimeSpan FailureDelay = TimeSpan.FromSeconds(1);

    private readonly InboxProjectionWorker _worker;
    private readonly IInboxProjectionFeed _feed;
    private readonly TimeProvider _timeProvider;
    private readonly ILogger<InboxProjectionHostedService> _logger;

    public InboxProjectionHostedService(
        InboxProjectionWorker worker,
        IInboxProjectionFeed feed,
        TimeProvider timeProvider,
        ILogger<InboxProjectionHostedService> logger)
    {
        _worker = worker ?? throw new ArgumentNullException(nameof(worker));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));
    }

    protected override Task ExecuteAsync(CancellationToken stoppingToken)
    {
        if (!_feed.IsConfigured)
        {
            return Task.CompletedTask;
        }

        return Task.WhenAll(
            RunContinuouslyAsync(
                "position journal",
                _worker.CapturePositionJournalBatchAsync,
                stoppingToken),
            RunContinuouslyAsync(
                "audit log",
                token => _worker.CaptureAuditLogBatchAsync(token).AsTask(),
                stoppingToken));
    }

    private async Task RunContinuouslyAsync(
        string source,
        Func<CancellationToken, Task<int>> captureBatch,
        CancellationToken stoppingToken)
    {
        while (!stoppingToken.IsCancellationRequested)
        {
            try
            {
                var captured = await captureBatch(stoppingToken);
                if (captured == 0)
                {
                    await Task.Delay(IdleDelay, _timeProvider, stoppingToken);
                }
            }
            catch (OperationCanceledException) when (stoppingToken.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    exception,
                    "Inbox projection failed while reading {ProjectionSource}; retrying.",
                    source);
                await Task.Delay(FailureDelay, _timeProvider, stoppingToken);
            }
        }
    }
}
