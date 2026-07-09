using Hive.Domain.Auditing;
using Hive.Domain.Identity;

namespace Hive.Tests;

public sealed class JourneyAuditIdempotencyKeyTests
{
    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-0000000011a0"));
    private static readonly DirectiveId Directive =
        DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-0000000011a0"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-0000000011a0"));
    private static readonly PositionId Position = PositionId.From("triage-agent");

    [Fact]
    public void From_is_deterministic_for_same_logical_persistence_point()
    {
        var first = JourneyAuditIdempotencyKey.From(
            JourneyAuditStage.GatewayCalled,
            JourneyAuditOutcome.Succeeded,
            Organization,
            Thread,
            Message,
            Directive,
            Position);
        var second = JourneyAuditIdempotencyKey.From(
            JourneyAuditStage.GatewayCalled,
            JourneyAuditOutcome.Succeeded,
            Organization,
            Thread,
            Message,
            Directive,
            Position);

        Assert.Equal(first, second);
        Assert.Equal(first.Value, second.Value);
        Assert.Equal(first.AuditEventId, second.AuditEventId);
    }

    [Fact]
    public void From_changes_when_phase_identity_or_result_type_changes()
    {
        var baseline = JourneyAuditIdempotencyKey.From(
            JourneyAuditStage.ResultMessageCreated,
            JourneyAuditOutcome.Succeeded,
            Organization,
            Thread,
            Message,
            Directive,
            Position,
            "Report");

        Assert.NotEqual(
            baseline,
            JourneyAuditIdempotencyKey.From(
                JourneyAuditStage.AgentDecided,
                JourneyAuditOutcome.Succeeded,
                Organization,
                Thread,
                Message,
                Directive,
                Position,
                "Report"));
        Assert.NotEqual(
            baseline,
            JourneyAuditIdempotencyKey.From(
                JourneyAuditStage.ResultMessageCreated,
                JourneyAuditOutcome.Succeeded,
                Organization,
                Thread,
                MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-0000000011a1")),
                Directive,
                Position,
                "Report"));
        Assert.NotEqual(
            baseline,
            JourneyAuditIdempotencyKey.From(
                JourneyAuditStage.ResultMessageCreated,
                JourneyAuditOutcome.Succeeded,
                Organization,
                Thread,
                Message,
                Directive,
                Position,
                "Escalation"));
    }

    [Fact]
    public void From_excludes_free_text_from_the_canonical_key()
    {
        var key = JourneyAuditIdempotencyKey.From(
            JourneyAuditStage.AgentDecided,
            JourneyAuditOutcome.Succeeded,
            Organization,
            Thread,
            Message,
            Directive,
            Position,
            "Report");

        Assert.DoesNotContain("Customer reports checkout failures", key.Value, StringComparison.Ordinal);
        Assert.DoesNotContain("provider output", key.Value, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("prompt", key.Value, StringComparison.OrdinalIgnoreCase);
    }
}
