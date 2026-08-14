using System.Collections.Immutable;
using Hive.Domain.Ai;
using Hive.Domain.Identity;

namespace Hive.Infrastructure.Connectors;

/// <summary>
/// Runtime-owned invocation of one connector tool after authorization. The operation key and
/// organizational correlation are supplied by HIVE and never come from model arguments.
/// </summary>
public sealed record ConnectorToolInvocation
{
    public ConnectorToolInvocation(
        string operationKey,
        OrganizationId organizationId,
        PositionId positionId,
        ThreadId threadId,
        MessageId sourceMessageId,
        DirectiveId directiveId,
        DirectiveId? parentDirectiveId,
        AiToolCall toolCall)
    {
        if (string.IsNullOrWhiteSpace(operationKey)
            || operationKey.Length != 64
            || operationKey.Any(character => character is not (>= '0' and <= '9')
                and not (>= 'a' and <= 'f')))
        {
            throw new ArgumentException(
                "Connector operation key must be a lowercase SHA-256 digest.",
                nameof(operationKey));
        }

        OperationKey = operationKey;
        OrganizationId = organizationId ?? throw new ArgumentNullException(nameof(organizationId));
        PositionId = positionId ?? throw new ArgumentNullException(nameof(positionId));
        ThreadId = threadId ?? throw new ArgumentNullException(nameof(threadId));
        SourceMessageId = sourceMessageId ?? throw new ArgumentNullException(nameof(sourceMessageId));
        DirectiveId = directiveId ?? throw new ArgumentNullException(nameof(directiveId));
        ParentDirectiveId = parentDirectiveId;
        ToolCall = toolCall ?? throw new ArgumentNullException(nameof(toolCall));
    }

    public string OperationKey { get; }

    public OrganizationId OrganizationId { get; }

    public PositionId PositionId { get; }

    public ThreadId ThreadId { get; }

    public MessageId SourceMessageId { get; }

    public DirectiveId DirectiveId { get; }

    public DirectiveId? ParentDirectiveId { get; }

    public AiToolCall ToolCall { get; }
}

/// <summary>Sanitized result returned across the internal connector-tool boundary.</summary>
public sealed record ConnectorToolResult
{
    private ConnectorToolResult(
        ImmutableDictionary<string, object?> output,
        string? errorCode,
        bool retryable)
    {
        Output = output;
        ErrorCode = errorCode;
        Retryable = retryable;
    }

    public IReadOnlyDictionary<string, object?> Output { get; }

    public string? ErrorCode { get; }

    public bool Retryable { get; }

    public bool IsSuccess => ErrorCode is null;

    public static ConnectorToolResult Succeeded(
        IReadOnlyDictionary<string, object?>? output = null) =>
        new(Snapshot(output), errorCode: null, retryable: false);

    public static ConnectorToolResult Failed(string errorCode, bool retryable = false) =>
        new(Empty, RequireCode(errorCode), retryable);

    private static ImmutableDictionary<string, object?> Empty { get; } =
        ImmutableDictionary<string, object?>.Empty.WithComparers(StringComparer.Ordinal);

    private static ImmutableDictionary<string, object?> Snapshot(
        IReadOnlyDictionary<string, object?>? values)
    {
        if (values is null || values.Count == 0)
        {
            return Empty;
        }

        var builder = ImmutableDictionary.CreateBuilder<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in values)
        {
            if (string.IsNullOrWhiteSpace(key) || !string.Equals(key, key.Trim(), StringComparison.Ordinal))
            {
                throw new ArgumentException("Connector tool output keys must be trimmed and non-empty.", nameof(values));
            }

            builder.Add(key, value);
        }

        return builder.ToImmutable();
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
                "Connector tool error code must be a lowercase kebab-case token.",
                nameof(value));
        }

        return value;
    }
}

/// <summary>One executable internal tool contributed by a connector plugin.</summary>
public interface IConnectorTool
{
    AiToolDefinition Definition { get; }

    ValueTask<ConnectorToolResult> ExecuteAsync(
        ConnectorToolInvocation invocation,
        CancellationToken cancellationToken = default);
}

/// <summary>Validated registry composed only from configured connector plugins.</summary>
public interface IConnectorToolRegistry
{
    IConnectorTool? Find(string name);
}

public sealed class ConnectorToolRegistry : IConnectorToolRegistry
{
    private readonly ImmutableDictionary<string, IConnectorTool> _tools;

    public ConnectorToolRegistry(IEnumerable<IConnectorTool> tools)
    {
        ArgumentNullException.ThrowIfNull(tools);
        var snapshot = tools.ToArray();
        if (snapshot.Any(tool => tool is null || tool.Definition is null))
        {
            throw new ArgumentException(
                "Connector tools and their definitions cannot contain null entries.",
                nameof(tools));
        }

        var duplicate = snapshot
            .GroupBy(tool => tool.Definition.Name, StringComparer.Ordinal)
            .FirstOrDefault(group => group.Count() > 1);
        if (duplicate is not null)
        {
            throw new InvalidOperationException(
                $"Connector tool '{duplicate.Key}' is registered more than once.");
        }

        _tools = snapshot.ToImmutableDictionary(
            tool => tool.Definition.Name,
            StringComparer.Ordinal);
    }

    public IConnectorTool? Find(string name)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(name);
        return _tools.GetValueOrDefault(name);
    }
}
