using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

public sealed class PeerChannelConfigurationTests
{
    internal const string Channel = "{ from: engineering, types: [peer-request, memo], max_open_requests: 2, on_rejection: escalate }";
    private const string ChannelPath = "units[0].allowed_peer_channels[0]";

    [Theory]
    [InlineData("peer-request", PeerChannelMessageType.PeerRequest)]
    [InlineData("memo", PeerChannelMessageType.Memo)]
    public void Parses_each_allowed_type(string wire, PeerChannelMessageType expected)
    {
        var configuration = Configuration($"[{Channel.Replace("peer-request, memo", wire)}]");
        var channel = Assert.Single(configuration.Units[0].AllowedPeerChannels);
        Assert.Equal(UnitId.From("engineering"), channel.From);
        Assert.Equal(expected, Assert.Single(channel.Types));
        Assert.Equal(2, channel.MaxOpenRequests);
        Assert.Equal(PeerChannelRejectionAction.Escalate, channel.OnRejection);
        Assert.Equal(wire, PeerChannelMessageTypeContract.ToWireValue(expected));
        Assert.Equal("escalate", PeerChannelRejectionActionContract.ToWireValue(channel.OnRejection));
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public async Task Legacy_or_empty_channels_import_without_contracts(string? channels)
    {
        var configuration = Configuration(channels);
        var imported = await new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry())
            .ImportAsync(configuration);
        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
        Assert.All(imported.Snapshot!.Units.Values, entry => Assert.Empty(entry.Value.AllowedPeerChannels));
    }

