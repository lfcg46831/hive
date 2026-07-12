using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Hive.Actors.Serialization;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;

namespace Hive.Tests;

/// <summary>
/// Verifies the versionable System.Text.Json serializer for the organizational message protocol
/// (US-F0-03-T08, ADR-007): stable manifests, lossless round-trips for every canonical message type,
/// canonical wire values, the discriminated endpoint union, tolerant reads and controlled rejection
/// of malformed payloads.
/// </summary>
public sealed class OrgMessageJsonSerializerTests : IClassFixture<OrgMessageJsonSerializerTests.SerializerFixture>
{
    private static readonly DateTimeOffset SentAt = new(2026, 6, 21, 10, 0, 0, TimeSpan.Zero);

    private readonly OrgMessageJsonSerializer _serializer;

    public OrgMessageJsonSerializerTests(SerializerFixture fixture)
    {
        _serializer = fixture.Serializer;
    }

    public static TheoryData<string, OrgMessage> CanonicalMessages
    {
        get
        {
            var data = new TheoryData<string, OrgMessage>();
            foreach (var (manifest, message) in Samples())
            {
                data.Add(manifest, message);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalMessages))]
    public void Emits_the_expected_manifest(string manifest, OrgMessage message)
    {
        Assert.Equal(manifest, _serializer.Manifest(message));
    }

    [Theory]
    [MemberData(nameof(CanonicalMessages))]
    public void Round_trips_every_canonical_message_type(string manifest, OrgMessage message)
    {
        var payload = _serializer.ToBinary(message);

        var restored = _serializer.FromBinary(payload, manifest);

        Assert.IsType(message.GetType(), restored);
        // Re-serializing the restored value must produce byte-identical output, proving the
        // round-trip is lossless without relying on record equality of immutable collections.
        Assert.Equal(payload, _serializer.ToBinary(restored));
    }

    [Fact]
    public void Preserves_the_envelope_schema_version()
    {
        var message = CreateMemo(schemaVersion: 7);

        var restored = (Memo)_serializer.FromBinary(
            _serializer.ToBinary(message),
            _serializer.Manifest(message));

        Assert.Equal(7, restored.SchemaVersion);
    }

    [Fact]
    public void Does_not_emit_the_derived_channel_property()
    {
        var node = Serialize(CreateMemo());

        Assert.False(node.ContainsKey("Channel"));
        Assert.False(node.ContainsKey("channel"));
    }

    [Fact]
    public void Writes_priority_as_its_canonical_wire_value()
    {
        var node = Serialize(CreateMemo());

        Assert.Equal("high", node["Priority"]!.GetValue<string>());
    }

    [Fact]
    public void Writes_report_kind_as_its_canonical_wire_value()
    {
        var report = CreateReport(ReportKind.Done);

        var node = Serialize(report);

        Assert.Equal("done", node["Kind"]!.GetValue<string>());
    }

    [Fact]
    public void Encodes_each_endpoint_variant_with_a_kind_discriminator()
    {
        var pulse = CreatePulse();

        var node = Serialize(pulse);

        Assert.Equal("system", node["From"]!["kind"]!.GetValue<string>());
        Assert.Equal("scheduler", node["From"]!["system"]!.GetValue<string>());
        Assert.Equal("position", node["To"]!["kind"]!.GetValue<string>());
    }

    [Fact]
    public void Round_trips_the_organization_owner_endpoint()
    {
        var escalation = CreateEscalation(to: new OrganizationOwnerEndpointRef());

        var restored = (Escalation)_serializer.FromBinary(
            _serializer.ToBinary(escalation),
            _serializer.Manifest(escalation));

        Assert.IsType<OrganizationOwnerEndpointRef>(restored.To);
    }

    [Fact]
    public void Ignores_unknown_properties_on_read()
    {
        var message = CreateMemo();
        var node = Serialize(message);
        node["unknownFutureField"] = "ignored";

        var restored = (Memo)_serializer.FromBinary(
            ToBytes(node),
            _serializer.Manifest(message));

        Assert.Equal(message.Body, restored.Body);
    }

    [Fact]
    public void Rejects_payloads_missing_a_required_field()
    {
        var message = CreateMemo();
        var node = Serialize(message);
        node.Remove("OrganizationId");

        Assert.ThrowsAny<Exception>(() =>
            _serializer.FromBinary(ToBytes(node), _serializer.Manifest(message)));
    }

    [Fact]
    public void Rejects_unknown_enum_wire_values()
    {
        var message = CreateMemo();
        var node = Serialize(message);
        node["Priority"] = "urgent";

        Assert.Throws<JsonException>(() =>
            _serializer.FromBinary(ToBytes(node), _serializer.Manifest(message)));
    }

