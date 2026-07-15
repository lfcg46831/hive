namespace Hive.DemoClient.Evaluation;

public static class EvaluationReportCommand
{
    public static async Task<int> RunAsync(
        string[] args,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = EvaluationReportOptions.Parse(args, AppContext.BaseDirectory);
            var report = EvaluationReportBuilder.Build(options.DatasetPath, options.ProfilePath);
            var markdown = EvaluationReportRenderer.Render(report);
            var directory = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await File.WriteAllTextAsync(
                    options.OutputPath,
                    markdown,
                    new System.Text.UTF8Encoding(encoderShouldEmitUTF8Identifier: false),
                    cancellationToken)
                .ConfigureAwait(false);
            await output.WriteLineAsync(options.OutputPath).ConfigureAwait(false);
            return 0;
        }
        catch (ArgumentException exception)
        {
            await output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await WriteUsageAsync(output).ConfigureAwait(false);
            return 2;
        }
        catch (InvalidDataException exception)
        {
            await output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            return 2;
        }
    }

    private static Task WriteUsageAsync(TextWriter output) => output.WriteLineAsync(
        "Usage: dotnet run --project src/Hive.DemoClient -- report " +
        "[--dataset <holdout.json>] [--profile <report-profile.json>] [--output <report.md>]");
}

public sealed record EvaluationReportOptions(
    string RepositoryRoot,
    string DatasetPath,
    string ProfilePath,
    string OutputPath)
{
    public static EvaluationReportOptions Parse(string[] args, string baseDirectory)
    {
        var repositoryRoot = FindRepositoryRoot(baseDirectory);
        string? datasetPath = null;
        string? profilePath = null;
        string? outputPath = null;
        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--repository-root":
                    repositoryRoot = Path.GetFullPath(Read(args, ref index, argument));
                    break;
                case "--dataset":
                    datasetPath = Path.GetFullPath(Read(args, ref index, argument));
                    break;
                case "--profile":
                    profilePath = Path.GetFullPath(Read(args, ref index, argument));
                    break;
                case "--output":
                    outputPath = Path.GetFullPath(Read(args, ref index, argument));
                    break;
                default:
                    throw new ArgumentException($"Unknown evaluation report argument '{argument}'.");
            }
        }

        datasetPath ??= Path.Combine(
            repositoryRoot,
            "evidence",
            "evaluation",
            "bug-triage-holdout-v1",
            "holdout-v1.json");
        profilePath ??= Path.Combine(
            repositoryRoot,
            "config",
            "organizations",
            "acme-delivery",
            "examples",
            "evaluation",
            "bug-triage-report-profile.v1.json");
        outputPath ??= Path.Combine(
            repositoryRoot,
            "evidence",
            "evaluation",
            "bug-triage-holdout-v1",
            "bug-triage-unit-economics-quality-report.v1.md");
        return new EvaluationReportOptions(
            repositoryRoot,
            datasetPath,
            profilePath,
            outputPath);
    }

    private static string Read(IReadOnlyList<string> args, ref int index, string argument)
    {
        if (++index >= args.Count) throw new ArgumentException($"Missing value for '{argument}'.");
        return args[index];
    }

    private static string FindRepositoryRoot(string path)
    {
        var current = new DirectoryInfo(path);
        while (current is not null)
        {
            if (File.Exists(Path.Combine(current.FullName, "Hive.sln"))) return current.FullName;
            current = current.Parent;
        }

        throw new InvalidOperationException("Could not locate the Hive repository root.");
    }
}
