using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Directives;
using Hive.Domain.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class DirectiveSubmissionEndpointTests
{
    [Fact]
    public async Task Submit_root_directive_accepts_canonical_payload_without_case_specific_surface()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        await using var app = BuildApp(sink);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            new
            {
                messageId = "aaaaaaaa-0000-0000-0000-000000000010",
                from = new { kind = "position", positionId = "ceo" },
                to = new { kind = "position", positionId = "delivery-lead" },
                threadId = "aaaaaaaa-0000-0000-0000-000000000011",
                priority = "high",
                schemaVersion = 1,
                sentAt = "2026-07-07T10:15:30Z",
                directiveId = "aaaaaaaa-0000-0000-0000-000000000012",
                objective = "Review the incoming production request and report the conclusion.",
                context = "Free-form producer context. Completion criteria: severity, missing information, and next action.",
            });

        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("accepted", json.GetProperty("status").GetString());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000010", json.GetProperty("messageId").GetString());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000012", json.GetProperty("directiveId").GetString());
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000011", json.GetProperty("threadId").GetString());
        Assert.Equal("delivery-lead", json.GetProperty("toPositionId").GetString());

        var directive = Assert.IsType<Directive>(sink.Submitted);
        Assert.Equal("acme-delivery", directive.OrganizationId.Value);
        Assert.Equal("ceo", Assert.IsType<PositionEndpointRef>(directive.From).PositionId.Value);
        Assert.Equal("delivery-lead", Assert.IsType<PositionEndpointRef>(directive.To).PositionId.Value);
        Assert.Equal(Priority.High, directive.Priority);
        Assert.Equal(1, directive.SchemaVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T10:15:30Z"), directive.SentAt);
        Assert.Null(directive.ParentDirectiveId);
        Assert.Equal("Review the incoming production request and report the conclusion.", directive.Objective);
        Assert.Contains("Completion criteria", directive.Context, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Submit_root_directive_rejects_parent_directive_id()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        await using var app = BuildApp(sink);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            new
            {
                messageId = "aaaaaaaa-0000-0000-0000-000000000020",
                from = new { kind = "position", positionId = "ceo" },
                to = new { kind = "position", positionId = "delivery-lead" },
                threadId = "aaaaaaaa-0000-0000-0000-000000000021",
                priority = "normal",
                schemaVersion = 1,
                sentAt = "2026-07-07T10:15:30Z",
                directiveId = "aaaaaaaa-0000-0000-0000-000000000022",
                parentDirectiveId = "aaaaaaaa-0000-0000-0000-000000000023",
                objective = "Review the incoming production request.",
                context = "Completion criteria: decide next action.",
            });
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid directive submission", problem!.Title);
        Assert.Equal("parentDirectiveId", Assert.IsType<JsonElement>(problem.Extensions["path"]).GetString());
        Assert.Null(sink.Submitted);
    }

    [Fact]
    public async Task Submit_root_directive_rejects_non_position_endpoint_variants()
    {
        var sink = new CapturingDirectiveSubmissionSink();
        await using var app = BuildApp(sink);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            new
            {
                messageId = "aaaaaaaa-0000-0000-0000-000000000030",
                from = new { kind = "organization-owner" },
                to = new { kind = "position", positionId = "delivery-lead" },
                threadId = "aaaaaaaa-0000-0000-0000-000000000031",
                priority = "normal",
                schemaVersion = 1,
                sentAt = "2026-07-07T10:15:30Z",
                directiveId = "aaaaaaaa-0000-0000-0000-000000000032",
                objective = "Review the incoming production request.",
                context = "Completion criteria: decide next action.",
            });
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid directive submission", problem!.Title);
        Assert.Equal("from.kind", Assert.IsType<JsonElement>(problem.Extensions["path"]).GetString());
        Assert.Null(sink.Submitted);
    }

    private static WebApplication BuildApp(IDirectiveSubmissionSink sink)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(sink);

        var app = builder.Build();
        app.MapHiveDirectiveSubmissionApi();
        return app;
    }

    private sealed class CapturingDirectiveSubmissionSink : IDirectiveSubmissionSink
    {
        public OrgMessage? Submitted { get; private set; }

        public ValueTask<DirectiveSubmissionResult> SubmitAsync(
            Directive directive,
            CancellationToken cancellationToken)
        {
            Submitted = directive;
            return ValueTask.FromResult(DirectiveSubmissionResult.Accepted(directive));
        }
    }
}
