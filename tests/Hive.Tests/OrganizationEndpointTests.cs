using System.Net;
using System.Net.Http.Headers;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Api.Authorization;
using Hive.Api.Organization;
using Hive.Contracts.Organization;
using Hive.Domain.Identity;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.Http;
using Microsoft.AspNetCore.Mvc;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class OrganizationEndpointTests
{
    private static readonly DateTimeOffset GeneratedAt =
        new(2026, 8, 2, 20, 30, 0, TimeSpan.Zero);

    private static readonly DateTimeOffset EventAt = GeneratedAt.AddMinutes(-2);

    [Fact]
    public async Task Public_queries_expose_the_four_read_only_organization_resources()
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        var organogram = await client.GetFromJsonAsync<JsonElement>(
            $"{OrganizationEndpointExtensions.BasePath}/acme/organogram");
        var subtree = await client.GetFromJsonAsync<JsonElement>(
            $"{OrganizationEndpointExtensions.BasePath}/acme/units/engineering/organogram");
        var position = await client.GetFromJsonAsync<JsonElement>(
            $"{OrganizationEndpointExtensions.BasePath}/acme/positions/engineer");
        var states = await client.GetFromJsonAsync<JsonElement>(
            $"{OrganizationEndpointExtensions.BasePath}/acme/position-states");

        Assert.Equal("delivery", organogram.GetProperty("root_unit_id").GetString());
        Assert.Equal("engineering", subtree.GetProperty("root_unit_id").GetString());
        Assert.Equal(
            "engineer",
            position.GetProperty("position").GetProperty("id").GetString());
        Assert.Equal(
            ["delivery-lead", "engineer"],
            states.GetProperty("states")
                .EnumerateArray()
                .Select(item => item.GetProperty("position_id").GetString()));
        Assert.Equal(
            [("acme", (string?)null), ("acme", "engineering")],
            readModel.OrganogramRequests);
        Assert.Equal([("acme", "engineer")], readModel.PositionRequests);
        Assert.Equal(["acme"], readModel.PositionStateRequests);
    }

    [Theory]
    [InlineData("/organogram")]
    [InlineData("/units/delivery/organogram")]
    [InlineData("/positions/delivery-lead")]
    [InlineData("/position-states")]
    public async Task All_queries_reject_invalid_organization_identifiers(string suffix)
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/%20acme{suffix}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Invalid organization identifier", problem!.Title);
        Assert.Equal(StatusCodes.Status400BadRequest, problem.Status);
        Assert.Empty(readModel.OrganogramRequests);
        Assert.Empty(readModel.PositionRequests);
        Assert.Empty(readModel.PositionStateRequests);
    }

    [Theory]
    [InlineData("/units/%20delivery/organogram", "Invalid unit identifier")]
    [InlineData("/positions/%20delivery-lead", "Invalid position identifier")]
    public async Task Resource_queries_reject_invalid_child_identifiers(
        string suffix,
        string expectedTitle)
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/acme{suffix}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal(expectedTitle, problem!.Title);
    }

    [Theory]
    [InlineData("/organogram", "Organization not found")]
    [InlineData("/units/delivery/organogram", "Unit not found")]
    [InlineData("/positions/delivery-lead", "Position not found")]
    [InlineData("/position-states", "Organization not found")]
    public async Task Missing_resources_return_problem_details(
        string suffix,
        string expectedTitle)
    {
        var readModel = RecordingReadModel.Missing();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/acme{suffix}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal(expectedTitle, problem!.Title);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
    }

    [Theory]
    [InlineData("/organogram")]
    [InlineData("/units/delivery/organogram")]
    [InlineData("/positions/delivery-lead")]
    [InlineData("/position-states")]
    public async Task Default_read_model_reports_unavailable_until_materializations_are_registered(
        string suffix)
    {
        await using var app = BuildApp(readModel: null);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/acme{suffix}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.ServiceUnavailable, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Organization read model unavailable", problem!.Title);
        Assert.Equal(StatusCodes.Status503ServiceUnavailable, problem.Status);
    }

    [Fact]
    public async Task Position_state_polling_uses_a_weak_version_etag_and_honors_if_none_match()
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);
        var path = $"{OrganizationEndpointExtensions.BasePath}/acme/position-states";

        using var first = await client.GetAsync(path);
        var entityTag = Assert.Single(first.Headers.GetValues("ETag"));
        using var conditionalRequest = new HttpRequestMessage(HttpMethod.Get, path);
        conditionalRequest.Headers.TryAddWithoutValidation("If-None-Match", entityTag);
        using var unchanged = await client.SendAsync(conditionalRequest);

        Assert.Equal(HttpStatusCode.OK, first.StatusCode);
        Assert.StartsWith("W/\"", entityTag, StringComparison.Ordinal);
        Assert.EndsWith("\"", entityTag, StringComparison.Ordinal);
        Assert.Equal(HttpStatusCode.NotModified, unchanged.StatusCode);
        Assert.Empty(await unchanged.Content.ReadAsByteArrayAsync());
        Assert.Equal(entityTag, Assert.Single(unchanged.Headers.GetValues("ETag")));
    }

    [Fact]
    public async Task Position_state_etag_changes_when_a_position_sequence_advances()
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);
        var path = $"{OrganizationEndpointExtensions.BasePath}/acme/position-states";

        using var first = await client.GetAsync(path);
        var firstEntityTag = Assert.Single(first.Headers.GetValues("ETag"));
        readModel.PositionStates = OrganizationReadResult<PositionStatesResponse>.Available(
            CreatePositionStates(deliveryLeadSequence: 13));
        using var changedRequest = new HttpRequestMessage(HttpMethod.Get, path);
        changedRequest.Headers.TryAddWithoutValidation("If-None-Match", firstEntityTag);
        using var changed = await client.SendAsync(changedRequest);

        Assert.Equal(HttpStatusCode.OK, changed.StatusCode);
        Assert.NotEqual(firstEntityTag, Assert.Single(changed.Headers.GetValues("ETag")));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("not-a-configured-token")]
    public async Task Public_queries_require_a_valid_bearer_token(string? token)
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = app.GetTestClient();
        if (token is not null)
        {
            client.DefaultRequestHeaders.Authorization =
                new AuthenticationHeaderValue("Bearer", token);
        }

        using var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/acme/organogram");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Equal("Bearer", Assert.Single(response.Headers.WwwAuthenticate).Scheme);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Bearer token required", problem!.Title);
        Assert.Equal(StatusCodes.Status401Unauthorized, problem.Status);
        Assert.Empty(readModel.OrganogramRequests);
    }

    [Fact]
    public async Task Public_rest_queries_do_not_accept_signalr_query_string_tokens()
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = app.GetTestClient();

        using var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/acme/organogram" +
            $"?access_token={OrganizationToken}");

        Assert.Equal(HttpStatusCode.Unauthorized, response.StatusCode);
        Assert.Empty(readModel.OrganogramRequests);
    }

    [Theory]
    [InlineData("/organogram")]
    [InlineData("/units/delivery/organogram")]
    [InlineData("/positions/delivery-lead")]
    [InlineData("/position-states")]
    public async Task Public_queries_hide_organizations_outside_the_principal_scope(
        string suffix)
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app, OtherOrganizationToken);

        using var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/acme{suffix}");
        var problem = await response.Content.ReadFromJsonAsync<ProblemDetails>();

        Assert.Equal(HttpStatusCode.NotFound, response.StatusCode);
        Assert.Equal("application/problem+json", response.Content.Headers.ContentType?.MediaType);
        Assert.Equal("Organization not found", problem!.Title);
        Assert.Equal(StatusCodes.Status404NotFound, problem.Status);
        Assert.Empty(readModel.OrganogramRequests);
        Assert.Empty(readModel.PositionRequests);
        Assert.Empty(readModel.PositionStateRequests);
    }

    [Fact]
    public async Task One_bearer_principal_can_read_every_configured_organization()
    {
        var readModel = RecordingReadModel.Available();
        await using var app = BuildApp(readModel);
        await app.StartAsync();
        using var client = CreateAuthorizedClient(app);

        using var response = await client.GetAsync(
            $"{OrganizationEndpointExtensions.BasePath}/umbrella/organogram");

        Assert.Equal(HttpStatusCode.OK, response.StatusCode);
        Assert.Equal([("umbrella", (string?)null)], readModel.OrganogramRequests);
    }

    private static WebApplication BuildApp(IOrganizationReadModel? readModel)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.AddInMemoryCollection(
            new Dictionary<string, string?>
            {
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:Token"] =
                    OrganizationToken,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:0"] =
                    "acme",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:0:OrganizationIds:1"] =
                    "umbrella",
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:Token"] =
                    OtherOrganizationToken,
                [$"{OrganizationAuthorizationOptions.SectionName}:Credentials:1:OrganizationIds:0"] =
                    "globex",
            });
        if (readModel is not null)
        {
            builder.Services.AddSingleton(readModel);
        }

        builder.Services.AddHiveOrganizationApi();
        var app = builder.Build();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveOrganizationApi();
        return app;
    }

    private static HttpClient CreateAuthorizedClient(
        WebApplication app,
        string token = OrganizationToken)
    {
        var client = app.GetTestClient();
        client.DefaultRequestHeaders.Authorization =
            new AuthenticationHeaderValue("Bearer", token);
        return client;
    }

    private static OrganogramResponse CreateOrganogram(string rootUnitId) => new(
        Registry,
        GeneratedAt,
        rootUnitId,
        Organization,
        rootUnitId == "delivery"
            ?
            [
                new OrganizationUnit("engineering", "Engineering", "delivery", "engineer"),
                new OrganizationUnit("delivery", "Delivery", null, "delivery-lead"),
            ]
            : [new OrganizationUnit("engineering", "Engineering", "delivery", "engineer")],
        rootUnitId == "delivery"
            ? CreatePositions()
            : [CreatePositions()[1]]);

    private static PositionDetailResponse CreatePositionDetail() =>
        new(Registry, GeneratedAt, CreatePositions()[1]);

    private static PositionStatesResponse CreatePositionStates(long deliveryLeadSequence = 12) =>
        new(
            Registry,
            GeneratedAt,
            EventAt,
            [
                new OrganizationPositionState(
                    "engineer",
                    PositionOperationalState.Idle,
                    2,
                    GeneratedAt),
                new OrganizationPositionState(
                    "delivery-lead",
                    PositionOperationalState.Working,
                    deliveryLeadSequence,
                    EventAt,
                    new PositionCorrelatedEvent(
                        "DirectiveReceived",
                        Guid.Parse("80e3feec-ea3b-4de8-8f59-52932f548b01"),
                        EventAt)),
            ]);

    private static OrganizationPosition[] CreatePositions() =>
    [
        new OrganizationPosition(
            "delivery-lead",
            "Delivery Lead",
            "delivery",
            new OrganizationOccupant(
                "configured-ai:acme/delivery-lead",
                OrganizationOccupantType.AiAgent),
            new PositionHierarchy(null, ["engineer"]),
            new OrganizationPositionState(
                "delivery-lead",
                PositionOperationalState.Working,
                12,
                EventAt)),
        new OrganizationPosition(
            "engineer",
            "Engineer",
            "engineering",
            new OrganizationOccupant(null, OrganizationOccupantType.Human),
            new PositionHierarchy("delivery-lead", []),
            new OrganizationPositionState(
                "engineer",
                PositionOperationalState.Idle,
                2,
                GeneratedAt)),
    ];

    private static OrganizationSummary Organization { get; } =
        new("acme", "Acme Delivery", "delivery", "delivery-lead");

    private static RegistryVersion Registry { get; } = new(7, Fingerprint);

    private const string Fingerprint =
        "sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef";

    private const string OrganizationToken = "test-token-for-acme";

    private const string OtherOrganizationToken = "test-token-for-globex";

    private sealed class RecordingReadModel : IOrganizationReadModel
    {
        private RecordingReadModel(bool available)
        {
            FullOrganogram = available
                ? OrganizationReadResult<OrganogramResponse>.Available(
                    CreateOrganogram("delivery"))
                : OrganizationReadResult<OrganogramResponse>.Unavailable;
            UnitOrganogram = available
                ? OrganizationReadResult<OrganogramResponse>.Available(
                    CreateOrganogram("engineering"))
                : OrganizationReadResult<OrganogramResponse>.Unavailable;
            Position = available
                ? OrganizationReadResult<PositionDetailResponse>.Available(CreatePositionDetail())
                : OrganizationReadResult<PositionDetailResponse>.Unavailable;
            PositionStates = available
                ? OrganizationReadResult<PositionStatesResponse>.Available(CreatePositionStates())
                : OrganizationReadResult<PositionStatesResponse>.Unavailable;
        }

        public List<(string OrganizationId, string? UnitId)> OrganogramRequests { get; } = [];

        public List<(string OrganizationId, string PositionId)> PositionRequests { get; } = [];

        public List<string> PositionStateRequests { get; } = [];

        public OrganizationReadResult<OrganogramResponse> FullOrganogram { get; set; }

        public OrganizationReadResult<OrganogramResponse> UnitOrganogram { get; set; }

        public OrganizationReadResult<PositionDetailResponse> Position { get; set; }

        public OrganizationReadResult<PositionStatesResponse> PositionStates { get; set; }

        public static RecordingReadModel Available() => new(available: true);

        public static RecordingReadModel Missing()
        {
            var readModel = new RecordingReadModel(available: true)
            {
                FullOrganogram = OrganizationReadResult<OrganogramResponse>.Available(null),
                UnitOrganogram = OrganizationReadResult<OrganogramResponse>.Available(null),
                Position = OrganizationReadResult<PositionDetailResponse>.Available(null),
                PositionStates = OrganizationReadResult<PositionStatesResponse>.Available(null),
            };
            return readModel;
        }

        public ValueTask<OrganizationReadResult<OrganogramResponse>> ReadOrganogramAsync(
            OrganizationId organizationId,
            UnitId? rootUnitId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            OrganogramRequests.Add((organizationId.Value, rootUnitId?.Value));
            return ValueTask.FromResult(
                rootUnitId is null ? FullOrganogram : UnitOrganogram);
        }

        public ValueTask<OrganizationReadResult<PositionDetailResponse>> ReadPositionAsync(
            OrganizationId organizationId,
            PositionId positionId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PositionRequests.Add((organizationId.Value, positionId.Value));
            return ValueTask.FromResult(Position);
        }

        public ValueTask<OrganizationReadResult<PositionStatesResponse>> ReadPositionStatesAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken)
        {
            cancellationToken.ThrowIfCancellationRequested();
            PositionStateRequests.Add(organizationId.Value);
            return ValueTask.FromResult(PositionStates);
        }
    }
}
