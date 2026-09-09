using Hive.Domain.Identity;
using Hive.Domain.Organization;
using Hive.Domain.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

public sealed class PeerChannelContractsTests
{
    private static readonly OrganizationId Org = OrganizationId.From("peer-test");
    private static readonly UnitId Source = UnitId.From("engineering");
    private static readonly UnitId Destination = UnitId.From("root");

    [Fact]
    public async Task Resolves_directional_contracts_with_canonical_types_and_independent_reverse_policy()
    {
        IPeerChannelContracts contracts = new MaterializedPeerChannelContracts(PeerChannelContractsSnapshot.CreateBuilder(Org)
            .AddUnit(Destination, [Channel()])
            .AddUnit(Source, [new(Destination, [PeerChannelMessageType.Memo], 1, PeerChannelRejectionAction.None)])
            .Build());

        var forward = await contracts.ResolveChannelAsync(Org, Source, Destination);
        Assert.NotNull(forward);
        Assert.Equal(Source, forward.From);
        Assert.Equal([PeerChannelMessageType.PeerRequest, PeerChannelMessageType.Memo], forward.Types);
        Assert.Equal(2, forward.MaxOpenRequests);
        Assert.Equal(PeerChannelRejectionAction.Escalate, forward.OnRejection);
        var reverse = await contracts.ResolveChannelAsync(Org, Destination, Source);
        Assert.NotNull(reverse);
        Assert.Equal(Destination, reverse.From);
        Assert.Equal(PeerChannelMessageType.Memo, Assert.Single(reverse.Types));
        Assert.Equal(1, reverse.MaxOpenRequests);
        Assert.Equal(PeerChannelRejectionAction.None, reverse.OnRejection);
        Assert.Null(await contracts.ResolveChannelAsync(Org, Source, Source));
    }

    [Fact]
    public async Task Absence_requires_known_units_in_the_requested_organization()
    {
        var other = OrganizationId.From("other");
        var contracts = new MaterializedPeerChannelContracts([Snapshot(),
            PeerChannelContractsSnapshot.CreateBuilder(other).AddUnit(Source, []).AddUnit(Destination, []).Build()]);
        Assert.Null(await contracts.ResolveChannelAsync(Org, Destination, Source));
        Assert.Null(await contracts.ResolveChannelAsync(other, Source, Destination));
        Assert.NotNull(await contracts.ResolveChannelAsync(Org, Source, Destination));
        foreach (var (org, from, to) in new[]
        {
            (OrganizationId.From("Peer-test"), Source, Destination),
            (Org, UnitId.From("Engineering"), Destination),
            (Org, Source, UnitId.From("missing")),
        })
            await Assert.ThrowsAsync<PeerChannelContractNotFoundException>(() => contracts.ResolveChannelAsync(org, from, to).AsTask());
    }

    [Fact]
    public async Task Input_collections_builder_reuse_and_returned_types_cannot_mutate_existing_snapshots()
    {
        var types = new[] { PeerChannelMessageType.Memo };
        var channels = new List<PeerChannelConfiguration> { new(Source, types, 2, PeerChannelRejectionAction.None) };
        var builder = PeerChannelContractsSnapshot.CreateBuilder(Org).AddUnit(Destination, channels).AddUnit(Source, []);
        var first = builder.Build();
        types[0] = PeerChannelMessageType.PeerRequest;
        channels.Clear();
        var third = UnitId.From("support");
        var second = builder.AddUnit(third, []).Build();
        var snapshots = new List<PeerChannelContractsSnapshot> { first };
        var resolver = new MaterializedPeerChannelContracts(snapshots);
        snapshots.Clear();

        var result = await resolver.ResolveChannelAsync(Org, Source, Destination);
        Assert.Equal(PeerChannelMessageType.Memo, Assert.Single(result!.Types));
        Assert.Throws<NotSupportedException>(() => ((IList<PeerChannelMessageType>)result.Types)[0] = PeerChannelMessageType.PeerRequest);
        Assert.Throws<PeerChannelContractNotFoundException>(() => first.Resolve(third, Destination));
        Assert.Null(second.Resolve(third, Destination));
        Assert.Equal(PeerChannelMessageType.Memo, Assert.Single(first.Resolve(Source, Destination)!.Types));
    }

    [Fact]
    public void Snapshot_construction_rejects_duplicate_and_invalid_structure()
    {
        var builder = PeerChannelContractsSnapshot.CreateBuilder(Org).AddUnit(Source, []);
        Assert.Throws<ArgumentException>(() => builder.AddUnit(Source, []));
        Assert.Throws<ArgumentException>(() => builder.AddUnit(Destination, [Channel(), Channel()]));
        Assert.Throws<ArgumentException>(() => builder.AddUnit(Destination, [new(Destination, [PeerChannelMessageType.Memo], 1, PeerChannelRejectionAction.None)]));
        Assert.Throws<ArgumentNullException>(() => builder.AddUnit(Destination, [null!]));
        Assert.Throws<InvalidOperationException>(() => PeerChannelContractsSnapshot.CreateBuilder(Org).AddUnit(Destination, [Channel()]).Build());
        Assert.Throws<ArgumentException>(() => new MaterializedPeerChannelContracts([Snapshot(), Snapshot()]));
    }

