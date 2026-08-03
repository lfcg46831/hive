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
}
