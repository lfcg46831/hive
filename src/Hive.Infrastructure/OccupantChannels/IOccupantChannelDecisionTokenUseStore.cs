namespace Hive.Infrastructure.OccupantChannels;

internal interface IOccupantChannelDecisionTokenUseStore
{
    ValueTask<bool> TryConsumeAsync(
        Guid tokenId,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableOccupantChannelDecisionTokenUseStore
    : IOccupantChannelDecisionTokenUseStore
{
    public static UnavailableOccupantChannelDecisionTokenUseStore Instance { get; } = new();

    private UnavailableOccupantChannelDecisionTokenUseStore()
    {
    }

    public ValueTask<bool> TryConsumeAsync(
        Guid tokenId,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default) =>
        ValueTask.FromException<bool>(new InvalidOperationException(
            "The durable occupant-channel decision-token use store is unavailable."));
}

internal sealed class InMemoryOccupantChannelDecisionTokenUseStore
    : IOccupantChannelDecisionTokenUseStore
{
    private readonly object _gate = new();
    private readonly Dictionary<Guid, DateTimeOffset> _uses = [];

    public ValueTask<bool> TryConsumeAsync(
        Guid tokenId,
        DateTimeOffset expiresAtUtc,
        DateTimeOffset consumedAtUtc,
        CancellationToken cancellationToken = default)
    {
        cancellationToken.ThrowIfCancellationRequested();
        if (tokenId == Guid.Empty)
        {
            throw new ArgumentException("Decision token id cannot be empty.", nameof(tokenId));
        }

        if (expiresAtUtc.Offset != TimeSpan.Zero || consumedAtUtc.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException("Decision token use timestamps must use the UTC offset.");
        }

        if (expiresAtUtc <= consumedAtUtc)
        {
            return ValueTask.FromResult(false);
        }

        lock (_gate)
        {
            foreach (var expired in _uses
                         .Where(use => use.Value <= consumedAtUtc)
                         .Select(use => use.Key)
                         .ToArray())
            {
                _uses.Remove(expired);
            }

            return ValueTask.FromResult(_uses.TryAdd(tokenId, expiresAtUtc));
        }
    }
}
