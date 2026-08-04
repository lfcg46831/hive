using Hive.Actors;
using Hive.Api.Auditing;
using Hive.Api.Diagnostics;
using Hive.Api.Directives;
using Hive.Api.Inbox;
using Hive.Api.OpenApi;
using Hive.Api.Organization;
using Hive.Infrastructure.Configuration;

namespace Hive.Api;

public static class Program
{
    public static void Main(string[] args) => Build(args).Run();

    public static WebApplication Build(string[] args)
    {
        var builder = WebApplication.CreateBuilder(args);
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        builder.AddHivePositionLiveStateProjection();
        builder.Services.AddHivePublicApiOpenApi();
        builder.Services.AddHiveDirectiveAuditExportApi();
        builder.Services.AddHiveDirectiveSubmissionApi();
        builder.Services.AddHiveInboxApi();
        builder.Services.AddHiveOrganizationApi();
        builder.Services.AddHiveOrganizationRegistryApi();

        var app = builder.Build();
        app.UseHivePublicApiOpenApi();
        app.UseAuthentication();
        app.UseAuthorization();
        app.MapHiveDiagnostics();
        app.MapHiveDirectiveAuditExportApi();
        app.MapHiveDirectiveSubmissionApi();
        app.MapHiveInboxApi();
        app.MapHiveOrganizationApi();
        app.MapHiveOrganizationUpdatesHub();
        app.MapHiveOrganizationRegistryApi();
        return app;
    }
}
