namespace Hive.Domain.Directives;

public static class DirectiveExecutionPolicyContractVersions
{
    public const int V1 = 1;
}

public enum DirectiveExecutionMode
{
    SingleShot = 1,
    Checkpointable = 2,
}

public static class DirectiveExecutionModeContract
{
    public static DirectiveExecutionMode RequireDefined(
        DirectiveExecutionMode value,
        string parameterName) => value switch
        {
            DirectiveExecutionMode.SingleShot or DirectiveExecutionMode.Checkpointable => value,
            _ => throw new ArgumentOutOfRangeException(
                parameterName,
                value,
                "Directive execution mode must be single-shot or checkpointable."),
        };

    public static string ToWireValue(DirectiveExecutionMode value) =>
        RequireDefined(value, nameof(value)) switch
        {
            DirectiveExecutionMode.SingleShot => "single-shot",
            DirectiveExecutionMode.Checkpointable => "checkpointable",
            _ => throw new InvalidOperationException("Validated directive execution mode is not mapped."),
        };

    public static bool TryParseWireValue(
        string? value,
        out DirectiveExecutionMode mode)
    {
        switch (value)
        {
            case "single-shot":
                mode = DirectiveExecutionMode.SingleShot;
                return true;
            case "checkpointable":
                mode = DirectiveExecutionMode.Checkpointable;
                return true;
            default:
                mode = default;
                return false;
        }
    }
}

/// <summary>
/// Additive request carried by a directive. The position capability remains the authoritative
/// ceiling; this request can select a stricter mode but cannot enable a capability by itself.
/// </summary>
public sealed record DirectiveExecutionPolicyRequest
{
    public DirectiveExecutionPolicyRequest(
        int contractVersion,
        DirectiveExecutionMode mode)
    {
        if (contractVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                "Directive execution policy contract version must be positive.");
        }

        ContractVersion = contractVersion;
        Mode = DirectiveExecutionModeContract.RequireDefined(mode, nameof(mode));
    }

    public int ContractVersion { get; }

    public DirectiveExecutionMode Mode { get; }
}

/// <summary>
/// Tighten-only capability declared by one position. Checkpointable capacity requires a positive
/// lead time; single-shot capacity cannot carry unused checkpoint timing.
/// </summary>
public sealed record DirectiveExecutionPolicyCapability
{
    public DirectiveExecutionPolicyCapability(
        int contractVersion,
        DirectiveExecutionMode maximumMode,
        TimeSpan? checkpointLeadTime = null)
    {
        if (contractVersion <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(contractVersion),
                contractVersion,
                "Directive execution policy contract version must be positive.");
        }

        maximumMode = DirectiveExecutionModeContract.RequireDefined(
            maximumMode,
            nameof(maximumMode));
        if (maximumMode == DirectiveExecutionMode.Checkpointable &&
            checkpointLeadTime is null)
        {
            throw new ArgumentException(
                "Checkpointable capacity requires a checkpoint lead time.",
                nameof(checkpointLeadTime));
        }

        if (checkpointLeadTime is { } leadTime && leadTime <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(checkpointLeadTime),
                checkpointLeadTime,
                "Checkpoint lead time must be greater than zero.");
        }

        if (maximumMode == DirectiveExecutionMode.SingleShot &&
            checkpointLeadTime is not null)
        {
            throw new ArgumentException(
                "Single-shot capacity cannot declare a checkpoint lead time.",
                nameof(checkpointLeadTime));
        }

        ContractVersion = contractVersion;
        MaximumMode = maximumMode;
        CheckpointLeadTime = checkpointLeadTime;
    }

    public int ContractVersion { get; }

    public DirectiveExecutionMode MaximumMode { get; }

    public TimeSpan? CheckpointLeadTime { get; }
}

public enum DirectiveExecutionPolicyDecisionCode
{
    DefaultSingleShot = 1,
    ExplicitSingleShot = 2,
    Checkpointable = 3,
    RequestVersionUnsupported = 4,
    PositionCapabilityMissing = 5,
    PositionVersionUnsupported = 6,
    PositionCapabilityExceeded = 7,
    ExecutionBudgetMissing = 8,
    TemporalValuesIncoherent = 9,
}

public static class DirectiveExecutionPolicyDecisionCodeContract
{
    public static string ToWireValue(DirectiveExecutionPolicyDecisionCode value) => value switch
    {
        DirectiveExecutionPolicyDecisionCode.DefaultSingleShot => "default-single-shot",
        DirectiveExecutionPolicyDecisionCode.ExplicitSingleShot => "explicit-single-shot",
        DirectiveExecutionPolicyDecisionCode.Checkpointable => "checkpointable",
        DirectiveExecutionPolicyDecisionCode.RequestVersionUnsupported =>
            "request-version-unsupported",
        DirectiveExecutionPolicyDecisionCode.PositionCapabilityMissing =>
            "position-capability-missing",
        DirectiveExecutionPolicyDecisionCode.PositionVersionUnsupported =>
            "position-version-unsupported",
        DirectiveExecutionPolicyDecisionCode.PositionCapabilityExceeded =>
            "position-capability-exceeded",
        DirectiveExecutionPolicyDecisionCode.ExecutionBudgetMissing =>
            "execution-budget-missing",
        DirectiveExecutionPolicyDecisionCode.TemporalValuesIncoherent =>
            "temporal-values-incoherent",
        _ => throw new ArgumentOutOfRangeException(
            nameof(value),
            value,
            "Directive execution policy decision code is not defined."),
    };
}

