using Hive.Actors.OccupantChannels;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.OccupantChannels;
using Hive.Domain.Positions;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class InboundOccupantEmailDecisionProcessorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 8, 12, 14, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Closed_grammar_emits_approved_and_rejected_decisions_without_inferring_intent()
    {
        var approvedAdmission = Admission(7, "\r\n approve \r\nRelease checks passed.");
        var rejectedAdmission = Admission(8, "REJECT\nRisk remains too high.");
        var store = new RecordingStore([approvedAdmission, rejectedAdmission]);
        var emitter = new RecordingEmitter((_, command, _) => Accepted(command));
        var processor = CreateProcessor(store, emitter);

        var result = await processor.ProcessAcceptedAsync();

        Assert.Equal(new InboundOccupantEmailDecisionProcessingResult(2, 2, 0, 0, 0), result);
        Assert.Collection(
            emitter.Commands,
            approved =>
            {
                Assert.True(approved.Command.Approved);
                Assert.Equal("Release checks passed.", approved.Command.Reason);
                Assert.Equal(approvedAdmission.Correlation.RequestId, approved.Command.RequestId);
                Assert.Equal(approvedAdmission.Correlation.ThreadId, approved.Command.RequestThread);
                Assert.Equal(approvedAdmission.UserId.Value.ToString("D"), approved.Command.Author.SubjectId);
                Assert.Equal("email", approved.Command.Author.Channel);
            },
            rejected =>
            {
                Assert.False(rejected.Command.Approved);
                Assert.Equal("Risk remains too high.", rejected.Command.Reason);
            });
        Assert.Equal(2, store.Emitted.Count);
        Assert.Empty(store.Rejected);
        Assert.NotEqual(
            store.Emitted[0].DecisionMessageId,
            store.Emitted[1].DecisionMessageId);
    }

    [Fact]
    public async Task Free_form_intent_and_oversized_reason_are_rejected_before_the_position()
    {
        var store = new RecordingStore(
        [
            Admission(9, "I think this should be approved"),
            Admission(
                10,
                $"APPROVE\n{new string('x', EmitOccupantApprovalDecision.MaximumReasonLength + 1)}"),
        ]);
        var emitter = new RecordingEmitter((_, command, _) => Accepted(command));
        var processor = CreateProcessor(store, emitter);

        var result = await processor.ProcessAcceptedAsync();

        Assert.Equal(new InboundOccupantEmailDecisionProcessingResult(2, 0, 2, 0, 0), result);
        Assert.Empty(emitter.Commands);
        Assert.All(
            store.Rejected,
            rejection => Assert.Equal(
                ["approval-decision-syntax-invalid"],
                rejection.FailureCodes));
    }

    [Fact]
    public async Task Governance_rejection_is_terminal_but_concurrent_validation_remains_pending()
    {
        var governanceAdmission = Admission(11, "APPROVE");
        var concurrentAdmission = Admission(12, "REJECT");
        var store = new RecordingStore([governanceAdmission, concurrentAdmission]);
        var emitter = new RecordingEmitter((_, command, _) =>
            command.RequestId == governanceAdmission.Correlation.RequestId
                ? OccupantReplyEmissionResult.Rejected(
                    command.RequestId,
                    new OccupantReplyEmissionError(
                        ApprovalValidationCatalog.Codes.UnauthorizedApprover,
                        "from.positionId",
                        RejectionReason.Unauthorized))
                : OccupantReplyEmissionResult.Rejected(
                    command.RequestId,
                    new OccupantReplyEmissionError(
                        "approval-decision-in-progress",
                        "requestId",
                        RejectionReason.Duplicate)));
        var processor = CreateProcessor(store, emitter);

        var result = await processor.ProcessAcceptedAsync();

        Assert.Equal(new InboundOccupantEmailDecisionProcessingResult(2, 0, 1, 1, 0), result);
        Assert.Equal(
            [ApprovalValidationCatalog.Codes.UnauthorizedApprover],
            Assert.Single(store.Rejected).FailureCodes);
        Assert.Empty(store.Emitted);
    }

    [Fact]
    public async Task Technical_retry_reuses_the_same_decision_message_id()
    {
        var admission = Admission(13, "APPROVE");
        var store = new RecordingStore([admission]);
        var emitter = new RecordingEmitter((_, command, attempt) =>
            attempt == 1
                ? throw new TimeoutException("transient")
                : Accepted(command));
        var processor = CreateProcessor(store, emitter);

        var first = await processor.ProcessAcceptedAsync();
        var second = await processor.ProcessAcceptedAsync();

        Assert.Equal(new InboundOccupantEmailDecisionProcessingResult(1, 0, 0, 1, 0), first);
        Assert.Equal(new InboundOccupantEmailDecisionProcessingResult(1, 1, 0, 0, 0), second);
        Assert.Equal(2, emitter.Commands.Count);
        Assert.Equal(
            emitter.Commands[0].Command.DecisionMessageId,
            emitter.Commands[1].Command.DecisionMessageId);
    }

    private static InboundOccupantEmailDecisionProcessor CreateProcessor(
        IImapInboundEmailStore store,
        IInboundOccupantEmailDecisionEmitter emitter) =>
        new(
            store,
            emitter,
            Options.Create(new ImapInboundEmailOptions
            {
                SourceId = "occupant-replies",
                Mailbox = "INBOX",
                BatchSize = 50,
            }),
            new FixedTimeProvider(At));

    private static InboundOccupantEmailAdmission Admission(uint uid, string reply)
    {
        var requestId = MessageId.From(
            Guid.Parse($"20000000-0000-0000-0000-{uid:D12}"));
        return new InboundOccupantEmailAdmission(
            new ImapInboundEmailEnvelope(
                "occupant-replies",
                "INBOX",
                5,
                uid,
                [(byte)uid],
                At),
            new OccupantChannelCorrelationTokenClaims(
                Guid.Parse($"10000000-0000-0000-0000-{uid:D12}"),
                OrganizationId.From("acme"),
                PositionId.From("ceo"),
                requestId,
                ThreadId.From(Guid.Parse($"30000000-0000-0000-0000-{uid:D12}")),
                requestId,
                At.AddMinutes(-1),
                At.AddHours(1)),
            OccupantId.From("human:ceo"),
            UserId.From(Guid.Parse($"40000000-0000-0000-0000-{uid:D12}")),
            OccupantChannelBindingId.From(
                Guid.Parse($"50000000-0000-0000-0000-{uid:D12}")),
            reply,
            InboundOccupantEmailContentTrust.Untrusted);
    }

    private static OccupantReplyEmissionResult Accepted(EmitOccupantApprovalDecision command) =>
        OccupantReplyEmissionResult.Accepted(
            command.RequestId,
            new ApprovalDecision(
                command.DecisionMessageId,
                OrganizationId.From("acme"),
                new PositionEndpointRef(PositionId.From("ceo")),
                new PositionEndpointRef(PositionId.From("delivery-lead")),
                command.RequestThread,
                Priority.Critical,
                schemaVersion: 1,
                At,
                deadline: null,
                command.RequestId,
                command.Approved,
                command.Reason));

    private sealed class RecordingEmitter(
        Func<PositionEntityId, EmitOccupantApprovalDecision, int, OccupantReplyEmissionResult> emit)
        : IInboundOccupantEmailDecisionEmitter
    {
        public List<(PositionEntityId Position, EmitOccupantApprovalDecision Command)> Commands
        { get; } = [];

        public ValueTask<OccupantReplyEmissionResult> EmitAsync(
            PositionEntityId position,
            EmitOccupantApprovalDecision command,
            CancellationToken cancellationToken = default)
        {
            Commands.Add((position, command));
            return ValueTask.FromResult(emit(position, command, Commands.Count));
        }
    }

    private sealed class RecordingStore(IReadOnlyList<InboundOccupantEmailAdmission> admissions)
        : IImapInboundEmailStore
    {
        public List<(InboundOccupantEmailAdmission Admission, MessageId DecisionMessageId,
            DateTimeOffset CompletedAtUtc)> Emitted { get; } = [];

        public List<(InboundOccupantEmailAdmission Admission, MessageId DecisionMessageId,
            IReadOnlyList<string> FailureCodes, DateTimeOffset CompletedAtUtc)> Rejected { get; } = [];

        public Task<IReadOnlyList<InboundOccupantEmailAdmission>> ReadAcceptedDecisionsAsync(
            string sourceId,
            string mailbox,
            int limit,
            CancellationToken cancellationToken = default) => Task.FromResult(admissions);

        public Task<bool> CompleteDecisionEmittedAsync(
            InboundOccupantEmailAdmission admission,
            MessageId decisionMessageId,
            DateTimeOffset emittedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Emitted.Add((admission, decisionMessageId, emittedAtUtc));
            return Task.FromResult(true);
        }

        public Task<bool> CompleteDecisionRejectedAsync(
            InboundOccupantEmailAdmission admission,
            MessageId decisionMessageId,
            IReadOnlyList<string> failureCodes,
            DateTimeOffset rejectedAtUtc,
            CancellationToken cancellationToken = default)
        {
            Rejected.Add((admission, decisionMessageId, failureCodes, rejectedAtUtc));
            return Task.FromResult(true);
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

        public Task<IReadOnlyList<ImapInboundEmailEnvelope>> ReadPendingAsync(
            string sourceId,
            string mailbox,
            int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteAcceptedAsync(
            InboundOccupantEmailAdmission admission,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<bool> CompleteRejectedAsync(
            ImapInboundEmailEnvelope envelope,
            InboundOccupantEmailFailureCode failure,
            DateTimeOffset processedAtUtc,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

        public Task<IReadOnlyList<InboundOccupantEmailAdmission>> ReadAcceptedWorkRepliesAsync(
            string sourceId,
            string mailbox,
            int limit,
            CancellationToken cancellationToken = default) => throw new NotSupportedException();

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

    private sealed class FixedTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }
}
