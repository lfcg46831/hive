using Hive.DemoClient;
using Hive.DemoClient.Evaluation;

using var httpClient = new HttpClient();
if (args.Length > 0 && string.Equals(args[0], "evaluate", StringComparison.Ordinal))
{
    return await EvaluationCommand.RunAsync(
        args[1..],
        httpClient,
        Console.Out,
        CancellationToken.None);
}

return await DemoDirectiveCommand.RunAsync(
    args,
    httpClient,
    Console.Out,
    CancellationToken.None);
