using Hive.Domain.Organization;

namespace Hive.Domain.Messaging;

public sealed class DirectiveRoutingValidator
{
    private static readonly RoutingPathRule DirectivePath =
        MessageRoutingRules.For<Directive>().Paths.Single(
            path => path.Relation == RoutingRelation.DirectSuperiorToDirectSubordinate);

    private readonly IOrganizationRelations _relations;

    public DirectiveRoutingValidator(IOrganizationRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        Directive directive,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(directive);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<ValidationError>();
        var from = RequirePositionEndpoint(
            directive.From,
            DirectivePath.FromEndpointType,
            "from",
            errors);
        var to = RequirePositionEndpoint(
            directive.To,
            DirectivePath.ToEndpointType,
            "to",
            errors);

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        var fromProbe = await ProbePositionAsync(
            directive,
            from!,
            cancellationToken);
        if (fromProbe.OrganizationMissing)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }

        var toProbe = await ProbePositionAsync(
            directive,
            to!,
            cancellationToken);
        if (toProbe.OrganizationMissing)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }

        if (!fromProbe.Exists)
        {
            errors.Add(RoutingValidationCatalog.PositionNotFound("from.positionId"));
        }

        if (!toProbe.Exists)
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
                directive.OrganizationId,
                to!.PositionId,
                cancellationToken);

            return superior == from!.PositionId
                ? ValidationResult.Valid
                : ValidationResult.Create([RoutingValidationCatalog.DirectSubordinateRequired()]);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([RoutingValidationCatalog.PositionNotFound("to.positionId")]);
        }
    }

    private async ValueTask<PositionProbe> ProbePositionAsync(
        Directive directive,
        PositionEndpointRef endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var unit = await _relations.GetUnitOfPositionAsync(
                directive.OrganizationId,
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
        Type expectedType,
        string path,
        ICollection<ValidationError> errors)
    {
        if (endpoint.GetType() == expectedType)
        {
            return (PositionEndpointRef)endpoint;
        }

        errors.Add(RoutingValidationCatalog.EndpointNotAllowed(path));
        return null;
    }

    private readonly record struct PositionProbe(bool Exists, bool OrganizationMissing);
}
