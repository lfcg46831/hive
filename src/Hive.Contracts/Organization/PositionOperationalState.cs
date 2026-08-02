using System.Text.Json.Serialization;

namespace Hive.Contracts.Organization;

/// <summary>
/// Public operational state in canonical precedence order, from strongest to weakest.
/// </summary>
[JsonConverter(typeof(JsonStringEnumConverter<PositionOperationalState>))]
public enum PositionOperationalState
{
    Offline,
    Blocked,
    WaitingHuman,
    Working,
    Idle,
}
