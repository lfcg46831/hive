using System.Collections.Immutable;
using System.Text;
using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Infrastructure.Connectors;
using Hive.Infrastructure.Governance;

namespace Hive.Connectors.GitHub;

internal static class GitHubIssuesOutboundToolDefinitions
{
    private static readonly ImmutableDictionary<string, AiToolDefinition> Definitions =
        new[]
        {
            Define(
                GitHubIssuesOutboundOperations.Comment,
                "Publish a comment on the GitHub issue correlated with the current directive.",
                "body",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "string",
                    ["minLength"] = 1,
                    ["maxLength"] = 65536,
                }),
            Define(
                GitHubIssuesOutboundOperations.UpdateState,
                "Set the state of the GitHub issue correlated with the current directive.",
                "state",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "string",
                    ["enum"] = new[] { "open", "closed" },
                }),
            Define(
                GitHubIssuesOutboundOperations.UpdateLabels,
                "Replace all labels on the GitHub issue correlated with the current directive.",
                "labels",
                new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    ["type"] = "array",
                    ["maxItems"] = 100,
                    ["uniqueItems"] = true,
                    ["items"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                    {
                        ["type"] = "string",
                        ["minLength"] = 1,
                        ["maxLength"] = 50,
                    },
                }),
        }.ToImmutableDictionary(definition => definition.Name, StringComparer.Ordinal);

    public static AiToolDefinition Get(string name) =>
        Definitions.GetValueOrDefault(name)
        ?? throw new ArgumentOutOfRangeException(nameof(name), name, "Unknown GitHub Issues tool.");

    private static AiToolDefinition Define(
        string name,
        string description,
        string propertyName,
        IReadOnlyDictionary<string, object?> propertySchema) =>
        new(
            name,
            description,
            new Dictionary<string, object?>(StringComparer.Ordinal)
            {
                ["type"] = "object",
                ["properties"] = new Dictionary<string, object?>(StringComparer.Ordinal)
                {
                    [propertyName] = propertySchema,
                },
                ["required"] = new[] { propertyName },
                ["additionalProperties"] = false,
            });
}

internal sealed class GitHubIssuesActionDomainContractSource : IActionDomainContractSource
{
    public IReadOnlyList<ActionDomainActionContract> ActionContracts { get; } =
    [
        ActionDomainActionContract.ForTool(GitHubIssuesOutboundOperations.Comment),
        ActionDomainActionContract.ForTool(GitHubIssuesOutboundOperations.UpdateState),
        ActionDomainActionContract.ForTool(GitHubIssuesOutboundOperations.UpdateLabels),
    ];

    // T07 adds the deterministic derived attributes without changing the tool selectors.
    public IReadOnlyList<ActionAttributeExtractorRegistration> ActionExtractors { get; } = [];
}

internal sealed class GitHubIssuesOutboundTool : IConnectorTool
{
    private readonly IGitHubIssuesOutboundExecutor _executor;

    public GitHubIssuesOutboundTool(string name, IGitHubIssuesOutboundExecutor executor)
    {
        Definition = GitHubIssuesOutboundToolDefinitions.Get(name);
        _executor = executor ?? throw new ArgumentNullException(nameof(executor));
    }

    public AiToolDefinition Definition { get; }

    public ValueTask<ConnectorToolResult> ExecuteAsync(
        ConnectorToolInvocation invocation,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(invocation);
        if (!string.Equals(invocation.ToolCall.Name, Definition.Name, StringComparison.Ordinal))
        {
            return ValueTask.FromResult(
                ConnectorToolResult.Failed("github-outbound-tool-mismatch"));
        }

        return _executor.ExecuteAsync(invocation, cancellationToken);
    }
}

internal sealed record GitHubIssuesOutboundOperation
{
    private GitHubIssuesOutboundOperation(
        string name,
        string canonicalPayload,
        string? body,
        string? state,
        ImmutableArray<string> labels)
    {
        Name = name;
        CanonicalPayload = canonicalPayload;
        Body = body;
        State = state;
        Labels = labels;
    }

    public string Name { get; }

    public string CanonicalPayload { get; }

    public string? Body { get; }

    public string? State { get; }

    public ImmutableArray<string> Labels { get; }

    public static bool TryParse(
        AiToolCall toolCall,
        out GitHubIssuesOutboundOperation? operation,
        out string? errorCode)
    {
        ArgumentNullException.ThrowIfNull(toolCall);
        operation = null;
        errorCode = "github-outbound-arguments-invalid";
        if (toolCall.Arguments.Count != 1)
        {
            return false;
        }

        switch (toolCall.Name)
        {
            case GitHubIssuesOutboundOperations.Comment:
                if (!TryReadString(toolCall.Arguments, "body", out var body)
                    || string.IsNullOrWhiteSpace(body)
                    || Encoding.UTF8.GetByteCount(body) > 65536)
                {
                    return false;
                }

                operation = new GitHubIssuesOutboundOperation(
                    toolCall.Name,
                    CanonicalStringPayload("body", body),
                    body,
                    state: null,
                    labels: []);
                return true;

            case GitHubIssuesOutboundOperations.UpdateState:
                if (!TryReadString(toolCall.Arguments, "state", out var state)
                    || state is not ("open" or "closed"))
                {
                    return false;
                }

                operation = new GitHubIssuesOutboundOperation(
                    toolCall.Name,
                    CanonicalStringPayload("state", state),
                    body: null,
                    state,
                    labels: []);
                return true;

            case GitHubIssuesOutboundOperations.UpdateLabels:
                if (!TryReadLabels(toolCall.Arguments, out var labels))
                {
                    return false;
                }

                operation = new GitHubIssuesOutboundOperation(
                    toolCall.Name,
                    CanonicalLabelsPayload(labels),
                    body: null,
                    state: null,
                    labels);
                return true;

            default:
                errorCode = "github-outbound-tool-unknown";
                return false;
        }
    }

