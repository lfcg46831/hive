using System.Text;
using Hive.Actors.Serialization;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Inbox.ReadModels;

namespace Hive.Actors.Inbox;

internal sealed class InboxProjectionWorker
{
    internal const int BatchSize = 128;

    private readonly IInboxProjectionJournal _journal;
    private readonly IInboxProjectionFeed _feed;
    private readonly TimeProvider _timeProvider;
    private InboxProjectionFactMapper? _factMapper;
    private long _mappedSequenceId;

    public InboxProjectionWorker(
        IInboxProjectionJournal journal,
        IInboxProjectionFeed feed,
        TimeProvider timeProvider)
    {
        _journal = journal ?? throw new ArgumentNullException(nameof(journal));
        _feed = feed ?? throw new ArgumentNullException(nameof(feed));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public async Task<int> CapturePositionJournalBatchAsync(CancellationToken cancellationToken)
    {
        var checkpoint = await _feed.ReadCheckpointAsync(
            InboxProjectionSubscription.PositionJournal,
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
            var applied = await EnsureMapperRestoredAsync(cancellationToken);
            var items = await _feed.ReadProjectionFactsAsync(
                _mappedSequenceId,
                BatchSize,
                cancellationToken);
            foreach (var item in items)
            {
                var changes = _factMapper!.Apply(item.Fact);
                if (await _feed.ApplyProjectionFactAsync(item, changes, cancellationToken))
                {
                    applied++;
                }

                _mappedSequenceId = item.SequenceId;
            }

            var expirations = _factMapper!.RefreshExpirations();
            if (expirations.Count > 0)
            {
                applied += await _feed.ApplyProjectionChangesAsync(
                    _mappedSequenceId,
                    expirations,
                    cancellationToken);
            }

            return applied;
        }
        catch
        {
            // The mapper may be ahead of durable progress when a commit or cancellation fails.
            // Rebuild it exclusively from committed facts before retrying.
            _factMapper = null;
            _mappedSequenceId = 0;
            throw;
        }
    }

    internal static IReadOnlyCollection<InboxProjectionFact> Facts(
        InboxProjectionJournalEvent item)
    {
        ArgumentNullException.ThrowIfNull(item);
        var @event = item.Event;
        var received = @event as MessageReceived;
        var facts = new List<InboxProjectionFact>(received is null ? 1 : 2)
        {
            new(
                InboxProjectionSource.PositionEvent,
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
            facts.Add(new InboxProjectionFact(
                InboxProjectionSource.OrganizationalMessage,
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

    private async Task<int> EnsureMapperRestoredAsync(CancellationToken cancellationToken)
    {
        if (_factMapper is not null)
        {
            return 0;
        }

        var progress = await _feed.ReadProjectionProgressAsync(cancellationToken);
        var replayClock = new ReplayTimeProvider(DateTimeOffset.MinValue);
        var mapper = new InboxProjectionFactMapper(replayClock);
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
                    $"Inbox projection progress {progress.LastAppliedSequenceId} cannot be " +
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
                $"Inbox projection progress {progress.LastAppliedSequenceId} does not " +
                "identify a durable projection fact.");
        }

        replayClock.UtcNow = _timeProvider.GetUtcNow().ToUniversalTime();
        var expirations = mapper.RefreshExpirations();
        var appliedExpirations = expirations.Count == 0
            ? 0
            : await _feed.ApplyProjectionChangesAsync(
                mappedSequenceId,
                expirations,
                cancellationToken);
        _factMapper = mapper;
        _mappedSequenceId = mappedSequenceId;
        return appliedExpirations;
    }

    private sealed class ReplayTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public DateTimeOffset UtcNow { get; set; } = utcNow;

        public override DateTimeOffset GetUtcNow() => UtcNow;
    }
}
