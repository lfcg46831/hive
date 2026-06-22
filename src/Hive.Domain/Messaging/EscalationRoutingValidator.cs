using Hive.Domain.Organization;

namespace Hive.Domain.Messaging;

public sealed class EscalationRoutingValidator
{
    private static readonly RoutingPathRule PositionPath =
        MessageRoutingRules.For<Escalation>().Paths.Single(
            path => path.Relation == RoutingRelation.DirectSubordinateToDirectSuperior);

    private static readonly RoutingPathRule OwnerPath =
        MessageRoutingRules.For<Escalation>().Paths.Single(
            path => path.Relation == RoutingRelation.RootLeadershipToOrganizationOwner);

    private readonly IOrganizationRelations _relations;

    public EscalationRoutingValidator(IOrganizationRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        Escalation escalation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(escalation);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<ValidationError>();
        var from = RequirePositionEndpoint(escalation.From, "from", errors);
        var destination = RequireDestination(escalation.To, errors);

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        return destination switch
        {
            PositionEndpointRef position => await ValidatePositionRouteAsync(
                escalation,
                from!,
                position,
                cancellationToken),
            OrganizationOwnerEndpointRef => await ValidateOwnerRouteAsync(
                escalation,
                from!,
                cancellationToken),
            _ => throw new InvalidOperationException(
                "Validated escalation destination is unsupported."),
        };
    }

    private async ValueTask<ValidationResult> ValidatePositionRouteAsync(
        Escalation escalation,
        PositionEndpointRef from,
        PositionEndpointRef to,
        CancellationToken cancellationToken)
    {
        var errors = new List<ValidationError>();
        var sourceProbe = await ProbePositionAsync(escalation, from, cancellationToken);
        if (sourceProbe.OrganizationMissing)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }

        var destinationProbe = await ProbePositionAsync(escalation, to, cancellationToken);
        if (destinationProbe.OrganizationMissing)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }

        if (!sourceProbe.Exists)
        {
            errors.Add(RoutingValidationCatalog.PositionNotFound("from.positionId"));
        }

        if (!destinationProbe.Exists)
        {
            errors.Add(RoutingValidationCatalog.PositionNotFound("to.positionId"));
        }

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        try
        {
            var superior = await _relations.GetDirectSuperiorAsync(
                escalation.OrganizationId,
                from.PositionId,
                cancellationToken);

            return superior == to.PositionId
                ? ValidationResult.Valid
                : ValidationResult.Create([RoutingValidationCatalog.DirectSuperiorRequired()]);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([RoutingValidationCatalog.PositionNotFound("from.positionId")]);
        }
    }

    private async ValueTask<ValidationResult> ValidateOwnerRouteAsync(
        Escalation escalation,
        PositionEndpointRef from,
        CancellationToken cancellationToken)
    {
        var sourceProbe = await ProbePositionAsync(escalation, from, cancellationToken);
        if (sourceProbe.OrganizationMissing)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }

        if (!sourceProbe.Exists)
        {
            return ValidationResult.Create([RoutingValidationCatalog.PositionNotFound("from.positionId")]);
        }

        try
        {
            var rootLeadership = await _relations.GetRootUnitLeadershipAsync(
                escalation.OrganizationId,
                cancellationToken);
            if (rootLeadership != from.PositionId)
            {
                return ValidationResult.Create([RoutingValidationCatalog.RootLeadershipRequired()]);
            }

            _ = await _relations.GetOrganizationOwnerAsync(
                escalation.OrganizationId,
                cancellationToken);
            return ValidationResult.Valid;
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }
    }

    private async ValueTask<PositionProbe> ProbePositionAsync(
        Escalation escalation,
        PositionEndpointRef endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var unit = await _relations.GetUnitOfPositionAsync(
                escalation.OrganizationId,
                endpoint.PositionId,
                cancellationToken);
            return new PositionProbe(unit is not null, OrganizationMissing: false);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return new PositionProbe(Exists: false, OrganizationMissing: true);
        }
    }

    private static PositionEndpointRef? RequirePositionEndpoint(
        EndpointRef endpoint,
        string path,
        ICollection<ValidationError> errors)
    {
        if (endpoint.GetType() == PositionPath.FromEndpointType)
        {
            return (PositionEndpointRef)endpoint;
        }

        errors.Add(RoutingValidationCatalog.EndpointNotAllowed(path));
        return null;
    }

    private static EndpointRef? RequireDestination(
        EndpointRef endpoint,
        ICollection<ValidationError> errors)
    {
        if (endpoint.GetType() == PositionPath.ToEndpointType
            || endpoint.GetType() == OwnerPath.ToEndpointType)
        {
            return endpoint;
        }

        errors.Add(RoutingValidationCatalog.EndpointNotAllowed("to"));
        return null;
    }

    private readonly record struct PositionProbe(bool Exists, bool OrganizationMissing);
}
