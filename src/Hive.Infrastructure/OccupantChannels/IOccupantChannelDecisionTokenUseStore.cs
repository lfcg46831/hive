namespace Hive.Infrastructure.OccupantChannels;

internal interface IOccupantChannelDecisionTokenUseStore
{
    ValueTask<OccupantChannelDecisionTokenUseResult> TryConsumeAsync(
        Guid tokenId,
        Guid operationId,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default);
}

internal enum OccupantChannelDecisionTokenUseResult
{
    Consumed = 1,
    AlreadyConsumedByOperation = 2,
    AlreadyConsumed = 3,
}

internal sealed class UnavailableOccupantChannelDecisionTokenUseStore
    : IOccupantChannelDecisionTokenUseStore
{
    public static UnavailableOccupantChannelDecisionTokenUseStore Instance { get; } = new();

    private UnavailableOccupantChannelDecisionTokenUseStore()
    {
    }

    public ValueTask<OccupantChannelDecisionTokenUseResult> TryConsumeAsync(
        Guid tokenId,
        Guid operationId,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<OccupantChannelDecisionTokenUseResult>(new InvalidOperationException(
            "The durable occupant-channel decision-token use store is unavailable."));
}

internal sealed class InMemoryOccupantChannelDecisionTokenUseStore
    : IOccupantChannelDecisionTokenUseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, TokenUse> _uses = [];

    public ValueTask<OccupantChannelDecisionTokenUseResult> TryConsumeAsync(
        Guid tokenId,
        Guid operationId,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tokenId == Guid.Empty)
        {
            throw new ArgumentException("Decision token id cannot be empty.", nameof(tokenId));
        }

        if (operationId == Guid.Empty)
        {
            throw new ArgumentException("Decision token operation id cannot be empty.", nameof(operationId));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero || consumedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Decision token use timestamps must use the UTC offset.");
        }

        if (expiresAtUtc <= consumedAtUtc)
        {
            return ValueTask.FromResult(OccupantChannelDecisionTokenUseResult.AlreadyConsumed);
        }

        lock (_gate)
        {
            foreach (var expired in _uses
                         .Where(use => use.Value.ExpiresAtUtc <= consumedAtUtc)
                         .Select(use => use.Key)
                         .ToArray())
            {
                _uses.Remove(expired);
            }

            if (_uses.TryGetValue(tokenId, out var existing))
            {
                return ValueTask.FromResult(existing.OperationId == operationId
                    ? OccupantChannelDecisionTokenUseResult.AlreadyConsumedByOperation
                    : OccupantChannelDecisionTokenUseResult.AlreadyConsumed);
            }

            _uses.Add(tokenId, new TokenUse(operationId, expiresAtUtc));
            return ValueTask.FromResult(OccupantChannelDecisionTokenUseResult.Consumed);
        }
    }

    private sealed record TokenUse(Guid OperationId, DateTimeOffset ExpiresAtUtc);
}