    private static bool TryReadString(
        IReadOnlyDictionary<string, object?> arguments,
        string name,
        out string value)
    {
        value = string.Empty;
        if (!arguments.TryGetValue(name, out var raw))
        {
            return false;
        }

        value = raw switch
        {
            string text => text,
            JsonElement { ValueKind: JsonValueKind.String } element => element.GetString() ?? string.Empty,
            _ => string.Empty,
        };
        return value.Length > 0;
    }

    private static bool TryReadLabels(
        IReadOnlyDictionary<string, object?> arguments,
        out ImmutableArray<string> labels)
    {
        labels = [];
        if (!arguments.TryGetValue("labels", out var raw))
        {
            return false;
        }

        IEnumerable<string?>? values = raw switch
        {
            JsonElement { ValueKind: JsonValueKind.Array } element =>
                element.EnumerateArray().Select(item =>
                    item.ValueKind == JsonValueKind.String ? item.GetString() : null),
            IEnumerable<string> strings => strings,
            IEnumerable<object?> objects => objects.Select(item => item switch
            {
                string text => text,
                JsonElement { ValueKind: JsonValueKind.String } element => element.GetString(),
                _ => null,
            }),
            _ => null,
        };
        if (values is null)
        {
            return false;
        }

        var snapshot = values.ToArray();
        if (snapshot.Length > 100
            || snapshot.Any(label => string.IsNullOrWhiteSpace(label)
                || !string.Equals(label, label.Trim(), StringComparison.Ordinal)
                || Encoding.UTF8.GetByteCount(label) > 50))
        {
            return false;
        }

        var canonical = snapshot.Cast<string>()
            .OrderBy(label => label, StringComparer.Ordinal)
            .ToImmutableArray();
        if (canonical.Distinct(StringComparer.Ordinal).Count() != canonical.Length)
        {
            return false;
        }

        labels = canonical;
        return true;
    }

    private static string CanonicalStringPayload(string name, string value) =>
        JsonSerializer.Serialize(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                [name] = value,
            });

    private static string CanonicalLabelsPayload(ImmutableArray<string> labels) =>
        JsonSerializer.Serialize(
            new SortedDictionary<string, object?>(StringComparer.Ordinal)
            {
                ["labels"] = labels,
            });
}

internal sealed record GitHubIssuesOutboundRequest(
    string OperationKey,
    GitHubIssuesConnectorInstanceConfiguration Instance,
    GitHubIssueCorrelation Issue,
    GitHubIssuesOutboundOperation Operation);

internal sealed record GitHubIssuesOutboundClientResult
{
    private GitHubIssuesOutboundClientResult(
        bool succeeded,
        bool retryable,
        string? errorCode,
        string? receipt)
    {
        Succeeded = succeeded;
        Retryable = retryable;
        ErrorCode = errorCode;
        Receipt = receipt;
    }

    public bool Succeeded { get; }

    public bool Retryable { get; }

    public string? ErrorCode { get; }

    public string? Receipt { get; }

    public static GitHubIssuesOutboundClientResult Success(string receipt)
    {
        return new(true, false, null, RequireReceipt(receipt));
    }

    public static GitHubIssuesOutboundClientResult Failed(string errorCode, bool retryable)
    {
        return new(false, retryable, RequireCode(errorCode), null);
    }

    private static string RequireReceipt(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 512
            || value.Any(char.IsControl))
        {
            throw new ArgumentException(
                "GitHub outbound receipt must be a trimmed opaque value of at most 512 characters.",
                nameof(value));
        }

        return value;
    }

    private static string RequireCode(string value)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(value);
        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal)
            || value.Length > 100
            || value[0] == '-'
            || value[^1] == '-'
            || value.Any(character => character is not (>= 'a' and <= 'z')
                and not (>= '0' and <= '9')
                and not '-'))
        {
            throw new ArgumentException(
                "GitHub outbound error code must be a lowercase kebab-case token.",
                nameof(value));
        }

        return value;
    }
}

internal interface IGitHubIssuesOutboundClient
{
    Task<GitHubIssuesOutboundClientResult> ExecuteAsync(
        GitHubIssuesOutboundRequest request,
        CancellationToken cancellationToken = default);
}

internal sealed class UnavailableGitHubIssuesOutboundClient : IGitHubIssuesOutboundClient
{
    public static UnavailableGitHubIssuesOutboundClient Instance { get; } = new();

    private UnavailableGitHubIssuesOutboundClient()
    {
    }

    public Task<GitHubIssuesOutboundClientResult> ExecuteAsync(
        GitHubIssuesOutboundRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        return Task.FromResult(GitHubIssuesOutboundClientResult.Failed(
            "github-outbound-client-unavailable",
            retryable: true));
    }
}
