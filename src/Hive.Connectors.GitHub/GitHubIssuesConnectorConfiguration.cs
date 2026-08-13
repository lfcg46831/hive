using System.Collections.Immutable;
using System.Text.Json;
using System.Text.RegularExpressions;
using Hive.Domain.Connectors;
using Hive.Domain.Identity;

namespace Hive.Connectors.GitHub;

/// <summary>The closed outbound operation vocabulary exposed by the F1 GitHub Issues connector.</summary>
public static class GitHubIssuesOutboundOperations
{
    public const string Comment = "issues.comment";
    public const string UpdateState = "issues.update-state";
    public const string UpdateLabels = "issues.update-labels";

    private static readonly ImmutableHashSet<string> Supported =
        ImmutableHashSet.Create(StringComparer.Ordinal, Comment, UpdateState, UpdateLabels);

    public static IReadOnlySet<string> All => Supported;

    public static bool IsSupported(string? value) => value is not null && Supported.Contains(value);
}

/// <summary>Validated polling parameters for one declarative GitHub Issues connector instance.</summary>
public sealed record GitHubIssuesPollingConfiguration
{
    public GitHubIssuesPollingConfiguration(TimeSpan interval, int pageSize)
    {
        if (interval < TimeSpan.FromSeconds(1))
        {
            throw new ArgumentOutOfRangeException(
                nameof(interval),
                interval,
                "GitHub Issues polling interval must be at least one second.");
        }

        if (pageSize is < 1 or > 100)
        {
            throw new ArgumentOutOfRangeException(
                nameof(pageSize),
                pageSize,
                "GitHub Issues polling page size must be between 1 and 100.");
        }

        Interval = interval;
        PageSize = pageSize;
    }

    public TimeSpan Interval { get; }

    public int PageSize { get; }
}

/// <summary>
/// Immutable, credential-free configuration of one GitHub Issues connector instance. The instance
/// is scoped to one organization, one or more repositories, one inbound target position and an
/// explicit outbound-operation allowlist.
/// </summary>
public sealed partial record GitHubIssuesConnectorInstanceConfiguration
{
    public GitHubIssuesConnectorInstanceConfiguration(
        string instanceId,
        OrganizationId organizationId,
        IReadOnlyList<string> repositories,
        PositionId inboundDirectiveTarget,
        IReadOnlyList<string> outboundOperations,
        GitHubIssuesPollingConfiguration polling)
    {
        ArgumentNullException.ThrowIfNull(organizationId);
        ArgumentNullException.ThrowIfNull(repositories);
        ArgumentNullException.ThrowIfNull(inboundDirectiveTarget);
        ArgumentNullException.ThrowIfNull(outboundOperations);
        ArgumentNullException.ThrowIfNull(polling);

        InstanceId = RequireInstanceId(instanceId, nameof(instanceId));
        OrganizationId = organizationId;
        Repositories = SnapshotRepositories(repositories, nameof(repositories));
        InboundDirectiveTarget = inboundDirectiveTarget;
        OutboundOperations = SnapshotOperations(outboundOperations, nameof(outboundOperations));
        Polling = polling;
    }

    public string InstanceId { get; }

    public OrganizationId OrganizationId { get; }

    public IReadOnlyList<string> Repositories { get; }

    public PositionId InboundDirectiveTarget { get; }

    public IReadOnlyList<string> OutboundOperations { get; }

    public GitHubIssuesPollingConfiguration Polling { get; }

    internal static string RequireInstanceId(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (!InstanceIdPattern().IsMatch(value))
        {
            throw new ArgumentException(
                "Connector instance id must be a lowercase dot- or kebab-separated token.",
                parameterName);
        }

        return value;
    }

    internal static bool IsValidRepository(string? value)
    {
        if (string.IsNullOrWhiteSpace(value)
            || !string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Any(char.IsWhiteSpace)
            || value.Any(char.IsControl))
        {
            return false;
        }

        var separator = value.IndexOf('/');
        return separator > 0
            && separator == value.LastIndexOf('/')
            && separator < value.Length - 1;
    }

