using Hive.Infrastructure.Configuration;

var builder = WebApplication.CreateBuilder(args);
builder.AddHiveBootstrap();

var app = builder.Build();

app.Run();
