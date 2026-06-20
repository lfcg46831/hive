using Hive.Infrastructure.Configuration;
using Hive.Infrastructure.Logging;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;
using Microsoft.Extensions.Logging.Console;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

public sealed class StructuredLoggingTests
{
    [Fact]
    public void Structured_logging_clears_existing_providers_and_registers_only_json_console()
    {
        var builder = NewBuilder();
        builder.Logging.AddProvider(new StubLoggerProvider());

        builder.AddHiveStructuredLogging();
        using var host = builder.Build();

        var providers = host.Services.GetServices<ILoggerProvider>().ToArray();
        var provider = Assert.Single(providers);
        Assert.IsType<ConsoleLoggerProvider>(provider);
    }

    [Fact]
    public void Structured_logging_enables_scopes_and_utc_timestamps()
    {
        var builder = NewBuilder();

        builder.AddHiveStructuredLogging();
        using var host = builder.Build();

        var options = host.Services
            .GetRequiredService<IOptionsMonitor<JsonConsoleFormatterOptions>>()
            .CurrentValue;

        Assert.True(options.IncludeScopes);
        Assert.True(options.UseUtcTimestamp);
    }

    [Fact]
    public async Task Bootstrap_composes_structured_logging_as_the_only_provider()
    {
        var builder = NewBuilder();
        builder.Configuration.AddInMemoryCollection(new Dictionary<string, string?>
        {
            ["Hive:Node:Roles:0"] = "api",
        });

        builder.AddHiveBootstrap();
        using var host = builder.Build();

        await host.StartAsync();
        var providers = host.Services.GetServices<ILoggerProvider>().ToArray();
        await host.StopAsync();

        var provider = Assert.Single(providers);
        Assert.IsType<ConsoleLoggerProvider>(provider);
    }

    private static HostApplicationBuilder NewBuilder() =>
        new(new HostApplicationBuilderSettings { DisableDefaults = true });

    private sealed class StubLoggerProvider : ILoggerProvider
    {
        public ILogger CreateLogger(string categoryName) => NullLogger.Instance;

        public void Dispose()
        {
        }

        private sealed class NullLogger : ILogger
        {
            public static readonly NullLogger Instance = new();

            public IDisposable BeginScope<TState>(TState state) where TState : notnull => NullScope.Instance;

            public bool IsEnabled(LogLevel logLevel) => false;

            public void Log<TState>(
                LogLevel logLevel,
                EventId eventId,
                TState state,
                Exception? exception,
                Func<TState, Exception?, string> formatter)
            {
            }
        }

        private sealed class NullScope : IDisposable
        {
            public static readonly NullScope Instance = new();

            public void Dispose()
            {
            }
        }
    }
}
