namespace Hive.Domain.Positions;

/// <summary>
/// Publishes PositionActor audit/read-model projection signals after recovery or confirmed writes
/// (US-F0-06-T10).
/// </summary>
public interface IPositionProjectionPublisher
{
    /// <summary>Publishes one projection signal for downstream audit/read-model consumers.</summary>
    void Publish(PositionProjectionEvent @event);
}
