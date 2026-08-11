using System.Globalization;
using Hive.Api.Authorization;
using Hive.Contracts.Inbox;
using Hive.Contracts.Organization;
using Microsoft.OpenApi.Any;
using Microsoft.OpenApi.Models;
using Swashbuckle.AspNetCore.SwaggerGen;

namespace Hive.Api.OpenApi;

public static class PublicApiOpenApiExtensions
{
    public const string DocumentName = "v1";

    public const string DocumentPath = "/openapi/v1.json";

    private const string RouteTemplate = "openapi/{documentName}.json";

    public static IServiceCollection AddHivePublicApiOpenApi(
        this IServiceCollection services)
    {
        ArgumentNullException.ThrowIfNull(services);

        services.AddEndpointsApiExplorer();
        services.AddSwaggerGen(options =>
        {
            options.SupportNonNullableReferenceTypes();
            options.UseAllOfToExtendReferenceSchemas();
            options.SwaggerDoc(
                DocumentName,
                new OpenApiInfo
                {
                    Title = "HIVE Public API",
                    Version = DocumentName,
                    Description =
                        "Public read-only and command contracts for HIVE clients. " +
                        "The /api/v1 path is compatibility-stable: compatible additions may be made in place, " +
                        "while incompatible changes require a new path version.",
                });
            options.AddSecurityDefinition(
                "Bearer",
                new OpenApiSecurityScheme
                {
                    Type = SecuritySchemeType.Http,
                    Scheme = "bearer",
                    BearerFormat = "opaque",
                    Description =
                        "Static public API credential scoped to one or more organizations.",
                });
            options.DocInclusionPredicate(
                static (documentName, description) =>
                    string.Equals(documentName, DocumentName, StringComparison.Ordinal) &&
                    description.RelativePath?.StartsWith(
                        "api/v1/",
                        StringComparison.Ordinal) == true);
            options.OperationFilter<OrganizationOpenApiOperationFilter>();
            options.SchemaFilter<InboxMessageContentSchemaFilter>();
            options.SchemaFilter<OrganizationPublicContractSchemaFilter>();
        });
        return services;
    }

    public static IApplicationBuilder UseHivePublicApiOpenApi(
        this IApplicationBuilder application)
    {
        ArgumentNullException.ThrowIfNull(application);

        application.UseSwagger(options => options.RouteTemplate = RouteTemplate);
        return application;
    }
}

internal sealed class InboxMessageContentSchemaFilter : ISchemaFilter
{
    private static readonly IReadOnlyDictionary<Type, string> ContentTypes =
        new Dictionary<Type, string>
        {
            [typeof(InboxDirectiveMessageContent)] = nameof(InboxMessageType.Directive),
            [typeof(InboxReportMessageContent)] = nameof(InboxMessageType.Report),
            [typeof(InboxEscalationMessageContent)] = nameof(InboxMessageType.Escalation),
            [typeof(InboxMemoMessageContent)] = nameof(InboxMessageType.Memo),
            [typeof(InboxPeerRequestMessageContent)] = nameof(InboxMessageType.PeerRequest),
            [typeof(InboxPeerResponseMessageContent)] = nameof(InboxMessageType.PeerResponse),
            [typeof(InboxApprovalRequestMessageContent)] = nameof(InboxMessageType.ApprovalRequest),
            [typeof(InboxApprovalDecisionMessageContent)] = nameof(InboxMessageType.ApprovalDecision),
        };

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (context.Type == typeof(InboxReportKind))
        {
            schema.Type = "string";
            schema.Format = null;
            schema.Enum =
            [
                new OpenApiString("progress"),
                new OpenApiString("done"),
            ];
            return;
        }

        if (ContentTypes.TryGetValue(context.Type, out var discriminatorValue))
        {
            schema.Properties["type"] = new OpenApiSchema
            {
                Type = "string",
                Enum = [new OpenApiString(discriminatorValue)],
            };
            schema.Required.Add("type");
            return;
        }

        if (context.Type != typeof(InboxMessageContent))
        {
            return;
        }

        schema.Type = null;
        schema.Properties.Clear();
        schema.Required.Clear();
        var subtypes = ContentTypes
            .Select(entry => new
            {
                entry.Value,
                Schema = context.SchemaGenerator.GenerateSchema(
                    entry.Key,
                    context.SchemaRepository),
            })
            .ToArray();
        schema.OneOf = subtypes.Select(static entry => entry.Schema).ToList();
        schema.Discriminator = new OpenApiDiscriminator
        {
            PropertyName = "type",
            Mapping = subtypes.ToDictionary(
                entry => entry.Value,
                entry => $"#/components/schemas/{entry.Schema.Reference.Id}",
                StringComparer.Ordinal),
        };
    }
}

internal sealed class OrganizationPublicContractSchemaFilter : ISchemaFilter
{
    private static readonly HashSet<Type> CompleteSnapshotTypes =
    [
        typeof(RegistryVersion),
        typeof(OrganizationSummary),
        typeof(OrganizationUnit),
        typeof(OrganizationOccupant),
        typeof(PositionHierarchy),
        typeof(PositionCorrelatedEvent),
        typeof(OrganizationPositionState),
        typeof(OrganizationPosition),
        typeof(OrganogramResponse),
        typeof(PositionDetailResponse),
        typeof(PositionStatesResponse),
        typeof(InboxMessageEndpoint),
        typeof(InboxApprovalMetadata),
        typeof(InboxItem),
        typeof(InboxItemResponse),
        typeof(InboxDirectiveMessageContent),
        typeof(InboxReportMessageContent),
        typeof(InboxEscalationMessageContent),
        typeof(InboxMemoMessageContent),
        typeof(InboxPeerRequestMessageContent),
        typeof(InboxPeerResponseMessageContent),
        typeof(InboxApprovalRequestMessageContent),
        typeof(InboxPage),
        typeof(InboxInteractionResponse),
        typeof(InboxReplyResponse),
        typeof(InboxDecisionResponse),
    ];

