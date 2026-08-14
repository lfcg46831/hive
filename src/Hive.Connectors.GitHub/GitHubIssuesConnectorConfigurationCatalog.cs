using System.Collections.Immutable;
using System.Xml;
using Hive.Domain.Identity;
using Microsoft.Extensions.Options;

namespace Hive.Connectors.GitHub;

/// <summary>
/// Validated immutable connector-instance catalog. Secrets remain in a private lookup and are never
/// exposed by the declarative instance projection.
/// </summary>
public sealed class GitHubIssuesConnectorConfigurationCatalog
{
    private readonly ImmutableDictionary<string, string> _tokens;

    internal GitHubIssuesConnectorConfigurationCatalog(IOptions<GitHubIssuesConnectorOptions> options)
    {
        ArgumentNullException.ThrowIfNull(options);

        var configured = options.Value;
        Instances = (configured.Instances ?? [])
            .Select(instance => new GitHubIssuesConnectorInstanceConfiguration(
                instance.InstanceId!,
                OrganizationId.From(instance.OrganizationId!),
                instance.Repositories!,
                PositionId.From(instance.InboundDirectiveTarget!),
                instance.OutboundOperations!,
                new GitHubIssuesPollingConfiguration(
                    XmlConvert.ToTimeSpan(instance.Polling!.Interval!),
                    instance.Polling.PageSize!.Value)))
            .ToImmutableArray();

        _tokens = (configured.Credentials ?? [])
            .ToImmutableDictionary(
                credential => credential.InstanceId!,
                credential => credential.Token!,
                StringComparer.Ordinal);
    }

    public IReadOnlyList<GitHubIssuesConnectorInstanceConfiguration> Instances { get; }

    internal GitHubIssuesConnectorInstanceConfiguration? FindInstance(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        var matches = Instances
            .Where(instance => string.Equals(
                instance.InstanceId,
                instanceId,
                StringComparison.Ordinal))
            .Take(2)
            .ToArray();
        return matches.Length == 1 ? matches[0] : null;
    }

    internal string GetToken(string instanceId)
    {
        ArgumentNullException.ThrowIfNull(instanceId);
        return _tokens.TryGetValue(instanceId, out var token)
            ? token
            : throw new KeyNotFoundException(
                $"No operational credential is configured for connector instance '{instanceId}'.");
    }
}
