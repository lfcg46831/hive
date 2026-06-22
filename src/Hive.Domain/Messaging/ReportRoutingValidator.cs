using Hive.Domain.Organization;

namespace Hive.Domain.Messaging;

public sealed class ReportRoutingValidator
{
    private static readonly RoutingPathRule ReportPath =
        MessageRoutingRules.For<Report>().Paths.Single(
            path => path.Relation == RoutingRelation.DirectSubordinateToDirectSuperior);

    private readonly IOrganizationRelations _relations;

    public ReportRoutingValidator(IOrganizationRelations relations)
    {
        ArgumentNullException.ThrowIfNull(relations);
        _relations = relations;
    }

    public async ValueTask<ValidationResult> ValidateAsync(
        Report report,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(report);
        cancellationToken.ThrowIfCancellationRequested();

        var errors = new List<ValidationError>();
        var from = RequirePositionEndpoint(
            report.From,
            ReportPath.FromEndpointType,
            "from",
            errors);
        var to = RequirePositionEndpoint(
            report.To,
            ReportPath.ToEndpointType,
            "to",
            errors);

        if (errors.Count != 0)
        {
            return ValidationResult.Create(errors);
        }

        var fromProbe = await ProbePositionAsync(report, from!, cancellationToken);
        if (fromProbe.OrganizationMissing)
        {
            return ValidationResult.Create([RoutingValidationCatalog.OrganizationNotFound()]);
        }

        var toProbe = await ProbePositionAsync(report, to!, cancellationToken);
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
                report.OrganizationId,
                from!.PositionId,
                cancellationToken);

            return superior == to!.PositionId
                ? ValidationResult.Valid
                : ValidationResult.Create([RoutingValidationCatalog.DirectSuperiorRequired()]);
        }
        catch (OrganizationRelationNotFoundException)
        {
            return ValidationResult.Create([RoutingValidationCatalog.PositionNotFound("from.positionId")]);
        }
    }

    private async ValueTask<PositionProbe> ProbePositionAsync(
        Report report,
        PositionEndpointRef endpoint,
        CancellationToken cancellationToken)
    {
        try
        {
            var unit = await _relations.GetUnitOfPositionAsync(
                report.OrganizationId,
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
