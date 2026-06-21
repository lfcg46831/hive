using System.Runtime.CompilerServices;
using System.Text;
using System.Text.Json;
using System.Text.Json.Nodes;
using Akka.Actor;
using Hive.Actors.Serialization;
using Hive.Domain.Messaging;

namespace Hive.Tests;

/// <summary>
/// Golden snapshots of the serialized payload of every canonical message type (US-F0-03-T09).
/// These committed fixtures pin the wire shape and the manifest of each type so that any disruptive
/// change to the format — a renamed or removed field, a changed enum wire value, a different endpoint
/// encoding or a manifest remap (§9.9) — fails the build instead of silently breaking existing
/// journals. Comparison is structural (object keys are order-insensitive) because property order is
/// cosmetic for a tolerant reader; everything else must match exactly.
/// </summary>
/// <remarks>
/// To regenerate the goldens after an intentional, versioned format change, run the suite with the
/// environment variable <c>HIVE_UPDATE_SERIALIZATION_FIXTURES=1</c>; the test rewrites each fixture
/// from the live serializer output and then passes. Review the diff before committing.
/// </remarks>
public sealed class OrgMessageSerializationSnapshotTests
    : IClassFixture<OrgMessageSerializationSnapshotTests.SerializerFixture>
{
    private readonly OrgMessageJsonSerializer _serializer;

    public OrgMessageSerializationSnapshotTests(SerializerFixture fixture)
    {
        _serializer = fixture.Serializer;
    }

    public static TheoryData<string> CanonicalManifests
    {
        get
        {
            var data = new TheoryData<string>();
            foreach (var (manifest, _) in CanonicalMessageFixturesData)
            {
                data.Add(manifest);
            }

            return data;
        }
    }

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Serialized_payload_matches_the_committed_snapshot(string manifest)
    {
        var message = CanonicalMessageFixturesData.Single(entry => entry.Manifest == manifest).Message;
        var payload = _serializer.ToBinary(message);

        var path = FixturePath(manifest);
        if (ShouldRegenerate)
        {
            WriteIndented(path, payload);
            return;
        }

        Assert.True(
            File.Exists(path),
            $"Missing serialization fixture for '{manifest}'. Expected at '{path}'. " +
            "Run with HIVE_UPDATE_SERIALIZATION_FIXTURES=1 to generate it.");

        var expected = Canonicalize(File.ReadAllText(path));
        var actual = Canonicalize(Encoding.UTF8.GetString(payload));

        Assert.Equal(expected, actual);
    }

    [Theory]
    [MemberData(nameof(CanonicalManifests))]
    public void Snapshot_file_name_matches_the_emitted_manifest(string manifest)
    {
        var message = CanonicalMessageFixturesData.Single(entry => entry.Manifest == manifest).Message;

        // The file name is the manifest; this guards against a record being remapped to a different
        // wire token without the fixture moving with it.
        Assert.Equal(manifest, _serializer.Manifest(message));
    }

    [Fact]
    public void Every_canonical_message_type_has_a_snapshot()
    {
        // Drawn straight from the domain taxonomy: any concrete OrgMessage subtype that ships without
        // a committed fixture (and the versioning decision that comes with it) fails here.
        var canonicalTypes = typeof(OrgMessage).Assembly
            .GetTypes()
            .Where(type => type is { IsAbstract: false } && type.IsSubclassOf(typeof(OrgMessage)))
            .ToHashSet();

        var coveredTypes = CanonicalMessageFixturesData
            .Select(entry => entry.Message.GetType())
            .ToHashSet();

        var missing = canonicalTypes.Except(coveredTypes).Select(type => type.Name);
        var unexpected = coveredTypes.Except(canonicalTypes).Select(type => type.Name);

        Assert.True(
            canonicalTypes.SetEquals(coveredTypes),
            $"Canonical message types without a snapshot: [{string.Join(", ", missing)}]; " +
            $"snapshots for unknown types: [{string.Join(", ", unexpected)}].");
    }

    [Fact]
    public void No_orphan_snapshot_files_remain()
    {
        var expected = CanonicalMessageFixturesData
            .Select(entry => entry.Manifest)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        var onDisk = Directory.EnumerateFiles(FixtureDirectory, "*.json")
            .Select(Path.GetFileNameWithoutExtension)
            .OrderBy(name => name, StringComparer.Ordinal)
            .ToArray();

        Assert.Equal(expected, onDisk);
    }

    private static IReadOnlyList<(string Manifest, OrgMessage Message)> CanonicalMessageFixturesData =>
        Serialization.CanonicalMessageFixtures.All;

    private static bool ShouldRegenerate =>
        Environment.GetEnvironmentVariable("HIVE_UPDATE_SERIALIZATION_FIXTURES") == "1";

    private static string FixturePath(string manifest) =>
        Path.Combine(FixtureDirectory, manifest + ".json");

    private static string FixtureDirectory =>
        Path.Combine(TestSourceDirectory(), "Fixtures", "Serialization");

    private static string TestSourceDirectory([CallerFilePath] string callerFilePath = "") =>
        Path.GetDirectoryName(callerFilePath)!;

    private static void WriteIndented(string path, byte[] payload)
    {
        Directory.CreateDirectory(Path.GetDirectoryName(path)!);
        var pretty = JsonNode.Parse(payload)!.ToJsonString(new JsonSerializerOptions { WriteIndented = true });
        File.WriteAllText(path, pretty + Environment.NewLine);
    }

    /// <summary>
    /// Produces a canonical, order-insensitive string form of a JSON payload: object keys are sorted
    /// ordinally and emitted compactly, array order is preserved. Both the golden and the live output
    /// pass through this, so only meaningful differences fail the assertion.
    /// </summary>
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
            _system = ActorSystem.Create("org-message-snapshot-tests");
            Serializer = new OrgMessageJsonSerializer((ExtendedActorSystem)_system);
        }

        public OrgMessageJsonSerializer Serializer { get; }

        public void Dispose() => _system.Dispose();
    }
}
