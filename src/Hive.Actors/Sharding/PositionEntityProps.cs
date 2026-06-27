using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Positions;

namespace Hive.Actors.Sharding;

/// <summary>
/// Supplies the real persistent <see cref="PositionActor"/> entity props for Cluster Sharding
/// (US-F0-06-T06b), keeping sharding setup independent from the concrete actor constructor.
/// </summary>
internal sealed class PositionEntityProps : IPositionEntityProps
{
    private readonly IPositionConfigurationProvider _configurationProvider;

    public PositionEntityProps(IPositionConfigurationProvider configurationProvider)
    {
        _configurationProvider = configurationProvider
            ?? throw new ArgumentNullException(nameof(configurationProvider));
    }

    public Props Create(string entityId) =>
        Props.Create(() => new PositionActor(entityId, _configurationProvider));
}
