using Akka.Actor;
using Akka.Cluster;
using Akka.Cluster.Sharding;
using Akka.Pattern;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Connectors;
using Microsoft.Extensions.Options;

namespace Hive.Actors.Sharding;

internal sealed class AkkaConnectorMessageSubmissionSink : IConnectorMessageSubmissionSink
{
    private static readonly TimeSpan AcknowledgementTimeout = TimeSpan.FromSeconds(30);

    private readonly ActorSystem _system;
    private readonly int _numberOfShards;
    private readonly object _regionGate = new();
    private IActorRef? _region;

    public AkkaConnectorMessageSubmissionSink(
        ActorSystem system,
        IOptions<HiveOptions> options)
    {
        _system = system ?? throw new ArgumentNullException(nameof(system));
        ArgumentNullException.ThrowIfNull(options);
        _numberOfShards = options.Value.Agents?.NumberOfShards
            ?? PositionMessageExtractor.DefaultNumberOfShards;
    }

    public async ValueTask<ConnectorMessageSubmissionResult> SubmitAsync(
        OrgMessage message,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();
        var destination = message.To as PositionEndpointRef
            ?? throw new ArgumentException(
                "Connector messages must target a position endpoint.",
                nameof(message));
        var envelope = PositionEnvelope.For(
            PositionEntityId.From(message.OrganizationId, destination.PositionId),
            new AcceptMessage(message));
        var acknowledgement = await GetOrStartShardRegion()
            .Ask<AcceptMessageResult>(
                envelope,
                AcknowledgementTimeout,
                cancellationToken)
            .ConfigureAwait(false);
        if (acknowledgement.MessageId != message.Id)
        {
            throw new InvalidOperationException(
                "Position acknowledgement did not match the submitted connector message.");
        }

        return acknowledgement.Decision switch
        {
            AcceptMessageDecision.Accepted => ConnectorMessageSubmissionResult.Accepted(),
            AcceptMessageDecision.AlreadyAccepted =>
                ConnectorMessageSubmissionResult.AlreadyAccepted(),
            _ => throw new InvalidOperationException(
                "Position returned an unsupported connector message acknowledgement."),
        };
    }

    private IActorRef GetOrStartShardRegion()
    {
        if (_region is { } existing)
        {
            return existing;
        }

        lock (_regionGate)
        {
            if (_region is { } cached)
            {
                return cached;
            }

            var sharding = ClusterSharding.Get(_system);
            try
            {
                _region = sharding.ShardRegion(PositionEntityId.EntityTypeName);
            }
            catch (ArgumentException) when (!Cluster.Get(_system).SelfRoles.Contains(
                                                NodeRoleNames.Agents))
            {
                _region = sharding.StartProxy(
                    PositionEntityId.EntityTypeName,
                    NodeRoleNames.Agents,
                    new PositionMessageExtractor(_numberOfShards));
            }

            return _region;
        }
    }
}
