using Hive.Actors;
using Hive.Api.Diagnostics;
using Hive.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddHiveBootstrap();
builder.AddHiveActorSystem();

var app = builder.Build();

app.MapHiveDiagnostics();

app.Run();
