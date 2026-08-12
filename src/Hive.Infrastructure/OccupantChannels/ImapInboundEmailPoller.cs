using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class ImapInboundEmailPoller(
    IImapInboundEmailClient client,
    IImapInboundEmailStore store,
    IOptions<ImapInboundEmailOptions> options,
    TimeProvider timeProvider) : IImapInboundEmailPoller
{
    private readonly ImapInboundEmailOptions _options = options.Value;

    public async Task<ImapInboundEmailPollResult> PollAsync(
        CancellationToken cancellationToken = default)
    {
        var checkpoint = await store
            .ReadCheckpointAsync(_options.SourceId, _options.Mailbox, cancellationToken)
            .ConfigureAwait(false);
        var batch = await client
            .FetchBatchAsync(checkpoint, cancellationToken)
            .ConfigureAwait(false);

        ValidateBatch(checkpoint, batch);
        var committed = await store
            .CommitBatchAsync(
                checkpoint,
                batch,
                timeProvider.GetUtcNow(),
                cancellationToken)
            .ConfigureAwait(false);

        return new ImapInboundEmailPollResult(
            committed.IsApplied,
            batch.Messages.Count,
            committed.InsertedCount,
            committed.Checkpoint);
    }

    private void ValidateBatch(
        ImapInboundEmailCheckpoint? checkpoint,
        ImapInboundEmailBatch batch)
    {
        if (!string.Equals(batch.SourceId, _options.SourceId, StringComparison.Ordinal)
            || !string.Equals(batch.Mailbox, _options.Mailbox, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The IMAP client returned a batch for a different configured source.");
        }

        if (batch.UidValidity == 0)
        {
            throw new InvalidOperationException("The IMAP server returned UIDVALIDITY zero.");
        }

        var baseline = checkpoint is not null
            && checkpoint.UidValidity == batch.UidValidity
                ? checkpoint.LastUid
                : 0;
        var previous = baseline;
        foreach (var message in batch.Messages)
        {
            if (message.Uid <= previous)
            {
                throw new InvalidOperationException(
                    "The IMAP client batch must contain unique UIDs in strictly increasing order after the checkpoint.");
            }

            if (message.RawMessage.Length == 0)
            {
                throw new InvalidOperationException(
                    $"The IMAP client returned an empty RFC 822 envelope for UID {message.Uid}.");
            }

            previous = message.Uid;
        }

        if (batch.HighestUid < previous || batch.HighestUid < baseline)
        {
            throw new InvalidOperationException(
                "The IMAP batch high-water UID cannot precede its checkpoint or captured messages.");
        }
    }
}
