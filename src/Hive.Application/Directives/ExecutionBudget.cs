using System.Collections.Immutable;

namespace Hive.Application.Directives;

/// <summary>
/// The bounded operation that consumes part of one directive execution budget.
/// </summary>
public enum ExecutionBudgetOperation
{
    PrimaryInference = 1,
    ContinuationInference = 2,
    ConnectorTool = 3,
    OutcomeVerifier = 4,
}

/// <summary>
/// Closed reasons why an operation cannot consume a directive execution budget.
/// </summary>
public enum ExecutionBudgetExhaustion
{
    DeadlineReached = 1,
    CostBudgetUnavailable = 2,
    MaxIterationsReached = 3,
}

/// <summary>
/// Immutable budget lineage shared by every subordinate operation of one directive execution.
/// The effective deadline is selected once and every successful consumption returns a narrowed
/// copy that preserves the original correlation, start and deadline.
/// </summary>
public sealed class ExecutionBudget
{
    private ExecutionBudget(
        string correlationId,
        DateTimeOffset startedAtUtc,
        DateTimeOffset? deadlineUtc,
        int? maxIterations,
        int consumedIterations,
        bool hasAvailableCostBudget,
        ImmutableArray<ExecutionBudgetOperation> consumedOperations)
    {
        CorrelationId = correlationId;
        StartedAtUtc = startedAtUtc;
        DeadlineUtc = deadlineUtc;
        MaxIterations = maxIterations;
        ConsumedIterations = consumedIterations;
        HasAvailableCostBudget = hasAvailableCostBudget;
        ConsumedOperations = consumedOperations;
    }

    public string CorrelationId { get; }

    public DateTimeOffset StartedAtUtc { get; }

    public DateTimeOffset? DeadlineUtc { get; }

    public int? MaxIterations { get; }

    public int ConsumedIterations { get; }

    public int? RemainingIterations => MaxIterations - ConsumedIterations;

    public bool HasAvailableCostBudget { get; }

    public ImmutableArray<ExecutionBudgetOperation> ConsumedOperations { get; }

