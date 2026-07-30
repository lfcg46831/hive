using System.Globalization;

namespace Hive.Evaluation.Tooling.Evaluation;

public static class EvaluationArtifactCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(args);
            var publication = await EvaluationArtifactPublisher.PublishAsync(
                    options,
                    cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(publication.Entry.Location)
                .ConfigureAwait(false);
            await output.WriteLineAsync(publication.IndexPath)
                .ConfigureAwait(false);
            return 0;
        }
        catch (Exception exception)
            when (exception is ArgumentException
                or InvalidDataException
                or IOException
                or UnauthorizedAccessException)
        {
            await output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await WriteUsageAsync(output).ConfigureAwait(false);
            return 2;
        }
    }

    private static EvaluationArtifactPublicationOptions Parse(string[] args)
    {
        if (args.Length == 0
            || !string.Equals(args[0], "publish", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Artifact command requires the 'publish' subcommand.");
        }

        string? repositoryRoot = null;
        string? datasetPath = null;
        string? manifestPath = null;
        string? summaryReportPath = null;
        string? artifactStore = null;
        Uri? locationBase = null;
        DateTimeOffset? publishedAt = null;
        DateTimeOffset? retainUntil = null;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--repository-root":
                    repositoryRoot = Path.GetFullPath(
                        Read(args, ref index, argument));
                    break;
                case "--dataset":
                    datasetPath = Path.GetFullPath(
                        Read(args, ref index, argument));
                    break;
                case "--manifest":
                    manifestPath = Path.GetFullPath(
                        Read(args, ref index, argument));
                    break;
                case "--summary-report":
                    summaryReportPath = Path.GetFullPath(
                        Read(args, ref index, argument));
                    break;
                case "--artifact-store":
                    artifactStore = Path.GetFullPath(
                        Read(args, ref index, argument));
                    break;
                case "--location-base":
                    locationBase = new Uri(
                        Read(args, ref index, argument),
                        UriKind.Absolute);
                    break;
                case "--published-at":
                    publishedAt = UtcTimestamp(
                        Read(args, ref index, argument),
                        argument);
                    break;
                case "--retain-until":
                    retainUntil = UtcTimestamp(
                        Read(args, ref index, argument),
                        argument);
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown artifact argument '{argument}'.");
            }
        }

        repositoryRoot ??= FindRepositoryRoot(AppContext.BaseDirectory);
        if (datasetPath is null
            || manifestPath is null
            || summaryReportPath is null
            || artifactStore is null
            || locationBase is null
            || publishedAt is null
            || retainUntil is null)
        {
            throw new ArgumentException(
                "Artifact publication requires dataset, manifest, summary report, artifact store, location base, published-at, and retain-until.");
        }

        if (retainUntil <= publishedAt)
        {
            throw new ArgumentException(
                "--retain-until must be later than --published-at.");
        }

        return new EvaluationArtifactPublicationOptions(
            repositoryRoot,
            datasetPath,
            manifestPath,
            summaryReportPath,
            artifactStore,
            locationBase,
            publishedAt.Value,
            retainUntil.Value);
    }

    private static DateTimeOffset UtcTimestamp(string value, string argument)
    {
        if (!DateTimeOffset.TryParse(
                value,
                CultureInfo.InvariantCulture,
                DateTimeStyles.RoundtripKind,
                out var parsed)
            || parsed.Offset != TimeSpan.Zero)
        {
            throw new ArgumentException(
                $"{argument} must be an explicit UTC timestamp.");
        }

        return parsed;
    }

    private static string Read(
        IReadOnlyList<string> args,
        ref int index,
        string argument)
    {
        if (++index >= args.Count)
        {
            throw new ArgumentException($"Missing value for '{argument}'.");
        }

        return args[index];
    }

    private static string FindRepositoryRoot(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
            {
                return current.FullName;
            }

            current = current.Parent;
        }

        throw new InvalidOperationException(
            "Could not locate the Hive repository root.");
    }

    private static Task WriteUsageAsync(TextWriter output) => output.WriteLineAsync(
        "Usage: dotnet run --project src/Hive.Evaluation.Tooling -- artifact publish " +
        "--dataset <raw.json> --manifest <experiment.json> --summary-report <report.md> " +
        "--artifact-store <external-directory> --location-base <absolute-uri> " +
        "--published-at <utc> --retain-until <utc> [--repository-root <path>]");
}
