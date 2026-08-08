using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Governance;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;

namespace Hive.Tests.Serialization;

/// <summary>
/// Deterministic persisted PositionActor protocol values for US-F0-06-T13b. The set covers the base
/// PositionEvent journal facts and one persisted snapshot; runtime-configuration stamp fixtures are
/// intentionally left to US-F0-06-T08c.
/// </summary>
internal static class CanonicalPositionProtocolFixtures
{
    public static readonly DateTimeOffset OccurredAt = new(2026, 6, 26, 10, 0, 0, TimeSpan.Zero);
    public static readonly DateTimeOffset SnapshotTakenAt = new(2026, 6, 26, 10, 5, 0, TimeSpan.Zero);

    public static IReadOnlyList<(string Manifest, object Value)> All { get; } =
    [
        ("message-received", new MessageReceived(Message(), OccurredAt)),
        ("task-created", new TaskCreated(TaskId(), ThreadId(), "triage incoming regression", Priority.High, OccurredAt, OccurredAt.AddHours(3), MessageId())),
        ("task-updated", new TaskUpdated(TaskId(), "reproduced on staging", OccurredAt.AddMinutes(5), Priority.Critical, OccurredAt.AddHours(1))),
        ("task-completed", new TaskCompleted(TaskId(), OccurredAt.AddMinutes(15), "hotfix shipped")),
        ("short-memory-updated", new ShortMemoryUpdated("current-thread", "customer-impact", OccurredAt.AddMinutes(20))),
        ("occupant-changed", new OccupantChanged(OccupantId.From("agent-7"), OccupantType.AiAgent, OccurredAt.AddMinutes(25))),
        ("message-dispatched", new MessageDispatched(MessageId(), ThreadId(), OccupantId.From("agent-7"), OccupantType.AiAgent, OccurredAt.AddMinutes(30))),
        ("message-processing-completed", new MessageProcessingCompleted("message:completed", MessageId(), ThreadId(), MessageProcessingCompletionStatus.Completed, OccurredAt.AddMinutes(35))),
        ("occupant-reply-emitted", new OccupantReplyEmitted(
            MessageId(),
            OccupantReplyAuthor.HumanUser("person-alice", "web-inbox"),
            OccupantReply(),
            OccurredAt.AddMinutes(40))),
        ("position-passivated", new PositionPassivated(OccurredAt.AddMinutes(45), "idle")),
        ("action-retained", new ActionRetained(RetainedAction())),
        ("retained-action-authorized", new RetainedActionAuthorized(Grant(), OccurredAt.AddMinutes(41))),
        ("retained-action-denied", new RetainedActionDenied(Denial(), OccurredAt.AddMinutes(41))),
        ("retained-action-consumed", new RetainedActionConsumed(ActionId(), GrantMessageId(), OccurredAt.AddMinutes(42))),
        ("retained-action-expired", new RetainedActionExpired(ActionId(), GrantMessageId(), "authorization-expired", OccurredAt.AddMinutes(42))),
        ("retained-action-returned", new RetainedActionReturned(ActionId(), GrantMessageId(), "policy-tightened", OccurredAt.AddMinutes(42))),
        ("directive-checkpoint-persisted", new DirectiveCheckpointPersisted(
            Checkpoint(),
            OccurredAt.AddMinutes(43))),
        ("position-snapshot", Snapshot()),
    ];

    public static IReadOnlyList<(string Manifest, PositionEvent Event)> BaseEvents { get; } =
        All.Where(entry => entry.Value is PositionEvent)
            .Select(entry => (entry.Manifest, Event: (PositionEvent)entry.Value))
            .ToArray();

    private static PositionSnapshot Snapshot() =>
        new(
            SnapshotTakenAt,
            OccupantId.From("agent-7"),
            OccupantType.AiAgent,
            new[] { Message() },
            new[]
            {
                new PersistedTask(TaskId(), ThreadId(), "triage incoming regression", Priority.Critical, OccurredAt, OccurredAt.AddHours(1), MessageId()),
            },
            new Dictionary<string, string> { ["current-thread"] = "customer-impact" },
            new[] { MessageId() },
            new[] { MessageId() },
            lastConfigurationStamp: null);

