using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Microsoft.AspNetCore.Mvc;

namespace Hive.Api.Directives;

public static class DirectiveSubmissionEndpointExtensions
{
    public const string BasePath = "/api/v1/organizations";

    public static IEndpointRouteBuilder MapHiveDirectiveSubmissionApi(
        this IEndpointRouteBuilder endpoints)
    {
        ArgumentNullException.ThrowIfNull(endpoints);

        var group = endpoints.MapGroup(BasePath);
        group.MapPost("/{organizationId}/directives", SubmitDirectiveAsync);
        return endpoints;
    }

    private static async Task<IResult> SubmitDirectiveAsync(
        string organizationId,
        SubmitDirectiveRequest? request,
        IDirectiveSubmissionSink sink,
        CancellationToken cancellationToken)
    {
        if (!TryCreateDirective(
                organizationId,
                request,
                out var directive,
                out var problem))
        {
            return problem!;
        }

        var result = await sink.SubmitAsync(directive!, cancellationToken);
        if (!result.IsAccepted)
        {
            return Rejected(result.Rejection!);
        }

        return TypedResults.Json(
            ToResponse(result.Directive),
            statusCode: StatusCodes.Status202Accepted);
    }

    private static bool TryCreateDirective(
        string organizationId,
        SubmitDirectiveRequest? request,
        out Directive? directive,
        out IResult? problem)
    {
        directive = null;
        problem = null;

        if (request is null)
        {
            problem = Invalid("Request body is required.", "body");
            return false;
        }

        if (!string.IsNullOrWhiteSpace(request.ParentDirectiveId))
        {
            problem = Invalid("Root directives must not include a parent directive.", "parentDirectiveId");
            return false;
        }

        try
        {
            directive = new Directive(
                ParseGuidId(request.MessageId, MessageId.From, "messageId"),
                OrganizationId.From(organizationId),
                ParseEndpoint(request.From, "from"),
                ParseEndpoint(request.To, "to"),
                ParseGuidId(request.ThreadId, ThreadId.From, "threadId"),
                ParsePriority(request.Priority),
                request.SchemaVersion ?? throw Missing("schemaVersion"),
                request.SentAt ?? throw Missing("sentAt"),
                request.Deadline,
                ParseGuidId(request.DirectiveId, DirectiveId.From, "directiveId"),
                parentDirectiveId: null,
                RequiredText(request.Objective, "objective"),
                RequiredText(request.Context, "context"));
            return true;
        }
        catch (ArgumentException exception)
        {
            problem = Invalid(exception.Message, exception.ParamName ?? "$");
            return false;
        }
    }

    private static SubmitDirectiveResponse ToResponse(Directive directive)
    {
        var from = (PositionEndpointRef)directive.From;
        var to = (PositionEndpointRef)directive.To;

        return new SubmitDirectiveResponse(
            "accepted",
            directive.Id.ToString(),
            directive.OrganizationId.ToString(),
            from.PositionId.ToString(),
            to.PositionId.ToString(),
            directive.Thread.ToString(),
            directive.DirectiveId.ToString());
    }

    private static PositionEndpointRef ParseEndpoint(
        DirectiveSubmissionEndpointRefRequest? endpoint,
        string path)
    {
        if (endpoint is null)
        {
            throw Missing(path);
        }

        if (!string.Equals(endpoint.Kind, "position", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Directive submission endpoints must use kind 'position'.",
                $"{path}.kind");
        }

        return new PositionEndpointRef(PositionId.From(RequiredText(endpoint.PositionId, $"{path}.positionId")));
    }

    private static TId ParseGuidId<TId>(
        string? value,
        Func<Guid, TId> factory,
        string path)
    {
        var text = RequiredText(value, path);
        if (!Guid.TryParse(text, out var parsed))
        {
            throw new ArgumentException(
                "Identifier must be a non-empty GUID.",
                path);
        }

        return factory(parsed);
    }

    private static Priority ParsePriority(string? value)
    {
        var text = RequiredText(value, "priority");
        if (PriorityContract.TryParseWireValue(text, out var priority))
        {
            return priority;
        }

        throw new ArgumentException(
            "Priority must be low, normal, high, or critical.",
            "priority");
    }

    private static string RequiredText(string? value, string path)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw Missing(path);
        }

        return value;
    }

    private static ArgumentException Missing(string path) =>
        new("Required field is missing or empty.", path);

    private static IResult Invalid(string detail, string path) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Invalid directive submission",
                Detail = detail,
                Extensions =
                {
                    ["path"] = path,
                },
            });

    private static IResult Rejected(RoutingRejection rejection) =>
        TypedResults.Problem(
            new ProblemDetails
            {
                Status = StatusCodes.Status400BadRequest,
                Title = "Directive submission rejected",
                Detail = "Directive routing was rejected.",
                Extensions =
                {
                    ["errors"] = rejection.PublicResult.Errors
                        .Select(error => new DirectiveSubmissionErrorResponse(
                            error.Code,
                            error.Path,
                            RejectionReasonContract.ToWireValue(error.Reason)))
                        .ToArray(),
                },
            });
}
