using System.Text.Json;
using Hive.DemoClient;

var repositoryRoot = args.Length > 0
    ? Path.GetFullPath(args[0])
    : FindRepositoryRoot(AppContext.BaseDirectory);

var contextPath = Path.Combine(
    repositoryRoot,
    "config",
    "organizations",
    "acme-delivery",
    "examples",
    "bug-triage-directive-context.md");

var context = File.ReadAllText(contextPath);
var submission = AcmeDeliveryDemoDirectiveClient.CreateTriageDirective(
    DemoDirectiveIds.New(),
    DateTimeOffset.UtcNow,
    context);

var output = new
{
    method = "POST",
    path = submission.RelativePath,
    body = submission.Request,
};

Console.WriteLine(JsonSerializer.Serialize(
    output,
    new JsonSerializerOptions(JsonSerializerDefaults.Web)
    {
        WriteIndented = true,
    }));

static string FindRepositoryRoot(string startPath)
{
    var current = new DirectoryInfo(startPath);

    while (current is not null)
    {
        if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
        {
            return current.FullName;
        }

        current = current.Parent;
    }

    throw new InvalidOperationException("Could not locate the Hive repository root.");
}
