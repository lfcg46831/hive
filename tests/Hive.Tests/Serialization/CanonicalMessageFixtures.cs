using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests.Serialization;

/// <summary>
/// Deterministic, hand-pinned instances of every canonical <see cref="OrgMessage"/> type
/// (US-F0-03-T09). Unlike the round-trip samples (US-F0-03-T08), these use fixed identities,
/// timestamps and values so the serialized payload is stable byte-for-byte across runs and can be
/// compared against the committed golden snapshots under <c>Fixtures/Serialization</c>. The set is
/// chosen to exercise every endpoint variant (§9.2), both report kinds and a spread of priorities,
/// so any drift in the wire format or the manifest is caught by a snapshot test.
/// </summary>
internal static class CanonicalMessageFixtures
{
    /// <summary>Fixed send timestamp shared by all fixtures (UTC, no fractional seconds).</summary>
    public static readonly DateTimeOffset SentAt = new(2026, 6, 21, 10, 0, 0, TimeSpan.Zero);

    private const string Org = "acme";

    /// <summary>Every canonical message paired with the manifest it must serialize under.</summary>
    public static IReadOnlyList<(string Manifest, OrgMessage Message)> All { get; } =
    [
        ("directive", CreateDirective()),
        ("report", CreateReport()),
        ("memo", CreateMemo()),
        ("escalation", CreateEscalation()),
        ("peer-request", CreatePeerRequest()),
        ("peer-response", CreatePeerResponse()),
        ("approval-request", CreateApprovalRequest()),
        ("approval-decision", CreateApprovalDecision()),
        ("pulse", CreatePulse()),
        ("event-trigger", CreateEventTrigger()),
    ];

    private static Directive CreateDirective() =>
        new(
            MessageId.From(new Guid("d1000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("delivery-lead"),
            Position("bug-triage"),
            ThreadId.From(new Guid("d1000000-0000-0000-0000-0000000000a1")),
            Priority.High,
            1,
            SentAt,
            SentAt.AddHours(4),
            DirectiveId.From(new Guid("d1000000-0000-0000-0000-0000000000c1")),
            DirectiveId.From(new Guid("d1000000-0000-0000-0000-0000000000c0")),
            "Triage the reported regression",
            "Customer impact is under investigation");

    private static Report CreateReport() =>
        new(
            MessageId.From(new Guid("d2000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("bug-triage"),
            Position("delivery-lead"),
            ThreadId.From(new Guid("d2000000-0000-0000-0000-0000000000a1")),
            Priority.Normal,
            1,
            SentAt,
            null,
            DirectiveId.From(new Guid("d2000000-0000-0000-0000-0000000000c1")),
            ReportKind.Progress,
            "Reproduction confirmed on the latest build");

    private static Memo CreateMemo() =>
        new(
            MessageId.From(new Guid("d3000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("bug-triage"),
            Position("release-manager"),
            ThreadId.From(new Guid("d3000000-0000-0000-0000-0000000000a1")),
            Priority.High,
            1,
            SentAt,
            null,
            "Heads up: the staging credential rotates tonight.");

    private static Escalation CreateEscalation() =>
        new(
            MessageId.From(new Guid("d4000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("bug-triage"),
            new OrganizationOwnerEndpointRef(),
            ThreadId.From(new Guid("d4000000-0000-0000-0000-0000000000a1")),
            Priority.Critical,
            1,
            SentAt,
            null,
            "Production deployment is blocked",
            "The deployment credential has expired",
            new[] { "Roll back the release", "Prepare a hotfix" });

    private static PeerRequest CreatePeerRequest() =>
        new(
            MessageId.From(new Guid("d5000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("bug-triage"),
            Position("qa"),
            ThreadId.From(new Guid("d5000000-0000-0000-0000-0000000000a1")),
            Priority.Normal,
            1,
            SentAt,
            null,
            "Can you confirm the regression on iOS 17?");

    private static PeerResponse CreatePeerResponse() =>
        new(
            MessageId.From(new Guid("d6000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("qa"),
            Position("bug-triage"),
            ThreadId.From(new Guid("d6000000-0000-0000-0000-0000000000a1")),
            Priority.Normal,
            1,
            SentAt,
            null,
            MessageId.From(new Guid("d6000000-0000-0000-0000-0000000000b1")),
            "Confirmed on iOS 17.");

    private static ApprovalRequest CreateApprovalRequest() =>
        new(
            MessageId.From(new Guid("d7000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("release-manager"),
            Position("vp-eng"),
            ThreadId.From(new Guid("d7000000-0000-0000-0000-0000000000a1")),
            Priority.High,
            1,
            SentAt,
            null,
            "Ship the hotfix to production",
            "Customer-facing outage in progress",
            ApprovalPolicyRef.From("policy:production-deploy"));

    private static ApprovalDecision CreateApprovalDecision() =>
        new(
            MessageId.From(new Guid("d8000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            new OrganizationOwnerEndpointRef(),
            Position("release-manager"),
            ThreadId.From(new Guid("d8000000-0000-0000-0000-0000000000a1")),
            Priority.High,
            1,
            SentAt,
            null,
            MessageId.From(new Guid("d8000000-0000-0000-0000-0000000000b1")),
            true,
            "Approved given the active outage.");

    private static Pulse CreatePulse() =>
        new(
            MessageId.From(new Guid("d9000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            Position("ops"),
            ThreadId.From(new Guid("d9000000-0000-0000-0000-0000000000a1")),
            Priority.Low,
            1,
            SentAt,
            null,
            "daily-rollup",
            "{}");

    private static EventTrigger CreateEventTrigger() =>
        new(
            MessageId.From(new Guid("da000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            new SystemEndpointRef(SystemEndpointKind.DomainEvents),
            Position("ops"),
            ThreadId.From(new Guid("da000000-0000-0000-0000-0000000000a1")),
            Priority.Normal,
            1,
            SentAt,
            null,
            "budget.threshold.crossed",
            "{\"budget\":\"q3\"}");

    private static PositionEndpointRef Position(string value) => new(PositionId.From(value));
}
