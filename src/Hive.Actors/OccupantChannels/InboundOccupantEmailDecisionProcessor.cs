using System.Security.Cryptography;
using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;

namespace Hive.Actors.OccupantChannels;

internal sealed class InboundOccupantEmailDecisionProcessor(
    IImapInboundEmailStore store,
    IInboundOccupantEmailDecisionEmitter emitter,
    IOptions<ImapInboundEmailOptions> options,
    TimeProvider timeProvider) : IInboundOccupantEmailDecisionProcessor
{
    private const string EmailChannel = "email";
    private const string InvalidSyntaxFailure = "approval-decision-syntax-invalid";
    private const string InvalidCorrelationFailure = "approval-decision-correlation-invalid";
    private static readonly string[] ConcurrentEmissionFailures =
    [
        "approval-decision-emission-in-progress",
        "approval-decision-in-progress",
    ];
    private readonly ImapInboundEmailOptions _options = options.Value;

    public async Task<InboundOccupantEmailDecisionProcessingResult> ProcessAcceptedAsync(
        CancellationToken cancellationToken = default)
    {
        var admissions = await store.ReadAcceptedDecisionsAsync(
            _options.SourceId,
            _options.Mailbox,
            _options.BatchSize,
            cancellationToken).ConfigureAwait(false);
        var emitted = 0;
        var rejected = 0;
        var retryable = 0;
        var alreadyCompleted = 0;

        foreach (var admission in admissions)
        {
            cancellationToken.ThrowIfCancellationRequested();
            var decisionMessageId = MessageId.From(DeterministicId(admission.Envelope));
            var requestId = admission.Correlation.RequestId;
            if (requestId is null || requestId != admission.Correlation.MessageId)
            {
                var completed = await store.CompleteDecisionRejectedAsync(
                    admission,
                    decisionMessageId,
                    [InvalidCorrelationFailure],
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                rejected += completed ? 1 : 0;
                alreadyCompleted += completed ? 0 : 1;
                continue;
            }

            if (!TryParseDecision(admission.PlainTextReply, out var approved, out var reason))
            {
                var completed = await store.CompleteDecisionRejectedAsync(
                    admission,
                    decisionMessageId,
                    [InvalidSyntaxFailure],
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                rejected += completed ? 1 : 0;
                alreadyCompleted += completed ? 0 : 1;
                continue;
            }

            EmitOccupantApprovalDecision command;
            try
            {
                command = new EmitOccupantApprovalDecision(
                    requestId,
                    decisionMessageId,
                    admission.Correlation.ThreadId,
                    admission.Correlation.PositionId,
                    Priority.Normal,
                    OccupantReplyAuthor.HumanUser(
                        admission.UserId.Value.ToString("D"),
                        EmailChannel),
                    approved,
                    reason);
            }
            catch (ArgumentException)
            {
                var completed = await store.CompleteDecisionRejectedAsync(
                    admission,
                    decisionMessageId,
                    [InvalidSyntaxFailure],
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                rejected += completed ? 1 : 0;
                alreadyCompleted += completed ? 0 : 1;
                continue;
            }

            try
            {
                var result = await emitter.EmitAsync(
                    PositionEntityId.From(
                        admission.Correlation.OrganizationId,
                        admission.Correlation.PositionId),
                    command,
                    cancellationToken).ConfigureAwait(false);
                if (result.IsAccepted)
                {
                    ValidateAcceptedResult(admission, command, result);
                    var completed = await store.CompleteDecisionEmittedAsync(
                        admission,
                        decisionMessageId,
                        timeProvider.GetUtcNow(),
                        cancellationToken).ConfigureAwait(false);
                    emitted += completed ? 1 : 0;
                    alreadyCompleted += completed ? 0 : 1;
                    continue;
                }

                var failureCodes = result.Errors
                    .Select(error => error.Code)
                    .Distinct(StringComparer.Ordinal)
                    .Order(StringComparer.Ordinal)
                    .ToArray();
                if (failureCodes.Any(code =>
                        ConcurrentEmissionFailures.Contains(code, StringComparer.Ordinal)))
                {
                    retryable++;
                    continue;
                }

                var rejectedNow = await store.CompleteDecisionRejectedAsync(
                    admission,
                    decisionMessageId,
                    failureCodes,
                    timeProvider.GetUtcNow(),
                    cancellationToken).ConfigureAwait(false);
                rejected += rejectedNow ? 1 : 0;
                alreadyCompleted += rejectedNow ? 0 : 1;
            }
            catch (OperationCanceledException) when (cancellationToken.IsCancellationRequested)
            {
                throw;
            }
            catch
            {
                retryable++;
            }
        }

        return new InboundOccupantEmailDecisionProcessingResult(
            admissions.Count,
            emitted,
            rejected,
            retryable,
            alreadyCompleted);
    }

    private static bool TryParseDecision(
        string reply,
        out bool approved,
        out string? reason)
    {
        approved = false;
        reason = null;
        if (string.IsNullOrWhiteSpace(reply))
        {
            return false;
        }

        var lines = reply
            .Replace("\r\n", "\n", StringComparison.Ordinal)
            .Replace('\r', '\n')
            .Split('\n');
        var decisionLine = Array.FindIndex(lines, static line => !string.IsNullOrWhiteSpace(line));
        if (decisionLine < 0)
        {
            return false;
        }

        var verb = lines[decisionLine].Trim();
        if (string.Equals(verb, "APPROVE", StringComparison.OrdinalIgnoreCase))
        {
            approved = true;
        }
        else if (!string.Equals(verb, "REJECT", StringComparison.OrdinalIgnoreCase))
        {
            return false;
        }

        var remaining = string.Join('\n', lines.Skip(decisionLine + 1)).Trim();
        reason = remaining.Length == 0 ? null : remaining;
        return reason is null || reason.Length <= EmitOccupantApprovalDecision.MaximumReasonLength;
    }

    private static void ValidateAcceptedResult(
        InboundOccupantEmailAdmission admission,
        EmitOccupantApprovalDecision command,
        OccupantReplyEmissionResult result)
    {
        var message = result.Message as ApprovalDecision ?? throw new InvalidOperationException(
            "An accepted occupant email decision did not return an ApprovalDecision.");
        if (result.SourceMessageId != command.RequestId
            || message.Id != command.DecisionMessageId
            || message.OrganizationId != admission.Correlation.OrganizationId
            || message.Thread != admission.Correlation.ThreadId
            || message.From is not PositionEndpointRef source
            || source.PositionId != admission.Correlation.PositionId
            || message.RequestId != command.RequestId
            || message.Approved != command.Approved
            || !string.Equals(message.Reason, command.Reason, StringComparison.Ordinal))
        {
            throw new InvalidOperationException(
                "The position returned an approval decision outside the authenticated email correlation.");
        }
    }

    internal static Guid DeterministicId(ImapInboundEmailEnvelope envelope)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        var material = Encoding.UTF8.GetBytes(
            $"hive:occupant-email-approval-decision:v1\n{envelope.SourceId}\n{envelope.Mailbox}\n{envelope.UidValidity}\n{envelope.Uid}");
        var hash = SHA256.HashData(material);
        return new Guid(hash.AsSpan(0, 16));
    }
}
