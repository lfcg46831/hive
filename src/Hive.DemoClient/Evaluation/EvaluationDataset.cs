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
    [property: JsonPropertyName("corpus_score")] double? CorpusScore = null,
    [property: JsonPropertyName("evaluation_plan_version")] int? EvaluationPlanVersion = null,
    [property: JsonPropertyName("freeze_id")] string? FreezeId = null,
    [property: JsonPropertyName("evaluation_partition")] string? EvaluationPartition = null,
    [property: JsonPropertyName("code_version")] string? CodeVersion = null,
    [property: JsonPropertyName("configuration_version")] string? ConfigurationVersion = null,
    [property: JsonPropertyName("run_analysis")] EvaluationRunAnalysis? RunAnalysis = null);

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
    [property: JsonPropertyName("scoring")] EvaluationCaseScoring? Scoring = null,
    [property: JsonPropertyName("invalid_output_diagnostics")]
    EvaluationInvalidOutputDiagnostics? InvalidOutputDiagnostics = null);

public sealed record EvaluationInvalidOutputDiagnostics(
    [property: JsonPropertyName("contract_version")] int ContractVersion,
    [property: JsonPropertyName("count")] int Count,
    [property: JsonPropertyName("errors")]
    IReadOnlyList<EvaluationInvalidOutputDiagnostic> Errors);

public sealed record EvaluationInvalidOutputDiagnostic(
    [property: JsonPropertyName("path")] string Path,
    [property: JsonPropertyName("code")] string Code);

public sealed record EvaluationPrediction(
    [property: JsonPropertyName("projection_version")] int ProjectionVersion,
    [property: JsonPropertyName("rubric_version")] int RubricVersion,
    [property: JsonPropertyName("dimensions")]
    IReadOnlyList<EvaluationDimensionPrediction> Dimensions);

public sealed record EvaluationDimensionPrediction(
    [property: JsonPropertyName("dimension_id")] string DimensionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("diagnostic_code")] string? DiagnosticCode = null);

public sealed record EvaluationCaseScoring(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failure_codes")] IReadOnlyList<string> FailureCodes,
    [property: JsonPropertyName("dimensions")]
    IReadOnlyList<EvaluationDimensionScoring> Dimensions,
    [property: JsonPropertyName("case_score")] double CaseScore);

public sealed record EvaluationDimensionScoring(
    [property: JsonPropertyName("dimension_id")] string DimensionId,
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("labels")] IReadOnlyList<string> Labels,
    [property: JsonPropertyName("score")] double Score);
