using Hive.Infrastructure.Configuration;

var builder = Host.CreateApplicationBuilder(args);
builder.AddHiveBootstrap();

var host = builder.Build();
host.Run();
