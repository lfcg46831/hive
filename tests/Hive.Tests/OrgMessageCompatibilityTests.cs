using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json.Nodes;
using Akka.Actor;
using Hive.Actors.Serialization;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Tests.Serialization;

namespace Hive.Tests;

/// <summary>
/// Schema-compatibility tests for the organizational message protocol (US-F0-03-T11). Where the
/// round-trip suite (US-F0-03-T10) proves the live serializer is internally consistent and the
/// snapshot suite (US-F0-03-T09) pins the wire shape, this suite proves the two compatibility
/// guarantees of §9.9 against payloads that did <em>not</em> originate from the current in-memory
/// objects:
/// <list type="bullet">
/// <item>reading payloads from a prior schema version — the committed v1 golden payloads still
/// deserialize into the correct domain objects, additive (unknown) fields written by a newer schema
/// are tolerated, and an older payload that omits an optional field still reads; and</item>
/// <item>controlled rejection of unknown versions — a payload carrying an unsupported
/// <c>SchemaVersion</c> is rejected by the contract validator as a structured
/// <see cref="ValidationResult"/> (not an exception), a payload that omits or zeroes the version is
/// refused at materialization rather than silently defaulted, and an unregistered manifest is
/// rejected explicitly.</item>
/// </list>
/// The committed snapshots under <c>Fixtures/Serialization</c> are reused as the version-1 baseline,
/// so this suite reads exactly the bytes a journal written today would hold.
/// </summary>
public sealed class OrgMessageCompatibilityTests
    : IClassFixture<OrgMessageCompatibilityTests.SerializerFixture>
{
    /// <summary>The supported envelope schema version on this build (§9.9, mirrors the validator).</summary>
    private const int SupportedSchemaVersion = 1;

    private readonly OrgMessageJsonSerializer _serializer;
    private readonly MessageContractValidator _validator = new();

    public OrgMessageCompatibilityTests(SerializerFixture fixture)
    {
        _serializer = fixture.Serializer;
    }

    public static TheoryData<string> CanonicalManifests
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (manifest, _) in CanonicalMessageFixtures.All)
            {
                data.Add(manifest);
            }

            return data;
        }
    }

    // ----- Reading payloads from a prior schema version (§9.9) ------------------------------------

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Reads_committed_v1_baseline_payload_into_the_canonical_object(string manifest)
    {
        // The committed golden is the persisted v1 baseline: a payload exactly as a journal written
        // today would store it. Reading it back must reconstruct the canonical domain object,
        // independent of the live in-memory instance.
        var expected = CanonicalMessageFixtures.All.Single(entry => entry.Manifest == manifest).Message;

        var restored = (OrgMessage)_serializer.FromBinary(ReadBaseline(manifest), manifest);

        Assert.IsType(expected.GetType(), restored);
        Assert.Equal(SupportedSchemaVersion, restored.SchemaVersion);
        // Re-serializing the value restored from the on-disk baseline must reproduce the same bytes
        // as serializing the canonical object, proving the read is lossless across the format.
        Assert.Equal(
            Canonicalize(_serializer.ToBinary(expected)),
            Canonicalize(_serializer.ToBinary(restored)));
    }

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Tolerates_additive_unknown_fields_written_by_a_newer_schema(string manifest)
    {
        // Forward compatibility: a payload produced by a newer schema that added optional fields must
        // still read on this build — unknown properties at the envelope and inside a nested endpoint
        // are ignored, never causing a failure (§9.9 "mudança aditiva").
        var node = BaselineNode(manifest);
        node["addedInAFutureVersion"] = "ignored";
        node["anotherFutureField"] = 42;
        if (node["From"] is JsonObject from)
        {
            from["futureEndpointField"] = "ignored";
        }

        var restored = (OrgMessage)_serializer.FromBinary(ToBytes(node), manifest);

        var expected = CanonicalMessageFixtures.All.Single(entry => entry.Manifest == manifest).Message;
        Assert.IsType(expected.GetType(), restored);
        // The known fields survive untouched: re-serializing drops the unknown additions and matches
        // the canonical payload.
        Assert.Equal(
            Canonicalize(_serializer.ToBinary(expected)),
            Canonicalize(_serializer.ToBinary(restored)));
    }

    [Fact]
    public void Tolerates_an_older_payload_that_omits_an_optional_field()
    {
        // Backward compatibility: a directive serialized before the optional ParentDirectiveId existed
        // omits it entirely. A current reader must accept the absence and leave the optional null,
        // without applying any other default.
        var node = BaselineNode("directive");
        node.Remove("ParentDirectiveId");
        node.Remove("Deadline");

        var restored = (Domain.Messaging.Directive)_serializer.FromBinary(ToBytes(node), "directive");

        Assert.Null(restored.ParentDirectiveId);
        Assert.Null(restored.Deadline);
        // A field that was present is still read correctly — absence of the optional does not corrupt
        // the rest of the envelope.
        Assert.NotNull(restored.DirectiveId);
        Assert.Equal(SupportedSchemaVersion, restored.SchemaVersion);
    }

    [Fact]
    public void Tolerates_an_authorization_grant_without_the_optional_reason()
    {
        var node = BaselineNode("authorization-grant");
        node.Remove("Reason");

        var restored = (AuthorizationGrant)_serializer.FromBinary(
            ToBytes(node),
            "authorization-grant");

        Assert.Null(restored.Reason);
        Assert.NotNull(restored.RetainedActionId);
        Assert.NotNull(restored.Fingerprint);
        Assert.NotNull(restored.Key);
    }

    [Theory]
    [InlineData("RetainedActionId", "00000000-0000-0000-0000-000000000000")]
    [InlineData("Fingerprint", "sha256:not-a-digest")]
    [InlineData("Key", "not-namespaced")]
    public void Rejects_invalid_authorization_value_objects(string property, string invalidValue)
    {
        var node = BaselineNode("authorization-grant");
        node[property] = invalidValue;

        Assert.ThrowsAny<Exception>(() =>
            _serializer.FromBinary(ToBytes(node), "authorization-grant"));
    }

    [Theory]
    [InlineData("authorization-grant", "RetainedActionId")]
    [InlineData("authorization-denial", "InReplyTo")]
    public void Rejects_authorization_payloads_missing_required_fields(
        string manifest,
        string requiredProperty)
    {
        var node = BaselineNode(manifest);
        node.Remove(requiredProperty);

        Assert.ThrowsAny<Exception>(() => _serializer.FromBinary(ToBytes(node), manifest));
    }

    // ----- Controlled rejection of unknown / unsupported versions (§9.9) --------------------------

    [Theory]
    [InlineData(2)]
    [InlineData(99)]
    public async Task Rejects_an_unsupported_future_schema_version_through_the_validator(int futureVersion)
    {
        // A payload from a newer, incompatible schema still materializes (the reader is tolerant and
        // does not crash on an unknown but well-formed version), so the rejection is a *controlled*
        // validation decision, not an exception. The validator reports UnsupportedSchemaVersion and
        // gates every later phase.
        var node = BaselineNode("memo");
        node["SchemaVersion"] = futureVersion;

        var restored = (Memo)_serializer.FromBinary(ToBytes(node), "memo");
        Assert.Equal(futureVersion, restored.SchemaVersion);

        var result = await _validator.ValidateAsync(restored, new EmptyValidationContext());

        Assert.False(result.IsValid);
        Assert.Equal(
            [new ValidationError(
                "unsupported-schema-version",
                "schemaVersion",
                RejectionReason.UnsupportedSchemaVersion)],
            result.Errors);
    }

    [Theory]
    [InlineData(0)]
    [InlineData(-1)]
    public void Refuses_a_non_positive_schema_version_at_materialization(int invalidVersion)
    {
        // A non-positive version can never be a valid envelope; the domain constructor rejects it on
        // read rather than coercing it to a default. The failure is surfaced as a thrown exception at
        // the serialization boundary, before any half-built message escapes.
        var node = BaselineNode("memo");
        node["SchemaVersion"] = invalidVersion;

        Assert.ThrowsAny<Exception>(() => _serializer.FromBinary(ToBytes(node), "memo"));
    }

    [Fact]
    public void Never_defaults_a_missing_schema_version_silently()
    {
        // A payload that omits the version entirely must not be silently treated as version 1. With no
        // value to bind, the envelope cannot be materialized, so the read fails loudly (§9.9: "nunca
        // aplica defaults silenciosos").
        var node = BaselineNode("memo");
        node.Remove("SchemaVersion");

        Assert.ThrowsAny<Exception>(() => _serializer.FromBinary(ToBytes(node), "memo"));
    }

    [Fact]
    public void Rejects_an_unknown_manifest_explicitly()
    {
        // A manifest that no longer maps to a registered type — e.g. a token from a future or removed
        // message kind — is rejected with an explicit ArgumentException rather than returning null or
        // crashing further down (§9.9: manifest is part of the versioned contract).
        var payload = ReadBaseline("memo");

        Assert.Throws<ArgumentException>(() => _serializer.FromBinary(payload, "unknown-future-message"));
    }

    // ----- Helpers -------------------------------------------------------------------------------

    private static byte[] ReadBaseline(string manifest) => File.ReadAllBytes(FixturePath(manifest));

    private static JsonObject BaselineNode(string manifest) =>
        JsonNode.Parse(ReadBaseline(manifest))!.AsObject();

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    /// <summary>Order-insensitive byte comparison key for two JSON payloads.</summary>
    private static string Canonicalize(byte[] payload)
    {
        var node = JsonNode.Parse(payload);
        return node!.ToJsonString();
    }

    private static string FixturePath(string manifest) =>
        Path.Combine(FixtureDirectory, manifest + ".json");

    private static string FixtureDirectory =>
        Path.Combine(TestSourceDirectory(), "Fixtures", "Serialization");

    private static string TestSourceDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(callerFilePath)!;

    private sealed class EmptyValidationContext : IMessageValidationContext
    {
        public ValueTask<Domain.Messaging.Directive?> FindDirectiveAsync(
            DirectiveId directiveId,
            CancellationToken cancellationToken = default) =>
            ValueTask.FromResult<Domain.Messaging.Directive?>(null);
    }

    public sealed class SerializerFixture : IDisposable
    {
        private readonly ActorSystem _system;

        public SerializerFixture()
        {
            _system = ActorSystem.Create("org-message-compatibility-tests");
            Serializer = new OrgMessageJsonSerializer((ExtendedActorSystem)_system);
        }

        public OrgMessageJsonSerializer Serializer { get; }

        public void Dispose() => _system.Dispose();
    }
}
