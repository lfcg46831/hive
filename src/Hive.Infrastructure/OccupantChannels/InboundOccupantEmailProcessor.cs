using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class InboundOccupantEmailProcessor(
    IImapInboundEmailStore store,
    IInboundOccupantEmailParser parser,
    IOptions<ImapInboundEmailOptions> options,
    TimeProvider timeProvider) : IInboundOccupantEmailProcessor
{
    private readonly ImapInboundEmailOptions _options = options.Value;

    public async Task<InboundOccupantEmailProcessingResult> ProcessPendingAsync(
        CancellationToken cancellationToken = default)
    {
        var pending = await store.ReadPendingAsync(
            _options.SourceId,
            _options.Mailbox,
            _options.BatchSize,
            cancellationToken).ConfigureAwait(false);
        var accepted = 0;
        var rejected = 0;
        var retryable = 0;
        var alreadyCompleted = 0;

        foreach (var envelope in pending)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var result = await parser.ParseAsync(envelope, cancellationToken).ConfigureAwait(false);
            bool completed;
            switch (result.Status)
            {
                case InboundOccupantEmailParseStatus.Accepted:
                    completed = await store.CompleteAcceptedAsync(
                        result.Admission!,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    accepted += completed ? 1 : 0;
                    alreadyCompleted += completed ? 0 : 1;
                    break;

                case InboundOccupantEmailParseStatus.Rejected:
                    completed = await store.CompleteRejectedAsync(
                        envelope,
                        result.Failure!.Value,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    rejected += completed ? 1 : 0;
                    alreadyCompleted += completed ? 0 : 1;
                    break;

                case InboundOccupantEmailParseStatus.RetryableFailure:
                    retryable++;
                    break;

                default:
                    throw new InvalidOperationException(
                        $"Unknown inbound occupant email parse status '{result.Status}'.");
            }
        }

        return new InboundOccupantEmailProcessingResult(
            pending.Count,
            accepted,
            rejected,
            retryable,
            alreadyCompleted);
    }
}
