using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Auditing;
using Hive.Domain.Positions;
using Hive.Infrastructure.Auditing;

namespace Hive.Actors.Sharding;

/// <summary>
/// Supplies the real persistent <see cref="PositionActor"/> entity props for Cluster Sharding
/// (US-F0-06-T06b), keeping sharding setup independent from the concrete actor constructor.
/// </summary>
internal sealed class PositionEntityProps : IPositionEntityProps
{
    private readonly IPositionConfigurationProvider _configurationProvider;
    private readonly IPositionOccupantFactory _occupantFactory;
    private readonly IPositionProjectionPublisher _projectionPublisher;

    public PositionEntityProps(
        IPositionConfigurationProvider configurationProvider,
        IPositionOccupantFactory occupantFactory,
        IJourneyAuditLog? auditLog = null)
    {
        _configurationProvider = configurationProvider
            ?? throw new ArgumentNullException(nameof(configurationProvider));
        _occupantFactory = occupantFactory
            ?? throw new ArgumentNullException(nameof(occupantFactory));
        var resolvedAuditLog = auditLog ?? NoopJourneyAuditLog.Instance;
        _projectionPublisher = new JourneyAuditPositionProjectionPublisher(
            resolvedAuditLog);
    }

    public Props Create(string entityId) =>
        Props.Create(() => new PositionActor(
            entityId,
            _configurationProvider,
            _occupantFactory,
            _projectionPublisher,
            () => DateTimeOffset.UtcNow));
}
