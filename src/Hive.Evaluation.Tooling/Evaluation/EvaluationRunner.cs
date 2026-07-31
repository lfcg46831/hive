using System.Net.Http.Json;
using System.Text.Json;

namespace Hive.Evaluation.Tooling.Evaluation;

public sealed class EvaluationRunner
{
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IEvaluationAuditReader _auditReader;
    private readonly IEvaluationProjectionReader _projectionReader;
    private readonly EvaluationRubric? _rubric;
    private readonly TimeProvider _timeProvider;

    public EvaluationRunner(
        HttpClient httpClient,
        IEvaluationAuditReader auditReader,
        TimeProvider? timeProvider = null,
        IEvaluationProjectionReader? projectionReader = null,
        EvaluationRubric? rubric = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _auditReader = auditReader ?? throw new ArgumentNullException(nameof(auditReader));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _projectionReader = projectionReader ?? NoopEvaluationProjectionReader.Instance;
        _rubric = rubric;
    }

    public async Task<EvaluationDataset> RunAsync(
        EvaluationCorpus corpus,
        EvaluationRunOptions options,
        CancellationToken cancellationToken)
    {
        var orderedCases = corpus.Cases
            .OrderBy(item => item.CaseId, StringComparer.Ordinal)
            .ToArray();
        var results = new List<EvaluationCaseResult>(orderedCases.Length);
        for (var index = 0; index < orderedCases.Length; index++)
        {
            var item = orderedCases[index];
            results.Add(await RunCaseAsync(corpus.CorpusVersion, item, index, options, cancellationToken)
                .ConfigureAwait(false));
        }

        double? corpusScore = _rubric is null
            ? null
            : _rubric.ScoreCorpus(results.Select(item => item.Scoring!).ToArray());
        var dataset = new EvaluationDataset(
            1,
            corpus.CorpusVersion,
            options.RunId,
            options.BaseUrl.AbsoluteUri.TrimEnd('/'),
            options.Timeout.TotalSeconds,
            options.PollInterval.TotalMilliseconds,
            results,
            _rubric is null ? null : 1,
            _rubric?.RubricVersion,
            corpusScore);
        if (options.Experiment is { } experiment)
        {
            dataset = dataset with
            {
                ExperimentManifestVersion = experiment.ManifestVersion,
                ExperimentId = experiment.ExperimentId,
                ExperimentManifestSha256 = experiment.ManifestSha256,
                EffectiveConfigurationSha256 =
                    experiment.EffectiveConfigurationSha256,
                EffectiveConfigurationValidation =
                    EvaluationExperimentValidator.Validate(results, experiment),
            };
            dataset = dataset with
            {
                RunAnalysis = EvaluationRunAnalyzer.Analyze(
                    corpus,
                    dataset,
                    experiment),
            };
        }

        if (options.Plan is null || options.Partition is null)
        {
            return dataset;
        }

        dataset = dataset with
        {
            EvaluationPlanVersion = options.Plan.PlanVersion,
            FreezeId = options.Plan.FreezeId,
            EvaluationPartition = options.Partition,
            CodeVersion = options.Plan.CodeVersion,
            ConfigurationVersion = options.Plan.ConfigurationVersion,
        };
        return dataset with
        {
            RunAnalysis = EvaluationRunAnalyzer.Analyze(
                corpus,
                dataset,
                options.Plan,
                options.Partition),
        };
    }

    private async Task<EvaluationCaseResult> RunCaseAsync(
        int corpusVersion,
        EvaluationCase item,
        int index,
        EvaluationRunOptions options,
        CancellationToken cancellationToken)
    {
        var ids = EvaluationDirectiveIds.FromSeed(
            $"evaluation:v{corpusVersion}:{options.RunId}:{item.CaseId}");
        var submission = EvaluationDirectiveFactory.Create(
            ids,
            options.SentAt.AddSeconds(index),
            item.Context,
            _rubric?.BuildObservationInstruction(),
            options.Experiment);

        int? httpStatus = null;
        try
        {
            using var response = await _httpClient.PostAsJsonAsync(
                new Uri(options.BaseUrl, submission.RelativePath),
                submission.Request,
                JsonOptions,
                cancellationToken).ConfigureAwait(false);
            httpStatus = (int)response.StatusCode;
            if (!response.IsSuccessStatusCode)
            {
                return Empty(item, ids, "rejected", httpStatus, "submission-rejected");
            }
        }
        catch (HttpRequestException)
        {
            return Empty(item, ids, "failed", null, "submission-unavailable");
        }

        var deadline = _timeProvider.GetUtcNow() + options.Timeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            var journey = await _auditReader.ReadAsync(
                submission.OrganizationId,
                ids.ThreadId,
                ids.DirectiveId,
                cancellationToken).ConfigureAwait(false);
            if (journey is not null)
            {
                var prediction = await _projectionReader.ReadAsync(
                    submission.OrganizationId,
                    ids.ThreadId,
                    ids.DirectiveId,
                    cancellationToken).ConfigureAwait(false);
                return FromJourney(item, ids, httpStatus, journey, prediction);
            }

            await Task.Delay(options.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return Empty(item, ids, "accepted", httpStatus, "timeout");
    }

    private EvaluationCaseResult FromJourney(
        EvaluationCase item,
        EvaluationDirectiveIds ids,
        int? httpStatus,
        EvaluationJourney journey,
        EvaluationPrediction? prediction)
    {
        var normalizedPrediction = _rubric?.NormalizePrediction(prediction) ?? prediction;
        return new EvaluationCaseResult(
            item.CaseId, ids.MessageId, ids.ThreadId, ids.DirectiveId,
            "accepted", httpStatus, journey.Outcome, journey.TerminalCode, journey.Decision,
            journey.ProviderId, journey.ModelId, journey.OutputConstraintMode,
            journey.InputTokens, journey.OutputTokens,
            journey.TotalTokens, journey.TokensEstimated, journey.CostAmount,
            journey.CostCurrency, journey.CostEstimated, journey.GatewayLatencyMilliseconds,
            journey.JourneyDurationMilliseconds, journey.CostStatus, journey.PricingVersion,
            journey.PricingTokenUnit, journey.InputPricePerTokenUnit,
            journey.OutputPricePerTokenUnit,
            normalizedPrediction,
            _rubric?.Score(item.HumanReference, normalizedPrediction),
            journey.InvalidOutputDiagnostics,
            journey.OutcomeResolution,
            journey.GatewayCalls,
            journey.OutcomeResolutionSteps);
    }

    private EvaluationCaseResult Empty(
        EvaluationCase item,
        EvaluationDirectiveIds ids,
        string submissionStatus,
        int? httpStatus,
        string outcome) => new(
            item.CaseId, ids.MessageId, ids.ThreadId, ids.DirectiveId,
            submissionStatus, httpStatus, outcome, outcome, null,
            ProviderId: null,
            ModelId: null,
            OutputConstraintMode: null,
            InputTokens: null,
            OutputTokens: null,
            TotalTokens: null,
            TokensEstimated: null,
            CostAmount: null,
            CostCurrency: null,
            CostEstimated: null,
            GatewayLatencyMilliseconds: null,
            JourneyDurationMilliseconds: null,
            CostStatus: null,
            PricingVersion: null,
            PricingTokenUnit: null,
            InputPricePerTokenUnit: null,
            OutputPricePerTokenUnit: null,
            Prediction: null,
            Scoring: _rubric?.Score(item.HumanReference, prediction: null));
}
