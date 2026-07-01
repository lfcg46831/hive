using System.Collections;
using System.Text.RegularExpressions;
using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

internal static class AiGatewayDetailedAuditEnvelopeFactory
{
    private const string EmailReason = "email";
    private const string SecretReason = "secret";
    private const string SensitiveFieldReason = "sensitive-field";
    private const string RedactedEmail = "[redacted:email]";
    private const string RedactedSecret = "[redacted:secret]";

    private static readonly Regex EmailPattern = new(
        @"[A-Z0-9._%+\-]+@[A-Z0-9.\-]+\.[A-Z]{2,}",
        RegexOptions.IgnoreCase | RegexOptions.CultureInvariant | RegexOptions.Compiled);

    private static readonly Regex SecretPattern = new(
        @"(?i)\b(?:bearer\s+[A-Za-z0-9._~+\-/=]+|(?:api[_-]?key|token|secret|password)\s*[:=]\s*\S+|sk-[A-Za-z0-9_\-]{8,})",
        RegexOptions.CultureInvariant | RegexOptions.Compiled);

    public static AiGatewayAuditEnvelope FromResponse(
        AiGatewayRequest request,
        AiGatewayResponse response,
        DateTimeOffset startedAt,
        DateTimeOffset completedAt)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);

        var redactions = new List<AiGatewayAuditRedaction>();
        var auditRequest = CreateRequestSnapshot(request, redactions);

        if (response.IsSuccess)
        {
            var auditResponse = CreateResponseSnapshot(response, redactions);
            return new AiGatewayAuditEnvelope(
                response.OrganizationId,
                response.PositionId,
                response.ThreadId,
                response.MessageId,
                startedAt,
                completedAt,
                AiGatewayCallResult.Succeeded,
                auditRequest,
                response.Provider ?? request.Provider,
                auditResponse,
                usage: response.Usage,
                cost: response.Cost,
                redactions: redactions);
        }

        var error = response.Error!;
        var auditError = new AiGatewayAuditErrorSnapshot(
            error.Code,
            RedactText(error.Message, "error.message", key: null, redactions),
            error.IsRetryable,
            error.Provider ?? request.Provider);
        var rejectionReason = AiGatewayErrorCodeContract.ToWireValue(error.Code);

        return new AiGatewayAuditEnvelope(
            error.OrganizationId,
            error.PositionId,
            error.ThreadId,
            error.MessageId,
            startedAt,
            completedAt,
            AiGatewayCallResult.Failed,
            auditRequest,
            error.Provider ?? request.Provider,
            error: auditError,
            rejectionReason: rejectionReason,
            redactions: redactions);
    }

    private static AiGatewayAuditRequestSnapshot CreateRequestSnapshot(
        AiGatewayRequest request,
        List<AiGatewayAuditRedaction> redactions)
    {
        var contextMessages = request.ContextMessages
            .Select((message, index) => new AiGatewayMessage(
                message.Role,
                RedactText(
                    message.Content,
                    $"request.contextMessages[{index}].content",
                    key: null,
                    redactions)))
            .ToArray();
        var tools = request.Tools
            .Select((tool, index) => new AiToolDefinition(
                tool.Name,
                RedactText(
                    tool.Description,
                    $"request.tools[{index}].description",
                    key: null,
                    redactions),
                RedactData(
                    tool.ParametersSchema,
                    $"request.tools[{index}].parametersSchema",
                    redactions)))
            .ToArray();

        return new AiGatewayAuditRequestSnapshot(
            RedactText(request.Content, "request.content", key: null, redactions),
            RedactOptionalText(
                request.SystemInstruction,
                "request.systemInstruction",
                key: null,
                redactions),
            contextMessages,
            tools,
            request.ModelParameters,
            RedactMetadata(request.Metadata, "request.metadata", redactions),
            request.Provider,
            request.ProcessingMode,
            request.Timeout);
    }

    private static AiGatewayAuditResponseSnapshot CreateResponseSnapshot(
        AiGatewayResponse response,
        List<AiGatewayAuditRedaction> redactions)
    {
        var toolCalls = response.ToolCalls
            .Select((toolCall, index) => new AiToolCall(
                toolCall.Id,
                toolCall.Name,
                RedactData(
                    toolCall.Arguments,
                    $"response.toolCalls[{index}].arguments",
                    redactions)))
            .ToArray();

        return new AiGatewayAuditResponseSnapshot(
            RedactOptionalText(response.Text, "response.text", key: null, redactions),
            response.FinishReason!.Value,
            response.Provider,
            toolCalls);
    }

    private static IReadOnlyDictionary<string, string> RedactMetadata(
        IReadOnlyDictionary<string, string> metadata,
        string path,
        List<AiGatewayAuditRedaction> redactions)
    {
        var redacted = new Dictionary<string, string>(StringComparer.Ordinal);
        foreach (var (key, value) in metadata)
        {
            redacted[key] = RedactText(value, $"{path}.{key}", key, redactions);
        }

        return redacted;
    }

    private static IReadOnlyDictionary<string, object?> RedactData(
        IReadOnlyDictionary<string, object?> data,
        string path,
        List<AiGatewayAuditRedaction> redactions)
    {
        var redacted = new Dictionary<string, object?>(StringComparer.Ordinal);
        foreach (var (key, value) in data)
        {
            redacted[key] = RedactValue(value, $"{path}.{key}", key, redactions);
        }

        return redacted;
    }

    private static object? RedactValue(
        object? value,
        string path,
        string? key,
        List<AiGatewayAuditRedaction> redactions)
    {
        if (value is null)
        {
            return null;
        }

        if (value is string text)
        {
            return RedactText(text, path, key, redactions);
        }

        if (value is IReadOnlyDictionary<string, object?> data)
        {
            return RedactData(data, path, redactions);
        }

        if (value is IEnumerable sequence)
        {
            return RedactSequence(sequence, path, redactions);
        }

        return value;
    }

    private static IReadOnlyList<object?> RedactSequence(
        IEnumerable sequence,
        string path,
        List<AiGatewayAuditRedaction> redactions)
    {
        var redacted = new List<object?>();
        var index = 0;
        foreach (var item in sequence)
        {
            redacted.Add(RedactValue(
                item,
                $"{path}[{index}]",
                key: null,
                redactions));
            index++;
        }

        return redacted;
    }

    private static string? RedactOptionalText(
        string? value,
        string path,
        string? key,
        List<AiGatewayAuditRedaction> redactions) =>
        value is null ? null : RedactText(value, path, key, redactions);

    private static string RedactText(
        string value,
        string path,
        string? key,
        List<AiGatewayAuditRedaction> redactions)
    {
        if (IsSensitiveField(key))
        {
            AddRedaction(redactions, path, SensitiveFieldReason);
            return RedactedSecret;
        }

        var result = SecretPattern.Replace(value, RedactedSecret);
        if (!string.Equals(result, value, StringComparison.Ordinal))
        {
            AddRedaction(redactions, path, SecretReason);
        }

        var withoutEmail = EmailPattern.Replace(result, RedactedEmail);
        if (!string.Equals(withoutEmail, result, StringComparison.Ordinal))
        {
            AddRedaction(redactions, path, EmailReason);
        }

        return withoutEmail;
    }

    private static bool IsSensitiveField(string? key)
    {
        if (key is null)
        {
            return false;
        }

        var normalized = key.Replace("_", "-", StringComparison.Ordinal).ToLowerInvariant();
        return normalized is "authorization" or "api-key" or "apikey" or "x-api-key" or
            "token" or "access-token" or "refresh-token" or "secret" or "password" or
            "client-secret";
    }

    private static void AddRedaction(
        List<AiGatewayAuditRedaction> redactions,
        string path,
        string reason)
    {
        if (redactions.Any(redaction =>
            string.Equals(redaction.Path, path, StringComparison.Ordinal) &&
            string.Equals(redaction.Reason, reason, StringComparison.Ordinal)))
        {
            return;
        }

        redactions.Add(new AiGatewayAuditRedaction(path, reason));
    }
}
