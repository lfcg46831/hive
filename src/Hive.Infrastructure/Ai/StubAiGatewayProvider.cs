using Hive.Domain.Ai;
using Microsoft.Extensions.Options;

namespace Hive.Infrastructure.Ai;

internal sealed class StubAiGatewayProvider : IAiGatewayProvider
{
    private const string OutcomeSuccess = "success";
    private const string OutcomeError = "error";
    private const string OutcomeTimeout = "timeout";
    private const string OutcomeToolCall = "tool-call";
    private const string ScenarioBugTriageReport = "bug-triage-report";
    private const string ScenarioBugTriageMissingInformation =
        "bug-triage-missing-information";
    private const string ScenarioBugTriageExternalDecisionBlocked =
        "bug-triage-external-decision-blocked";
    private const string ScenarioProviderControlledFailure =
        "provider-controlled-failure";

    private const string BugTriageReportText =
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "acting_under": "delivery.bug-triage",
          "report": {
            "kind": "Done",
            "body": "Bug triage complete: checkout confirmation failures are reproducible with high user impact."
          }
        }
        """;

    private const string BugTriageMissingInformationText =
        """
        {
          "schema_version": 1,
          "intent": "Escalation",
          "acting_under": "delivery.bug-triage",
          "escalation": {
            "issue": "Missing bug triage information",
            "context": "The report lacks enough reproduction or environment evidence to complete triage deterministically.",
            "options_considered": [
              "Proceed with the partial report",
              "Request reproduction steps and affected environment"
            ]
          }
        }
        """;

    private const string BugTriageExternalDecisionBlockedText =
        """
        {
          "schema_version": 1,
          "intent": "Escalation",
          "acting_under": "delivery.bug-triage",
          "escalation": {
            "issue": "External decision required",
            "context": "The next action depends on an external production or customer-impact decision outside the triage position authority.",
            "options_considered": [
              "Classify only the technical symptoms",
              "Escalate for an accountable external decision"
            ]
          }
        }
        """;

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
        var scenario = OptionalScenario(options.Scenario);
        if (scenario is not null)
        {
            return Task.FromResult(CreateScenarioResponse(
                request,
                options,
                provider,
                scenario));
        }

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

    private static AiGatewayResponse CreateScenarioResponse(
        AiGatewayRequest request,
        StubAiGatewayProviderOptions options,
        AiProviderMetadata provider,
        string scenario) =>
        scenario switch
        {
            ScenarioBugTriageReport => CreateScenarioSuccess(
                request,
                options,
                provider,
                BugTriageReportText),
            ScenarioBugTriageMissingInformation => CreateScenarioSuccess(
                request,
                options,
                provider,
                BugTriageMissingInformationText),
            ScenarioBugTriageExternalDecisionBlocked => CreateScenarioSuccess(
                request,
                options,
                provider,
                BugTriageExternalDecisionBlockedText),
            ScenarioProviderControlledFailure => CreateProviderControlledFailure(
                request,
                provider),
            _ => throw new ArgumentException(
                $"AI gateway stub scenario '{options.Scenario}' is not supported.",
                nameof(options.Scenario)),
        };

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

    private static AiGatewayResponse CreateScenarioSuccess(
        AiGatewayRequest request,
        StubAiGatewayProviderOptions options,
        AiProviderMetadata provider,
        string text) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            text,
            AiFinishReason.Stop,
            provider,
            usage: CreateUsage(options.Usage),
            cost: CreateCost(options.Cost));

    private static AiGatewayResponse CreateProviderControlledFailure(
        AiGatewayRequest request,
        AiProviderMetadata provider) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            AiGatewayErrorCode.ProviderUnavailable,
            "AI gateway stub returned a controlled provider failure.",
            isRetryable: true,
            provider));

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

    private static string? OptionalScenario(string? value)
    {
        if (value is null || value.Length == 0)
        {
            return null;
        }

        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException(
                "AI gateway stub scenario cannot be whitespace.",
                nameof(value));
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "AI gateway stub scenario cannot contain leading or trailing whitespace.",
                nameof(value));
        }

        return value;
    }

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