    private static DirectiveCheckpoint Checkpoint() => new(
        DirectiveCheckpointContractVersions.V1,
        revision: 1,
        new DirectiveCheckpointPlan(
            DirectiveCheckpointContractVersions.V1,
            [
                new DirectiveCheckpointSubtask(
                    1,
                    "inspect",
                    "Inspect the work",
                    ["inspection recorded"],
                    TimeSpan.FromMinutes(1)),
                new DirectiveCheckpointSubtask(
                    2,
                    "verify",
                    "Verify the work",
                    ["verification recorded"],
                    TimeSpan.FromMinutes(2)),
            ]),
        new DirectiveCheckpointCorrelation(
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            ThreadId(),
            DirectiveId.From(new Guid("d3000000-0000-0000-0000-0000000000d1")),
            DirectiveId.From(new Guid("d3000000-0000-0000-0000-0000000000d0")),
            TaskId()),
        [
            new CompletedDirectiveCheckpointSubtask(
                "inspect",
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.PersistedState,
                    "state.inspect")]),
        ],
        nextSubtaskId: "verify");

    private static OrgMessage Message() =>
        CanonicalMessageFixtures.All.Single(entry => entry.Manifest == "memo").Message;

    private static OrgMessage OccupantReply() => new PeerResponse(
        Hive.Domain.Identity.MessageId.From(
            new Guid("d3000000-0000-0000-0000-000000000002")),
        OrganizationId.From("acme"),
        new PositionEndpointRef(PositionId.From("release-manager")),
        new PositionEndpointRef(PositionId.From("bug-triage")),
        ThreadId(),
        Priority.High,
        schemaVersion: 1,
        OccurredAt.AddMinutes(40),
        deadline: null,
        MessageId(),
        "The release window is confirmed.");

    private static MessageId MessageId() =>
        Hive.Domain.Identity.MessageId.From(new Guid("d3000000-0000-0000-0000-000000000001"));

    private static ThreadId ThreadId() =>
        Hive.Domain.Identity.ThreadId.From(new Guid("d3000000-0000-0000-0000-0000000000a1"));

    private static PositionTaskId TaskId() =>
        PositionTaskId.From(new Guid("c6000000-0000-0000-0000-000000000001"));

    private static PersistedRetainedAction RetainedAction() =>
        new(
            RetainedActionId.From(new Guid("f9000000-0000-0000-0000-000000000001")),
            ActionFingerprint.From("sha256:0000000000000000000000000000000000000000000000000000000000000004"),
            RetainedActionKind.OrganizationalMessage,
            "Memo",
            "{\"body\":\"Customer reported a regression.\"}",
            "{}",
            "directive:retained",
            OrganizationId.From("acme"),
            PositionId.From("delivery-lead"),
            ThreadId(),
            MessageId(),
            DirectiveId.From(new Guid("f9000000-0000-0000-0000-0000000000d1")),
            null,
            "action-gate-escalation-required",
            OccurredAt.AddMinutes(40),
            governanceMessages: new[] { Message() });

    private static RetainedActionId ActionId() =>
        Hive.Domain.Identity.RetainedActionId.From(new Guid("f9000000-0000-0000-0000-000000000001"));

    private static MessageId GrantMessageId() =>
        Hive.Domain.Identity.MessageId.From(new Guid("f9000000-0000-0000-0000-0000000000a1"));

    private static AuthorizationGrant Grant() =>
        new(
            GrantMessageId(),
            OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            ThreadId(),
            Priority.High,
            1,
            OccurredAt.AddMinutes(41),
            null,
            MessageId(),
            ActionId(),
            ActionFingerprint.From("sha256:0000000000000000000000000000000000000000000000000000000000000004"),
            AuthorityKey.From("governance.authorize-retained-action"),
            OccurredAt.AddHours(2),
            "Approved for this retained action.");

    private static AuthorizationDenial Denial() =>
        new(
            Hive.Domain.Identity.MessageId.From(new Guid("f9000000-0000-0000-0000-0000000000b1")),
            OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            ThreadId(),
            Priority.High,
            1,
            OccurredAt.AddMinutes(41),
            null,
            MessageId(),
            ActionId(),
            "Denied by organization owner.");
}
