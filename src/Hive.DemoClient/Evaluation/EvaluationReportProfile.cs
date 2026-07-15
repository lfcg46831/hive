using System.Globalization;
using System.Text.Json;
using System.Text.Json.Serialization;

namespace Hive.DemoClient.Evaluation;

public sealed class EvaluationReportProfile
{
    private const string ExpectedFixtureKind = "evaluation-report-profile";
    private const string ExpectedSensitivityMethod =
        "reprice-observed-input-output-tokens";

    [JsonPropertyName("profile_version")]
    public int ProfileVersion { get; init; }

    [JsonPropertyName("fixture_kind")]
    public string FixtureKind { get; init; } = string.Empty;

    [JsonPropertyName("report_id")]
    public string ReportId { get; init; } = string.Empty;

    [JsonPropertyName("title")]
    public string Title { get; init; } = string.Empty;

    [JsonPropertyName("work_item_singular")]
    public string WorkItemSingular { get; init; } = string.Empty;

    [JsonPropertyName("work_item_plural")]
    public string WorkItemPlural { get; init; } = string.Empty;

    [JsonPropertyName("work_items_per_position_day")]
    public decimal WorkItemsPerPositionDay { get; init; }

    [JsonPropertyName("cost_sensitivity_method")]
    public string CostSensitivityMethod { get; init; } = string.Empty;

    [JsonPropertyName("model_scenarios")]
    public IReadOnlyList<EvaluationModelCostScenario> ModelScenarios { get; init; } = [];

    public static EvaluationReportProfile Load(string path)
    {
        if (string.IsNullOrWhiteSpace(path))
        {
            throw new ArgumentException("Evaluation report profile path is required.", nameof(path));
        }

        try
        {
            using var stream = File.OpenRead(path);
            var profile = JsonSerializer.Deserialize<EvaluationReportProfile>(stream)
                ?? throw new InvalidDataException("Evaluation report profile is empty.");
            profile.Validate();
            return profile;
        }
        catch (InvalidDataException)
        {
            throw;
        }
        catch (Exception exception)
            when (exception is JsonException or IOException or UnauthorizedAccessException)
        {
            throw new InvalidDataException(
                "Evaluation report profile is malformed or unavailable.",
                exception);
        }
    }

    internal void Validate()
    {
        if (ProfileVersion != 1
            || !string.Equals(FixtureKind, ExpectedFixtureKind, StringComparison.Ordinal)
            || string.IsNullOrWhiteSpace(ReportId)
            || string.IsNullOrWhiteSpace(Title)
            || string.IsNullOrWhiteSpace(WorkItemSingular)
            || string.IsNullOrWhiteSpace(WorkItemPlural)
            || WorkItemsPerPositionDay <= 0
            || !string.Equals(
                CostSensitivityMethod,
                ExpectedSensitivityMethod,
                StringComparison.Ordinal))
        {
            throw new InvalidDataException(
                "Evaluation report profile metadata or workload assumption is invalid.");
        }

        if (ModelScenarios is null || ModelScenarios.Count < 2)
        {
            throw new InvalidDataException(
                "Evaluation report sensitivity requires at least two model scenarios.");
        }

        var keys = new HashSet<string>(StringComparer.Ordinal);
        foreach (var scenario in ModelScenarios)
        {
            if (scenario is null
                || string.IsNullOrWhiteSpace(scenario.ProviderId)
                || string.IsNullOrWhiteSpace(scenario.ModelId)
                || string.IsNullOrWhiteSpace(scenario.PricingVersion)
                || scenario.TokenUnit <= 0
                || scenario.InputPricePerTokenUnit < 0
                || scenario.OutputPricePerTokenUnit < 0
                || !IsCurrency(scenario.Currency)
                || !Uri.TryCreate(scenario.SourceUrl, UriKind.Absolute, out var source)
                || source.Scheme != Uri.UriSchemeHttps
                || !DateOnly.TryParseExact(
                    scenario.SourceAccessedOn,
                    "yyyy-MM-dd",
                    CultureInfo.InvariantCulture,
                    DateTimeStyles.None,
                    out _))
            {
                throw new InvalidDataException(
                    "Evaluation report model scenarios require canonical pricing and source metadata.");
            }

            if (!keys.Add($"{scenario.ProviderId}\n{scenario.ModelId}"))
            {
                throw new InvalidDataException(
                    "Evaluation report model scenarios must identify distinct provider/model pairs.");
            }
        }

        if (ModelScenarios
            .Select(item => new
            {
                item.TokenUnit,
                item.InputPricePerTokenUnit,
                item.OutputPricePerTokenUnit,
                item.Currency,
            })
            .Distinct()
            .Count() < 2)
        {
            throw new InvalidDataException(
                "Evaluation report model scenarios must include at least two distinct costs.");
        }

        if (ModelScenarios.Select(item => item.ProviderId)
            .Distinct(StringComparer.Ordinal).Count() != 1)
        {
            throw new InvalidDataException(
                "Evaluation report model sensitivity must compare models from the same provider.");
        }
    }

    private static bool IsCurrency(string value) =>
        value.Length == 3 && value.All(character => character is >= 'A' and <= 'Z');
}

public sealed class EvaluationModelCostScenario
{
    [JsonPropertyName("provider_id")]
    public string ProviderId { get; init; } = string.Empty;

    [JsonPropertyName("model_id")]
    public string ModelId { get; init; } = string.Empty;

    [JsonPropertyName("pricing_version")]
    public string PricingVersion { get; init; } = string.Empty;

    [JsonPropertyName("token_unit")]
    public int TokenUnit { get; init; }

    [JsonPropertyName("input_price_per_token_unit")]
    public decimal InputPricePerTokenUnit { get; init; }

    [JsonPropertyName("output_price_per_token_unit")]
    public decimal OutputPricePerTokenUnit { get; init; }

    [JsonPropertyName("currency")]
    public string Currency { get; init; } = string.Empty;

    [JsonPropertyName("source_url")]
    public string SourceUrl { get; init; } = string.Empty;

    [JsonPropertyName("source_accessed_on")]
    public string SourceAccessedOn { get; init; } = string.Empty;
}
