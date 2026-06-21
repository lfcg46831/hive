using System.Collections.Immutable;
using Akka.Actor;
using Hive.Actors.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Tests.Serialization;

namespace Hive.Tests;

/// <summary>
/// Serialize → deserialize round-trip tests for every type of the canonical taxonomy
/// (US-F0-03-T10). Where the serializer-binding tests (US-F0-03-T08) prove the round-trip is
/// lossless at the byte level (re-serializing the restored value reproduces the same payload) and
/// the snapshot tests (US-F0-03-T09) pin the on-the-wire shape, these tests prove the other half:
/// that deserialization rebuilds an equal <em>domain object</em> — every value object, enum, the
/// discriminated <see cref="EndpointRef"/> union, nullable members and the one immutable collection
/// are reconstructed with their original values. The matrix deliberately spans both nullable states
/// of <c>Deadline</c> and <c>ParentDirectiveId</c>, both <see cref="ReportKind"/> values, both
/// <see cref="ApprovalDecision"/> branches (with and without a reason), an empty and a populated
/// <see cref="Escalation.OptionsConsidered"/>, every <see cref="Priority"/> level and every endpoint
/// variant, so a regression in any converter surfaces as a failed assertion rather than a silent
/// data loss.
/// </summary>
public sealed class OrgMessageRoundTripTests : IClassFixture<OrgMessageRoundTripTests.SerializerFixture>
{
    private static readonly DateTimeOffset SentAt = new(2026, 6, 21, 10, 0, 0, TimeSpan.Zero);
    private const string Org = "acme";

    private readonly OrgMessageJsonSerializer _serializer;

    public OrgMessageRoundTripTests(SerializerFixture fixture)
    {
        _serializer = fixture.Serializer;
    }

    /// <summary>The pinned, taxonomy-complete fixtures shared with the snapshot suite (US-F0-03-T09).</summary>
    public static TheoryData<string, OrgMessage> CanonicalMessages
    {
        get
        {
            var data = new TheoryData<string, OrgMessage>();
            foreach (var (manifest, message) in CanonicalMessageFixtures.All)
            {
                data.Add(manifest, message);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalMessages))]
    public void Round_trips_every_canonical_taxonomy_type(string manifest, OrgMessage original)
    {
        var restored = RoundTrip(original, manifest);

        Assert.IsType(original.GetType(), restored);
        AssertEnvelopeEqual(original, restored);
        AssertEqual(original, restored);
    }

    [Theory]
    [MemberData(nameof(VariantMatrix))]
    public void Round_trips_each_field_and_endpoint_variant(OrgMessage original)
    {
        var restored = RoundTrip(original);

        Assert.IsType(original.GetType(), restored);
        AssertEnvelopeEqual(original, restored);
        AssertEqual(original, restored);
    }

    [Theory]
    [MemberData(nameof(CanonicalMessages))]
    public void Restored_value_round_trips_again_unchanged(string manifest, OrgMessage original)
    {
        // Deserializing, re-serializing and deserializing again must reach the same object. This
        // guards against a converter that is only stable on the first pass (e.g. one that normalizes
        // a value on read but not on write).
        var first = RoundTrip(original, manifest);
        var second = RoundTrip(first, manifest);

        AssertEnvelopeEqual(first, second);
        AssertEqual(first, second);
    }

