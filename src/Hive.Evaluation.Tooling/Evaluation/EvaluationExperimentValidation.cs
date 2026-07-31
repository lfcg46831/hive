using System.Text.Json.Serialization;

namespace Hive.Evaluation.Tooling.Evaluation;

public static class EvaluationExperimentValidator
{
    public static EvaluationExperimentValidation Validate(
        IReadOnlyList<EvaluationCaseResult> cases,
        EvaluationExperimentManifest manifest)
    {
        ArgumentNullException.ThrowIfNull(cases);
        ArgumentNullException.ThrowIfNull(manifest);

        var failures = new HashSet<string>(StringComparer.Ordinal);
        var inferenceCalls = cases
            .SelectMany(item => item.GatewayCalls ?? [])
            .Where(call => call.Operation == "directive-inference")
            .ToArray();
        if (inferenceCalls.Length == 0)
        {
            failures.Add("inference-configuration-unobserved");
        }
        else
        {
            if (inferenceCalls.Any(call =>
                !string.Equals(
                    call.ProviderId,
                    manifest.Model.ProviderId,
                    StringComparison.Ordinal)))
            {
                failures.Add("provider-drift");
            }

            if (inferenceCalls.Any(call =>
                !string.Equals(
                    call.ModelId,
                    manifest.Model.ModelId,
                    StringComparison.Ordinal)))
            {
                failures.Add("model-drift");
            }

            if (inferenceCalls.Any(call =>
                !string.Equals(
                    call.OutputConstraintMode,
                    manifest.Model.OutputConstraintMode,
                    StringComparison.Ordinal)))
            {
                failures.Add("output-constraint-drift");
            }

            if (inferenceCalls.Any(call =>
                call.MaxOutputTokens != manifest.Limits.MaxOutputTokens))
            {
                failures.Add("max-output-tokens-drift");
            }

            ValidateExecutionLimits(inferenceCalls, manifest, failures);

            ValidateInferenceTimeouts(cases, manifest, failures);
        }

        var verifierCalls = cases
            .SelectMany(item => item.GatewayCalls ?? [])
            .Where(call => call.Operation == "outcome-verification")
            .ToArray();
        if (verifierCalls.Any(call =>
            call.RequestTimeoutMilliseconds is null
            or <= 0
            || call.RequestTimeoutMilliseconds
                > manifest.Limits.VerifierTimeoutMilliseconds))
        {
            failures.Add("verifier-timeout-drift");
        }
        if (verifierCalls.Length > 0)
        {
            ValidateExecutionLimits(verifierCalls, manifest, failures);
        }

        var resolutions = cases
            .SelectMany(item => item.OutcomeResolutionSteps ?? [])
            .ToArray();
        if (resolutions.Length == 0)
        {
            failures.Add("outcome-mode-unobserved");
        }
        else if (resolutions.Any(item =>
            !string.Equals(
                item.Mode,
                manifest.Policy.OutcomeMode,
                StringComparison.Ordinal)))
        {
            failures.Add("outcome-mode-drift");
        }

        var ordered = failures.OrderBy(value => value, StringComparer.Ordinal).ToArray();
        return new EvaluationExperimentValidation(
            ordered.Length == 0 ? "validated" : "invalid",
            ordered);
    }

    private static void ValidateInferenceTimeouts(
        IReadOnlyList<EvaluationCaseResult> cases,
        EvaluationExperimentManifest manifest,
        ISet<string> failures)
    {
        foreach (var item in cases)
        {
            var calls = (item.GatewayCalls ?? [])
                .Where(call => call.Operation == "directive-inference")
                .OrderBy(call => call.Iteration)
                .ThenBy(call => call.CallIndex)
                .ToArray();
            if (calls.Length == 0)
            {
                continue;
            }

            if (calls[0].RequestTimeoutMilliseconds
                != manifest.Limits.ProviderTimeoutMilliseconds)
            {
                failures.Add("provider-timeout-drift");
            }

            if (calls.Any(call =>
                call.RequestTimeoutMilliseconds is null
                or <= 0
                || call.RequestTimeoutMilliseconds
                    > manifest.Limits.ProviderTimeoutMilliseconds))
            {
                failures.Add("provider-timeout-expanded");
            }

            if (calls.Zip(calls.Skip(1)).Any(pair =>
                pair.Second.RequestTimeoutMilliseconds >
                    pair.First.RequestTimeoutMilliseconds))
            {
                failures.Add("provider-timeout-expanded");
            }
        }
    }

    private static void ValidateExecutionLimits(
        IReadOnlyList<EvaluationGatewayCall> calls,
        EvaluationExperimentManifest manifest,
        ISet<string> failures)
    {
        if (manifest.Limits.LimitsVersion == 0 && calls.All(call =>
            call.ExecutionLimitsVersion is null
            && call.ExecutionBudgetMilliseconds is null
            && call.PerCallTimeoutMilliseconds is null))
        {
            return;
        }

        if (calls.Any(call =>
            call.ExecutionLimitsVersion != manifest.Limits.LimitsVersion))
        {
            failures.Add("execution-limits-version-drift");
        }

        if (calls.Any(call =>
            call.ExecutionBudgetMilliseconds !=
                manifest.Limits.EffectiveExecutionTimeoutMilliseconds))
        {
            failures.Add("execution-budget-drift");
        }

        if (calls.Any(call =>
            call.PerCallTimeoutMilliseconds !=
                manifest.Limits.ProviderTimeoutMilliseconds))
        {
            failures.Add("per-call-timeout-drift");
        }
    }
}

public sealed record EvaluationExperimentValidation(
    [property: JsonPropertyName("status")] string Status,
    [property: JsonPropertyName("failure_codes")] IReadOnlyList<string> FailureCodes);
