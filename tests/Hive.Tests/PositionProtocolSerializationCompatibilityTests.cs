using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Hive.Actors.Serialization;
using Hive.Domain.Positions;
using Hive.Tests.Serialization;

namespace Hive.Tests;

/// <summary>
/// Compatibility tests for the persisted PositionActor protocol (US-F0-06-T13b). The committed v1
/// fixtures under <c>Fixtures/PositionProtocol/v1</c> represent journal and snapshot payloads that
/// did not originate from the current in-memory objects, so this suite protects stable manifests,
/// tolerant reads and lossless materialization of the base events and persisted snapshot.
/// </summary>
public sealed class PositionProtocolSerializationCompatibilityTests
    : IClassFixture<PositionProtocolSerializationCompatibilityTests.SerializerFixture>
{
    private readonly PositionProtocolJsonSerializer _serializer;

    public PositionProtocolSerializationCompatibilityTests(SerializerFixture fixture)
    {
        _serializer = fixture.Serializer;
    }

    public static TheoryData<string> CanonicalManifests
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (manifest, _) in CanonicalPositionProtocolFixtures.All)
            {
                data.Add(manifest);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Serialized_payload_matches_the_committed_v1_fixture(string manifest)
    {
        var value = CanonicalPositionProtocolFixtures.All.Single(entry => entry.Manifest == manifest).Value;
        var payload = _serializer.ToBinary(value);

        var path = FixturePath(manifest);
        if (ShouldRegenerate)
        {
            WriteIndented(path, payload);
            return;
        }

        Assert.True(
            File.Exists(path),
            $"Missing v1 position protocol fixture for '{manifest}'. Expected at '{path}'. " +
            "Run with HIVE_UPDATE_POSITION_PROTOCOL_FIXTURES=1 to generate it.");

        var expected = Canonicalize(File.ReadAllText(path));
        var actual = Canonicalize(Encoding.UTF8.GetString(payload));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Reads_committed_v1_fixture_into_the_canonical_value(string manifest)
    {
        if (ShouldRegenerate)
        {
            return;
        }

        var expected = CanonicalPositionProtocolFixtures.All.Single(entry => entry.Manifest == manifest).Value;

        var restored = _serializer.FromBinary(ReadBaseline(manifest), manifest);

        Assert.IsType(expected.GetType(), restored);
        Assert.Equal(
            Canonicalize(Encoding.UTF8.GetString(_serializer.ToBinary(expected))),
            Canonicalize(Encoding.UTF8.GetString(_serializer.ToBinary(restored))));
    }

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Tolerates_additive_unknown_fields_written_by_a_newer_schema(string manifest)
    {
        if (ShouldRegenerate)
        {
            return;
        }

        var expected = CanonicalPositionProtocolFixtures.All.Single(entry => entry.Manifest == manifest).Value;
        var node = BaselineNode(manifest);
        node["addedInFutureVersion"] = "ignored";
        node["anotherFutureField"] = 42;

        var restored = _serializer.FromBinary(ToBytes(node), manifest);

        Assert.IsType(expected.GetType(), restored);
        Assert.Equal(
            Canonicalize(Encoding.UTF8.GetString(_serializer.ToBinary(expected))),
            Canonicalize(Encoding.UTF8.GetString(_serializer.ToBinary(restored))));
    }

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Fixture_file_name_matches_the_emitted_manifest(string manifest)
    {
        var value = CanonicalPositionProtocolFixtures.All.Single(entry => entry.Manifest == manifest).Value;

        Assert.Equal(manifest, _serializer.Manifest(value));
    }

    [Fact]
    public void Every_base_position_event_and_snapshot_has_a_v1_fixture()
    {
        var baseEventTypes = typeof(PositionEvent).Assembly
            .GetTypes()
            .Where(type =>
                type is { IsAbstract: false } &&
                type.IsSubclassOf(typeof(PositionEvent)) &&
                type != typeof(PositionConfigurationApplied))
            .ToHashSet();

        var coveredBaseEventTypes = CanonicalPositionProtocolFixtures.BaseEvents
            .Select(entry => entry.Event.GetType())
            .ToHashSet();

        var missing = baseEventTypes.Except(coveredBaseEventTypes).Select(type => type.Name);
        var unexpected = coveredBaseEventTypes.Except(baseEventTypes).Select(type => type.Name);

        Assert.True(
            baseEventTypes.SetEquals(coveredBaseEventTypes),
            $"Base PositionEvent types without v1 fixtures: [{string.Join(", ", missing)}]; " +
            $"fixtures for unknown base event types: [{string.Join(", ", unexpected)}].");

        Assert.Contains(
            CanonicalPositionProtocolFixtures.All,
            entry => entry.Value is PositionSnapshot && entry.Manifest == "position-snapshot");
    }

    [Fact]
    public void No_orphan_v1_fixture_files_remain()
    {
        if (ShouldRegenerate)
        {
            return;
        }

        var expected = CanonicalPositionProtocolFixtures.All
            .Select(entry => entry.Manifest)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var onDisk = Directory.Exists(FixtureDirectory)
            ? Directory.EnumerateFiles(FixtureDirectory, "*.json")
                .Select(Path.GetFileNameWithoutExtension)
                .OrderBy(name => name, StringComparer.Ordinal)
                .ToArray()
            : [];

        Assert.Equal(expected, onDisk);
    }

    [Fact]
    public void Rejects_an_unknown_manifest_explicitly()
    {
        if (ShouldRegenerate)
        {
            return;
        }

        var payload = ReadBaseline("message-received");

        Assert.Throws<ArgumentException>(() => _serializer.FromBinary(payload, "unknown-position-event"));
    }

    private static byte[] ReadBaseline(string manifest) => File.ReadAllBytes(FixturePath(manifest));

    private static JsonObject BaselineNode(string manifest) =>
        JsonNode.Parse(ReadBaseline(manifest))!.AsObject();

    private static byte[] ToBytes(JsonNode node) => Encoding.UTF8.GetBytes(node.ToJsonString());

    private static string FixturePath(string manifest) =>
        Path.Combine(FixtureDirectory, manifest + ".json");

    private static string FixtureDirectory =>
        Path.Combine(TestSourceDirectory(), "Fixtures", "PositionProtocol", "v1");

    private static string TestSourceDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(callerFilePath)!;

    private static bool ShouldRegenerate =>
        Environment.GetEnvironmentVariable("HIVE_UPDATE_POSITION_PROTOCOL_FIXTURES") == "1";

    private static void WriteIndented(string path, byte[] payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pretty = JsonNode.Parse(payload)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, pretty + Environment.NewLine);
    }

    private static string Canonicalize(string json)
    {
        var node = JsonNode.Parse(
            json,
            documentOptions: new JsonDocumentOptions
            {
                CommentHandling = JsonCommentHandling.Skip,
                AllowTrailingCommas = true,
            });

        var builder = new StringBuilder();
        WriteCanonical(node, builder);
        return builder.ToString();
    }

    private static void WriteCanonical(JsonNode? node, StringBuilder builder)
    {
        switch (node)
        {
            case null:
                builder.Append("null");
                break;

            case JsonObject obj:
                builder.Append('{');
                var first = true;
                foreach (var pair in obj.OrderBy(p => p.Key, StringComparer.Ordinal))
                {
                    if (!first)
                    {
                        builder.Append(',');
                    }

                    first = false;
                    builder.Append(JsonSerializer.Serialize(pair.Key));
                    builder.Append(':');
                    WriteCanonical(pair.Value, builder);
                }

                builder.Append('}');
                break;

            case JsonArray array:
                builder.Append('[');
                for (var index = 0; index < array.Count; index++)
                {
                    if (index > 0)
                    {
                        builder.Append(',');
                    }

                    WriteCanonical(array[index], builder);
                }

                builder.Append(']');
                break;

            default:
                builder.Append(node.ToJsonString());
                break;
        }
    }

    public sealed class SerializerFixture : IDisposable
    {
        private readonly ActorSystem _system;

        public SerializerFixture()
        {
            _system = ActorSystem.Create("position-protocol-compatibility-tests");
            Serializer = new PositionProtocolJsonSerializer((ExtendedActorSystem)_system);
        }

        public PositionProtocolJsonSerializer Serializer { get; }

        public void Dispose() => _system.Dispose();
    }
}
