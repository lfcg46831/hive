using Hive.Domain.Identity;

namespace Hive.Infrastructure.OccupantChannels;

internal sealed class UnavailableImapInboundEmailStore : IImapInboundEmailStore
{
    public static UnavailableImapInboundEmailStore Instance { get; } = new();

    private UnavailableImapInboundEmailStore()
    {
    }

    public ValueTask<ImapInboundEmailCheckpoint?> ReadCheckpointAsync(
        string sourceId,
        string mailbox,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<ImapInboundEmailCheckpoint?>(Unavailable());

    public Task<ImapInboundEmailCommitResult> CommitBatchAsync(
        ImapInboundEmailCheckpoint? expectedCheckpoint,
        ImapInboundEmailBatch batch,
        DateTimeOffset capturedAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromException<ImapInboundEmailCommitResult>(Unavailable());

    public Task<IReadOnlyList<ImapInboundEmailEnvelope>> ReadPendingAsync(
        string sourceId,
        string mailbox,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<ImapInboundEmailEnvelope>>(Unavailable());

    public Task<bool> CompleteAcceptedAsync(
        InboundOccupantEmailAdmission admission,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(Unavailable());

    public Task<bool> CompleteRejectedAsync(
        ImapInboundEmailEnvelope envelope,
        InboundOccupantEmailFailureCode failure,
        DateTimeOffset processedAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(Unavailable());

    public Task<IReadOnlyList<InboundOccupantEmailAdmission>> ReadAcceptedWorkRepliesAsync(
        string sourceId,
        string mailbox,
        int limit,
        CancellationToken cancellationToken = default) =>
        Task.FromException<IReadOnlyList<InboundOccupantEmailAdmission>>(Unavailable());

    public Task<bool> CompleteWorkReplyEmittedAsync(
        InboundOccupantEmailAdmission admission,
        MessageId replyMessageId,
        DirectiveId replyDirectiveId,
        DateTimeOffset emittedAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(Unavailable());

    public Task<bool> CompleteWorkReplyRejectedAsync(
        InboundOccupantEmailAdmission admission,
        MessageId replyMessageId,
        DirectiveId replyDirectiveId,
        IReadOnlyList<string> failureCodes,
        DateTimeOffset rejectedAtUtc,
        CancellationToken cancellationToken = default) =>
        Task.FromException<bool>(Unavailable());

    private static InvalidOperationException Unavailable() => new(
        "The durable IMAP inbound store is unavailable because PostgreSQL is not configured.");
}