    [Fact]
    public async Task Cancellation_is_not_reported_as_absence_or_structural_failure()
    {
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        var contracts = new MaterializedPeerChannelContracts(Snapshot());
        var error = await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contracts.ResolveChannelAsync(
            OrganizationId.From("missing"), Source, Destination, cancellation.Token).AsTask());
        Assert.Equal(cancellation.Token, error.CancellationToken);
    }

    [Fact]
    public async Task Registry_resolution_observes_updates_and_removal_while_old_snapshot_stays_immutable()
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var initial = await importer.ImportAsync(PeerChannelConfigurationTests.Configuration($"[{PeerChannelConfigurationTests.Channel}]"));
        var old = initial.Snapshot!.PeerChannelContracts;
        IPeerChannelContracts contracts = new RegistryPeerChannelContracts(registry);
        Assert.Equal(2, (await contracts.ResolveChannelAsync(Org, Source, Destination))!.MaxOpenRequests);
        await importer.ImportAsync(PeerChannelConfigurationTests.Configuration(
            $"[{PeerChannelConfigurationTests.Channel.Replace("max_open_requests: 2", "max_open_requests: 5").Replace("escalate", "none")}]"));
        var updated = await contracts.ResolveChannelAsync(Org, Source, Destination);
        Assert.Equal(5, updated!.MaxOpenRequests);
        Assert.Equal(PeerChannelRejectionAction.None, updated.OnRejection);
        await importer.ImportAsync(PeerChannelConfigurationTests.Configuration());
        Assert.Null(await contracts.ResolveChannelAsync(Org, Source, Destination));
        Assert.Equal(2, old.Resolve(Source, Destination)!.MaxOpenRequests);
        await Assert.ThrowsAsync<PeerChannelContractNotFoundException>(() => contracts.ResolveChannelAsync(OrganizationId.From("unknown"), Source, Destination).AsTask());
        await Assert.ThrowsAsync<PeerChannelContractNotFoundException>(() => contracts.ResolveChannelAsync(Org, Source, UnitId.From("unknown")).AsTask());
    }

    [Fact]
    public async Task Registry_reader_is_called_once_with_scope_and_token_and_technical_errors_propagate()
    {
        using var cancellation = new CancellationTokenSource();
        var failure = new IOException("registry unavailable");
        var reader = new ProbeReader((organizationId, token) =>
        {
            Assert.Equal(Org, organizationId);
            Assert.Equal(cancellation.Token, token);
            return ValueTask.FromException<OrganizationRegistrySnapshot?>(failure);
        });
        var contracts = new RegistryPeerChannelContracts(reader);
        Assert.Same(failure, await Assert.ThrowsAsync<IOException>(() => contracts.ResolveChannelAsync(Org, Source, Destination, cancellation.Token).AsTask()));
        Assert.Equal(1, reader.Calls);
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => contracts.ResolveChannelAsync(Org, Source, Destination, cancellation.Token).AsTask());
        Assert.Equal(1, reader.Calls);
    }

    [Fact]
    public async Task Registry_does_not_leak_a_snapshot_returned_for_another_organization()
    {
        var import = await new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry())
            .ImportAsync(PeerChannelConfigurationTests.Configuration($"[{PeerChannelConfigurationTests.Channel}]"));
        var reader = new ProbeReader((_, _) => new(import.Snapshot));
        await Assert.ThrowsAsync<InvalidOperationException>(() => new RegistryPeerChannelContracts(reader)
            .ResolveChannelAsync(OrganizationId.From("other"), Source, Destination).AsTask());
    }

    private static PeerChannelConfiguration Channel() =>
        new(Source, [PeerChannelMessageType.Memo, PeerChannelMessageType.PeerRequest], 2, PeerChannelRejectionAction.Escalate);

    private static PeerChannelContractsSnapshot Snapshot() => PeerChannelContractsSnapshot.CreateBuilder(Org)
        .AddUnit(Destination, [Channel()]).AddUnit(Source, []).Build();

    private sealed class ProbeReader(Func<OrganizationId, CancellationToken, ValueTask<OrganizationRegistrySnapshot?>> read) : IOrganizationRegistryReader
    {
        public int Calls { get; private set; }
        public ValueTask<OrganizationRegistrySnapshot?> FindSnapshotAsync(OrganizationId organizationId, CancellationToken cancellationToken = default)
        {
            Calls++;
            return read(organizationId, cancellationToken);
        }
    }
}
