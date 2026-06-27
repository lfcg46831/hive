using System.Collections.Immutable;

namespace Hive.Domain.Positions;

/// <summary>
/// Domain decision for a safe passivation request (US-F0-06-T11a).
/// </summary>
public sealed record PositionPassivationDecision
{
    public PositionPassivationDecision(IEnumerable<PositionPassivationBlockReason> blockReasons)
    {
        ArgumentNullException.ThrowIfNull(blockReasons);

        var builder = ImmutableArray.CreateBuilder<PositionPassivationBlockReason>();
        foreach (var reason in blockReasons)
        {
            if (!Enum.IsDefined(reason))
            {
                throw new ArgumentOutOfRangeException(
                    nameof(blockReasons),
                    reason,
                    "Unknown position passivation block reason.");
            }

            builder.Add(reason);
        }

        BlockReasons = builder.ToImmutable();
    }

    /// <summary>Whether the position currently has no guard rail blocking passivation.</summary>
    public bool IsAllowed => BlockReasons.IsEmpty;

    /// <summary>The deterministic set of guard rails that currently block passivation.</summary>
    public ImmutableArray<PositionPassivationBlockReason> BlockReasons { get; }
}

/// <summary>Coarse guard-rail reasons that make a position ineligible for passivation.</summary>
public enum PositionPassivationBlockReason
{
    PendingDelivery = 1,
    CriticalTaskOpen = 2,
    ActiveSchedule = 3,
    ActiveSubscription = 4,
}
