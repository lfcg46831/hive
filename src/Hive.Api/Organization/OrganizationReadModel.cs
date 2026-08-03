using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.ReadModels;

namespace Hive.Api.Organization;

internal sealed class OrganizationReadModel : IOrganizationReadModel
{
    private readonly IOrganogramSnapshotReader _snapshotReader;
    private readonly TimeProvider _timeProvider;

    public OrganizationReadModel(IOrganogramSnapshotReader snapshotReader)
        : this(snapshotReader, TimeProvider.System)
    {
    }

    internal OrganizationReadModel(
        IOrganogramSnapshotReader snapshotReader,
        TimeProvider? timeProvider)
    {
        _snapshotReader = snapshotReader
            ?? throw new ArgumentNullException(nameof(snapshotReader));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async ValueTask<OrganizationReadResult<OrganogramResponse>> ReadOrganogramAsync(
        OrganizationId organizationId,
        UnitId? rootUnitId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshotReader.IsAvailable)
        {
            return OrganizationReadResult<OrganogramResponse>.Unavailable;
        }

        var snapshot = await _snapshotReader.FindAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return OrganizationReadResult<OrganogramResponse>.Available(null);
        }

        var responseRoot = rootUnitId?.Value ?? snapshot.RootUnitId;
        var includedUnitIds = SelectUnitSubtree(snapshot.Units, responseRoot);
        if (includedUnitIds is null)
        {
            return OrganizationReadResult<OrganogramResponse>.Available(null);
        }

