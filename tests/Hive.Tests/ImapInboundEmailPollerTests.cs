using Hive.Domain.Identity;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class ImapInboundEmailPollerTests
{
    [Fact]
    public async Task Poll_reads_checkpoint_fetches_once_and_commits_with_observed_utc_time()
    {
        var checkpoint = new ImapInboundEmailCheckpoint(
            "occupant-replies",
            "INBOX",
            7,
            10);
        var batch = Batch(7, 12, (11, "first"), (12, "second"));
        var client = new RecordingClient(batch);
        var store = new RecordingStore(checkpoint);
        var observedAt = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var poller = CreatePoller(client, store, observedAt);

        var result = await poller.PollAsync();

        Assert.True(result.IsCommitted);
        Assert.Equal(2, result.FetchedCount);
        Assert.Equal(2, result.InsertedCount);
        Assert.Equal(checkpoint, Assert.Single(client.Checkpoints));
        var commit = Assert.Single(store.Commits);
        Assert.Equal(checkpoint, commit.ExpectedCheckpoint);
        Assert.Same(batch, commit.Batch);
        Assert.Equal(observedAt, commit.CapturedAtUtc);
        Assert.Equal(12U, result.Checkpoint!.LastUid);
    }

    [Fact]
    public async Task Uidvalidity_change_allows_new_generation_to_restart_from_its_first_uid()
    {
        var checkpoint = new ImapInboundEmailCheckpoint(
            "occupant-replies",
            "INBOX",
            7,
            900);
        var client = new RecordingClient(Batch(8, 2, (1, "new-one"), (2, "new-two")));
        var store = new RecordingStore(checkpoint);
        var poller = CreatePoller(
            client,
            store,
            new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));

        var result = await poller.PollAsync();

        Assert.True(result.IsCommitted);
        Assert.Equal(8U, result.Checkpoint!.UidValidity);
        Assert.Equal(2U, result.Checkpoint.LastUid);
    }

    [Fact]
    public async Task Invalid_or_reordered_client_batch_fails_before_the_store_is_mutated()
    {
        var checkpoint = new ImapInboundEmailCheckpoint(
            "occupant-replies",
            "INBOX",
            7,
            10);
        var client = new RecordingClient(Batch(7, 12, (12, "second"), (11, "first")));
        var store = new RecordingStore(checkpoint);
        var poller = CreatePoller(
            client,
            store,
            new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));

        await Assert.ThrowsAsync<InvalidOperationException>(() => poller.PollAsync());

        Assert.Empty(store.Commits);
    }

    [Fact]
    public async Task Concurrent_checkpoint_result_is_returned_for_a_safe_refetch()
    {
        var checkpoint = new ImapInboundEmailCheckpoint(
            "occupant-replies",
            "INBOX",
            7,
            10);
        var client = new RecordingClient(Batch(7, 11, (11, "message")));
        var store = new RecordingStore(checkpoint)
        {
            ApplyCommit = false,
        };
        var poller = CreatePoller(
            client,
            store,
            new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero));

        var result = await poller.PollAsync();

        Assert.False(result.IsCommitted);
        Assert.Null(result.Checkpoint);
        Assert.Equal(0, result.InsertedCount);
    }

    private static ImapInboundEmailPoller CreatePoller(
        IImapInboundEmailClient client,
        IImapInboundEmailStore store,
        DateTimeOffset observedAt) =>
        new(
            client,
            store,
            Options.Create(new ImapInboundEmailOptions
            {
                SourceId = "occupant-replies",
                Mailbox = "INBOX",
            }),
            new ManualTimeProvider(observedAt));

    private static ImapInboundEmailBatch Batch(
        uint uidValidity,
        uint highestUid,
        params (uint Uid, string Body)[] messages) =>
        new(
            "occupant-replies",
            "INBOX",
            uidValidity,
            highestUid,
            messages
                .Select(message => new FetchedImapMessage(
                    message.Uid,
                    System.Text.Encoding.ASCII.GetBytes(message.Body)))
                .ToArray());

    private sealed class RecordingClient(ImapInboundEmailBatch batch)
        : IImapInboundEmailClient
    {
        public List<ImapInboundEmailCheckpoint?> Checkpoints { get; } = [];

        public Task<ImapInboundEmailBatch> FetchBatchAsync(
            ImapInboundEmailCheckpoint? checkpoint,
            CancellationToken cancellationToken = default)
        {
            Checkpoints.Add(checkpoint);
            return Task.FromResult(batch);
        }
    }

    private sealed class RecordingStore(ImapInboundEmailCheckpoint? checkpoint)
        : IImapInboundEmailStore
    {
        public bool ApplyCommit { get; init; } = true;

        public List<CommitCall> Commits { get; } = [];

        public ValueTask<ImapInboundEmailCheckpoint?> ReadCheckpointAsync(
            string sourceId,
            string mailbox,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult(checkpoint);

        public Task<ImapInboundEmailCommitResult> CommitBatchAsync(
            ImapInboundEmailCheckpoint? expectedCheckpoint,
            ImapInboundEmailBatch batch,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Commits.Add(new CommitCall(expectedCheckpoint, batch, capturedAtUtc));
            return Task.FromResult(ApplyCommit
                ? new ImapInboundEmailCommitResult(
                    true,
                    batch.Messages.Count,
                    new ImapInboundEmailCheckpoint(
                        batch.SourceId,
                        batch.Mailbox,
                        batch.UidValidity,
                        batch.HighestUid))
                : ImapInboundEmailCommitResult.ConcurrentCheckpoint());
        }

        public Task<IReadOnlyList<ImapInboundEmailEnvelope>> ReadPendingAsync(
            string sourceId,
            string mailbox,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CompleteAcceptedAsync(
            InboundOccupantEmailAdmission admission,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CompleteRejectedAsync(
            ImapInboundEmailEnvelope envelope,
            InboundOccupantEmailFailureCode failure,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<IReadOnlyList<InboundOccupantEmailAdmission>> ReadAcceptedWorkRepliesAsync(
            string sourceId,
            string mailbox,
            int limit,
            CancellationToken cancellationToken = default) =>
            throw new NotSupportedException();

        public Task<bool> CompleteWorkReplyEmittedAsync(
            InboundOccupantEmailAdmission admission,
            MessageId replyMessageId,
            DirectiveId replyDirectiveId,
            DateTimeOffset emittedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteWorkReplyRejectedAsync(
            InboundOccupantEmailAdmission admission,
            MessageId replyMessageId,
            DirectiveId replyDirectiveId,
            IReadOnlyList<string> failureCodes,
            DateTimeOffset rejectedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed record CommitCall(
        ImapInboundEmailCheckpoint? ExpectedCheckpoint,
        ImapInboundEmailBatch Batch,
        DateTimeOffset CapturedAtUtc);

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
