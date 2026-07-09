using System.Globalization;
using System.Net.Http.Json;
using System.Text.Json;

namespace Hive.DemoClient;

public static class DemoDirectiveCommand
{
    public const string DefaultSeed = "us-f0-10-t12-demo";

    public static readonly DateTimeOffset DefaultSentAt =
        DateTimeOffset.Parse(
            "2026-07-09T12:00:00Z",
            CultureInfo.InvariantCulture,
            DateTimeStyles.AssumeUniversal);

    public static readonly Uri DefaultBaseUrl = new("http://localhost:8080");

    private static readonly JsonSerializerOptions JsonOptions =
        new(JsonSerializerDefaults.Web)
        {
            WriteIndented = true,
        };

    public static async Task<int> RunAsync(
        string[] args,
        HttpClient httpClient,
        TextWriter output,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(args);
        ArgumentNullException.ThrowIfNull(httpClient);
        ArgumentNullException.ThrowIfNull(output);

        DemoDirectiveCommandOptions options;
        try
        {
            options = DemoDirectiveCommandOptions.Parse(args, AppContext.BaseDirectory);
        }
        catch (ArgumentException exception)
        {
            await output.WriteLineAsync(exception.Message).ConfigureAwait(false);
            await WriteUsageAsync(output).ConfigureAwait(false);
            return 2;
        }

        if (options.ShowHelp)
        {
            await WriteUsageAsync(output).ConfigureAwait(false);
            return 0;
        }

        var submission = CreateSubmission(options);
        if (!options.Submit)
        {
            await WriteSubmissionEnvelopeAsync(output, submission).ConfigureAwait(false);
            return 0;
        }

        var requestUri = new Uri(options.BaseUrl, submission.RelativePath);
        using var response = await httpClient
            .PostAsJsonAsync(requestUri, submission.Request, JsonOptions, cancellationToken)
            .ConfigureAwait(false);

        var responseBody = await response.Content
            .ReadAsStringAsync(cancellationToken)
            .ConfigureAwait(false);

        await WriteResponseAsync(output, responseBody).ConfigureAwait(false);
        return response.IsSuccessStatusCode ? 0 : 1;
    }

    private static DemoDirectiveSubmission CreateSubmission(
        DemoDirectiveCommandOptions options)
    {
        var contextPath = Path.Combine(
            options.RepositoryRoot,
            "config",
            "organizations",
            "acme-delivery",
            "examples",
            "bug-triage-directive-context.md");

        var context = File.ReadAllText(contextPath);
        return AcmeDeliveryDemoDirectiveClient.CreateTriageDirective(
            DemoDirectiveIds.FromSeed(options.Seed),
            options.SentAt,
            context);
    }

    private static Task WriteSubmissionEnvelopeAsync(
        TextWriter output,
        DemoDirectiveSubmission submission)
    {
        var envelope = new
        {
            method = "POST",
            path = submission.RelativePath,
            body = submission.Request,
        };

        return output.WriteLineAsync(JsonSerializer.Serialize(envelope, JsonOptions));
    }

    private static async Task WriteResponseAsync(
        TextWriter output,
        string responseBody)
    {
        if (string.IsNullOrWhiteSpace(responseBody))
        {
            await output.WriteLineAsync("{}").ConfigureAwait(false);
            return;
        }

        try
        {
            using var document = JsonDocument.Parse(responseBody);
            await output
                .WriteLineAsync(JsonSerializer.Serialize(document.RootElement, JsonOptions))
                .ConfigureAwait(false);
        }
        catch (JsonException)
        {
            await output.WriteLineAsync(responseBody).ConfigureAwait(false);
        }
    }

    private static Task WriteUsageAsync(TextWriter output) =>
        output.WriteLineAsync(
            "Usage: dotnet run --project src/Hive.DemoClient -- " +
            "[--repository-root <path>] [--seed <text>] [--sent-at <iso-8601>] " +
            "[--base-url <url>] [--submit]");

    private sealed record DemoDirectiveCommandOptions(
        string RepositoryRoot,
        string Seed,
        DateTimeOffset SentAt,
        Uri BaseUrl,
        bool Submit,
        bool ShowHelp)
    {
        public static DemoDirectiveCommandOptions Parse(
            string[] args,
            string baseDirectory)
        {
            var defaultRepositoryRoot = FindRepositoryRoot(baseDirectory);
            var repositoryRoot = defaultRepositoryRoot;
            var seed = DefaultSeed;
            var sentAt = DefaultSentAt;
            var baseUrl = DefaultBaseUrl;
            var submit = false;
            var showHelp = false;

            for (var index = 0; index < args.Length; index++)
            {
                var argument = args[index];
                switch (argument)
                {
                    case "--help":
                    case "-h":
                        showHelp = true;
                        break;

                    case "--repository-root":
                        repositoryRoot = Path.GetFullPath(ReadValue(args, ref index, argument));
                        break;

                    case "--seed":
                        seed = ReadValue(args, ref index, argument);
                        break;

                    case "--sent-at":
                        sentAt = DateTimeOffset.Parse(
                            ReadValue(args, ref index, argument),
                            CultureInfo.InvariantCulture,
                            DateTimeStyles.AssumeUniversal);
                        break;

                    case "--base-url":
                        baseUrl = new Uri(
                            ReadValue(args, ref index, argument),
                            UriKind.Absolute);
                        break;

                    case "--submit":
                        submit = true;
                        break;

                    default:
                        if (!argument.StartsWith("--", StringComparison.Ordinal)
                            && string.Equals(
                                repositoryRoot,
                                defaultRepositoryRoot,
                                StringComparison.OrdinalIgnoreCase))
                        {
                            repositoryRoot = Path.GetFullPath(argument);
                            break;
                        }

                        throw new ArgumentException($"Unknown argument '{argument}'.");
                }
            }

            return new DemoDirectiveCommandOptions(
                repositoryRoot,
                seed,
                sentAt,
                baseUrl,
                submit,
                showHelp);
        }

        private static string ReadValue(
            IReadOnlyList<string> args,
            ref int index,
            string argument)
        {
            if (index + 1 >= args.Count)
            {
                throw new ArgumentException($"Missing value for '{argument}'.");
            }

            index++;
            return args[index];
        }

        private static string FindRepositoryRoot(string startPath)
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
    }
}
