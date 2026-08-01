using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class DirectiveCheckpointPersistenceTests
{
    private static readonly DateTimeOffset At = new(
        2026,
        7,
        31,
        14,
        0,
        0,
        TimeSpan.Zero);
    private static readonly PositionEntityId Entity = PositionEntityId.From(
        OrganizationId.From("acme"),
        PositionId.From("triage"));

    [Fact]
    public void State_applies_checkpoint_revisions_and_snapshot_restore_preserves_latest_revision()
    {
        var first = Checkpoint(revision: 1, completed: ["inspect"], next: "verify");
        var second = Checkpoint(revision: 2, completed: ["inspect", "verify"], next: null);

        var state = PositionState.Empty
            .Apply(new DirectiveCheckpointPersisted(first, At))
            .Apply(new DirectiveCheckpointPersisted(second, At.AddMinutes(1)))
            .Apply(new DirectiveCheckpointPersisted(first, At.AddMinutes(2)));

        var persisted = Assert.Single(state.DirectiveCheckpoints).Value;
        Assert.Equal(2, persisted.Revision);
        Assert.Equal(["inspect", "verify"], persisted.CompletedSubtasks.Select(item => item.LocalId));

        var snapshot = state.ToSnapshot(At.AddMinutes(3));
        var restored = PositionState.Restore(snapshot);

        Assert.Equal(2, Assert.Single(snapshot.DirectiveCheckpoints).Revision);
        Assert.Equal(persisted, Assert.Single(restored.DirectiveCheckpoints).Value);
    }

    [Fact]
    public void Persistence_decision_is_idempotent_and_rejects_gaps_regression_or_drift()
    {
        var first = Checkpoint(revision: 1, completed: ["inspect"], next: "verify");
        var state = PositionState.Empty.Apply(new DirectiveCheckpointPersisted(first, At));

        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.AlreadyPersisted,
            state.EvaluateDirectiveCheckpointPersistence(Entity, first));
        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Persist,
            state.EvaluateDirectiveCheckpointPersistence(
                Entity,
                Checkpoint(revision: 2, completed: ["inspect", "verify"], next: null)));
        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Rejected,
            state.EvaluateDirectiveCheckpointPersistence(
                Entity,
                Checkpoint(revision: 3, completed: ["inspect", "verify"], next: null)));
        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Rejected,
            state.EvaluateDirectiveCheckpointPersistence(
                Entity,
                Checkpoint(revision: 2, completed: [], next: "inspect")));
        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Rejected,
            state.EvaluateDirectiveCheckpointPersistence(
                Entity,
                Checkpoint(revision: 1, completed: ["inspect"], next: null)));

        var changedPlan = new DirectiveCheckpoint(
            DirectiveCheckpointContractVersions.V1,
            revision: 2,
            new DirectiveCheckpointPlan(
                DirectiveCheckpointContractVersions.V1,
                [
                    Subtask(1, "inspect", "Changed objective"),
                    Subtask(2, "verify", "Objective for verify"),
                ]),
            Correlation(),
            [Completed("inspect"), Completed("verify")]);
        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Rejected,
            state.EvaluateDirectiveCheckpointPersistence(Entity, changedPlan));
    }

    [Fact]
    public void Persistence_rejects_wrong_position_unknown_subtasks_and_oversized_projection()
    {
        var checkpoint = Checkpoint(revision: 1, completed: ["inspect"], next: "verify");
        var otherEntity = PositionEntityId.From(
            Entity.Organization,
            PositionId.From("other"));
        var unknown = new DirectiveCheckpoint(
            1,
            1,
            Plan(),
            Correlation(),
            [Completed("unknown")]);
        var oversized = new DirectiveCheckpoint(
            1,
            1,
            new DirectiveCheckpointPlan(
                1,
                Enumerable.Range(1, DirectiveCheckpointContractLimits.MaximumSubtasks)
                    .Select(index => new DirectiveCheckpointSubtask(
                        index,
                        $"task-{index:D2}",
                        new string('o', 400),
                        [new string('c', 200)],
                        TimeSpan.FromMinutes(index)))),
            Correlation(),
            nextSubtaskId: "task-01");

        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Rejected,
            PositionState.Empty.EvaluateDirectiveCheckpointPersistence(otherEntity, checkpoint));
        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Rejected,
            PositionState.Empty.EvaluateDirectiveCheckpointPersistence(Entity, unknown));
        Assert.Equal(
            DirectiveCheckpointPersistenceDecision.Rejected,
            PositionState.Empty.EvaluateDirectiveCheckpointPersistence(Entity, oversized));
    }

    private static DirectiveCheckpoint Checkpoint(
        int revision,
        IEnumerable<string> completed,
        string? next) => new(
        DirectiveCheckpointContractVersions.V1,
        revision,
        Plan(),
        Correlation(),
        completed.Select(Completed),
        nextSubtaskId: next);

    private static DirectiveCheckpointPlan Plan() => new(
        DirectiveCheckpointContractVersions.V1,
        [
            Subtask(1, "inspect", "Objective for inspect"),
            Subtask(2, "verify", "Objective for verify"),
        ]);

    private static DirectiveCheckpointSubtask Subtask(
        int sequence,
        string id,
        string objective) => new(
        sequence,
        id,
        objective,
        ["criterion"],
        TimeSpan.FromMinutes(sequence));

    private static CompletedDirectiveCheckpointSubtask Completed(string id) => new(
        id,
        [new OutcomeEvidenceReference(
            OutcomeEvidenceSource.PersistedState,
            $"state.{id}")]);

    private static DirectiveCheckpointCorrelation Correlation() => new(
        Entity.Organization,
        Entity.Position,
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
        DirectiveId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
        DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
        PositionTaskId.From(Guid.Parse("44444444-4444-4444-4444-444444444444")));
}