/// <summary>
/// Provider-neutral effective configuration delivered to the execution runtime and prompt layer.
/// It always has one closed mode; invalid or incomplete checkpointable inputs become single-shot.
/// </summary>
public sealed record EffectiveDirectiveExecutionPolicy
{
    internal EffectiveDirectiveExecutionPolicy(
        DirectiveExecutionMode mode,
        DirectiveExecutionPolicyDecisionCode decisionCode,
        TimeSpan? totalExecutionBudget = null,
        TimeSpan? remainingExecutionTime = null,
        TimeSpan? checkpointLeadTime = null)
    {
        Mode = DirectiveExecutionModeContract.RequireDefined(mode, nameof(mode));
        if (!Enum.IsDefined(decisionCode))
        {
            throw new ArgumentOutOfRangeException(
                nameof(decisionCode),
                decisionCode,
                "Directive execution policy decision code is not defined.");
        }

        if (mode == DirectiveExecutionMode.Checkpointable &&
            decisionCode != DirectiveExecutionPolicyDecisionCode.Checkpointable)
        {
            throw new ArgumentException(
                "Checkpointable effective policy requires the checkpointable decision code.",
                nameof(decisionCode));
        }

        if (mode == DirectiveExecutionMode.SingleShot &&
            decisionCode == DirectiveExecutionPolicyDecisionCode.Checkpointable)
        {
            throw new ArgumentException(
                "Single-shot effective policy cannot use the checkpointable decision code.",
                nameof(decisionCode));
        }

        if (mode == DirectiveExecutionMode.Checkpointable &&
            (totalExecutionBudget is null ||
             remainingExecutionTime is null ||
             checkpointLeadTime is null))
        {
            throw new ArgumentException(
                "Checkpointable effective policy requires total, remaining, and lead times.",
                nameof(totalExecutionBudget));
        }

        if (mode == DirectiveExecutionMode.Checkpointable &&
            (totalExecutionBudget <= TimeSpan.Zero ||
             remainingExecutionTime <= TimeSpan.Zero ||
             remainingExecutionTime > totalExecutionBudget ||
             checkpointLeadTime <= TimeSpan.Zero ||
             checkpointLeadTime >= totalExecutionBudget))
        {
            throw new ArgumentException(
                "Checkpointable effective policy requires positive, coherent temporal values.",
                nameof(totalExecutionBudget));
        }

        if (mode == DirectiveExecutionMode.SingleShot &&
            (totalExecutionBudget is not null ||
             remainingExecutionTime is not null ||
             checkpointLeadTime is not null))
        {
            throw new ArgumentException(
                "Single-shot effective policy cannot expose checkpoint temporal values.",
                nameof(totalExecutionBudget));
        }

        ContractVersion = DirectiveExecutionPolicyContractVersions.V1;
        DecisionCode = decisionCode;
        TotalExecutionBudget = totalExecutionBudget;
        RemainingExecutionTime = remainingExecutionTime;
        CheckpointLeadTime = checkpointLeadTime;
    }

    public int ContractVersion { get; }

    public DirectiveExecutionMode Mode { get; }

    public DirectiveExecutionPolicyDecisionCode DecisionCode { get; }

    public TimeSpan? TotalExecutionBudget { get; }

    public TimeSpan? RemainingExecutionTime { get; }

    public TimeSpan? CheckpointLeadTime { get; }

    public bool AllowsProgressReports => Mode == DirectiveExecutionMode.Checkpointable;
}

public static class DirectiveExecutionPolicyComposer
{
    public static EffectiveDirectiveExecutionPolicy ComposeV1(
        DirectiveExecutionPolicyRequest? request,
        DirectiveExecutionPolicyCapability? positionCapability,
        TimeSpan? totalExecutionBudget,
        TimeSpan? remainingExecutionTime = null)
    {
        if (request is null)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.DefaultSingleShot);
        }

        if (request.ContractVersion != DirectiveExecutionPolicyContractVersions.V1)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.RequestVersionUnsupported);
        }

        if (request.Mode == DirectiveExecutionMode.SingleShot)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.ExplicitSingleShot);
        }

        if (positionCapability is null)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.PositionCapabilityMissing);
        }

        if (positionCapability.ContractVersion != DirectiveExecutionPolicyContractVersions.V1)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.PositionVersionUnsupported);
        }

        if (positionCapability.MaximumMode < request.Mode)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.PositionCapabilityExceeded);
        }

        if (totalExecutionBudget is null)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.ExecutionBudgetMissing);
        }

        var total = totalExecutionBudget.Value;
        var remaining = remainingExecutionTime ?? total;
        var leadTime = positionCapability.CheckpointLeadTime;
        if (total <= TimeSpan.Zero ||
            remaining <= TimeSpan.Zero ||
            remaining > total ||
            leadTime is null ||
            leadTime <= TimeSpan.Zero ||
            leadTime >= total)
        {
            return SingleShot(DirectiveExecutionPolicyDecisionCode.TemporalValuesIncoherent);
        }

        return new EffectiveDirectiveExecutionPolicy(
            DirectiveExecutionMode.Checkpointable,
            DirectiveExecutionPolicyDecisionCode.Checkpointable,
            total,
            remaining,
            leadTime);
    }

    private static EffectiveDirectiveExecutionPolicy SingleShot(
        DirectiveExecutionPolicyDecisionCode decisionCode) =>
        new(DirectiveExecutionMode.SingleShot, decisionCode);
}
