using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Auditing;
using Hive.Domain.Organization;
using Hive.Domain.Messaging;
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
    private readonly RetainedActionResumeCoordinator? _resumeCoordinator;
    private readonly IOccupantReplyMessageValidator _occupantReplyValidator;
    private readonly IOccupantResponseEscalationTargetResolver _responseTargetResolver;
    private readonly PeerRequestLimitResolver _peerRequestLimits;
    private readonly HorizontalRoutingValidator _horizontalRouting;

    public PositionEntityProps(
        IPositionConfigurationProvider configurationProvider,
        IPositionOccupantFactory occupantFactory,
        IOrganizationRelations organizationRelations,
        IPeerChannelContracts peerChannelContracts,
        IJourneyAuditLog? auditLog = null,
        RetainedActionResumeCoordinator? resumeCoordinator = null)
    {
        _configurationProvider = configurationProvider
            ?? throw new ArgumentNullException(nameof(configurationProvider));
        _occupantFactory = occupantFactory
            ?? throw new ArgumentNullException(nameof(occupantFactory));
        ArgumentNullException.ThrowIfNull(organizationRelations);
        _peerRequestLimits = new PeerRequestLimitResolver(organizationRelations, peerChannelContracts);
        _horizontalRouting = new HorizontalRoutingValidator(organizationRelations, peerChannelContracts);
        _occupantReplyValidator = new OccupantReplyMessageValidator(organizationRelations);
        _responseTargetResolver = new OrganizationRelationsOccupantResponseEscalationTargetResolver(
            organizationRelations);
        var resolvedAuditLog = auditLog ?? NoopJourneyAuditLog.Instance;
        _projectionPublisher = new JourneyAuditPositionProjectionPublisher(
            resolvedAuditLog);
        _resumeCoordinator = resumeCoordinator;
    }

    public Props Create(string entityId) =>
        Props.Create(() => new PositionActor(
            entityId,
            _configurationProvider,
            _occupantFactory,
            _projectionPublisher,
            () => DateTimeOffset.UtcNow,
            _resumeCoordinator,
            _occupantReplyValidator,
            ShardedPositionMessageEmitter.Instance,
            null,
            _responseTargetResolver,
            null,
            _peerRequestLimits,
            _horizontalRouting,
            null));
}
