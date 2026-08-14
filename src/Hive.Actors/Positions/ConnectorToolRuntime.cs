using System.Security.Cryptography;
using System.Text;
using System.Text.Json;
using Hive.Domain.Ai;
using Hive.Domain.Positions;
using Hive.Infrastructure.Connectors;

namespace Hive.Actors.Positions;

internal sealed class ConnectorToolRuntime
    : IAiDirectiveConnectorToolExecutor, IRetainedActionExecutor
{
    private readonly IConnectorToolRegistry _registry;

    public ConnectorToolRuntime(IConnectorToolRegistry registry)
    {
        _registry = registry ?? throw new ArgumentNullException(nameof(registry));
    }

    public async ValueTask<AiDirectiveConnectorToolExecutionResult> ExecuteAsync(
        AiDirectiveConnectorToolExecution execution,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(execution);
        var context = execution.Context;
        var invocation = CreateInvocation(
            context.OrganizationId,
            context.PositionId,
            context.Directive.ThreadId,
            context.Directive.MessageId,
            context.Directive.DirectiveId,
            context.Directive.ParentDirectiveId,
            execution.ToolCall);
        var result = await InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        if (result.IsSuccess)
        {
            return AiDirectiveConnectorToolExecutionResult.Succeeded(execution, result.Output);
        }

        return AiDirectiveConnectorToolExecutionResult.Failed(
            execution,
            new AiDirectiveIterationExecutionFailure(
                result.ErrorCode!,
                $"Connector tool '{execution.ToolCall.Name}' returned structured failure '{result.ErrorCode}'."));
    }

    public async ValueTask<RetainedActionExecutionResult> ExecuteAsync(
        RetainedActionExecutionRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        if (request.Action.Kind != RetainedActionKind.Tool
            || !TryReadToolCall(request.Action.CanonicalPayload, out var toolCall)
            || toolCall is null
            || !string.Equals(request.Action.Selector, toolCall.Name, StringComparison.Ordinal))
        {
            return RetainedActionExecutionResult.Failed("retained-tool-payload-invalid");
        }

        var action = request.Action;
        var invocation = CreateInvocation(
            action.OrganizationId,
            action.PositionId,
            action.ThreadId,
            action.SourceMessageId,
            action.DirectiveId,
            action.ParentDirectiveId,
            toolCall!);
        var result = await InvokeAsync(invocation, cancellationToken).ConfigureAwait(false);
        return result.IsSuccess
            ? RetainedActionExecutionResult.Success()
            : RetainedActionExecutionResult.Failed(result.ErrorCode!);
    }

    private async ValueTask<ConnectorToolResult> InvokeAsync(
        ConnectorToolInvocation invocation,
        CancellationToken cancellationToken)
    {
        var tool = _registry.Find(invocation.ToolCall.Name);
        if (tool is null)
        {
            return ConnectorToolResult.Failed("connector-tool-unavailable");
        }

        try
        {
            return await tool.ExecuteAsync(invocation, cancellationToken).ConfigureAwait(false)
                ?? ConnectorToolResult.Failed("connector-tool-result-invalid");
        }
        catch (OperationCanceledException)
        {
            throw;
        }
        catch (Exception)
        {
            return ConnectorToolResult.Failed("connector-tool-execution-failed");
        }
    }

    private static ConnectorToolInvocation CreateInvocation(
        Hive.Domain.Identity.OrganizationId organizationId,
        Hive.Domain.Identity.PositionId positionId,
        Hive.Domain.Identity.ThreadId threadId,
        Hive.Domain.Identity.MessageId sourceMessageId,
        Hive.Domain.Identity.DirectiveId directiveId,
        Hive.Domain.Identity.DirectiveId? parentDirectiveId,
        AiToolCall toolCall) =>
        new(
            ConnectorToolOperationKey.Create(organizationId.Value, directiveId.Value, toolCall),
            organizationId,
            positionId,
            threadId,
            sourceMessageId,
            directiveId,
            parentDirectiveId,
            toolCall);

    private static bool TryReadToolCall(string canonicalPayload, out AiToolCall? toolCall)
    {
        toolCall = null;
        try
        {
            using var document = JsonDocument.Parse(canonicalPayload);
            var root = document.RootElement;
            if (root.ValueKind != JsonValueKind.Object
                || !TryReadText(root, "Id", out var id)
                || !TryReadText(root, "Name", out var name)
                || !root.TryGetProperty("Arguments", out var arguments)
                || arguments.ValueKind != JsonValueKind.Object)
            {
                return false;
            }

            var values = arguments.EnumerateObject().ToDictionary(
                property => property.Name,
                property => (object?)property.Value.Clone(),
                StringComparer.Ordinal);
            toolCall = new AiToolCall(id!, name!, values);
            return true;
        }
        catch (Exception exception) when (exception is JsonException or ArgumentException)
        {
            return false;
        }
    }

    private static bool TryReadText(JsonElement element, string propertyName, out string? value)
    {
        value = null;
        return element.TryGetProperty(propertyName, out var property)
            && property.ValueKind == JsonValueKind.String
            && !string.IsNullOrWhiteSpace(value = property.GetString());
    }
}

internal static class ConnectorToolOperationKey
{
    public static string Create(string organizationId, Guid directiveId, AiToolCall toolCall)
    {
        ArgumentException.ThrowIfNullOrWhiteSpace(organizationId);
        ArgumentNullException.ThrowIfNull(toolCall);
        var material = string.Join(
            '|',
            "hive-connector-operation-v1",
            Segment(organizationId),
            directiveId.ToString("N"),
            Segment(toolCall.Id),
            Segment(toolCall.Name));
        return Convert.ToHexString(SHA256.HashData(Encoding.UTF8.GetBytes(material)))
            .ToLowerInvariant();
    }

    private static string Segment(string value) => $"{value.Length}:{value}";
}
