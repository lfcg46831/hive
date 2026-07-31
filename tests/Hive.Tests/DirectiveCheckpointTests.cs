using System.Text.Json;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Outcomes;

namespace Hive.Tests;

public sealed class DirectiveCheckpointTests
{
    private static readonly OutcomeEvidenceReference GroundedEvidence =
        new(OutcomeEvidenceSource.ToolResult, "tool.call-1");

    [Fact]
    public void Plan_canonicalizes_sequence_and_completion_criteria()
    {
        var plan = new DirectiveCheckpointPlan(
            DirectiveCheckpointContractVersions.V1,
            [
                Subtask(2, "verify", criteria: ["zeta", "alpha"]),
                Subtask(1, "inspect"),
            ]);

        Assert.Equal(["inspect", "verify"], plan.Subtasks.Select(item => item.LocalId));
        Assert.Equal(["alpha", "zeta"], plan.Subtasks[1].CompletionCriteria);
    }

    [Fact]
    public void Contracts_reject_unbounded_or_ambiguous_plan_shapes()
    {
        Assert.Throws<ArgumentException>(() => Subtask(1, "unsafe id"));
        Assert.Throws<ArgumentException>(() =>
            new DirectiveCheckpointSubtask(
                1,
                "inspect",
                new string('x', DirectiveCheckpointContractLimits.MaximumObjectiveUtf8Bytes + 1),
                ["done"],
                TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentException>(() =>
            new DirectiveCheckpointSubtask(
                1,
                "inspect",
                "Inspect",
                Enumerable.Range(
                        1,
                        DirectiveCheckpointContractLimits.MaximumCompletionCriteriaPerSubtask + 1)
                    .Select(index => $"criterion-{index}"),
                TimeSpan.FromMinutes(1)));
        Assert.Throws<ArgumentOutOfRangeException>(() =>
            new DirectiveCheckpointSubtask(
                1,
                "inspect",
                "Inspect",
                ["done"],
                TimeSpan.Zero));
        Assert.Throws<ArgumentException>(() =>
            new DirectiveCheckpointPlan(1, [Subtask(2, "inspect")]));
        Assert.Throws<ArgumentException>(() =>
            new DirectiveCheckpointPlan(
                1,
                [Subtask(1, "inspect"), Subtask(2, "inspect")]));
        Assert.Throws<ArgumentException>(() =>
            new CompletedDirectiveCheckpointSubtask("inspect", []));
    }

    [Fact]
    public void Evidence_context_is_bounded_and_requires_exact_source_and_reference()
    {
        var context = new DirectiveCheckpointEvidenceContext(
        [
            GroundedEvidence,
            new OutcomeEvidenceReference(OutcomeEvidenceSource.DirectiveInput, "directive.context"),
        ]);

        Assert.True(context.Allows(GroundedEvidence));
        Assert.False(context.Allows(
            new OutcomeEvidenceReference(OutcomeEvidenceSource.DirectiveInput, "tool.call-1")));
        Assert.False(context.Allows(
            new OutcomeEvidenceReference(OutcomeEvidenceSource.ToolResult, "tool.call-2")));
        Assert.Throws<ArgumentException>(() =>
            new DirectiveCheckpointEvidenceContext(
                Enumerable.Range(
                        1,
                        DirectiveCheckpointContractLimits.MaximumEvidenceContextReferences + 1)
                    .Select(index => new OutcomeEvidenceReference(
                        OutcomeEvidenceSource.RuntimeFact,
                        $"runtime.fact-{index}"))));
    }

    [Fact]
    public void Grounded_checkpoint_is_progress_ready_and_projects_complete_canonical_context()
    {
        var checkpoint = Checkpoint();
        var result = DirectiveCheckpointValidator.ValidateForProgress(
            checkpoint,
            ValidationContext(checkpoint.Correlation));

        Assert.True(result.IsValid);
        Assert.True(DirectiveCheckpointContextProjector.TryProject(checkpoint, out var projection));
        Assert.NotNull(projection);
        Assert.InRange(
            projection.Utf8Bytes,
            1,
            DirectiveCheckpointContractLimits.MaximumContextProjectionUtf8Bytes);
        Assert.DoesNotContain("reasoning", projection.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("provider", projection.Content, StringComparison.OrdinalIgnoreCase);

        using var document = JsonDocument.Parse(projection.Content);
        var root = document.RootElement;
        Assert.Equal(1, root.GetProperty("contract_version").GetInt32());
        Assert.Equal("inspect", root
            .GetProperty("completed_subtasks")[0]
            .GetProperty("local_id")
            .GetString());
        Assert.Equal("verify", root.GetProperty("next_subtask_id").GetString());
        Assert.Equal("ToolResult", root
            .GetProperty("completed_subtasks")[0]
            .GetProperty("evidence_references")[0]
            .GetProperty("source")
            .GetString());
    }

    [Fact]
    public void Projection_is_identical_for_equivalent_noncanonical_input_order()
    {
        var correlation = Correlation();
        var first = new DirectiveCheckpoint(
            1,
            2,
            new DirectiveCheckpointPlan(
                1,
                [
                    Subtask(2, "verify", criteria: ["zeta", "alpha"]),
                    Subtask(1, "inspect"),
                ]),
            correlation,
            [
                Completed("verify", new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.PersistedState,
                    "state.verified")),
                Completed(
                    "inspect",
                    new OutcomeEvidenceReference(
                        OutcomeEvidenceSource.ToolResult,
                        "tool.call-2"),
                    GroundedEvidence),
            ],
            [OutcomeBlocker.ToolFailure, OutcomeBlocker.Budget]);
        var second = new DirectiveCheckpoint(
            1,
            2,
            new DirectiveCheckpointPlan(
                1,
                [
                    Subtask(1, "inspect"),
                    Subtask(2, "verify", criteria: ["alpha", "zeta"]),
                ]),
            correlation,
            [
                Completed(
                    "inspect",
                    GroundedEvidence,
                    new OutcomeEvidenceReference(
                        OutcomeEvidenceSource.ToolResult,
                        "tool.call-2")),
                Completed("verify", new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.PersistedState,
                    "state.verified")),
            ],
            [OutcomeBlocker.Budget, OutcomeBlocker.ToolFailure]);

        Assert.True(DirectiveCheckpointContextProjector.TryProject(first, out var firstProjection));
        Assert.True(DirectiveCheckpointContextProjector.TryProject(second, out var secondProjection));
        Assert.Equal(firstProjection!.Content, secondProjection!.Content);
    }

    [Fact]
    public void Validation_rejects_unknown_or_ungrounded_work_and_correlation_drift()
    {
        var expectedCorrelation = Correlation();
        var actualCorrelation = new DirectiveCheckpointCorrelation(
            expectedCorrelation.OrganizationId,
            expectedCorrelation.PositionId,
            ThreadId.New(),
            expectedCorrelation.DirectiveId,
            expectedCorrelation.ParentDirectiveId,
            expectedCorrelation.PositionTaskId);
        var ungrounded = new OutcomeEvidenceReference(
            OutcomeEvidenceSource.DirectiveInput,
            GroundedEvidence.Reference);
        var checkpoint = new DirectiveCheckpoint(
            1,
            1,
            Plan(),
            actualCorrelation,
            [Completed("unknown", ungrounded)],
            nextSubtaskId: "also-unknown");

        var result = DirectiveCheckpointValidator.Validate(
            checkpoint,
            ValidationContext(expectedCorrelation));

        Assert.Equal(
            [
                DirectiveCheckpointValidationCode.EvidenceUngrounded,
                DirectiveCheckpointValidationCode.CompletedSubtaskUnknown,
                DirectiveCheckpointValidationCode.CorrelationMismatch,
                DirectiveCheckpointValidationCode.NextSubtaskUnknown,
            ],
            result.Errors.Select(error => error.Code));
        Assert.Equal(
            [
                "completed_subtasks[0].evidence_references[0]",
                "completed_subtasks[0].local_id",
                "correlation.thread_id",
                "next_subtask_id",
            ],
            result.Errors.Select(error => error.Path));
    }

    [Fact]
    public void Progress_validation_fails_closed_without_all_runtime_gates()
    {
        var correlation = Correlation();
        var checkpoint = new DirectiveCheckpoint(
            1,
            1,
            Plan(),
            correlation,
            completedSubtasks: [],
            blockers: [OutcomeBlocker.ExternalDependency],
            nextSubtaskId: null);
        var context = new DirectiveCheckpointValidationContext(
            correlation,
            new DirectiveCheckpointEvidenceContext([GroundedEvidence]),
            responsibilityRetained: false,
            OutcomeRequiredIntervention.HumanApproval);

        var result = DirectiveCheckpointValidator.ValidateForProgress(checkpoint, context);

        Assert.Equal(
            [
                DirectiveCheckpointValidationCode.InterventionRequired,
                DirectiveCheckpointValidationCode.ResponsibilityNotRetained,
                DirectiveCheckpointValidationCode.BlockersPresent,
                DirectiveCheckpointValidationCode.NoCompletedSubtask,
                DirectiveCheckpointValidationCode.NextSubtaskMissing,
            ],
            result.Errors.Select(error => error.Code));
    }

    [Fact]
    public void Progress_validation_rejects_a_completed_next_subtask()
    {
        var checkpoint = Checkpoint(nextSubtaskId: "inspect");

        var result = DirectiveCheckpointValidator.ValidateForProgress(
            checkpoint,
            ValidationContext(checkpoint.Correlation));

        var error = Assert.Single(result.Errors);
        Assert.Equal(
            DirectiveCheckpointValidationCode.NextSubtaskAlreadyCompleted,
            error.Code);
        Assert.Equal("next_subtask_id", error.Path);
    }

    [Fact]
    public void Unsupported_versions_and_oversized_projection_fail_with_closed_codes()
    {
        var largePlan = new DirectiveCheckpointPlan(
            2,
            Enumerable.Range(1, DirectiveCheckpointContractLimits.MaximumSubtasks)
                .Select(index => new DirectiveCheckpointSubtask(
                    index,
                    $"task-{index:D2}",
                    new string('o', 400),
                    [new string('c', 200)],
                    TimeSpan.FromMinutes(index))));
        var checkpoint = new DirectiveCheckpoint(
            2,
            1,
            largePlan,
            Correlation(),
            nextSubtaskId: "task-01");

        Assert.False(DirectiveCheckpointContextProjector.TryProject(checkpoint, out var projection));
        Assert.Null(projection);

        var result = DirectiveCheckpointValidator.Validate(
            checkpoint,
            ValidationContext(checkpoint.Correlation));

        Assert.Equal(
            [
                DirectiveCheckpointValidationCode.ProjectionBudgetExceeded,
                DirectiveCheckpointValidationCode.UnsupportedCheckpointVersion,
                DirectiveCheckpointValidationCode.UnsupportedPlanVersion,
            ],
            result.Errors.Select(error => error.Code));
        Assert.Equal(
            ["$projection", "contract_version", "plan.contract_version"],
            result.Errors.Select(error => error.Path));
        Assert.All(result.Errors, error =>
            Assert.False(string.IsNullOrWhiteSpace(
                DirectiveCheckpointValidationCodeContract.ToWireValue(error.Code))));
    }

    private static DirectiveCheckpoint Checkpoint(string? nextSubtaskId = "verify")
    {
        var correlation = Correlation();
        return new DirectiveCheckpoint(
            DirectiveCheckpointContractVersions.V1,
            revision: 1,
            Plan(),
            correlation,
            [Completed("inspect", GroundedEvidence)],
            nextSubtaskId: nextSubtaskId);
    }

    private static DirectiveCheckpointPlan Plan() => new(
        DirectiveCheckpointContractVersions.V1,
        [Subtask(1, "inspect"), Subtask(2, "verify")]);

    private static DirectiveCheckpointSubtask Subtask(
        int sequence,
        string localId,
        IEnumerable<string>? criteria = null) => new(
        sequence,
        localId,
        $"Objective for {localId}",
        criteria ?? ["criterion"],
        TimeSpan.FromMinutes(sequence));

    private static CompletedDirectiveCheckpointSubtask Completed(
        string localId,
        params OutcomeEvidenceReference[] evidence) => new(localId, evidence);

    private static DirectiveCheckpointValidationContext ValidationContext(
        DirectiveCheckpointCorrelation correlation) => new(
        correlation,
        new DirectiveCheckpointEvidenceContext([GroundedEvidence]),
        responsibilityRetained: true,
        OutcomeRequiredIntervention.None);

    private static DirectiveCheckpointCorrelation Correlation() => new(
        OrganizationId.From("acme"),
        PositionId.From("triage"),
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        DirectiveId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        PositionTaskId.From(Guid.Parse("44444444-4444-4444-4444-444444444444")));
}
