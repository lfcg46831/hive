using System.Text.Json.Serialization;

namespace Hive.DemoClient.Evaluation;

public sealed record EvaluationDataset(
    [property: JsonPropertyName("schema_version")] int SchemaVersion,
    [property: JsonPropertyName("corpus_version")] int CorpusVersion,
    [property: JsonPropertyName("run_id")] string RunId,
    [property: JsonPropertyName("api_base_url")] string ApiBaseUrl,
    [property: JsonPropertyName("timeout_seconds")] double TimeoutSeconds,
    [property: JsonPropertyName("poll_interval_milliseconds")] double PollIntervalMilliseconds,
    [property: JsonPropertyName("cases")] IReadOnlyList<EvaluationCaseResult> Cases,
    [property: JsonPropertyName("projection_version")] int? ProjectionVersion = null,
    [property: JsonPropertyName("rubric_version")] int? RubricVersion = null,
    [property: JsonPropertyName("corpus_score")] double? CorpusScore = null);

public sealed record EvaluationCaseResult(
    [property: JsonPropertyName("case_id")] string CaseId,
    [property: JsonPropertyName("message_id")] Guid MessageId,
    [property: JsonPropertyName("thread_id")] Guid ThreadId,
    [property: JsonPropertyName("directive_id")] Guid DirectiveId,
    [property: JsonPropertyName("submission_status")] string SubmissionStatus,
    [property: JsonPropertyName("http_status")] int? HttpStatus,
    [property: JsonPropertyName("outcome")] string Outcome,
    [property: JsonPropertyName("terminal_code")] string? TerminalCode,
    [property: JsonPropertyName("decision")] string? Decision,
    [property: JsonPropertyName("provider_id")] string? ProviderId,
    [property: JsonPropertyName("model_id")] string? ModelId,
    [property: JsonPropertyName("output_constraint_mode")] string? OutputConstraintMode,
    [property: JsonPropertyName("input_tokens")] int? InputTokens,
    [property: JsonPropertyName("output_tokens")] int? OutputTokens,
    [property: JsonPropertyName("total_tokens")] int? TotalTokens,
    [property: JsonPropertyName("tokens_estimated")] bool? TokensEstimated,
    [property: JsonPropertyName("cost_amount")] decimal? CostAmount,
    [property: JsonPropertyName("cost_currency")] string? CostCurrency,
    [property: JsonPropertyName("cost_estimated")] bool? CostEstimated,
    [property: JsonPropertyName("gateway_latency_ms")] long? GatewayLatencyMilliseconds,
    [property: JsonPropertyName("journey_duration_ms")] long? JourneyDurationMilliseconds,
    [property: JsonPropertyName("cost_status")] string? CostStatus = null,
    [property: JsonPropertyName("pricing_version")] string? PricingVersion = null,
    [property: JsonPropertyName("pricing_token_unit")] int? PricingTokenUnit = null,
    [property: JsonPropertyName("input_price_per_token_unit")] decimal? InputPricePerTokenUnit = null,
    [property: JsonPropertyName("output_price_per_token_unit")] decimal? OutputPricePerTokenUnit = null,
    [property: JsonPropertyName("prediction")] EvaluationPrediction? Prediction = null,
    [property: JsonPropertyName("scoring")] EvaluationCaseScoring? Scoring = null);

public sealed record EvaluationPrediction(
    [property: JsonPropertyName("projection_version")] int ProjectionVersion,
    [property: JsonPropertyName("severity")] string? Severity,
    [property: JsonPropertyName("missing_information")] IReadOnlyList<string>? MissingInformation);

public sealed record EvaluationCaseScoring(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failure_codes")] IReadOnlyList<string> FailureCodes,
    [property: JsonPropertyName("dimensions")] EvaluationDimensionScores Dimensions,
    [property: JsonPropertyName("case_score")] double CaseScore);

public sealed record EvaluationDimensionScores(
    [property: JsonPropertyName("severity")] double Severity,
    [property: JsonPropertyName("missing_information")] double MissingInformation,
    [property: JsonPropertyName("decision")] double Decision);
