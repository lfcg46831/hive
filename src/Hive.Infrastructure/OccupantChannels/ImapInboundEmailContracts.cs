namespace Hive.Infrastructure.OccupantChannels;

internal sealed record ImapInboundEmailCheckpoint(
    string SourceId,
    string Mailbox,
    uint UidValidity,
    uint LastUid);

internal sealed record ImapInboundEmailEnvelope(
    string SourceId,
    string Mailbox,
    uint UidValidity,
    uint Uid,
    byte[] RawMessage,
    DateTimeOffset CapturedAtUtc);

internal sealed record FetchedImapMessage(uint Uid, byte[] RawMessage);

internal sealed record ImapInboundEmailBatch(
    string SourceId,
    string Mailbox,
    uint UidValidity,
    uint HighestUid,
    IReadOnlyList<FetchedImapMessage> Messages);

internal sealed record ImapInboundEmailCommitResult(
    bool IsApplied,
    int InsertedCount,
    ImapInboundEmailCheckpoint? Checkpoint)
{
    public static ImapInboundEmailCommitResult ConcurrentCheckpoint() =>
        new(false, 0, null);
}

internal sealed record ImapInboundEmailPollResult(
    bool IsCommitted,
    int FetchedCount,
    int InsertedCount,
    ImapInboundEmailCheckpoint? Checkpoint);

internal interface IImapInboundEmailClient
{
    Task<ImapInboundEmailBatch> FetchBatchAsync(
        ImapInboundEmailCheckpoint? checkpoint,
        CancellationToken cancellationToken = default);
}

internal interface IImapInboundEmailStore
{
    ValueTask<ImapInboundEmailCheckpoint?> ReadCheckpointAsync(
        string sourceId,
        string mailbox,
        CancellationToken cancellationToken = default);

    Task<ImapInboundEmailCommitResult> CommitBatchAsync(
        ImapInboundEmailCheckpoint? expectedCheckpoint,
        ImapInboundEmailBatch batch,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default);

    Task<IReadOnlyList<ImapInboundEmailEnvelope>> ReadPendingAsync(
        string sourceId,
        string mailbox,
        int limit,
        CancellationToken cancellationToken = default);
}

internal interface IImapInboundEmailPoller
{
    Task<ImapInboundEmailPollResult> PollAsync(
        CancellationToken cancellationToken = default);
}