    [Fact]
    public void Every_concrete_canonical_type_has_round_trip_coverage()
    {
        // Drawn straight from the domain assembly: any concrete OrgMessage subtype that ships without
        // a round-trip fixture fails here, so a newly added canonical message cannot merge without
        // proving it serializes both ways.
        var canonicalTypes = typeof(OrgMessage).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(OrgMessage)))
            .ToHashSet();

        var coveredTypes = CanonicalMessageFixtures.All
            .Select(entry => entry.Message.GetType())
            .ToHashSet();

        var missing = canonicalTypes.Except(coveredTypes).Select(type => type.Name);
        var unexpected = coveredTypes.Except(canonicalTypes).Select(type => type.Name);

        Assert.True(
            canonicalTypes.SetEquals(coveredTypes),
            $"Canonical message types without round-trip coverage: [{string.Join(", ", missing)}]; " +
            $"round-trip fixtures for unknown types: [{string.Join(", ", unexpected)}].");
    }

    /// <summary>
    /// Explicit per-field and per-endpoint variants. Each instance toggles exactly the dimensions the
    /// envelope and converters have to preserve, so a failure points at the responsible member.
    /// </summary>
    public static TheoryData<OrgMessage> VariantMatrix
    {
        get
        {
            var data = new TheoryData<OrgMessage>();

            // Deadline present and absent on the same type.
            data.Add(Memo(deadline: SentAt.AddHours(2)));
            data.Add(Memo(deadline: null));

            // Optional lineage present and absent.
            data.Add(Directive(parent: DirectiveId.From(new Guid("11111111-0000-0000-0000-0000000000ff"))));
            data.Add(Directive(parent: null));

            // Both closed report kinds.
            data.Add(Report(ReportKind.Progress));
            data.Add(Report(ReportKind.Done));

            // Both governance decision branches, with and without a reason.
            data.Add(ApprovalDecision(approved: true, reason: "Approved given the active outage."));
            data.Add(ApprovalDecision(approved: false, reason: null));

            // Empty and populated immutable collection.
            data.Add(Escalation(options: Array.Empty<string>()));
            data.Add(Escalation(options: new[] { "Roll back the release", "Prepare a hotfix" }));

            // Full priority spread (closed set §9.3).
            foreach (var priority in new[] { Priority.Low, Priority.Normal, Priority.High, Priority.Critical })
            {
                data.Add(Memo(priority: priority));
            }

            // Every endpoint variant (§9.2) on both origin and destination.
            data.Add(Memo(from: Position("a"), to: Position("b")));
            data.Add(Escalation(to: new OrganizationOwnerEndpointRef()));
            data.Add(ApprovalDecision(from: new OrganizationOwnerEndpointRef()));
            data.Add(Pulse(from: new SystemEndpointRef(SystemEndpointKind.Scheduler)));
            data.Add(EventTrigger(from: new SystemEndpointRef(SystemEndpointKind.DomainEvents)));

            return data;
        }
    }

    private OrgMessage RoundTrip(OrgMessage message, string? manifest = null)
    {
        manifest ??= _serializer.Manifest(message);
        var payload = _serializer.ToBinary(message);
        return (OrgMessage)_serializer.FromBinary(payload, manifest);
    }

    private static void AssertEnvelopeEqual(OrgMessage expected, OrgMessage actual)
    {
        Assert.Equal(expected.Id, actual.Id);
        Assert.Equal(expected.OrganizationId, actual.OrganizationId);
        Assert.Equal(expected.From, actual.From);
        Assert.Equal(expected.To, actual.To);
        Assert.Equal(expected.Thread, actual.Thread);
        Assert.Equal(expected.Priority, actual.Priority);
        Assert.Equal(expected.SchemaVersion, actual.SchemaVersion);
        Assert.Equal(expected.SentAt, actual.SentAt);
        Assert.Equal(expected.Deadline, actual.Deadline);
        Assert.Equal(expected.Channel, actual.Channel);
    }

    /// <summary>
    /// Asserts the restored value equals the original. Record value-equality covers every type except
    /// <see cref="Escalation"/>, whose <see cref="ImmutableArray{T}"/> member compares by reference;
    /// for it the payload-bearing fields are compared explicitly and the collection by sequence.
    /// </summary>
    private static void AssertEqual(OrgMessage expected, OrgMessage actual)
    {
        if (expected is Escalation expectedEscalation)
        {
            var actualEscalation = Assert.IsType<Escalation>(actual);
            Assert.Equal(expectedEscalation.Issue, actualEscalation.Issue);
            Assert.Equal(expectedEscalation.Context, actualEscalation.Context);
            Assert.Equal(expectedEscalation.OptionsConsidered, actualEscalation.OptionsConsidered);
            return;
        }

        Assert.Equal(expected, actual);
    }

    private static Memo Memo(
        Priority priority = Priority.High,
        DateTimeOffset? deadline = null,
        EndpointRef? from = null,
        EndpointRef? to = null) =>
        new(
            MessageId.From(new Guid("a0000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            from ?? Position("bug-triage"),
            to ?? Position("release-manager"),
            ThreadId.From(new Guid("a0000000-0000-0000-0000-0000000000a1")),
            priority,
            1,
            SentAt,
            deadline,
            "Heads up: the staging credential rotates tonight.");

    private static Domain.Messaging.Directive Directive(DirectiveId? parent) =>
        new(
            MessageId.From(new Guid("b0000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("delivery-lead"),
            Position("bug-triage"),
            ThreadId.From(new Guid("b0000000-0000-0000-0000-0000000000a1")),
            Priority.High,
            1,
            SentAt,
            SentAt.AddHours(4),
            DirectiveId.From(new Guid("b0000000-0000-0000-0000-0000000000c1")),
            parent,
            "Triage the reported regression",
            "Customer impact is under investigation");

    private static Report Report(ReportKind kind) =>
        new(
            MessageId.From(new Guid("c0000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("bug-triage"),
            Position("delivery-lead"),
            ThreadId.From(new Guid("c0000000-0000-0000-0000-0000000000a1")),
            Priority.Normal,
            1,
            SentAt,
            null,
            DirectiveId.From(new Guid("c0000000-0000-0000-0000-0000000000c1")),
            kind,
            "Reproduction confirmed on the latest build");

    private static ApprovalDecision ApprovalDecision(
        bool approved = true,
        string? reason = "Approved given the active outage.",
        EndpointRef? from = null) =>
        new(
            MessageId.From(new Guid("d0000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            from ?? new OrganizationOwnerEndpointRef(),
            Position("release-manager"),
            ThreadId.From(new Guid("d0000000-0000-0000-0000-0000000000a1")),
            Priority.High,
            1,
            SentAt,
            null,
            MessageId.From(new Guid("d0000000-0000-0000-0000-0000000000b1")),
            approved,
            reason);

    private static Escalation Escalation(
        IEnumerable<string>? options = null,
        EndpointRef? to = null) =>
        new(
            MessageId.From(new Guid("e0000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            Position("bug-triage"),
            to ?? new OrganizationOwnerEndpointRef(),
            ThreadId.From(new Guid("e0000000-0000-0000-0000-0000000000a1")),
            Priority.Critical,
            1,
            SentAt,
            null,
            "Production deployment is blocked",
            "The deployment credential has expired",
            options ?? new[] { "Roll back the release", "Prepare a hotfix" });

    private static Pulse Pulse(EndpointRef from) =>
        new(
            MessageId.From(new Guid("f0000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            from,
            Position("ops"),
            ThreadId.From(new Guid("f0000000-0000-0000-0000-0000000000a1")),
            Priority.Low,
            1,
            SentAt,
            null,
            "daily-rollup",
            "{}");

    private static EventTrigger EventTrigger(EndpointRef from) =>
        new(
            MessageId.From(new Guid("fa000000-0000-0000-0000-000000000001")),
            OrganizationId.From(Org),
            from,
            Position("ops"),
            ThreadId.From(new Guid("fa000000-0000-0000-0000-0000000000a1")),
            Priority.Normal,
            1,
            SentAt,
            null,
            "budget.threshold.crossed",
            "{\"budget\":\"q3\"}");

    private static PositionEndpointRef Position(string value) => new(PositionId.From(value));

    public sealed class SerializerFixture : IDisposable
    {
        private readonly ActorSystem _system;

        public SerializerFixture()
        {
            _system = ActorSystem.Create("org-message-round-trip-tests");
            Serializer = new OrgMessageJsonSerializer((ExtendedActorSystem)_system);
        }

        public OrgMessageJsonSerializer Serializer { get; }

        public void Dispose() => _system.Dispose();
    }
}
