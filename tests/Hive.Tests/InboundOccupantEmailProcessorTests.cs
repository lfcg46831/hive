using Hive.Domain.Identity;
using Hive.Domain.OccupantChannels;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class InboundOccupantEmailProcessorTests
{
    [Fact]
    public async Task Processor_closes_terminal_results_and_leaves_technical_failures_pending()
    {
        var at = new DateTimeOffset(2026, 8, 12, 12, 0, 0, TimeSpan.Zero);
        var envelopes = Enumerable.Range(1, 4)
            .Select(uid => Envelope((uint)uid, at))
            .ToArray();
        var admission = Admission(envelopes[0], at);
        var store = new RecordingStore(envelopes);
        var parser = new SequenceParser(
            InboundOccupantEmailParseResult.Accepted(admission),
            InboundOccupantEmailParseResult.Rejected(
                InboundOccupantEmailFailureCode.SenderMismatch),
            InboundOccupantEmailParseResult.Retryable(
                InboundOccupantEmailFailureCode.IdentityUnavailable),
            InboundOccupantEmailParseResult.Rejected(
                InboundOccupantEmailFailureCode.TokenExpired));
        store.CompletionResults.Enqueue(true);
        store.CompletionResults.Enqueue(false);
        store.CompletionResults.Enqueue(true);
        var processor = new InboundOccupantEmailProcessor(
            store,
            parser,
            Options.Create(new ImapInboundEmailOptions
            {
                SourceId = "occupant-replies",
                Mailbox = "INBOX",
                BatchSize = 50,
            }),
            new FixedTimeProvider(at));

        var result = await processor.ProcessPendingAsync();

        Assert.Equal(new InboundOccupantEmailProcessingResult(4, 1, 1, 1, 1), result);
        Assert.Single(store.Accepted);
        Assert.Equal(at, store.Accepted[0].ProcessedAtUtc);
        Assert.Equal(2, store.Rejected.Count);
        Assert.Equal(InboundOccupantEmailFailureCode.SenderMismatch, store.Rejected[0].Failure);
        Assert.Equal(InboundOccupantEmailFailureCode.TokenExpired, store.Rejected[1].Failure);
    }

    private static ImapInboundEmailEnvelope Envelope(uint uid, DateTimeOffset at) =>
        new("occupant-replies", "INBOX", 7, uid, [(byte)uid], at);

    private static InboundOccupantEmailAdmission Admission(
        ImapInboundEmailEnvelope envelope,
        DateTimeOffset at) =>
        new(
            envelope,
            new OccupantChannelCorrelationTokenClaims(
                Guid.NewGuid(),
                OrganizationId.From("acme"),
                PositionId.From("delivery-lead"),
                MessageId.New(),
                ThreadId.New(),
                requestId: null,
                at,
                at.AddHours(1)),
            OccupantId.From("human:delivery-lead"),
            UserId.New(),
            OccupantChannelBindingId.New(),
            "done",
            InboundOccupantEmailContentTrust.Untrusted);

    private sealed class SequenceParser(params InboundOccupantEmailParseResult[] results)
        : IInboundOccupantEmailParser
    {
        private readonly Queue<InboundOccupantEmailParseResult> _results = new(results);

        public Task<InboundOccupantEmailParseResult> ParseAsync(
            ImapInboundEmailEnvelope envelope,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.FromResult(_results.Dequeue());
        }
    }

    private sealed class RecordingStore(IReadOnlyList<ImapInboundEmailEnvelope> pending)
        : IImapInboundEmailStore
    {
        public Queue<bool> CompletionResults { get; } = new();

        public List<(InboundOccupantEmailAdmission Admission, DateTimeOffset ProcessedAtUtc)>
            Accepted { get; } = [];

        public List<(ImapInboundEmailEnvelope Envelope, InboundOccupantEmailFailureCode Failure)>
            Rejected { get; } = [];

        public Task<IReadOnlyList<ImapInboundEmailEnvelope>> ReadPendingAsync(
            string sourceId,
            string mailbox,
            int limit,
            CancellationToken cancellationToken = default) => Task.FromResult(pending);

        public Task<bool> CompleteAcceptedAsync(
            InboundOccupantEmailAdmission admission,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Accepted.Add((admission, processedAtUtc));
            return Task.FromResult(CompletionResults.Dequeue());
        }

        public Task<bool> CompleteRejectedAsync(
            ImapInboundEmailEnvelope envelope,
            InboundOccupantEmailFailureCode failure,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Rejected.Add((envelope, failure));
            return Task.FromResult(CompletionResults.Dequeue());
        }

        public ValueTask<ImapInboundEmailCheckpoint?> ReadCheckpointAsync(
            string sourceId,
            string mailbox,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<ImapInboundEmailCommitResult> CommitBatchAsync(
            ImapInboundEmailCheckpoint? expectedCheckpoint,
            ImapInboundEmailBatch batch,
            DateTimeOffset capturedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<InboundOccupantEmailAdmission>> ReadAcceptedAsync(
            string sourceId,
            string mailbox,
            int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();
    }

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
