namespace Hive.Domain.Positions;

/// <summary>
/// Request that the position be passivated (stopped while idle to free resources), to be re-activated
/// on the next message, schedule or subscription without loss of addressability. The command is a
/// bare intent: the inactivity criteria and the guard rails that refuse passivation while critical
/// tasks or pending delivery exist are decided by the entity (US-F0-06-T11), not by this contract.
/// <see cref="Reason"/> is optional diagnostic context and, when provided, must carry content.
/// </summary>
public sealed record RequestPassivation : PositionCommand
{
    public RequestPassivation(string? reason = null)
    {
        Reason = reason is null ? null : CommandText.RequireContent(reason, nameof(reason));
    }

    /// <summary>An optional human-readable reason for the passivation request.</summary>
    public string? Reason { get; }
}
