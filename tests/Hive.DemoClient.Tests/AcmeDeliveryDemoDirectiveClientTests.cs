using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Directives;
using Hive.DemoClient;
using Hive.Domain.Messaging;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.DemoClient.Tests;

public sealed class AcmeDeliveryDemoDirectiveClientTests
{
    [Fact]
    public async Task Builds_canonical_root_directive_submission_for_the_example_triage_flow()
    {
        var context = File.ReadAllText(ContextFile);
        var ids = new DemoDirectiveIds(
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000310"),
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000311"),
            Guid.Parse("aaaaaaaa-0000-0000-0000-000000000312"));

        var submission = AcmeDeliveryDemoDirectiveClient.CreateTriageDirective(
            ids,
            DateTimeOffset.Parse("2026-07-07T10:15:30Z"),
            context);

        Assert.Equal("acme-delivery", submission.OrganizationId);
        Assert.Equal(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme-delivery/directives",
            submission.RelativePath);

        var request = submission.Request;
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000310", request.MessageId);
        Assert.Equal("position", request.From.Kind);
        Assert.Equal("delivery-lead", request.From.PositionId);
        Assert.Equal("position", request.To.Kind);
        Assert.Equal("bug-triage", request.To.PositionId);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000311", request.ThreadId);
        Assert.Equal("aaaaaaaa-0000-0000-0000-000000000312", request.DirectiveId);
        Assert.Null(request.ParentDirectiveId);
        Assert.Equal("high", request.Priority);
        Assert.Equal(1, request.SchemaVersion);
        Assert.Equal(DateTimeOffset.Parse("2026-07-07T10:15:30Z"), request.SentAt);
        Assert.Null(request.Deadline);
        Assert.Contains("triage", request.Objective, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("Checkout returns HTTP 500 after payment confirmation", request.Context);
        Assert.Contains("Completion criteria:", request.Context, StringComparison.Ordinal);
        Assert.Contains("reported_severity", request.Context, StringComparison.Ordinal);

        var sink = new CapturingDirectiveSubmissionSink();
        await using var app = BuildApp(sink);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(submission.RelativePath, request);
        var json = await response.Content.ReadFromJsonAsync<JsonElement>();

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        Assert.Equal("accepted", json.GetProperty("status").GetString());

        var directive = Assert.IsType<Directive>(sink.Submitted);
        Assert.Equal("acme-delivery", directive.OrganizationId.Value);
        Assert.Equal("delivery-lead", Assert.IsType<PositionEndpointRef>(directive.From).PositionId.Value);
        Assert.Equal("bug-triage", Assert.IsType<PositionEndpointRef>(directive.To).PositionId.Value);
        Assert.Equal(Priority.High, directive.Priority);
        Assert.Equal(ids.ThreadId, directive.Thread.Value);
        Assert.Equal(ids.DirectiveId, directive.DirectiveId.Value);
        Assert.Null(directive.ParentDirectiveId);
        Assert.Contains("Completion criteria:", directive.Context, StringComparison.Ordinal);
    }

    [Fact]
    public void Demo_client_keeps_bug_semantics_out_of_compiled_contract_types()
    {
        var publicTypeNames = typeof(AcmeDeliveryDemoDirectiveClient)
            .Assembly
            .GetExportedTypes()
            .Select(type => type.Name);

        Assert.DoesNotContain(
            publicTypeNames,
            name => name.Contains("Bug", StringComparison.OrdinalIgnoreCase));
    }

    [Fact]
    public void Seeded_ids_are_stable_distinct_and_seed_dependent()
    {
        var first = DemoDirectiveIds.FromSeed("us-f0-10-t12-demo");
        var second = DemoDirectiveIds.FromSeed("us-f0-10-t12-demo");
        var other = DemoDirectiveIds.FromSeed("another-demo-run");

        Assert.Equal(first, second);
        Assert.NotEqual(Guid.Empty, first.MessageId);
        Assert.NotEqual(Guid.Empty, first.ThreadId);
        Assert.NotEqual(Guid.Empty, first.DirectiveId);
        Assert.NotEqual(first.MessageId, first.ThreadId);
        Assert.NotEqual(first.MessageId, first.DirectiveId);
        Assert.NotEqual(first.ThreadId, first.DirectiveId);
        Assert.NotEqual(first.MessageId, other.MessageId);
        Assert.NotEqual(first.ThreadId, other.ThreadId);
        Assert.NotEqual(first.DirectiveId, other.DirectiveId);
    }

    [Fact]
    public async Task Demo_command_submits_reproducible_directive_to_the_configured_api()
    {
        var handler = new CapturingHttpMessageHandler(new HttpResponseMessage(HttpStatusCode.Accepted)
        {
            Content = JsonContent.Create(new { status = "accepted" }),
        });
        using var client = new HttpClient(handler);
        using var output = new StringWriter();

        var exitCode = await DemoDirectiveCommand.RunAsync(
            [
                "--repository-root",
                RepositoryRoot,
                "--base-url",
                "http://localhost:8080",
                "--submit",
                "--seed",
                "us-f0-10-t12-demo",
            ],
            client,
            output,
            CancellationToken.None);

        Assert.Equal(0, exitCode);
        Assert.NotNull(handler.Request);
        Assert.Equal(HttpMethod.Post, handler.Request.Method);
        Assert.Equal(
            "http://localhost:8080/api/v1/organizations/acme-delivery/directives",
            handler.Request.RequestUri!.ToString());

        var body = handler.ReadRequestBody<DemoDirectiveRequest>();
        var ids = DemoDirectiveIds.FromSeed("us-f0-10-t12-demo");
        Assert.Equal(ids.MessageId.ToString("D"), body.MessageId);
        Assert.Equal(ids.ThreadId.ToString("D"), body.ThreadId);
        Assert.Equal(ids.DirectiveId.ToString("D"), body.DirectiveId);
        Assert.Equal(DemoDirectiveCommand.DefaultSentAt, body.SentAt);
        Assert.Contains("\"status\": \"accepted\"", output.ToString(), StringComparison.Ordinal);
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

    private static string ContextFile => Path.Combine(
        RepositoryRoot,
        "config",
        "organizations",
        "acme-delivery",
        "examples",
        "bug-triage-directive-context.md");

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);

            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
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

    private sealed class CapturingHttpMessageHandler : HttpMessageHandler
    {
        private readonly HttpResponseMessage _response;
        private string? _requestBody;

        public CapturingHttpMessageHandler(HttpResponseMessage response)
        {
            _response = response;
        }

        public HttpRequestMessage? Request { get; private set; }

        public T ReadRequestBody<T>()
        {
            Assert.NotNull(_requestBody);
            var value = JsonSerializer.Deserialize<T>(
                _requestBody,
                new JsonSerializerOptions(JsonSerializerDefaults.Web));
            Assert.NotNull(value);
            return value;
        }

        protected override async Task<HttpResponseMessage> SendAsync(
            HttpRequestMessage request,
            CancellationToken cancellationToken)
        {
            Request = request;
            _requestBody = request.Content is null
                ? string.Empty
                : await request.Content.ReadAsStringAsync(cancellationToken);

            return _response;
        }
    }
}
