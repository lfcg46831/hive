using Hive.Domain.Identity;
using Hive.Domain.Positions;

namespace Hive.Infrastructure.Organization.Registry;

internal sealed class UnavailablePositionConfigurationProvider(string connectionStringName)
    : IPositionConfigurationProvider
{
    public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
        PositionEntityId entityId,
        CancellationToken cancellationToken) =>
        Task.FromResult(PositionRuntimeConfigurationLoadResult.TechnicalFailure(
            new InvalidOperationException(
                $"Position runtime configuration is unavailable because connection string '{connectionStringName}' is not configured.")));
}
