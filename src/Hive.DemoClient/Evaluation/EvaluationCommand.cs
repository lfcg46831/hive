using System.Text.Json;

namespace Hive.DemoClient.Evaluation;

public static class EvaluationCommand
{
    private static readonly JsonSerializerOptions OutputJson = new(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    };

    public static async Task<int> RunAsync(
        string[] args,
        HttpClient httpClient,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        try
        {
            var options = EvaluationRunOptions.Parse(args, AppContext.BaseDirectory);
            var corpus = EvaluationCorpus.Load(options.CorpusPath);
            var rubric = EvaluationRubric.Load(options.RubricPath!);
            rubric.ValidateCorpus(corpus);

            await using var reader = new PostgreSqlEvaluationAuditReader(options.ConnectionString);
            await using var projectionReader = new PostgreSqlEvaluationProjectionReader(
                options.ConnectionString);
            await projectionReader.EnsureAvailableAsync(cancellationToken)
                .ConfigureAwait(false);
            var runner = new EvaluationRunner(
                httpClient,
                reader,
                projectionReader: projectionReader,
                rubric: rubric);
            var dataset = await runner.RunAsync(corpus, options, cancellationToken).ConfigureAwait(false);

            var directory = Path.GetDirectoryName(options.OutputPath);
            if (!string.IsNullOrEmpty(directory)) Directory.CreateDirectory(directory);
            await using var stream = File.Create(options.OutputPath);
            await JsonSerializer.SerializeAsync(stream, dataset, OutputJson, cancellationToken).ConfigureAwait(false);
            await stream.WriteAsync("\n"u8.ToArray(), cancellationToken).ConfigureAwait(false);
            await output.WriteLineAsync(options.OutputPath).ConfigureAwait(false);
            return dataset.Cases.All(item =>
                (item.Outcome is "succeeded" or "accepted")
                && item.Scoring?.Status == "scored")
                ? 0
                : 1;
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
        "Usage: dotnet run --project src/Hive.DemoClient -- evaluate --run-id <id> " +
        "[--base-url <url>] [--connection-string <postgres>] [--corpus <path>] [--rubric <path>] " +
        "[--output <path>] [--timeout-seconds <n>] [--poll-milliseconds <n>]");
}
