using System.Text;
using Hive.Actors.Serialization;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Actors.Positions;

internal sealed class PositionLiveStateProjectionWorker
{
    internal const int BatchSize = 128;

    private readonly IPositionLiveStateProjectionJournal _journal;
    private readonly IPositionLiveStateProjectionFeed _feed;
    private PositionLiveStateFactMapper? _factMapper;
    private long _mappedSequenceId;

    public PositionLiveStateProjectionWorker(
        IPositionLiveStateProjectionJournal journal,
        IPositionLiveStateProjectionFeed feed)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
    }

    public async Task<int> CapturePositionJournalBatchAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await _feed.ReadCheckpointAsync(
            PositionLiveStateProjectionSubscription.PositionJournal,
            cancellationToken);
        var events = await _journal.ReadBatchAsync(checkpoint, BatchSize, cancellationToken);
        var captured = 0;
        foreach (var item in events.OrderBy(item => item.Offset))
        {
            var facts = Facts(item);
            if (await _feed.CapturePositionJournalAsync(item.Offset, facts, cancellationToken))
            {
                captured++;
            }
        }

        return captured;
    }

    public ValueTask<int> CaptureAuditLogBatchAsync(CancellationToken cancellationToken) =>
        _feed.CaptureAuditLogBatchAsync(BatchSize, cancellationToken);

    public async Task<int> ApplyProjectionBatchAsync(CancellationToken cancellationToken)
    {
        try
        {
            await EnsureMapperRestoredAsync(cancellationToken);
            var items = await _feed.ReadProjectionFactsAsync(
                _mappedSequenceId,
                BatchSize,
                cancellationToken);
            var applied = 0;
            foreach (var item in items)
            {
                var transition = _factMapper!.Apply(item.Fact);
                var update = transition is null
                    ? null
                    : new PositionLiveStateProjectionUpdate(
                        transition.EntityId.Organization,
                        transition.EntityId.Position,
                        transition.State,
                        transition.OccurredAtUtc,
                        transition.CorrelatedEvent);
                if (await _feed.ApplyProjectionFactAsync(item, update, cancellationToken))
                {
                    applied++;
                }

                _mappedSequenceId = item.SequenceId;
            }

            return applied;
        }
        catch
        {
            // The mapper is ahead of durable progress when a commit or cancellation fails. Drop
            // it so the next attempt rebuilds exclusively from committed facts.
            _factMapper = null;
            _mappedSequenceId = 0;
            throw;
        }
    }

    internal static IReadOnlyCollection<PositionLiveStateProjectionFact> Facts(
        PositionLiveStateProjectionJournalEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var @event = item.Event;
        var received = @event as MessageReceived;
        var facts = new List<PositionLiveStateProjectionFact>(received is null ? 1 : 2)
        {
            new(
                PositionLiveStateProjectionSource.PositionEvent,
                item.Offset,
                item.EntityId.Organization,
                PositionProtocolManifests.ForType(@event.GetType()),
                @event.OccurredAt.ToUniversalTime(),
                Encoding.UTF8.GetString(PositionProtocolJsonFormat.Serialize(@event)),
                item.EntityId.Position,
                item.PersistenceId,
                item.PersistenceSequence,
                received?.Message.Id,
                received?.Message.Thread),
        };

        if (received is not null)
        {
            OrgMessage message = received.Message;
            facts.Add(new PositionLiveStateProjectionFact(
                PositionLiveStateProjectionSource.OrganizationalMessage,
                item.Offset,
                item.EntityId.Organization,
                OrgMessageManifests.ForType(message.GetType()),
                @event.OccurredAt.ToUniversalTime(),
                Encoding.UTF8.GetString(OrgMessageJsonFormat.Serialize(message)),
                item.EntityId.Position,
                item.PersistenceId,
                item.PersistenceSequence,
                message.Id,
                message.Thread));
        }

        return facts;
    }

    private async Task EnsureMapperRestoredAsync(CancellationToken cancellationToken)
    {
        if (_factMapper is not null)
        {
            return;
        }

        var progress = await _feed.ReadProjectionProgressAsync(cancellationToken);
        var mapper = new PositionLiveStateFactMapper();
        var mappedSequenceId = 0L;
        while (mappedSequenceId < progress.LastAppliedSequenceId)
        {
            var items = await _feed.ReadProjectionFactsAsync(
                mappedSequenceId,
                BatchSize,
                cancellationToken);
            if (items.Count == 0)
            {
                throw new InvalidOperationException(
                    $"Live-state projection progress {progress.LastAppliedSequenceId} cannot be " +
                    "restored because its durable facts are incomplete.");
            }

            foreach (var item in items)
            {
                if (item.SequenceId > progress.LastAppliedSequenceId)
                {
                    break;
                }

                mapper.Apply(item.Fact);
                mappedSequenceId = item.SequenceId;
            }
        }

        if (mappedSequenceId != progress.LastAppliedSequenceId)
        {
            throw new InvalidOperationException(
                $"Live-state projection progress {progress.LastAppliedSequenceId} does not " +
                "identify a durable projection fact.");
        }

        _factMapper = mapper;
        _mappedSequenceId = mappedSequenceId;
    }
}
