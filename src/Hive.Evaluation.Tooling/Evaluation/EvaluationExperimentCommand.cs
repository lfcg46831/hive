using System.Text.Json;

namespace Hive.Evaluation.Tooling.Evaluation;

public static class EvaluationExperimentCommand
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = Parse(args);
            var manifest = EvaluationExperimentManifest.Load(options.ManifestPath);
            var outputDirectory = options.OutputDirectory
                ?? Path.Combine(
                    manifest.RepositoryRoot,
                    "artifacts",
                    "evaluation",
                    "experiments",
                    manifest.ExperimentId);
            EnsureDisposableOutput(manifest.RepositoryRoot, outputDirectory);
            Directory.CreateDirectory(outputDirectory);

            var environmentPath = Path.Combine(outputDirectory, "compose.env");
            var configurationPath = Path.Combine(
                outputDirectory,
                "effective-configuration.v2.json");
            await File.WriteAllTextAsync(
                    environmentPath,
                    manifest.RenderEnvironmentFile(),
                    cancellationToken)
                .ConfigureAwait(false);
            await using (var stream = File.Create(configurationPath))
            {
                await JsonSerializer.SerializeAsync(
                        stream,
                        manifest.PreparedConfiguration(),
                        OutputJson,
                        cancellationToken)
                    .ConfigureAwait(false);
                await stream.WriteAsync("\n"u8.ToArray(), cancellationToken)
                    .ConfigureAwait(false);
            }

            await output.WriteLineAsync(environmentPath).ConfigureAwait(false);
            await output.WriteLineAsync(configurationPath).ConfigureAwait(false);
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

    private static EvaluationExperimentCommandOptions Parse(string[] args)
    {
        if (args.Length == 0 || !string.Equals(args[0], "prepare", StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Experiment command requires the 'prepare' subcommand.");
        }

        string? manifestPath = null;
        string? outputDirectory = null;
        for (var index = 1; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--manifest":
                    manifestPath = Path.GetFullPath(Read(args, ref index, argument));
                    break;
                case "--output-directory":
                    outputDirectory = Path.GetFullPath(Read(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException(
                        $"Unknown experiment argument '{argument}'.");
            }
        }

        if (manifestPath is null)
        {
            throw new ArgumentException("--manifest is required.");
        }

        return new EvaluationExperimentCommandOptions(manifestPath, outputDirectory);
    }

    private static void EnsureDisposableOutput(
        string repositoryRoot,
        string outputDirectory)
    {
        var artifactsRoot = Path.GetFullPath(Path.Combine(
            repositoryRoot,
            "artifacts"));
        var artifactsPrefix = artifactsRoot.TrimEnd(Path.DirectorySeparatorChar)
            + Path.DirectorySeparatorChar;
        var fullOutput = Path.GetFullPath(outputDirectory);
        if (!fullOutput.StartsWith(
                artifactsPrefix,
                StringComparison.OrdinalIgnoreCase))
        {
            throw new ArgumentException(
                "Experiment preparation output must stay under the repository artifacts directory.");
        }
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

    private static Task WriteUsageAsync(TextWriter output) => output.WriteLineAsync(
        "Usage: dotnet run --project src/Hive.Evaluation.Tooling -- experiment prepare " +
        "--manifest <path> [--output-directory <path>]");

    private sealed record EvaluationExperimentCommandOptions(
        string ManifestPath,
        string? OutputDirectory);
}
