using Hive.Actors;
using Hive.Api.Diagnostics;
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

        var app = builder.Build();
        app.MapHiveDiagnostics();
        return app;
    }
}
