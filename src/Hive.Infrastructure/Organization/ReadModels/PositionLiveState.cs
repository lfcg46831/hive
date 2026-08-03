namespace Hive.Infrastructure.Organization.ReadModels;

/// <summary>
/// Canonical live position state in deterministic precedence order, strongest first.
/// </summary>
public enum PositionLiveState
{
    Offline,
    Blocked,
    WaitingHuman,
    Working,
    Idle,
}
