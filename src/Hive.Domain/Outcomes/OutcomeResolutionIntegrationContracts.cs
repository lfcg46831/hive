using System.Collections.Immutable;

namespace Hive.Domain.Outcomes;

public enum OutcomeResolutionMode
{
    Shadow = 1,
    Enforcement = 2,
}

public static class OutcomeResolutionModeContract
{
    public const string Shadow = "shadow";
    public const string Enforcement = "enforcement";

    public static string ToWireValue(OutcomeResolutionMode mode) => mode switch
    {
        OutcomeResolutionMode.Shadow => Shadow,
        OutcomeResolutionMode.Enforcement => Enforcement,
        _ => throw new ArgumentOutOfRangeException(nameof(mode), mode, "Unknown outcome resolution mode."),
    };

    public static bool TryParse(string? value, out OutcomeResolutionMode mode)
    {
        switch (value)
        {
            case Shadow:
                mode = OutcomeResolutionMode.Shadow;
                return true;
            case Enforcement:
                mode = OutcomeResolutionMode.Enforcement;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}

/// <summary>
/// Closed, provider-neutral diagnostics for the integration boundary. Diagnostics describe the
/// failed boundary only; exception text and rejected model values never cross into audit data.
/// </summary>
public enum OutcomeResolutionDiagnostic
{
    FactsUnavailable = 1,
    PolicyUnavailable = 2,
    PolicyIncompatible = 3,
    ResolutionUnavailable = 4,
    MaterializationIncompatible = 5,
}

public static class OutcomeResolutionDiagnosticContract
{
    private static readonly IReadOnlyDictionary<OutcomeResolutionDiagnostic, string> WireValues =
        new Dictionary<OutcomeResolutionDiagnostic, string>
        {
            [OutcomeResolutionDiagnostic.FactsUnavailable] = "facts-unavailable",
            [OutcomeResolutionDiagnostic.PolicyUnavailable] = "policy-unavailable",
            [OutcomeResolutionDiagnostic.PolicyIncompatible] = "policy-incompatible",
            [OutcomeResolutionDiagnostic.ResolutionUnavailable] = "resolution-unavailable",
            [OutcomeResolutionDiagnostic.MaterializationIncompatible] = "materialization-incompatible",
        };

    public static ImmutableArray<string> AllWireValues { get; } = WireValues.Values
        .Order(StringComparer.Ordinal)
        .ToImmutableArray();

    public static string ToWireValue(OutcomeResolutionDiagnostic diagnostic) =>
        WireValues.TryGetValue(diagnostic, out var value)
            ? value
            : throw new ArgumentOutOfRangeException(
                nameof(diagnostic),
                diagnostic,
                "Unknown outcome resolution diagnostic.");

    public static bool TryParse(string? value, out OutcomeResolutionDiagnostic diagnostic)
    {
        foreach (var candidate in WireValues)
        {
            if (string.Equals(candidate.Value, value, StringComparison.Ordinal))
            {
                diagnostic = candidate.Key;
                return true;
            }
        }

        diagnostic = default;
        return false;
    }
}