    private static ImmutableArray<string> SnapshotRepositories(
        IReadOnlyList<string> repositories,
        string parameterName)
    {
        if (repositories.Count == 0)
        {
            throw new ArgumentException(
                "A GitHub Issues connector instance must scope at least one repository.",
                parameterName);
        }

        var snapshot = repositories.ToImmutableArray();
        if (snapshot.Any(repository => !IsValidRepository(repository)))
        {
            throw new ArgumentException(
                "Repository scopes must be trimmed 'owner/repository' identifiers without whitespace.",
                parameterName);
        }

        var duplicate = snapshot
            .GroupBy(repository => repository, StringComparer.OrdinalIgnoreCase)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Repository scope '{duplicate.Key}' is declared more than once.",
                parameterName);
        }

        return snapshot;
    }

    private static ImmutableArray<string> SnapshotOperations(
        IReadOnlyList<string> operations,
        string parameterName)
    {
        var snapshot = operations.ToImmutableArray();
        if (snapshot.Any(operation => !GitHubIssuesOutboundOperations.IsSupported(operation)))
        {
            throw new ArgumentException(
                "Outbound operations must use the closed GitHub Issues operation vocabulary.",
                parameterName);
        }

        var duplicate = snapshot
            .GroupBy(operation => operation, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new ArgumentException(
                $"Outbound operation '{duplicate.Key}' is declared more than once.",
                parameterName);
        }

        return snapshot;
    }

    [GeneratedRegex("^[a-z0-9]+(?:[.-][a-z0-9]+)*$", RegexOptions.CultureInvariant)]
    private static partial Regex InstanceIdPattern();
}

/// <summary>
/// JSON schema published by the GitHub Issues adapter for its credential-free instance
/// configuration. Credentials are deliberately absent and are resolved from host configuration.
/// </summary>
public static class GitHubIssuesConnectorConfigurationSchema
{
    private static readonly ConnectorConfigurationSchema Value = Create();

    public static ConnectorConfigurationSchema Instance => Value;

    private static ConnectorConfigurationSchema Create()
    {
        using var document = JsonDocument.Parse(
            """
            {
              "$schema": "https://json-schema.org/draft/2020-12/schema",
              "type": "object",
              "required": [
                "instance_id",
                "organization_id",
                "repositories",
                "inbound_directive_target",
                "outbound_operations",
                "polling"
              ],
              "properties": {
                "instance_id": { "type": "string", "pattern": "^[a-z0-9]+(?:[.-][a-z0-9]+)*$" },
                "organization_id": { "type": "string", "minLength": 1 },
                "repositories": {
                  "type": "array",
                  "minItems": 1,
                  "uniqueItems": true,
                  "items": { "type": "string", "pattern": "^[^/\\s]+/[^/\\s]+$" }
                },
                "inbound_directive_target": { "type": "string", "minLength": 1 },
                "outbound_operations": {
                  "type": "array",
                  "uniqueItems": true,
                  "items": {
                    "type": "string",
                    "enum": ["issues.comment", "issues.update-state", "issues.update-labels"]
                  }
                },
                "polling": {
                  "type": "object",
                  "required": ["interval", "page_size"],
                  "properties": {
                    "interval": { "type": "string", "format": "duration" },
                    "page_size": { "type": "integer", "minimum": 1, "maximum": 100 }
                  },
                  "additionalProperties": false
                }
              },
              "additionalProperties": false
            }
            """);

        return new ConnectorConfigurationSchema(
            version: 1,
            document.RootElement,
            [
                new ConnectorScopeDefinition(
                    "repository",
                    ConnectorScopeDirection.Both,
                    "$.repositories",
                    "Repositories that the instance may read or change."),
                new ConnectorScopeDefinition(
                    "operation",
                    ConnectorScopeDirection.Outbound,
                    "$.outbound_operations",
                    "Outbound operations that the instance may invoke."),
            ]);
    }
}
