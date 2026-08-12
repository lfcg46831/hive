using System.Security.Cryptography;
using System.Text;
using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Akka.Pattern;
using Hive.Actors.Sharding;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.OccupantChannels;
using Microsoft.Extensions.Options;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Actors.OccupantChannels;

internal interface IInboundOccupantEmailReplyEmitter
{
    ValueTask<OccupantReplyEmissionResult> EmitAsync(
        PositionEntityId position,
        EmitCorrelatedOccupantReply command,
        CancellationToken cancellationToken = default);
}

internal sealed class ShardedInboundOccupantEmailReplyEmitter :
    IInboundOccupantEmailReplyEmitter
{
    private readonly ActorSystem _system;
    private readonly int _numberOfShards;
    private readonly object _regionGate = new();
    private IActorRef? _region;

    public ShardedInboundOccupantEmailReplyEmitter(
        ActorSystem system,
        IOptions<HiveOptions> options)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        ArgumentNullException.ThrowIfNull(options);
        _numberOfShards = options.Value.Agents?.NumberOfShards
            ?? PositionMessageExtractor.DefaultNumberOfShards;
    }

    public async ValueTask<OccupantReplyEmissionResult> EmitAsync(
        PositionEntityId position,
        EmitCorrelatedOccupantReply command,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(position);
        ArgumentNullException.ThrowIfNull(command);
        cancellationToken.ThrowIfCancellationRequested();

        return await GetOrStartShardRegion()
            .Ask<OccupantReplyEmissionResult>(
                PositionEnvelope.For(position, command),
                TimeSpan.FromSeconds(30),
                cancellationToken)
            .ConfigureAwait(false);
    }

    private IActorRef GetOrStartShardRegion()
    {
        if (_region is { } existing)
        {
            return existing;
        }

        lock (_regionGate)
        {
            if (_region is { } cached)
            {
                return cached;
            }

            var sharding = ClusterSharding.Get(_system);
            try
            {
                _region = sharding.ShardRegion(PositionEntityId.EntityTypeName);
            }
            catch (ArgumentException) when (
                !Cluster.Get(_system).SelfRoles.Contains(NodeRoleNames.Agents))
            {
                _region = sharding.StartProxy(
                    PositionEntityId.EntityTypeName,
                    NodeRoleNames.Agents,
                    new PositionMessageExtractor(_numberOfShards));
            }

            return _region;
        }
    }
}

internal sealed class InboundOccupantEmailReplyProcessor(
    IImapInboundEmailStore store,
    IInboundOccupantEmailReplyEmitter emitter,
    IOptions<ImapInboundEmailOptions> options,
    TimeProvider timeProvider) : IInboundOccupantEmailReplyProcessor
{
    private const string EmailChannel = "email";
    private const string InvalidCommandFailure = "reply-command-invalid";
    private const string ConcurrentEmissionFailure = "reply-emission-in-progress";
    private readonly ImapInboundEmailOptions _options = options.Value;

    public async Task<InboundOccupantEmailReplyProcessingResult> ProcessAcceptedAsync(
        CancellationToken cancellationToken = default)
    {
        var admissions = await store.ReadAcceptedWorkRepliesAsync(
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
            var replyMessageId = MessageId.From(DeterministicId(admission.Envelope, "message"));
            var replyDirectiveId = DirectiveId.From(
                DeterministicId(admission.Envelope, "directive"));
            EmitCorrelatedOccupantReply command;
            try
            {
                if (admission.Correlation.IsDecision)
                {
                    throw new InvalidOperationException(
                        "A decision admission cannot be processed as a work reply.");
                }

                command = new EmitCorrelatedOccupantReply(
                    admission.Correlation.MessageId,
                    admission.Correlation.ThreadId,
                    replyMessageId,
                    replyDirectiveId,
                    OccupantReplyAuthor.HumanUser(
                        admission.UserId.Value.ToString("D"),
                        EmailChannel),
                    admission.PlainTextReply,
                    ReportKind.Progress);
            }
            catch (ArgumentException)
            {
                var completed = await store.CompleteWorkReplyRejectedAsync(
                    admission,
                    replyMessageId,
                    replyDirectiveId,
                    [InvalidCommandFailure],
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
                    ValidateAcceptedResult(admission, replyMessageId, result);
                    var completed = await store.CompleteWorkReplyEmittedAsync(
                        admission,
                        replyMessageId,
                        replyDirectiveId,
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
                if (failureCodes.Contains(ConcurrentEmissionFailure, StringComparer.Ordinal))
                {
                    retryable++;
                    continue;
                }

                var rejectedNow = await store.CompleteWorkReplyRejectedAsync(
                    admission,
                    replyMessageId,
                    replyDirectiveId,
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

        return new InboundOccupantEmailReplyProcessingResult(
            admissions.Count,
            emitted,
            rejected,
            retryable,
            alreadyCompleted);
    }

    private static void ValidateAcceptedResult(
        InboundOccupantEmailAdmission admission,
        MessageId replyMessageId,
        OccupantReplyEmissionResult result)
    {
        var message = result.Message ?? throw new InvalidOperationException(
            "An accepted occupant work reply did not return a canonical message.");
        if (result.SourceMessageId != admission.Correlation.MessageId
            || message.Id != replyMessageId
            || message.OrganizationId != admission.Correlation.OrganizationId
            || message.Thread != admission.Correlation.ThreadId
            || message.From is not PositionEndpointRef source
            || source.PositionId != admission.Correlation.PositionId
            || message is not Report and not PeerResponse and not OrgDirective)
        {
            throw new InvalidOperationException(
                "The position returned a canonical message outside the authenticated reply correlation.");
        }
    }

    internal static Guid DeterministicId(
        ImapInboundEmailEnvelope envelope,
        string purpose)
    {
        ArgumentNullException.ThrowIfNull(envelope);
        if (string.IsNullOrWhiteSpace(purpose))
        {
            throw new ArgumentException("Reply id purpose cannot be empty.", nameof(purpose));
        }

        var material = Encoding.UTF8.GetBytes(
            $"hive:occupant-email-reply:v1\n{purpose}\n{envelope.SourceId}\n{envelope.Mailbox}\n{envelope.UidValidity}\n{envelope.Uid}");
        var hash = SHA256.HashData(material);
        return new Guid(hash.AsSpan(0, 16));
    }
}
