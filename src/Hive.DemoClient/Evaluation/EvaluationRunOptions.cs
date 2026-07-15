using System.Text.RegularExpressions;

namespace Hive.DemoClient.Evaluation;

public sealed partial record EvaluationRunOptions(
    string RepositoryRoot,
    string RunId,
    Uri BaseUrl,
    string ConnectionString,
    string CorpusPath,
    string OutputPath,
    TimeSpan Timeout,
    TimeSpan PollInterval,
    DateTimeOffset SentAt,
    string? RubricPath = null,
    EvaluationPlan? Plan = null,
    string? Partition = null)
{
    public static readonly DateTimeOffset DefaultSentAt =
        DateTimeOffset.Parse("2026-07-12T00:00:00Z", System.Globalization.CultureInfo.InvariantCulture);

    public static EvaluationRunOptions Parse(string[] args, string baseDirectory)
    {
        var repositoryRoot = FindRepositoryRoot(baseDirectory);
        string? runId = null;
        var baseUrl = DemoDirectiveCommand.DefaultBaseUrl;
        var connectionString = Environment.GetEnvironmentVariable("ConnectionStrings__PostgreSql");
        string? corpusPath = null;
        string? outputPath = null;
        string? rubricPath = null;
        string? planPath = null;
        string? partition = null;
        var corpusOverride = false;
        var rubricOverride = false;
        var timeoutOverride = false;
        var pollOverride = false;
        var timeout = TimeSpan.FromMinutes(2);
        var pollInterval = TimeSpan.FromSeconds(1);

        for (var index = 0; index < args.Length; index++)
        {
            var argument = args[index];
            switch (argument)
            {
                case "--repository-root": repositoryRoot = Path.GetFullPath(Read(args, ref index, argument)); break;
                case "--run-id": runId = Read(args, ref index, argument); break;
                case "--base-url": baseUrl = new Uri(Read(args, ref index, argument), UriKind.Absolute); break;
                case "--connection-string": connectionString = Read(args, ref index, argument); break;
                case "--corpus":
                    corpusPath = Path.GetFullPath(Read(args, ref index, argument));
                    corpusOverride = true;
                    break;
                case "--output": outputPath = Path.GetFullPath(Read(args, ref index, argument)); break;
                case "--rubric":
                    rubricPath = Path.GetFullPath(Read(args, ref index, argument));
                    rubricOverride = true;
                    break;
                case "--plan": planPath = Path.GetFullPath(Read(args, ref index, argument)); break;
                case "--partition": partition = Read(args, ref index, argument); break;
                case "--timeout-seconds":
                    timeout = PositiveDuration(Read(args, ref index, argument), argument, 1000);
                    timeoutOverride = true;
                    break;
                case "--poll-milliseconds":
                    pollInterval = PositiveDuration(Read(args, ref index, argument), argument, 1);
                    pollOverride = true;
                    break;
                default: throw new ArgumentException($"Unknown evaluation argument '{argument}'.");
            }
        }

        if (string.IsNullOrWhiteSpace(runId) || !RunIdPattern().IsMatch(runId))
        {
            throw new ArgumentException("--run-id must use lowercase letters, digits, and single hyphens.");
        }

        if (string.IsNullOrWhiteSpace(connectionString))
        {
            throw new ArgumentException("PostgreSQL connection string is required via --connection-string or ConnectionStrings__PostgreSql.");
        }

        if ((planPath is null) != (partition is null))
        {
            throw new ArgumentException("--plan and --partition must be supplied together.");
        }

        EvaluationPlan? plan = null;
        if (planPath is not null)
        {
            if (corpusOverride || rubricOverride || timeoutOverride || pollOverride)
            {
                throw new ArgumentException(
                    "--corpus, --rubric, --timeout-seconds, and --poll-milliseconds cannot override a frozen evaluation plan.");
            }

            plan = EvaluationPlan.Load(planPath, partition!);
            var selection = plan.Select(partition!);
            corpusPath = selection.CorpusPath;
            rubricPath = selection.RubricPath;
            timeout = TimeSpan.FromSeconds(plan.Runner.TimeoutSeconds);
            pollInterval = TimeSpan.FromMilliseconds(plan.Runner.PollMilliseconds);
        }

        corpusPath ??= Path.Combine(repositoryRoot, "config", "organizations", "acme-delivery", "examples", "evaluation", "bug-triage-corpus.v1.json");
        rubricPath ??= Path.Combine(repositoryRoot, "config", "organizations", "acme-delivery", "examples", "evaluation", "bug-triage-rubric.v1.json");
        outputPath ??= Path.Combine(repositoryRoot, "artifacts", "evaluation", $"{runId}.json");
        return new EvaluationRunOptions(
            repositoryRoot,
            runId,
            baseUrl,
            connectionString,
            corpusPath,
            outputPath,
            timeout,
            pollInterval,
            DefaultSentAt,
            rubricPath,
            plan,
            partition);
    }

    private static string Read(IReadOnlyList<string> args, ref int index, string argument)
    {
        if (++index >= args.Count) throw new ArgumentException($"Missing value for '{argument}'.");
        return args[index];
    }

    private static TimeSpan PositiveDuration(string value, string argument, double multiplier)
    {
        if (!double.TryParse(value, System.Globalization.NumberStyles.Float, System.Globalization.CultureInfo.InvariantCulture, out var parsed)
            || parsed <= 0)
        {
            throw new ArgumentException($"{argument} must be a positive number.");
        }

        return TimeSpan.FromMilliseconds(parsed * multiplier);
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

    [GeneratedRegex("^[a-z0-9]+(?:-[a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex RunIdPattern();
}
