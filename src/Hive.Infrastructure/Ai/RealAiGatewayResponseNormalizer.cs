using System.Globalization;
using System.Text.Json;
using Hive.Domain.Ai;
using Microsoft.Extensions.AI;

namespace Hive.Infrastructure.Ai;

internal sealed class RealAiGatewayResponseNormalizer
{
    private const string CostAmountKey = "hive.cost.amount";
    private const string CostCurrencyKey = "hive.cost.currency";
    private const string CostIsEstimatedKey = "hive.cost.isEstimated";

    public NormalizedAiGatewayProviderResponse Normalize(
        AiGatewayRequest request,
        ChatResponse response,
        RealAiGatewayProviderSettings settings)
    {
        ArgumentNullException.ThrowIfNull(request);
        ArgumentNullException.ThrowIfNull(response);
        ArgumentNullException.ThrowIfNull(settings);

        var rawResponse = CaptureRawResponse(response, settings, request.Provider);

        List<AiToolCall> toolCalls;
        try
        {
            toolCalls = response.Messages
                .SelectMany(message => message.Contents)
                .OfType<FunctionCallContent>()
                .Select(MapToolCall)
                .ToList();
        }
        catch (ArgumentException ex)
        {
            return new(
                InvalidProviderResponse(request, settings, ex.Message),
                rawResponse);
        }

        var text = NormalizeText(response.Text);

        if (text is null && toolCalls.Count == 0)
        {
            return new(
                InvalidProviderResponse(
                    request,
                    settings,
                    "AI gateway real provider returned neither text nor a tool call."),
                rawResponse);
        }

        if (!TryMapCost(response.AdditionalProperties, out var cost, out var costError))
        {
            return new(
                InvalidProviderResponse(request, settings, costError!),
                rawResponse);
        }

        return new(
            AiGatewayResponse.Succeeded(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                text,
                MapFinishReason(response.FinishReason, toolCalls.Count > 0),
                ResolveProviderMetadata(response, settings, request.Provider),
                toolCalls.Count == 0 ? null : toolCalls,
                MapUsage(response.Usage),
                cost),
            rawResponse);
    }

    private static string? NormalizeText(string? text) =>
        string.IsNullOrWhiteSpace(text) ? null : text;

    private static AiToolCall MapToolCall(FunctionCallContent content)
    {
        IReadOnlyDictionary<string, object?>? arguments = content.Arguments is null
            ? null
            : new Dictionary<string, object?>(content.Arguments, StringComparer.Ordinal);

        return new AiToolCall(content.CallId, content.Name, arguments);
    }

    private static AiProviderMetadata ResolveProviderMetadata(
        ChatResponse response,
        RealAiGatewayProviderSettings settings,
        AiProviderMetadata? requestProvider)
    {
        var metadata = new Dictionary<string, string>(StringComparer.Ordinal);

        if (!string.IsNullOrWhiteSpace(response.ResponseId))
        {
            metadata["response-id"] = response.ResponseId;
        }

        return new AiProviderMetadata(
            requestProvider?.ProviderId ?? settings.DefaultProvider.ProviderId,
            string.IsNullOrWhiteSpace(response.ModelId)
                ? requestProvider?.ModelId ?? settings.DefaultProvider.ModelId
                : response.ModelId,
            metadata.Count == 0 ? null : metadata);
    }

    private static AiFinishReason MapFinishReason(
        ChatFinishReason? finishReason,
        bool hasToolCalls)
    {
        if (finishReason is { } reason)
        {
            if (reason == ChatFinishReason.Stop)
            {
                return AiFinishReason.Stop;
            }

            if (reason == ChatFinishReason.Length)
            {
                return AiFinishReason.Length;
            }

            if (reason == ChatFinishReason.ToolCalls)
            {
                return AiFinishReason.ToolCalls;
            }

            if (reason == ChatFinishReason.ContentFilter)
            {
                return AiFinishReason.ContentFiltered;
            }

            return AiFinishReason.Unknown;
        }

        return hasToolCalls ? AiFinishReason.ToolCalls : AiFinishReason.Stop;
    }

    private static AiTokenUsage? MapUsage(UsageDetails? usage)
    {
        if (usage is null)
        {
            return null;
        }

        var inputTokens = ToInt(usage.InputTokenCount);
        var outputTokens = ToInt(usage.OutputTokenCount);
        var totalTokens = ToInt(usage.TotalTokenCount);

        if (inputTokens is null && outputTokens is null && totalTokens is null)
        {
            return null;
        }

        return new AiTokenUsage(inputTokens, outputTokens, totalTokens);
    }

    private static int? ToInt(long? value)
    {
        if (value is not { } count || count < 0)
        {
            return null;
        }

        return count > int.MaxValue ? int.MaxValue : (int)count;
    }

