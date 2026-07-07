using Hive.Actors;
using Hive.Api.Diagnostics;
using Hive.Api.Directives;
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
        builder.Services.AddHiveDirectiveSubmissionApi();
        builder.Services.AddHiveOrganizationRegistryApi();

        var app = builder.Build();
        app.MapHiveDiagnostics();
        app.MapHiveDirectiveSubmissionApi();
        app.MapHiveOrganizationRegistryApi();
        return app;
    }
}
