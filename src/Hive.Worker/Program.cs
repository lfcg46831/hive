using Hive.Actors;
using Hive.Infrastructure.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.AddHiveBootstrap();
builder.AddHiveActorSystem();

var host = builder.Build();
host.Run();
