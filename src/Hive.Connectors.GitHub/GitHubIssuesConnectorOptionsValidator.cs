using System.Xml;
using Hive.Domain.Identity;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub;

internal sealed class GitHubIssuesConnectorOptionsValidator
    : IValidateOptions<GitHubIssuesConnectorOptions>
{
    private const string Prefix = GitHubIssuesConnectorOptions.SectionName;

    public ValidateOptionsResult Validate(string? name, GitHubIssuesConnectorOptions options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var failures = new List<string>();
        var instances = options.Instances ?? [];
        var credentials = options.Credentials ?? [];
        var instanceIds = new HashSet<string>(StringComparer.Ordinal);
        var credentialsByInstance = new Dictionary<
            string,
            GitHubIssuesConnectorCredentialOptions>(StringComparer.Ordinal);

        for (var index = 0; index < credentials.Length; index++)
        {
            var credential = credentials[index];
            var path = $"{Prefix}:Credentials:{index}";
            if (credential is null)
            {
                failures.Add($"{path} must be a credential mapping.");
                continue;
            }

            var instanceIdValid = ValidateInstanceId(
                credential.InstanceId,
                $"{path}:InstanceId",
                failures);
            if (instanceIdValid
                && !credentialsByInstance.TryAdd(credential.InstanceId!, credential))
            {
                failures.Add($"{path}:InstanceId is declared more than once.");
            }

            if (string.IsNullOrWhiteSpace(credential.Token)
                || !string.Equals(credential.Token, credential.Token.Trim(), StringComparison.Ordinal))
            {
                failures.Add($"{path}:Token must be a trimmed, non-empty operational secret.");
            }
        }

        for (var index = 0; index < instances.Length; index++)
        {
            var instance = instances[index];
            var path = $"{Prefix}:Instances:{index}";
            if (instance is null)
            {
                failures.Add($"{path} must be an instance mapping.");
                continue;
            }

            var instanceIdValid = ValidateInstanceId(instance.InstanceId, $"{path}:InstanceId", failures);
            if (instanceIdValid && !instanceIds.Add(instance.InstanceId!))
            {
                failures.Add($"{path}:InstanceId is declared more than once.");
            }

            ValidateIdentity(
                instance.OrganizationId,
                OrganizationId.From,
                $"{path}:OrganizationId",
                failures);
            ValidateIdentity(
                instance.InboundDirectiveTarget,
                PositionId.From,
                $"{path}:InboundDirectiveTarget",
                failures);
            ValidateRepositories(instance.Repositories, $"{path}:Repositories", failures);
            ValidateOperations(instance.OutboundOperations, $"{path}:OutboundOperations", failures);
            ValidatePolling(instance.Polling, $"{path}:Polling", failures);

            if (instanceIdValid
                && !credentialsByInstance.ContainsKey(instance.InstanceId!))
            {
                failures.Add(
                    $"{Prefix}:Credentials is missing an operational secret entry for instance '{instance.InstanceId}'.");
            }
        }

        foreach (var instanceId in credentialsByInstance.Keys.OrderBy(value => value, StringComparer.Ordinal))
        {
            if (!instanceIds.Contains(instanceId))
            {
                failures.Add(
                    $"{Prefix}:Credentials contains an entry for undeclared instance '{instanceId}'.");
            }
        }

        return failures.Count == 0
            ? ValidateOptionsResult.Success
            : ValidateOptionsResult.Fail(failures);
    }

    internal static bool TryParseInterval(string? value, out TimeSpan interval)
    {
        interval = default;
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            return false;
        }

        try
        {
            interval = XmlConvert.ToTimeSpan(value);
            return interval >= TimeSpan.FromSeconds(1);
        }
        catch (Exception exception)
            when (exception is FormatException or OverflowException)
        {
            return false;
        }
    }

    private static bool ValidateInstanceId(
        string? value,
        string path,
        ICollection<string> failures)
    {
        try
        {
            GitHubIssuesConnectorInstanceConfiguration.RequireInstanceId(value!, nameof(value));
            return true;
        }
        catch (ArgumentException)
        {
            failures.Add($"{path} must be a lowercase dot- or kebab-separated token.");
            return false;
        }
    }

    private static void ValidateIdentity<T>(
        string? value,
        Func<string, T> factory,
        string path,
        ICollection<string> failures)
    {
        try
        {
            factory(value!);
        }
        catch (ArgumentException)
        {
            failures.Add($"{path} must be a valid canonical identifier.");
        }
    }

    private static void ValidateRepositories(
        IReadOnlyList<string>? repositories,
        string path,
        ICollection<string> failures)
    {
        if (repositories is null || repositories.Count == 0)
        {
            failures.Add($"{path} must contain at least one 'owner/repository' scope.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.OrdinalIgnoreCase);
        for (var index = 0; index < repositories.Count; index++)
        {
            var repository = repositories[index];
            if (!GitHubIssuesConnectorInstanceConfiguration.IsValidRepository(repository))
            {
                failures.Add($"{path}:{index} must be a trimmed 'owner/repository' identifier without whitespace.");
            }
            else if (!seen.Add(repository))
            {
                failures.Add($"{path}:{index} duplicates another repository scope.");
            }
        }
    }

    private static void ValidateOperations(
        IReadOnlyList<string>? operations,
        string path,
        ICollection<string> failures)
    {
        if (operations is null)
        {
            failures.Add($"{path} must be declared; use an empty list to disable outbound operations.");
            return;
        }

        var seen = new HashSet<string>(StringComparer.Ordinal);
        for (var index = 0; index < operations.Count; index++)
        {
            if (!GitHubIssuesOutboundOperations.IsSupported(operations[index]))
            {
                failures.Add(
                    $"{path}:{index} must be one of '{GitHubIssuesOutboundOperations.Comment}', "
                    + $"'{GitHubIssuesOutboundOperations.UpdateState}' or '{GitHubIssuesOutboundOperations.UpdateLabels}'.");
            }
            else if (!seen.Add(operations[index]))
            {
                failures.Add($"{path}:{index} duplicates another outbound operation.");
            }
        }
    }

    private static void ValidatePolling(
        GitHubIssuesPollingOptions? polling,
        string path,
        ICollection<string> failures)
    {
        if (polling is null)
        {
            failures.Add($"{path} is required.");
            return;
        }

        if (!TryParseInterval(polling.Interval, out _))
        {
            failures.Add($"{path}:Interval must be an ISO-8601 duration of at least one second.");
        }

        if (polling.PageSize is not (>= 1 and <= 100))
        {
            failures.Add($"{path}:PageSize must be between 1 and 100.");
        }
    }
}
