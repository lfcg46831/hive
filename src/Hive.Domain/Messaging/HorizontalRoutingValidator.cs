using Hive.Domain.Identity;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;

namespace Hive.Domain.Messaging;

/// <summary>Validates same-unit, leadership and declared-channel routes for horizontal initiation.</summary>
/// <remarks>
/// Correlation, open-request limits and admission are separate concerns. Lookup failures after
/// successful position probes propagate: they do not establish the absence of a declared channel.
/// </remarks>
public sealed class HorizontalRoutingValidator
{
    private readonly IOrganizationRelations _relations;
    private readonly IPeerChannelContracts _contracts;

    public PeerRequestRejectionPolicy RejectionPolicy { get; }

    public HorizontalRoutingValidator(IOrganizationRelations relations, IPeerChannelContracts contracts)
    {
        ArgumentNullException.ThrowIfNull(relations);
        ArgumentNullException.ThrowIfNull(contracts);
        _relations = relations;
        _contracts = contracts;
        RejectionPolicy = new PeerRequestRejectionPolicy(relations, contracts);
    }

    public ValueTask<ValidationResult> ValidateAsync(
        Memo memo,
        CancellationToken cancellationToken = default) =>
        ValidateInitiationAsync(memo, PeerChannelMessageType.Memo, cancellationToken);

    public ValueTask<ValidationResult> ValidateAsync(
        PeerRequest request,
        CancellationToken cancellationToken = default) =>
        ValidateInitiationAsync(request, PeerChannelMessageType.PeerRequest, cancellationToken);

    private async ValueTask<ValidationResult> ValidateInitiationAsync(
        OrgMessage message,
        PeerChannelMessageType messageType,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(message);
        cancellationToken.ThrowIfCancellationRequested();

        var rule = MessageRoutingRules.For(message.GetType());
        var errors = new List<ValidationError>();
        var from = message.From as PositionEndpointRef;
        var to = message.To as PositionEndpointRef;
        if (from is null || !rule.Paths.Any(path => path.FromEndpointType == message.From.GetType()))
        {
            errors.Add(RoutingValidationCatalog.EndpointNotAllowed("from"));
        }

        if (to is null || !rule.Paths.Any(path => path.ToEndpointType == message.To.GetType()))
        {
            errors.Add(RoutingValidationCatalog.EndpointNotAllowed("to"));
        }

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        UnitId? fromUnit;
        UnitId? toUnit;
        try
        {
            fromUnit = await _relations.GetUnitOfPositionAsync(
                message.OrganizationId, from!.PositionId, cancellationToken);
            toUnit = await _relations.GetUnitOfPositionAsync(
                message.OrganizationId, to!.PositionId, cancellationToken);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }

        if (fromUnit is null)
        {
            errors.Add(RoutingValidationCatalog.PositionNotFound("from.positionId"));
        }

        if (toUnit is null)
        {
            errors.Add(RoutingValidationCatalog.PositionNotFound("to.positionId"));
        }

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        if (fromUnit == toUnit)
        {
            return ValidationResult.Valid;
        }

        var fromLeader = await _relations.GetUnitLeadershipAsync(
            message.OrganizationId, fromUnit!, cancellationToken);
        var toLeader = await _relations.GetUnitLeadershipAsync(
            message.OrganizationId, toUnit!, cancellationToken);
        if (from!.PositionId == fromLeader && to!.PositionId == toLeader)
        {
            return ValidationResult.Valid;
        }

        var contract = await _contracts.ResolveChannelAsync(
            message.OrganizationId, fromUnit!, toUnit!, cancellationToken);
        if (contract is null)
        {
            return ValidationResult.Create([RoutingValidationCatalog.PeerChannelRequired()]);
        }

        return contract.Types.Contains(messageType)
            ? ValidationResult.Valid
            : ValidationResult.Create([RoutingValidationCatalog.PeerTypeNotAllowed()]);
    }
}
