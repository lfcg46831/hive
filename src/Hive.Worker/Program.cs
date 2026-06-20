using Hive.Actors;
using Hive.Infrastructure.Configuration;

namespace Hive.Worker;

public static class Program
{
    public static void Main(string[] args) => Build(args).Run();

    public static IHost Build(string[] args)
    {
        var builder = Host.CreateApplicationBuilder(args);
        builder.AddHiveBootstrap();
        builder.AddHiveActorSystem();
        return builder.Build();
    }
}
