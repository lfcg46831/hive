using Hive.Domain.Identity;

namespace Hive.Domain.Positions;

/// <summary>
/// Loads the runtime configuration for a position entity from the materialized registry/read model
/// (US-F0-06-T08a). Implementations are supplied by US-F0-06-T08b.
/// </summary>
public interface IPositionConfigurationProvider
{
    Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
        PositionEntityId entityId,
        CancellationToken cancellationToken);
}
