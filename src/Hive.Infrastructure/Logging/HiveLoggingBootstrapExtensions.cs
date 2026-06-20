using Microsoft.Extensions.Hosting;
using Microsoft.Extensions.Logging;

namespace Hive.Infrastructure.Logging;

/// <summary>
/// Configures the common structured logging shared by every executable host (US-F0-01-T07).
/// Both <c>Hive.Api</c> and <c>Hive.Worker</c> get an identical machine-readable JSON console
/// sink through the single shared bootstrap, so a host never opts out by accident and the two
/// executables cannot drift. Scopes are preserved so future correlation metadata
/// (<c>ThreadId</c>/<c>DirectiveId</c>, §11) flows into the output; richer observability such as
/// OpenTelemetry is reserved for later phases. The standard <c>Logging</c> configuration section
/// keeps driving level filters; this only fixes the provider set and output format.
/// </summary>
public static class HiveLoggingBootstrapExtensions
{
    public static IHostApplicationBuilder AddHiveStructuredLogging(this IHostApplicationBuilder builder)
    {
        ArgumentNullException.ThrowIfNull(builder);

        builder.Logging.ClearProviders();
        builder.Logging.AddJsonConsole(options =>
        {
            options.IncludeScopes = true;
            options.UseUtcTimestamp = true;
            options.TimestampFormat = "yyyy-MM-ddTHH:mm:ss.fffZ";
        });

        return builder;
    }
}
