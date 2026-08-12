using Akka.Actor;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Logging;

namespace Hive.Actors.OccupantChannels;

internal sealed class ImapInboundEmailSourceActor : ReceiveActor
{
    private readonly IImapInboundEmailPoller _poller;
    private readonly IInboundOccupantEmailProcessor _processor;
    private readonly TimeSpan _pollInterval;
    private readonly string _sourceId;
    private readonly string _mailbox;
    private readonly ILogger _logger;
    private readonly CancellationTokenSource _stopping = new();
    private ICancelable? _scheduledPoll;

    public ImapInboundEmailSourceActor(
        IImapInboundEmailPoller poller,
        IInboundOccupantEmailProcessor processor,
        TimeSpan pollInterval,
        string sourceId,
        string mailbox,
        ILogger logger)
    {
        _poller = poller ?? throw new ArgumentNullException(nameof(poller));
        _processor = processor ?? throw new ArgumentNullException(nameof(processor));
        _pollInterval = pollInterval > TimeSpan.Zero
            ? pollInterval
            : throw new ArgumentOutOfRangeException(nameof(pollInterval));
        _sourceId = !string.IsNullOrWhiteSpace(sourceId)
            ? sourceId
            : throw new ArgumentException("IMAP source id cannot be empty.", nameof(sourceId));
        _mailbox = !string.IsNullOrWhiteSpace(mailbox)
            ? mailbox
            : throw new ArgumentException("IMAP mailbox cannot be empty.", nameof(mailbox));
        _logger = logger ?? throw new ArgumentNullException(nameof(logger));

        ReceiveAsync<Poll>(HandlePollAsync);
    }

    public static Props Props(
        IImapInboundEmailPoller poller,
        IInboundOccupantEmailProcessor processor,
        TimeSpan pollInterval,
        string sourceId,
        string mailbox,
        ILogger logger) =>
        Akka.Actor.Props.Create(
            () => new ImapInboundEmailSourceActor(
                poller,
                processor,
                pollInterval,
                sourceId,
                mailbox,
                logger));

    protected override void PreStart()
    {
        Self.Tell(Poll.Instance);
        base.PreStart();
    }

    protected override void PostStop()
    {
        _scheduledPoll?.Cancel();
        _stopping.Cancel();
        _stopping.Dispose();
        base.PostStop();
    }

    private async Task HandlePollAsync(Poll message)
    {
        var scheduler = Context.System.Scheduler;
        var self = Self;
        try
        {
            var result = await _poller
                .PollAsync(_stopping.Token)
                .ConfigureAwait(false);
            if (!result.IsCommitted)
            {
                _logger.LogWarning(
                    "IMAP ingestion checkpoint changed concurrently for source {SourceId} mailbox {Mailbox}; the batch will be re-read.",
                    _sourceId,
                    _mailbox);
            }
            else if (result.FetchedCount > 0)
            {
                _logger.LogInformation(
                    "IMAP ingestion committed {InsertedCount} new envelope(s) from {FetchedCount} fetched item(s) for source {SourceId} mailbox {Mailbox} at UID {LastUid}.",
                    result.InsertedCount,
                    result.FetchedCount,
                    _sourceId,
                    _mailbox,
                    result.Checkpoint?.LastUid);
            }
        }
        catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
        {
            return;
        }
        catch (Exception exception)
        {
            _logger.LogError(
                "IMAP ingestion poll failed closed for source {SourceId} mailbox {Mailbox} with error type {ErrorType}; the checkpoint was not advanced.",
                _sourceId,
                _mailbox,
                exception.GetType().Name);
        }

        if (!_stopping.IsCancellationRequested)
        {
            try
            {
                var processing = await _processor
                    .ProcessPendingAsync(_stopping.Token)
                    .ConfigureAwait(false);
                if (processing.PendingCount > 0)
                {
                    _logger.LogInformation(
                        "Inbound occupant-email admission processed {PendingCount} staged envelope(s): {AcceptedCount} accepted, {RejectedCount} rejected, {RetryableCount} pending for retry and {AlreadyCompletedCount} already completed for source {SourceId} mailbox {Mailbox}.",
                        processing.PendingCount,
                        processing.AcceptedCount,
                        processing.RejectedCount,
                        processing.RetryableCount,
                        processing.AlreadyCompletedCount,
                        _sourceId,
                        _mailbox);
                }
            }
            catch (OperationCanceledException) when (_stopping.IsCancellationRequested)
            {
                return;
            }
            catch (Exception exception)
            {
                _logger.LogError(
                    "Inbound occupant-email admission failed closed for source {SourceId} mailbox {Mailbox} with error type {ErrorType}; unfinished envelopes remain pending.",
                    _sourceId,
                    _mailbox,
                    exception.GetType().Name);
            }
        }

        if (!_stopping.IsCancellationRequested)
        {
            _scheduledPoll = scheduler.ScheduleTellOnceCancelable(
                _pollInterval,
                self,
                Poll.Instance,
                self);
        }
    }

    private sealed class Poll
    {
        public static Poll Instance { get; } = new();

        private Poll()
        {
        }
    }
}
