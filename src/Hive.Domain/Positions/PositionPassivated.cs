namespace Hive.Domain.Positions;

/// <summary>
/// The position was passivated (stopped while idle to free resources) — the fact produced by a
/// successful <see cref="RequestPassivation"/> once the entity's guard rails allowed it
/// (US-F0-06-T11). Persisting the fact lets the audit/read model observe the lifecycle; the position
/// stays addressable and re-activates on the next message, schedule or subscription.
/// <see cref="Reason"/> is optional diagnostic context and, when provided, carries content.
/// </summary>
public sealed record PositionPassivated : PositionEvent
{
    public PositionPassivated(DateTimeOffset occurredAt, string? reason = null)
        : base(occurredAt)
    {
        Reason = reason is null ? null : CommandText.RequireContent(reason, nameof(reason));
    }

    /// <summary>An optional human-readable reason for the passivation.</summary>
    public string? Reason { get; }
}