    private static bool TryMapCost(
        AdditionalPropertiesDictionary? properties,
        out AiCostMetadata? cost,
        out string? error)
    {
        cost = null;
        error = null;

        if (properties is null)
        {
            return true;
        }

        var hasAmount = properties.TryGetValue(CostAmountKey, out var amountValue);
        var hasCurrency = properties.TryGetValue(CostCurrencyKey, out var currencyValue);
        var hasIsEstimated = properties.TryGetValue(
            CostIsEstimatedKey,
            out var isEstimatedValue);

        if (!hasAmount && !hasCurrency && !hasIsEstimated)
        {
            return true;
        }

        if (!hasAmount || !hasCurrency || !hasIsEstimated)
        {
            error = "AI gateway real provider returned incomplete cost metadata.";
            return false;
        }

        if (!TryReadDecimal(amountValue, out var amount) || amount < 0)
        {
            error = "AI gateway real provider returned invalid cost amount metadata.";
            return false;
        }

        if (currencyValue is not string currency ||
            string.IsNullOrWhiteSpace(currency) ||
            !string.Equals(currency, currency.Trim(), StringComparison.Ordinal))
        {
            error = "AI gateway real provider returned invalid cost currency metadata.";
            return false;
        }

        if (!TryReadBool(isEstimatedValue, out var isEstimated))
        {
            error = "AI gateway real provider returned invalid cost estimation metadata.";
            return false;
        }

        cost = new AiCostMetadata(amount, currency, isEstimated);
        return true;
    }

    private static bool TryReadDecimal(object? value, out decimal amount)
    {
        switch (value)
        {
            case decimal decimalValue:
                amount = decimalValue;
                return true;
            case double doubleValue:
                return TryConvertFloatingPoint(doubleValue, out amount);
            case float floatValue:
                return TryConvertFloatingPoint(floatValue, out amount);
            case int intValue:
                amount = intValue;
                return true;
            case long longValue:
                amount = longValue;
                return true;
            case string text:
                return decimal.TryParse(
                    text,
                    NumberStyles.Number,
                    CultureInfo.InvariantCulture,
                    out amount);
            default:
                amount = default;
                return false;
        }
    }

    private static bool TryReadBool(object? value, out bool result)
    {
        switch (value)
        {
            case bool boolValue:
                result = boolValue;
                return true;
            case string text:
                return bool.TryParse(text, out result);
            default:
                result = default;
                return false;
        }
    }

    private static bool TryConvertFloatingPoint(double value, out decimal amount)
    {
        if (!double.IsFinite(value))
        {
            amount = default;
            return false;
        }

        try
        {
            amount = (decimal)value;
            return true;
        }
        catch (OverflowException)
        {
            amount = default;
            return false;
        }
    }

    private static AiGatewayResponse InvalidProviderResponse(
        AiGatewayRequest request,
        RealAiGatewayProviderSettings settings,
        string message) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.InvalidProviderResponse,
            message,
            isRetryable: false,
            new AiProviderMetadata(
                request.Provider?.ProviderId ?? settings.DefaultProvider.ProviderId,
                request.Provider?.ModelId ?? settings.DefaultProvider.ModelId)));

    private static RedactableAiGatewayProviderResponse CaptureRawResponse(
        ChatResponse response,
        RealAiGatewayProviderSettings settings,
        AiProviderMetadata? requestProvider)
    {
        var additionalProperties = new Dictionary<string, string>(StringComparer.Ordinal);

        if (response.AdditionalProperties is not null)
        {
            foreach (var (key, value) in response.AdditionalProperties)
            {
                additionalProperties[key] = ToRedactableScalar(value) ?? "(null)";
            }
        }

        return new RedactableAiGatewayProviderResponse(
            requestProvider?.ProviderId ?? settings.DefaultProvider.ProviderId,
            string.IsNullOrWhiteSpace(response.ModelId)
                ? requestProvider?.ModelId ?? settings.DefaultProvider.ModelId
                : response.ModelId,
            response.ResponseId,
            ToRedactableScalar(response.RawRepresentation),
            additionalProperties,
            response.Messages
                .SelectMany(message => message.Contents)
                .Select(content => content.GetType().Name)
                .Distinct(StringComparer.Ordinal));
    }

    private static string? ToRedactableScalar(object? value)
    {
        switch (value)
        {
            case null:
                return null;
            case string text:
                return text;
            case JsonElement json:
                return json.GetRawText();
            case bool boolValue:
                return boolValue.ToString(CultureInfo.InvariantCulture);
            case char charValue:
                return charValue.ToString();
            case byte or sbyte or short or ushort or int or uint or long or ulong:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            case float or double or decimal:
                return Convert.ToString(value, CultureInfo.InvariantCulture);
            case DateTime dateTime:
                return dateTime.ToString("O", CultureInfo.InvariantCulture);
            case DateTimeOffset dateTimeOffset:
                return dateTimeOffset.ToString("O", CultureInfo.InvariantCulture);
            case Guid guid:
                return guid.ToString("D");
            default:
                return $"{value.GetType().FullName} (redaction-required)";
        }
    }
}
