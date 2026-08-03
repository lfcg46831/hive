using Hive.Api.Authorization;
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

        if (relativePath.EndsWith(
                "/position-states",
                StringComparison.Ordinal))
        {
            DescribeConditionalPolling(operation);
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
            relativePath.Contains("/units/", StringComparison.Ordinal)
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

    private static void DescribeConditionalPolling(OpenApiOperation operation)
    {
        operation.Parameters.Add(
            new OpenApiParameter
            {
                Name = "If-None-Match",
                In = ParameterLocation.Header,
                Required = false,
                Description = "Weak ETag returned by an earlier position-state snapshot.",
                Schema = new OpenApiSchema { Type = "string" },
            });

        AddEntityTagHeader(operation, StatusCodes.Status200OK);
        AddEntityTagHeader(operation, StatusCodes.Status304NotModified);
        SetResponseDescription(
            operation,
            StatusCodes.Status304NotModified,
            "The position-state snapshot still matches If-None-Match; the response has no body.");
    }

    private static void AddEntityTagHeader(
        OpenApiOperation operation,
        int statusCode)
    {
        var response = operation.Responses[statusCode.ToString(
            System.Globalization.CultureInfo.InvariantCulture)];
        response.Headers["ETag"] = new OpenApiHeader
        {
            Description = "Weak validator for the complete position-state snapshot.",
            Schema = new OpenApiSchema { Type = "string" },
        };
    }

    private static void SetResponseDescription(
        OpenApiOperation operation,
        int statusCode,
        string description)
    {
        var key = statusCode.ToString(System.Globalization.CultureInfo.InvariantCulture);
        if (operation.Responses.TryGetValue(key, out var response))
        {
            response.Description = description;
        }
    }
}