    public static ExecutionBudget Start(
        string correlationId,
        DateTimeOffset startedAtUtc,
        TimeSpan? configuredTimeout = null,
        DateTimeOffset? directiveDeadlineUtc = null,
        int? maxIterations = null,
        bool hasAvailableCostBudget = true)
    {
        var requiredCorrelationId = RequireText(correlationId, nameof(correlationId));
        if (startedAtUtc == default)
        {
            throw new ArgumentException(
                "Directive execution budget start time must be specified.",
                nameof(startedAtUtc));
        }

        if (configuredTimeout is { } timeout && timeout <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredTimeout),
                configuredTimeout,
                "Directive execution timeout must be greater than zero.");
        }

        if (maxIterations is <= 0)
        {
            throw new ArgumentOutOfRangeException(
                nameof(maxIterations),
                maxIterations,
                "Directive execution max iterations must be greater than zero.");
        }

        var configuredDeadline = configuredTimeout is { } value
            ? startedAtUtc.Add(value)
            : (DateTimeOffset?)null;
        var effectiveDeadline = Earliest(configuredDeadline, directiveDeadlineUtc);

        return new ExecutionBudget(
            requiredCorrelationId,
            startedAtUtc,
            effectiveDeadline,
            maxIterations,
            consumedIterations: 0,
            hasAvailableCostBudget,
            ImmutableArray<ExecutionBudgetOperation>.Empty);
    }

    /// <summary>
    /// Returns the non-negative time left at <paramref name="observedAtUtc"/>, or null when the
    /// execution has no deadline.
    /// </summary>
    public TimeSpan? RemainingTime(DateTimeOffset observedAtUtc)
    {
        RequireObservedAt(observedAtUtc);
        if (DeadlineUtc is not { } deadline)
        {
            return null;
        }

        var remaining = deadline - observedAtUtc;
        return remaining > TimeSpan.Zero ? remaining : TimeSpan.Zero;
    }

    /// <summary>
    /// Caps a configured subordinate timeout at the original remaining deadline.
    /// Returns false when no positive time remains, so the caller must not start the operation.
    /// </summary>
    public bool TryGetEffectiveTimeout(
        TimeSpan? configuredTimeout,
        DateTimeOffset observedAtUtc,
        out TimeSpan? effectiveTimeout)
    {
        if (configuredTimeout is { } configured && configured <= TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(configuredTimeout),
                configuredTimeout,
                "Subordinate operation timeout must be greater than zero.");
        }

        RequireObservedAt(observedAtUtc);
        var remaining = RemainingTime(observedAtUtc);
        if (remaining == TimeSpan.Zero)
        {
            effectiveTimeout = null;
            return false;
        }

        effectiveTimeout = remaining is { } available
            ? configuredTimeout is { } requested && requested <= available
                ? requested
                : available
            : configuredTimeout;
        return true;
    }

    /// <summary>
    /// Attempts to consume one operation. Inference and connector tools share the iteration
    /// allowance; the verifier shares deadline and cost availability without changing the
    /// characterized iteration count.
    /// </summary>
    public bool TryConsume(
        ExecutionBudgetOperation operation,
        DateTimeOffset observedAtUtc,
        out ExecutionBudget remainingBudget,
        out ExecutionBudgetExhaustion? exhaustion)
    {
        if (!Enum.IsDefined(operation))
        {
            throw new ArgumentOutOfRangeException(
                nameof(operation),
                operation,
                "Unknown directive execution budget operation.");
        }

        RequireObservedAt(observedAtUtc);
        if (RemainingTime(observedAtUtc) == TimeSpan.Zero)
        {
            remainingBudget = this;
            exhaustion = ExecutionBudgetExhaustion.DeadlineReached;
            return false;
        }

        if (!HasAvailableCostBudget)
        {
            remainingBudget = this;
            exhaustion = ExecutionBudgetExhaustion.CostBudgetUnavailable;
            return false;
        }

        var consumesIteration = ConsumesIteration(operation);
        if (consumesIteration && RemainingIterations is <= 0)
        {
            remainingBudget = this;
            exhaustion = ExecutionBudgetExhaustion.MaxIterationsReached;
            return false;
        }

        remainingBudget = new ExecutionBudget(
            CorrelationId,
            StartedAtUtc,
            DeadlineUtc,
            MaxIterations,
            ConsumedIterations + (consumesIteration ? 1 : 0),
            HasAvailableCostBudget,
            ConsumedOperations.Add(operation));
        exhaustion = null;
        return true;
    }

    /// <summary>
    /// Narrows the cost facet of the budget. Availability can be removed but never restored.
    /// </summary>
    public ExecutionBudget MarkCostBudgetUnavailable() =>
        HasAvailableCostBudget
            ? new ExecutionBudget(
                CorrelationId,
                StartedAtUtc,
                DeadlineUtc,
                MaxIterations,
                ConsumedIterations,
                hasAvailableCostBudget: false,
                ConsumedOperations)
            : this;

    private static bool ConsumesIteration(ExecutionBudgetOperation operation) =>
        operation is
            ExecutionBudgetOperation.PrimaryInference or
            ExecutionBudgetOperation.ContinuationInference or
            ExecutionBudgetOperation.ConnectorTool;

    private static DateTimeOffset? Earliest(
        DateTimeOffset? configuredDeadline,
        DateTimeOffset? directiveDeadline) =>
        (configuredDeadline, directiveDeadline) switch
        {
            ({ } configured, { } directive) =>
                configured <= directive ? configured : directive,
            ({ } configured, null) => configured,
            (null, { } directive) => directive,
            _ => null,
        };

    private static void RequireObservedAt(DateTimeOffset observedAtUtc)
    {
        if (observedAtUtc == default)
        {
            throw new ArgumentException(
                "Directive execution budget observation time must be specified.",
                nameof(observedAtUtc));
        }
    }

    private static string RequireText(string value, string parameterName)
    {
        ArgumentNullException.ThrowIfNull(value, parameterName);
        if (string.IsNullOrWhiteSpace(value))
        {
            throw new ArgumentException("Value cannot be empty or whitespace.", parameterName);
        }

        if (!string.Equals(value, value.Trim(), StringComparison.Ordinal))
        {
            throw new ArgumentException(
                "Value cannot contain leading or trailing whitespace.",
                parameterName);
        }

        return value;
    }
}
