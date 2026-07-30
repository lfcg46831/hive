using Hive.Evaluation.Tooling.Evaluation;

if (args.Length > 0 && string.Equals(args[0], "experiment", StringComparison.Ordinal))
{
    return await EvaluationExperimentCommand.RunAsync(
        args[1..],
        Console.Out,
        CancellationToken.None);
}

if (args.Length > 0 && string.Equals(args[0], "report", StringComparison.Ordinal))
{
    return await EvaluationReportCommand.RunAsync(
        args[1..],
        Console.Out,
        CancellationToken.None);
}

if (args.Length > 0 && string.Equals(args[0], "artifact", StringComparison.Ordinal))
{
    return await EvaluationArtifactCommand.RunAsync(
        args[1..],
        Console.Out,
        CancellationToken.None);
}

using var httpClient = new HttpClient();
if (args.Length > 0 && string.Equals(args[0], "evaluate", StringComparison.Ordinal))
{
    return await EvaluationCommand.RunAsync(
        args[1..],
        httpClient,
        Console.Out,
        CancellationToken.None);
}

await Console.Error.WriteLineAsync(
    "Usage: dotnet run --project src/Hive.Evaluation.Tooling -- <experiment|evaluate|report|artifact> [options]");
return 2;
