using System.Net.Http.Json;
using System.Text.Json;

namespace Hive.DemoClient.Evaluation;

public sealed class EvaluationRunner
{
    private const string OrganizationId = "acme-delivery";
    private static readonly JsonSerializerOptions JsonOptions = new(JsonSerializerDefaults.Web);

    private readonly HttpClient _httpClient;
    private readonly IEvaluationAuditReader _auditReader;
    private readonly TimeProvider _timeProvider;

    public EvaluationRunner(
        HttpClient httpClient,
        IEvaluationAuditReader auditReader,
        TimeProvider? timeProvider = null)
    {
        _httpClient = httpClient ?? throw new ArgumentNullException(nameof(httpClient));
        _auditReader = auditReader ?? throw new ArgumentNullException(nameof(auditReader));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public async Task<EvaluationDataset> RunAsync(
        EvaluationCorpus corpus,
        EvaluationRunOptions options,
        CancellationToken cancellationToken)
    {
        var results = new List<EvaluationCaseResult>(corpus.Cases.Count);
        for (var index = 0; index < corpus.Cases.Count; index++)
        {
            var item = corpus.Cases[index];
            results.Add(await RunCaseAsync(corpus.CorpusVersion, item, index, options, cancellationToken)
                .ConfigureAwait(false));
        }

        return new EvaluationDataset(
            1,
            corpus.CorpusVersion,
            options.RunId,
            options.BaseUrl.AbsoluteUri.TrimEnd('/'),
            options.Timeout.TotalSeconds,
            options.PollInterval.TotalMilliseconds,
            results);
    }

    private async Task<EvaluationCaseResult> RunCaseAsync(
        int corpusVersion,
        EvaluationCase item,
        int index,
        EvaluationRunOptions options,
        CancellationToken cancellationToken)
    {
        var ids = DemoDirectiveIds.FromSeed($"evaluation:v{corpusVersion}:{options.RunId}:{item.CaseId}");
        var submission = AcmeDeliveryDemoDirectiveClient.CreateTriageDirective(
            ids,
            options.SentAt.AddSeconds(index),
            item.Context);

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
                return Empty(item.CaseId, ids, "rejected", httpStatus, "submission-rejected");
            }
        }
        catch (HttpRequestException)
        {
            return Empty(item.CaseId, ids, "failed", null, "submission-unavailable");
        }

        var deadline = _timeProvider.GetUtcNow() + options.Timeout;
        while (_timeProvider.GetUtcNow() < deadline)
        {
            var journey = await _auditReader.ReadAsync(
                OrganizationId,
                ids.ThreadId,
                ids.DirectiveId,
                cancellationToken).ConfigureAwait(false);
            if (journey is not null)
            {
                return FromJourney(item.CaseId, ids, httpStatus, journey);
            }

            await Task.Delay(options.PollInterval, _timeProvider, cancellationToken).ConfigureAwait(false);
        }

        return Empty(item.CaseId, ids, "accepted", httpStatus, "timeout");
    }

    private static EvaluationCaseResult FromJourney(
        string caseId,
        DemoDirectiveIds ids,
        int? httpStatus,
        EvaluationJourney journey) => new(
            caseId, ids.MessageId, ids.ThreadId, ids.DirectiveId,
            "accepted", httpStatus, journey.Outcome, journey.TerminalCode, journey.Decision,
            journey.ProviderId, journey.ModelId, journey.OutputConstraintMode,
            journey.InputTokens, journey.OutputTokens,
            journey.TotalTokens, journey.TokensEstimated, journey.CostAmount,
            journey.CostCurrency, journey.CostEstimated, journey.GatewayLatencyMilliseconds,
            journey.JourneyDurationMilliseconds, journey.CostStatus, journey.PricingVersion,
            journey.PricingTokenUnit, journey.InputPricePerTokenUnit,
            journey.OutputPricePerTokenUnit);

    private static EvaluationCaseResult Empty(
        string caseId,
        DemoDirectiveIds ids,
        string submissionStatus,
        int? httpStatus,
        string outcome) => new(
            caseId, ids.MessageId, ids.ThreadId, ids.DirectiveId,
            submissionStatus, httpStatus, outcome, outcome, null,
            null, null, null, null, null, null, null, null, null, null, null, null);
}
