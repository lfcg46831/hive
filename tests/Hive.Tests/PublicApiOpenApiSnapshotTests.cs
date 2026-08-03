using System.Text.Json;
using Hive.Api.OpenApi;
using Hive.Api.Organization;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

/// <summary>
/// Exports the published public API document to <c>console/openapi/v1.json</c>.
/// The TypeScript console client is verified against that export, so the document
/// consumed by the parity check always comes from the current backend and is never
/// a hand-maintained copy.
/// </summary>
public sealed class PublicApiOpenApiSnapshotTests
{
    [Fact]
    public async Task Public_document_is_exported_for_the_typescript_client()
    {
        var document = await ExportDocumentAsync();
        var snapshotPath = SnapshotPath;

        Directory.CreateDirectory(Path.GetDirectoryName(snapshotPath)!);
        await File.WriteAllTextAsync(snapshotPath, document);

        using var parsed = JsonDocument.Parse(document);
        var paths = parsed.RootElement
            .GetProperty("paths")
            .EnumerateObject()
            .Select(path => path.Name)
            .ToArray();

        Assert.NotEmpty(paths);
        Assert.DoesNotContain(
            paths,
            path => !path.StartsWith("/api/v1/", StringComparison.Ordinal));
        Assert.True(
            parsed.RootElement
                .GetProperty("components")
                .GetProperty("schemas")
                .TryGetProperty("OrganogramResponse", out _));
    }

    private static async Task<string> ExportDocumentAsync()
    {
        var app = BuildApp();
        try
        {
            await app.StartAsync();
            using var client = app.GetTestClient();
            using var response = await client.GetAsync(PublicApiOpenApiExtensions.DocumentPath);
            response.EnsureSuccessStatusCode();
            return await response.Content.ReadAsStringAsync();
        }
        finally
        {
            await app.DisposeAsync();
        }
    }

    private static WebApplication BuildApp()
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddHivePublicApiOpenApi();
        builder.Services.AddHiveOrganizationApi();
        builder.Services.AddHiveOrganizationRegistryApi();

        var app = builder.Build();
        app.UseHivePublicApiOpenApi();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveOrganizationApi();
        app.MapHiveOrganizationRegistryApi();
        return app;
    }

    private static string SnapshotPath => Path.Combine(
        RepositoryRoot,
        "console",
        "openapi",
        "v1.json");

    private static string RepositoryRoot => FindRepositoryRoot();

    private static string FindRepositoryRoot()
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