    public static IEnumerable<object[]> InvalidChannels()
    {
        foreach (var value in new[] { "null", "{}", "text" })
            yield return [value, "units[0].allowed_peer_channels"];
        foreach (var value in new[] { "null", "text", "[]" })
            yield return [$"[{value}]", ChannelPath];
        foreach (var field in new[] { "from: engineering, ", "types: [peer-request, memo], ", "max_open_requests: 2, ", ", on_rejection: escalate" })
            yield return [$"[{Channel.Replace(field, "")}]", $"{ChannelPath}.{field.TrimStart(',', ' ').Split(':')[0]}"];
        foreach (var value in new[] { "[]", "null", "memo", "[peer-response]", "[directive]", "[Memo]", "[1]", "[{}]", "[memo, memo]" })
            yield return [$"[{Channel.Replace("[peer-request, memo]", value)}]", $"{ChannelPath}.types"];
        foreach (var value in new[] { "0", "-1", "1.5", "2147483648", "null" })
            yield return [$"[{Channel.Replace("max_open_requests: 2", $"max_open_requests: {value}")}]", $"{ChannelPath}.max_open_requests"];
        foreach (var value in new[] { "None", "reject", "1", "null" })
            yield return [$"[{Channel.Replace("on_rejection: escalate", $"on_rejection: {value}")}]", $"{ChannelPath}.on_rejection"];
        yield return [$"[{Channel.Replace("from: engineering", "from: null")}]", $"{ChannelPath}.from"];
        yield return [$"[{Channel.Replace(" }", ", unexpected: true }")}]", $"{ChannelPath}.unexpected"];
    }

    [Theory]
    [MemberData(nameof(InvalidChannels))]
    public void Malformed_channels_fail_with_source_locations(string channels, string path)
    {
        var result = Parse(channels);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Configuration);
        Assert.Contains(result.Errors, error => error.FieldPath.StartsWith(path, StringComparison.Ordinal));
        Assert.All(result.Errors, error =>
        {
            Assert.Equal("peer-channels.yaml", error.FilePath);
            Assert.True(error.Line > 0);
            Assert.True(error.Column > 0);
        });
    }

    [Theory]
    [InlineData("missing", "peer-channel-unit-not-found")]
    [InlineData("Engineering", "peer-channel-unit-not-found")]
    [InlineData("root", "peer-channel-same-unit")]
    public async Task Invalid_reference_leaves_existing_registry_untouched(string source, string code)
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var initial = await importer.ImportAsync(Configuration($"[{Channel}]"));
        var invalid = await importer.ImportAsync(Configuration($"[{Channel.Replace("engineering", source)}]"));
        Assert.Equal(OrganizationImportStatus.Invalid, invalid.Status);
        var error = Assert.Single(invalid.ValidationErrors);
        Assert.Equal(code, error.Code);
        Assert.Equal($"{ChannelPath}.from", error.Path);
        Assert.Same(initial.Snapshot, await registry.FindSnapshotAsync(initial.Snapshot!.OrganizationId));
    }

    [Fact]
    public async Task Duplicate_sources_are_rejected_even_when_types_differ()
    {
        var importer = new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry());
        var result = await importer.ImportAsync(Configuration($"[{Channel}, {Channel.Replace("peer-request, memo", "memo")}]"));
        Assert.Equal(OrganizationImportStatus.Invalid, result.Status);
        var error = Assert.Single(result.ValidationErrors);
        Assert.Equal("duplicate-peer-channel", error.Code);
        Assert.Equal("units[0].allowed_peer_channels[1].from", error.Path);
    }

    [Fact]
    public async Task Reordered_channels_and_types_are_a_no_op_and_reverse_channels_are_independent()
    {
        var importer = new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry());
        var secondChannel = Channel.Replace("engineering", "support").Replace("escalate", "none");
        var configuration = Configuration($"[{Channel}, {secondChannel}]");
        var first = await importer.ImportAsync(configuration);
        var second = await importer.ImportAsync(Configuration($"[{secondChannel}, {Channel.Replace("peer-request, memo", "memo, peer-request")}]"));
        Assert.Equal(OrganizationImportStatus.NoChanges, second.Status);
        Assert.Equal(first.Snapshot!.Fingerprint, second.Snapshot!.Fingerprint);
        Assert.Empty(second.Plan!.Changes);
        Assert.Empty(second.Snapshot.Units[UnitId.From("engineering")].Value.AllowedPeerChannels);

        var engineering = configuration.Units[1];
        var bidirectional = new OrganizationConfiguration(configuration.Organization,
            [configuration.Units[0], new UnitConfiguration(engineering.Id, engineering.Leadership,
                engineering.Parent, engineering.Name,
                [new PeerChannelConfiguration(UnitId.From("root"), [PeerChannelMessageType.Memo], 1, PeerChannelRejectionAction.None)]),
                configuration.Units[2]], configuration.Positions);
        var third = await importer.ImportAsync(bidirectional);
        Assert.Equal(OrganizationImportStatus.Applied, third.Status);
        Assert.Single(third.Snapshot!.Units[engineering.Id].Value.AllowedPeerChannels);
    }

    [Theory]
    [InlineData("max_open_requests: 2", "max_open_requests: 3")]
    [InlineData("on_rejection: escalate", "on_rejection: none")]
    [InlineData("peer-request, memo", "memo")]
    [InlineData("from: engineering", "from: support")]
    public async Task Channel_changes_and_removal_update_only_the_destination_unit(string before, string after)
    {
        var importer = new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry());
        var initial = await importer.ImportAsync(Configuration());
        var added = await importer.ImportAsync(Configuration($"[{Channel}]"));
        var changed = await importer.ImportAsync(Configuration($"[{Channel.Replace(before, after)}]"));
        var removed = await importer.ImportAsync(Configuration());
        Assert.Equal(initial.Snapshot!.Fingerprint, removed.Snapshot!.Fingerprint);
        Assert.Equal(4, removed.Snapshot.Version);
        Assert.NotEqual(added.Snapshot!.Fingerprint, changed.Snapshot!.Fingerprint);
        foreach (var result in new[] { added, changed, removed })
        {
            Assert.Equal(OrganizationImportStatus.Applied, result.Status);
            var change = Assert.Single(result.Plan!.Changes);
            Assert.Equal(RegistryEntityKind.Unit, change.EntityKind);
            Assert.Equal("root", change.Key);
            Assert.Equal(RegistryChangeKind.Updated, change.Kind);
        }
        Assert.Empty(removed.Snapshot.Units[UnitId.From("root")].Value.AllowedPeerChannels);
    }

    [Fact]
    public void Model_and_registry_snapshot_channel_collections()
    {
        var types = new[] { PeerChannelMessageType.Memo };
        var channel = new PeerChannelConfiguration(UnitId.From("engineering"), types, 1, PeerChannelRejectionAction.None);
        var channels = new[] { channel };
        var unit = new UnitConfiguration(UnitId.From("root"), PositionId.From("ceo"), allowedPeerChannels: channels);
        var registryUnit = new RegistryUnit(unit.Id, unit.Name, unit.Parent, unit.Leadership, channels);
        types[0] = PeerChannelMessageType.PeerRequest;
        channels[0] = new PeerChannelConfiguration(UnitId.From("support"), types, 2, PeerChannelRejectionAction.Escalate);
        Assert.Same(channel, Assert.Single(unit.AllowedPeerChannels));
        Assert.Same(channel, Assert.Single(registryUnit.AllowedPeerChannels));
        Assert.Equal(PeerChannelMessageType.Memo, Assert.Single(channel.Types));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerChannelConfiguration(channel.From, types, 0, channel.OnRejection));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerChannelConfiguration(channel.From, [(PeerChannelMessageType)99], 1, channel.OnRejection));
        Assert.Throws<ArgumentOutOfRangeException>(() => new PeerChannelConfiguration(channel.From, types, 1, (PeerChannelRejectionAction)99));
        Assert.Throws<ArgumentException>(() => new PeerChannelConfiguration(channel.From, [], 1, channel.OnRejection));
        Assert.Throws<ArgumentException>(() => new PeerChannelConfiguration(channel.From, [types[0], types[0]], 1, channel.OnRejection));
    }

    internal static OrganizationConfiguration Configuration(string? channels = null)
    {
        var result = Parse(channels);
        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static OrganizationConfigurationParseResult Parse(string? channels)
    {
        var yaml = """
            organization:
              id: peer-test
              root_unit: root
              owner: { type: human, ref: owner@example.test }
            units:
              - id: root
                parent: null
                leadership: ceo
                allowed_peer_channels: CHANNELS
              - id: engineering
                parent: root
                leadership: engineer
              - id: support
                parent: root
                leadership: supporter
            positions:
              - id: ceo
                unit: root
                reports_to: null
                occupant: { type: human }
              - id: engineer
                unit: engineering
                reports_to: ceo
                occupant: { type: human }
              - id: supporter
                unit: support
                reports_to: ceo
                occupant: { type: human }
            """;
        yaml = channels is null
            ? yaml.Replace("    allowed_peer_channels: CHANNELS", "")
            : yaml.Replace("CHANNELS", channels);
        return new OrganizationConfigurationParser().Parse(yaml, "peer-channels.yaml");
    }
}
