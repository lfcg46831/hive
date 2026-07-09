using Hive.DemoClient;

using var httpClient = new HttpClient();
return await DemoDirectiveCommand.RunAsync(
    args,
    httpClient,
    Console.Out,
    CancellationToken.None);
