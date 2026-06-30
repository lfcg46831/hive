using Hive.Domain.Ai;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Ai;

internal sealed class StubAiGatewayProvider : IAiGatewayProvider
{
    private const string OutcomeSuccess = "success";
    private const string OutcomeError = "error";
    private const string OutcomeTimeout = "timeout";
    private const string OutcomeToolCall = "tool-call";

    private readonly IOptions<StubAiGatewayProviderOptions> _options;

    public StubAiGatewayProvider(IOptions<StubAiGatewayProviderOptions> options)
    {
        _options = options ?? throw new ArgumentNullException(nameof(options));
    }

    public Task<AiGatewayResponse> CompleteAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();

        var options = _options.Value ?? new StubAiGatewayProviderOptions();
        var provider = request.Provider ?? CreateProvider(options);
        var outcome = RequireOutcome(options.Outcome);

        var response = outcome switch
        {
            OutcomeSuccess => CreateSuccess(request, options, provider),
            OutcomeError => CreateError(request, options, provider),
            OutcomeTimeout => CreateTimeout(request, options, provider),
            OutcomeToolCall => CreateToolCallResponse(request, options, provider),
            _ => throw new ArgumentException(
                $"AI gateway stub outcome '{options.Outcome}' is not supported.",
                nameof(options.Outcome)),
        };

        return Task.FromResult(response);
    }

    private static AiProviderMetadata CreateProvider(
        StubAiGatewayProviderOptions options) =>
        new(options.ProviderId, options.ModelId);

    private static AiGatewayResponse CreateSuccess(
        AiGatewayRequest request,
        StubAiGatewayProviderOptions options,
        AiProviderMetadata provider) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            OptionalConfiguredText(options.Text),
            AiFinishReasonContract.ParseWireValue(options.FinishReason),
            provider,
            usage: CreateUsage(options.Usage),
            cost: CreateCost(options.Cost));

    private static AiGatewayResponse CreateError(
        AiGatewayRequest request,
        StubAiGatewayProviderOptions options,
        AiProviderMetadata provider)
    {
        var errorOptions = options.Error ?? new StubAiGatewayErrorOptions();
        var code = string.IsNullOrWhiteSpace(errorOptions.Code)
            ? AiGatewayErrorCode.ProviderRejected
            : AiGatewayErrorCodeContract.ParseWireValue(errorOptions.Code);
        var message = string.IsNullOrWhiteSpace(errorOptions.Message)
            ? "AI gateway stub returned a configured error."
            : errorOptions.Message;

        return AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            code,
            message,
            errorOptions.IsRetryable ?? false,
            provider));
    }

    private static AiGatewayResponse CreateTimeout(
        AiGatewayRequest request,
        StubAiGatewayProviderOptions options,
        AiProviderMetadata provider)
    {
        var errorOptions = options.Error ?? new StubAiGatewayErrorOptions();
        var message = string.IsNullOrWhiteSpace(errorOptions.Message)
            ? "AI gateway stub timed out."
            : errorOptions.Message;

        return AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.Timeout,
            message,
            errorOptions.IsRetryable ?? true,
            provider));
    }

    private static AiGatewayResponse CreateToolCallResponse(
        AiGatewayRequest request,
        StubAiGatewayProviderOptions options,
        AiProviderMetadata provider)
    {
        var toolCall = CreateToolCall(options.ToolCall);

        return AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            OptionalConfiguredText(options.Text),
            AiFinishReason.ToolCalls,
            provider,
            [toolCall],
            CreateUsage(options.Usage),
            CreateCost(options.Cost));
    }

    private static AiToolCall CreateToolCall(
        StubAiGatewayToolCallOptions? options)
    {
        var toolCallOptions = options ?? new StubAiGatewayToolCallOptions();
        var configuredArguments =
            toolCallOptions.Arguments ?? new Dictionary<string, string>();
        var arguments = configuredArguments.ToDictionary(
            pair => pair.Key,
            pair => (object?)pair.Value,
            StringComparer.Ordinal);

        return new AiToolCall(
            toolCallOptions.Id,
            toolCallOptions.Name,
            arguments);
    }

    private static AiTokenUsage? CreateUsage(
        StubAiGatewayUsageOptions? options) =>
        options is null
            ? null
            : new AiTokenUsage(
                options.InputTokens,
                options.OutputTokens,
                options.TotalTokens,
                options.IsEstimated);

    private static AiCostMetadata? CreateCost(
        StubAiGatewayCostOptions? options) =>
        options is null
            ? null
            : new AiCostMetadata(
                options.Amount,
                options.Currency,
                options.IsEstimated);

    private static string? OptionalConfiguredText(string? value) =>
        value == string.Empty ? null : value;

    private static string RequireOutcome(string value)
    {
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "AI gateway stub outcome cannot be empty.",
                nameof(value));
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AI gateway stub outcome cannot contain leading or trailing whitespace.",
                nameof(value));
        }

        return value;
    }
}
