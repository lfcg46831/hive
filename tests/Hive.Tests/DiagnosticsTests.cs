using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Diagnostics;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class DiagnosticsTests
{
    [Fact]
    public async Task Diagnostics_report_version_active_roles_and_ready_when_fully_configured()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "API",
            ["Hive:Node:Roles:1"] = "Agents",
            ["ConnectionStrings:PostgreSql"] = "Host=localhost;Database=hive",
        });

        var diagnostics = await host.Services
            .GetRequiredService<NodeDiagnosticsProvider>()
            .GetAsync();

        Assert.False(string.IsNullOrWhiteSpace(diagnostics.Version));
        Assert.Equal(new[] { "agents", "api" }, diagnostics.Roles);
        Assert.True(diagnostics.Live);
        Assert.True(diagnostics.Ready);
    }

    [Fact]
    public async Task Diagnostics_are_live_but_not_ready_when_mandatory_dependency_is_missing()
    {
        using var host = BuildHost(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
        });

        var diagnostics = await host.Services
            .GetRequiredService<NodeDiagnosticsProvider>()
            .GetAsync();

        Assert.True(diagnostics.Live);
        Assert.False(diagnostics.Ready);
    }

    [Fact]
    public void Version_is_resolved_from_an_assembly_without_a_build_metadata_suffix()
    {
        var version = HiveVersion.Resolve(typeof(HiveVersion).Assembly);

        Assert.False(string.IsNullOrWhiteSpace(version));
        Assert.DoesNotContain('+', version);
    }

    [Fact]
    public void Version_falls_back_to_unknown_without_an_assembly()
    {
        Assert.Equal("unknown", HiveVersion.Resolve(null));
    }

    private static IHost BuildHost(IReadOnlyDictionary<string, string?> configuration)
    {
        var builder = new HostApplicationBuilder(new HostApplicationBuilderSettings
        {
            DisableDefaults = true,
        });
        builder.Configuration.AddInMemoryCollection(configuration);
        builder.AddHiveBootstrap();
        return builder.Build();
    }
}