        var generatedAt = Utc(_timeProvider.GetUtcNow());
        var response = new OrganogramResponse(
            MapRegistry(snapshot),
            generatedAt,
            responseRoot,
            MapOrganization(snapshot),
            snapshot.Units
                .Where(unit => includedUnitIds.Contains(unit.Id))
                .Select(MapUnit)
                .ToArray(),
            snapshot.Positions
                .Where(position => includedUnitIds.Contains(position.UnitId))
                .Select(position => MapPosition(snapshot, position))
                .ToArray());
        return OrganizationReadResult<OrganogramResponse>.Available(response);
    }

    public async ValueTask<OrganizationReadResult<PositionDetailResponse>> ReadPositionAsync(
        OrganizationId organizationId,
        PositionId positionId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(positionId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshotReader.IsAvailable)
        {
            return OrganizationReadResult<PositionDetailResponse>.Unavailable;
        }

        var snapshot = await _snapshotReader.FindAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return OrganizationReadResult<PositionDetailResponse>.Available(null);
        }

        var position = snapshot.Positions.FirstOrDefault(item =>
            string.Equals(item.Id, positionId.Value, StringComparison.Ordinal));
        if (position is null)
        {
            return OrganizationReadResult<PositionDetailResponse>.Available(null);
        }

        return OrganizationReadResult<PositionDetailResponse>.Available(
            new PositionDetailResponse(
                MapRegistry(snapshot),
                Utc(_timeProvider.GetUtcNow()),
                MapPosition(snapshot, position)));
    }

    public async ValueTask<OrganizationReadResult<PositionStatesResponse>> ReadPositionStatesAsync(
        OrganizationId organizationId,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        cancellationToken.ThrowIfCancellationRequested();
        if (!_snapshotReader.IsAvailable)
        {
            return OrganizationReadResult<PositionStatesResponse>.Unavailable;
        }

        var snapshot = await _snapshotReader.FindAsync(organizationId, cancellationToken)
            .ConfigureAwait(false);
        if (snapshot is null)
        {
            return OrganizationReadResult<PositionStatesResponse>.Available(null);
        }

        return OrganizationReadResult<PositionStatesResponse>.Available(
            new PositionStatesResponse(
                MapRegistry(snapshot),
                Utc(_timeProvider.GetUtcNow()),
                snapshot.LastEventAppliedAtUtc,
                snapshot.PositionStates
                    .Select(MapPositionState)
                    .ToArray()));
    }

    private static HashSet<string>? SelectUnitSubtree(
        IReadOnlyList<OrganogramUnitSnapshot> units,
        string rootUnitId)
    {
        if (!units.Any(unit => string.Equals(unit.Id, rootUnitId, StringComparison.Ordinal)))
        {
            return null;
        }

        var included = new HashSet<string>(StringComparer.Ordinal) { rootUnitId };
        var pending = new Queue<string>();
        pending.Enqueue(rootUnitId);
        while (pending.TryDequeue(out var parent))
        {
            foreach (var child in units.Where(unit =>
                         string.Equals(unit.ParentUnitId, parent, StringComparison.Ordinal)))
            {
                if (included.Add(child.Id))
                {
                    pending.Enqueue(child.Id);
                }
            }
        }

        return included;
    }

    private static OrganizationSummary MapOrganization(OrganogramSnapshot snapshot) =>
        new(
            snapshot.OrganizationId,
            snapshot.OrganizationName,
            snapshot.RootUnitId,
            snapshot.RootPositionId);

    private static OrganizationUnit MapUnit(OrganogramUnitSnapshot unit) =>
        new(unit.Id, unit.Name, unit.ParentUnitId, unit.LeadershipPositionId);

    private static OrganizationPosition MapPosition(
        OrganogramSnapshot snapshot,
        OrganogramPositionSnapshot position)
    {
        var occupantType = position.OccupantType switch
        {
            OccupantType.AiAgent => OrganizationOccupantType.AiAgent,
            OccupantType.Human => OrganizationOccupantType.Human,
            _ => throw new InvalidOperationException(
                $"Unknown materialized occupant type '{position.OccupantType}'."),
        };
        var occupantId = occupantType == OrganizationOccupantType.AiAgent
            ? ConfiguredAiOccupantIdentity.For(
                OrganizationId.From(snapshot.OrganizationId),
                PositionId.From(position.Id)).Value
            : null;
        return new OrganizationPosition(
            position.Id,
            position.Name,
            position.UnitId,
            new OrganizationOccupant(occupantId, occupantType),
            new PositionHierarchy(
                position.ReportsToPositionId,
                snapshot.Positions
                    .Where(candidate => string.Equals(
                        candidate.ReportsToPositionId,
                        position.Id,
                        StringComparison.Ordinal))
                    .Select(candidate => candidate.Id)
                    .ToArray()),
            MapPositionState(FindPositionState(snapshot, position.Id)));
    }

    private static PositionLiveStateSnapshot FindPositionState(
        OrganogramSnapshot snapshot,
        string positionId) =>
        snapshot.PositionStates.SingleOrDefault(state => string.Equals(
            state.PositionId,
            positionId,
            StringComparison.Ordinal))
        ?? throw new InvalidOperationException(
            $"Position '{positionId}' does not have a materialized live state.");

    private static OrganizationPositionState MapPositionState(PositionLiveStateSnapshot snapshot) =>
        new(
            snapshot.PositionId,
            snapshot.State switch
            {
                PositionLiveState.Offline => PositionOperationalState.Offline,
                PositionLiveState.Blocked => PositionOperationalState.Blocked,
                PositionLiveState.WaitingHuman => PositionOperationalState.WaitingHuman,
                PositionLiveState.Working => PositionOperationalState.Working,
                PositionLiveState.Idle => PositionOperationalState.Idle,
                _ => throw new InvalidOperationException(
                    $"Unknown materialized position live state '{snapshot.State}'."),
            },
            snapshot.Sequence,
            Utc(snapshot.UpdatedAtUtc),
            snapshot.LastCorrelatedEvent is null
                ? null
                : new PositionCorrelatedEvent(
                    snapshot.LastCorrelatedEvent.Type,
                    snapshot.LastCorrelatedEvent.ThreadId,
                    Utc(snapshot.LastCorrelatedEvent.OccurredAtUtc)));

    private static RegistryVersion MapRegistry(OrganogramSnapshot snapshot) =>
        new(snapshot.RegistryVersion, snapshot.RegistryFingerprint);

    private static DateTimeOffset Utc(DateTimeOffset value) => value.ToUniversalTime();
}
