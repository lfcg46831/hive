using System.Net;
using System.Net.Http.Json;
using Hive.Api.Diagnostics;
using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Diagnostics;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class DiagnosticsEndpointTests
{
    [Fact]
    public async Task Diagnostics_endpoint_returns_version_roles_and_startup_state()
    {
        await using var app = BuildApp(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
            ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=hive",
        });
        await app.StartAsync();
        using var client = app.GetTestClient();

        var diagnostics = await client.GetFromJsonAsync<NodeDiagnostics>(
            DiagnosticsEndpointExtensions.DiagnosticsPath);

        Assert.NotNull(diagnostics);
        Assert.False(string.IsNullOrWhiteSpace(diagnostics!.Version));
        Assert.Equal(new[] { "api" }, diagnostics.Roles);
        Assert.True(diagnostics.Live);
        Assert.True(diagnostics.Ready);
    }

    [Fact]
    public async Task Liveness_is_ok_and_readiness_is_unavailable_until_dependencies_are_configured()
    {
        await using var app = BuildApp(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
        });
        await app.StartAsync();
        using var client = app.GetTestClient();

        var live = await client.GetAsync(DiagnosticsEndpointExtensions.LivePath);
        var ready = await client.GetAsync(DiagnosticsEndpointExtensions.ReadyPath);

        Assert.Equal(HttpStatusCode.OK, live.StatusCode);
        Assert.Equal(HttpStatusCode.ServiceUnavailable, ready.StatusCode);
    }

    [Fact]
    public async Task Readiness_is_ok_once_dependencies_are_configured()
    {
        await using var app = BuildApp(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
            ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=hive",
        });
        await app.StartAsync();
        using var client = app.GetTestClient();

        var ready = await client.GetAsync(DiagnosticsEndpointExtensions.ReadyPath);

        Assert.Equal(HttpStatusCode.OK, ready.StatusCode);
    }

    private static WebApplication BuildApp(IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Configuration.Sources.Clear();
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();

        var app = builder.Build();
        app.MapHiveDiagnostics();
        return app;
    }
}