    public void Apply(OpenApiSchema schema, SchemaFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(schema);
        ArgumentNullException.ThrowIfNull(context);

        if (!CompleteSnapshotTypes.Contains(context.Type) ||
            !string.Equals(schema.Type, "object", StringComparison.Ordinal))
        {
            return;
        }

        // These response DTOs are serialized as complete snapshots. Nullable
        // properties are present with a JSON null value; they are not optional.
        // Recording every property as required keeps the OpenAPI document aligned
        // with the TypeScript mirror of the actual System.Text.Json wire shape.
        foreach (var propertyName in schema.Properties.Keys)
        {
            schema.Required.Add(propertyName);
        }
    }
}

internal sealed class OrganizationOpenApiOperationFilter : IOperationFilter
{
    private const string OrganizationPathPrefix = "api/v1/organizations/";

    public void Apply(OpenApiOperation operation, OperationFilterContext context)
    {
        ArgumentNullException.ThrowIfNull(operation);
        ArgumentNullException.ThrowIfNull(context);

        var relativePath = context.ApiDescription.RelativePath;
        if (relativePath?.StartsWith(
                OrganizationPathPrefix,
                StringComparison.Ordinal) != true)
        {
            return;
        }

        DescribeRouteParameters(operation);
        DescribeResponses(operation, relativePath);
        DescribeAuthorization(operation);

        // Every endpoint that answers 304 is polled conditionally, so the ETag
        // contract is documented from the declared responses rather than from a
        // list of routes that a new endpoint would silently fall outside of.
        if (operation.Responses.ContainsKey(
                StatusCodes.Status304NotModified.ToString(CultureInfo.InvariantCulture)))
        {
            DescribeConditionalPolling(
                operation,
                relativePath.EndsWith("/position-states", StringComparison.Ordinal)
                    ? "position-state"
                    : "inbox");
        }
    }

    private static void DescribeRouteParameters(OpenApiOperation operation)
    {
        foreach (var parameter in operation.Parameters)
        {
            parameter.Description = parameter.Name switch
            {
                "organizationId" => "Stable organization identifier from the published registry.",
                "unitId" => "Stable unit identifier scoped to the organization.",
                "positionId" => "Stable position identifier scoped to the organization.",
                "itemId" => "Stable inbox item identifier scoped to the authenticated person.",
                _ => parameter.Description,
            };
        }
    }

    private static void DescribeResponses(
        OpenApiOperation operation,
        string relativePath)
    {
        SetResponseDescription(
            operation,
            StatusCodes.Status401Unauthorized,
            "A valid organization bearer token is required. Returns RFC 7807 Problem Details.");
        SetResponseDescription(
            operation,
            StatusCodes.Status400BadRequest,
            "A route identifier is invalid. Returns RFC 7807 Problem Details.");
        SetResponseDescription(
            operation,
            StatusCodes.Status404NotFound,
            relativePath.Contains("/inbox/", StringComparison.Ordinal)
                ? "The organization or inbox item was not found. Returns RFC 7807 Problem Details."
                : relativePath.Contains("/units/", StringComparison.Ordinal)
                    ? "The organization or unit was not found. Returns RFC 7807 Problem Details."
                    : relativePath.Contains("/positions/", StringComparison.Ordinal)
                        ? "The organization or position was not found. Returns RFC 7807 Problem Details."
                        : "The organization was not found. Returns RFC 7807 Problem Details.");
        SetResponseDescription(
            operation,
            StatusCodes.Status503ServiceUnavailable,
            "The materialized organization read model is unavailable. Returns RFC 7807 Problem Details.");
    }

    private static void DescribeAuthorization(OpenApiOperation operation)
    {
        operation.Security.Add(
            new OpenApiSecurityRequirement
            {
                [new OpenApiSecurityScheme
                {
                    Reference = new OpenApiReference
                    {
                        Type = ReferenceType.SecurityScheme,
                        Id = "Bearer",
                    },
                }] = Array.Empty<string>(),
            });
    }

    private static void DescribeConditionalPolling(
        OpenApiOperation operation,
        string snapshotLabel)
    {
        operation.Parameters.Add(
            new OpenApiParameter
            {
                Name = "If-None-Match",
                In = ParameterLocation.Header,
                Required = false,
                Description = $"Weak ETag returned by an earlier {snapshotLabel} snapshot.",
                Schema = new OpenApiSchema { Type = "string" },
            });

        AddEntityTagHeader(operation, StatusCodes.Status200OK, snapshotLabel);
        AddEntityTagHeader(operation, StatusCodes.Status304NotModified, snapshotLabel);
        SetResponseDescription(
            operation,
            StatusCodes.Status304NotModified,
            $"The {snapshotLabel} snapshot still matches If-None-Match; the response has no body.");
    }

    private static void AddEntityTagHeader(
        OpenApiOperation operation,
        int statusCode,
        string snapshotLabel)
    {
        var response = operation.Responses[statusCode.ToString(CultureInfo.InvariantCulture)];
        response.Headers["ETag"] = new OpenApiHeader
        {
            Description = $"Weak validator for the complete {snapshotLabel} snapshot.",
            Schema = new OpenApiSchema { Type = "string" },
        };
    }

    private static void SetResponseDescription(
        OpenApiOperation operation,
        int statusCode,
        string description)
    {
        var key = statusCode.ToString(CultureInfo.InvariantCulture);
        if (operation.Responses.TryGetValue(key, out var response))
        {
            response.Description = description;
        }
    }
}
