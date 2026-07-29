using Hive.Application.Directives;

namespace Hive.Tests;

public sealed class ExecutionBudgetTests
{
    private static readonly DateTimeOffset StartedAt =
        new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public void Start_fixes_the_earliest_deadline_and_consumption_only_narrows_the_lineage()
    {
        var budget = ExecutionBudget.Start(
            "directive:budget-test",
            StartedAt,
            configuredTimeout: TimeSpan.FromMinutes(10),
            directiveDeadlineUtc: StartedAt.AddMinutes(4),
            maxIterations: 3);

        Assert.Equal(StartedAt.AddMinutes(4), budget.DeadlineUtc);
        Assert.Equal(3, budget.RemainingIterations);

        Assert.True(budget.TryConsume(
            ExecutionBudgetOperation.PrimaryInference,
            StartedAt,
            out var afterPrimary,
            out var primaryExhaustion));
        Assert.Null(primaryExhaustion);
        Assert.Equal(2, afterPrimary.RemainingIterations);

        Assert.True(afterPrimary.TryConsume(
            ExecutionBudgetOperation.ContinuationInference,
            StartedAt.AddMinutes(1),
            out var afterContinuation,
            out _));
        Assert.True(afterContinuation.TryConsume(
            ExecutionBudgetOperation.ConnectorTool,
            StartedAt.AddMinutes(2),
            out var exhausted,
            out _));

        Assert.Equal(0, exhausted.RemainingIterations);
        Assert.Equal(budget.CorrelationId, exhausted.CorrelationId);
        Assert.Equal(budget.StartedAtUtc, exhausted.StartedAtUtc);
        Assert.Equal(budget.DeadlineUtc, exhausted.DeadlineUtc);
        Assert.Equal(3, budget.RemainingIterations);
        Assert.Equal(
            [
                ExecutionBudgetOperation.PrimaryInference,
                ExecutionBudgetOperation.ContinuationInference,
                ExecutionBudgetOperation.ConnectorTool,
            ],
            exhausted.ConsumedOperations);

        Assert.False(exhausted.TryConsume(
            ExecutionBudgetOperation.ContinuationInference,
            StartedAt.AddMinutes(3),
            out var rejectedBudget,
            out var exhaustion));
        Assert.Same(exhausted, rejectedBudget);
        Assert.Equal(ExecutionBudgetExhaustion.MaxIterationsReached, exhaustion);
    }

    [Fact]
    public void Verifier_uses_the_same_deadline_and_cost_lineage_without_changing_iteration_count()
    {
        var budget = ExecutionBudget.Start(
            "directive:verifier-test",
            StartedAt,
            configuredTimeout: TimeSpan.FromMinutes(5),
            maxIterations: 2);

        Assert.True(budget.TryConsume(
            ExecutionBudgetOperation.OutcomeVerifier,
            StartedAt.AddMinutes(1),
            out var afterVerifier,
            out var exhaustion));

        Assert.Null(exhaustion);
        Assert.Equal(2, afterVerifier.RemainingIterations);
        Assert.Equal(
            [ExecutionBudgetOperation.OutcomeVerifier],
            afterVerifier.ConsumedOperations);
        Assert.Equal(budget.DeadlineUtc, afterVerifier.DeadlineUtc);
    }

    [Fact]
    public void Deadline_and_cost_exhaustion_prevent_new_operations()
    {
        var expired = ExecutionBudget.Start(
            "directive:expired-test",
            StartedAt,
            directiveDeadlineUtc: StartedAt);

        Assert.False(expired.TryConsume(
            ExecutionBudgetOperation.PrimaryInference,
            StartedAt,
            out _,
            out var deadlineExhaustion));
        Assert.Equal(ExecutionBudgetExhaustion.DeadlineReached, deadlineExhaustion);

        var unavailable = ExecutionBudget.Start(
            "directive:cost-test",
            StartedAt,
            hasAvailableCostBudget: false);
        Assert.False(unavailable.TryConsume(
            ExecutionBudgetOperation.OutcomeVerifier,
            StartedAt,
            out _,
            out var costExhaustion));
        Assert.Equal(ExecutionBudgetExhaustion.CostBudgetUnavailable, costExhaustion);
    }

    [Fact]
    public void Effective_timeout_is_capped_at_positive_remaining_time()
    {
        var budget = ExecutionBudget.Start(
            "directive:timeout-test",
            StartedAt,
            configuredTimeout: TimeSpan.FromSeconds(30));

        Assert.True(budget.TryGetEffectiveTimeout(
            TimeSpan.FromMinutes(1),
            StartedAt.AddSeconds(12),
            out var capped));
        Assert.Equal(TimeSpan.FromSeconds(18), capped);

        Assert.False(budget.TryGetEffectiveTimeout(
            TimeSpan.FromSeconds(5),
            StartedAt.AddSeconds(30),
            out var exhausted));
        Assert.Null(exhausted);
    }

    [Fact]
    public void Cost_availability_can_only_be_narrowed()
    {
        var budget = ExecutionBudget.Start("directive:cost-lineage", StartedAt);
        var unavailable = budget.MarkCostBudgetUnavailable();

        Assert.True(budget.HasAvailableCostBudget);
        Assert.False(unavailable.HasAvailableCostBudget);
        Assert.Same(unavailable, unavailable.MarkCostBudgetUnavailable());
        Assert.Equal(budget.DeadlineUtc, unavailable.DeadlineUtc);
        Assert.Equal(budget.RemainingIterations, unavailable.RemainingIterations);
    }
}
