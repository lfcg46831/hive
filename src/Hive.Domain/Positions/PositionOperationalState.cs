namespace Hive.Domain.Positions;

/// <summary>
/// Operational phases of a position entity around recovery and the runtime-configuration gate
/// (US-F0-06-T08a).
/// </summary>
public enum PositionOperationalState
{
    Recovering = 1,
    LoadingConfiguration = 2,
    ConfigurationBlocked = 3,
    Ready = 4,
}