    [Fact]
    public void Rejects_an_unregistered_manifest()
    {
        var payload = _serializer.ToBinary(CreateMemo());

        Assert.Throws<ArgumentException>(() => _serializer.FromBinary(payload, "not-a-message"));
    }

    private JsonObject Serialize(OrgMessage message) =>
        JsonNode.Parse(_serializer.ToBinary(message))!.AsObject();

    private static byte[] ToBytes(JsonNode node) =>
        System.Text.Encoding.UTF8.GetBytes(node.ToJsonString());

    private static IEnumerable<(string Manifest, OrgMessage Message)> Samples()
    {
        yield return ("directive", CreateDirective());
        yield return ("report", CreateReport(ReportKind.Progress));
        yield return ("memo", CreateMemo());
        yield return ("escalation", CreateEscalation(new OrganizationOwnerEndpointRef()));
        yield return ("peer-request", CreatePeerRequest());
        yield return ("peer-response", CreatePeerResponse());
        yield return ("approval-request", CreateApprovalRequest());
        yield return ("approval-decision", CreateApprovalDecision());
        yield return ("authorization-grant", CreateAuthorizationGrant());
        yield return ("authorization-denial", CreateAuthorizationDenial());
        yield return ("pulse", CreatePulse());
        yield return ("event-trigger", CreateEventTrigger());
    }

    private static Domain.Messaging.Directive CreateDirective() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("delivery-lead"),
            Position("bug-triage"),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            SentAt.AddHours(4),
            DirectiveId.New(),
            DirectiveId.New(),
            "Triage the reported bug",
            "Customer impact is under investigation");

    private static Report CreateReport(ReportKind kind) =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("bug-triage"),
            Position("delivery-lead"),
            ThreadId.New(),
            Priority.Normal,
            1,
            SentAt,
            null,
            DirectiveId.New(),
            kind,
            "Reproduction confirmed");

    private static Memo CreateMemo(int schemaVersion = 1) =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("bug-triage"),
            Position("release-manager"),
            ThreadId.New(),
            Priority.High,
            schemaVersion,
            SentAt,
            null,
            "Heads up: the staging credential rotates tonight.");

    private static Escalation CreateEscalation(EndpointRef to) =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("bug-triage"),
            to,
            ThreadId.New(),
            Priority.Critical,
            1,
            SentAt,
            null,
            "Production deployment is blocked",
            "The deployment credential has expired",
            new[] { "Rollback", "Prepare a hotfix" });

    private static PeerRequest CreatePeerRequest() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("bug-triage"),
            Position("qa"),
            ThreadId.New(),
            Priority.Normal,
            1,
            SentAt,
            null,
            "Can you confirm the regression on iOS?");

    private static PeerResponse CreatePeerResponse() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("qa"),
            Position("bug-triage"),
            ThreadId.New(),
            Priority.Normal,
            1,
            SentAt,
            null,
            MessageId.New(),
            "Confirmed on iOS 17.");

    private static ApprovalRequest CreateApprovalRequest() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("release-manager"),
            Position("vp-eng"),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            null,
            "Ship the hotfix to production",
            "Customer-facing outage in progress",
            ApprovalPolicyRef.From("policy:production-deploy"));

    private static ApprovalDecision CreateApprovalDecision() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(),
            Position("release-manager"),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            null,
            MessageId.New(),
            true,
            "Approved given the active outage.");

    private static AuthorizationGrant CreateAuthorizationGrant() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            new OrganizationOwnerEndpointRef(),
            Position("release-manager"),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            null,
            MessageId.New(),
            RetainedActionId.New(),
            ActionFingerprint.From("sha256:0123456789abcdef0123456789abcdef0123456789abcdef0123456789abcdef"),
            AuthorityKey.From("delivery.production-deploy"),
            SentAt.AddHours(24),
            null);

    private static AuthorizationDenial CreateAuthorizationDenial() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            Position("vp-eng"),
            Position("release-manager"),
            ThreadId.New(),
            Priority.High,
            1,
            SentAt,
            null,
            MessageId.New(),
            RetainedActionId.New(),
            "Denied because the deployment window is closed.");

    private static Pulse CreatePulse() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            new SystemEndpointRef(SystemEndpointKind.Scheduler),
            Position("ops"),
            ThreadId.New(),
            Priority.Low,
            1,
            SentAt,
            null,
            "daily-rollup",
            "{}");

    private static EventTrigger CreateEventTrigger() =>
        new(
            MessageId.New(),
            OrganizationId.From("acme"),
            new SystemEndpointRef(SystemEndpointKind.DomainEvents),
            Position("ops"),
            ThreadId.New(),
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
            _system = ActorSystem.Create("org-message-serializer-tests");
            Serializer = new OrgMessageJsonSerializer((ExtendedActorSystem)_system);
        }

        public OrgMessageJsonSerializer Serializer { get; }

        public void Dispose() => _system.Dispose();
    }
}
