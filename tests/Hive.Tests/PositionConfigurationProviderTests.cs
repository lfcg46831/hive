using System.Reflection;
using Hive.Domain.Ai;
using Hive.Domain.Governance;
using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;

namespace Hive.Tests;

public sealed class PositionConfigurationProviderTests
{
    private static readonly DateTimeOffset ImportAt =
        new(2026, 6, 26, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Provider_loads_runtime_configuration_from_materialized_registry_snapshot()
    {
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(ImportAt))
            .ImportAsync(ExampleConfiguration());
        var entityId = PositionEntityId.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"));
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(registry);

        var result = await provider.LoadAsync(entityId, CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Loaded, result.Status);
        var configuration = Assert.IsType<PositionRuntimeConfiguration>(result.Configuration);
        Assert.Equal(imported.Snapshot!.Version, configuration.Stamp.Version);
        Assert.Equal(imported.Snapshot.Fingerprint, configuration.Stamp.Fingerprint);
        Assert.Equal(entityId.Organization, configuration.OrganizationId);
        Assert.Equal(entityId.Position, configuration.PositionId);
        Assert.Equal(UnitId.From("engenharia"), configuration.Position.Unit);
        Assert.Equal(PositionId.From("ceo"), configuration.Position.ReportsTo);
        Assert.Equal("Delivery Lead", configuration.Position.Name);
        Assert.Equal("Europe/Lisbon", configuration.Position.Timezone);
        Assert.Equal(OccupantType.AiAgent, configuration.Occupant.Type);
        Assert.Equal("engineer-v1", configuration.Occupant.IdentityPromptRef);
        Assert.NotNull(configuration.Occupant.IdentityPrompt);
        Assert.Equal("engineer-v1", configuration.Occupant.IdentityPrompt.Id);
        Assert.Equal("prompts/engineer-v1.md", configuration.Occupant.IdentityPrompt.Path);
        Assert.Contains("Delivery Lead", configuration.Occupant.IdentityPrompt.Content, StringComparison.Ordinal);
        Assert.NotNull(configuration.Occupant.Ai);
        var aiGateway = configuration.Occupant.AiGateway;
        Assert.NotNull(aiGateway);
        Assert.Equal("stub", aiGateway.Primary.ProviderId);
        Assert.Equal("deterministic", aiGateway.Primary.ModelId);
        Assert.Equal(0.2m, aiGateway.Parameters.Temperature);
        Assert.Equal(1024, aiGateway.Parameters.MaxOutputTokens);
        Assert.Equal(4, aiGateway.MaxIterations);
        Assert.Equal(TimeSpan.FromSeconds(30), aiGateway.Timeout);
        Assert.Equal(AiProcessingMode.Interactive, aiGateway.ProcessingMode);
        var fallback = Assert.Single(aiGateway.Fallback);
        Assert.Equal("stub", fallback.ProviderId);
        Assert.Equal("deterministic-backup", fallback.ModelId);
        var limits = aiGateway.CostLimits;
        Assert.NotNull(limits);
        Assert.Equal(5.00m, limits.ReactiveMaxEurPerDay);
        Assert.Equal(1.00m, limits.ProactiveMaxEurPerDay);
        Assert.Equal(6.00m, limits.TotalMaxEurPerDay);
        Assert.Equal(60, limits.MaxCallsPerHour);
        Assert.Equal(["delivery.bug-triage"], configuration.Authority.CanDecide.Select(key => key.Value));
        var authorityOverride = Assert.Single(configuration.Authority.Overrides);
        Assert.Equal("comms.external-official", authorityOverride.Key.Value);
        Assert.Equal(ActionDomainGate.HumanApproval, authorityOverride.Gate);
        Assert.Equal("ceo", authorityOverride.Approver);
        var schedule = Assert.Single(configuration.Schedules);
        Assert.Equal("relatorio-diario", schedule.Id);
        Assert.Equal("0 55 17 ? * MON-FRI", schedule.Cron);
        Assert.Equal("Compilar e enviar relatorio diario ao superior", schedule.Instruction);
    }

    [Fact]
    public async Task Provider_loads_vertical_slice_triage_position_runtime_configuration()
    {
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(ImportAt))
            .ImportAsync(ExampleConfiguration());
        var entityId = PositionEntityId.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("bug-triage"));
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(registry);

        var result = await provider.LoadAsync(entityId, CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Loaded, result.Status);
        var configuration = Assert.IsType<PositionRuntimeConfiguration>(result.Configuration);
        Assert.Equal(imported.Snapshot!.Version, configuration.Stamp.Version);
        Assert.Equal(entityId.Organization, configuration.OrganizationId);
        Assert.Equal(entityId.Position, configuration.PositionId);
        Assert.Equal(UnitId.From("engenharia"), configuration.Position.Unit);
        Assert.Equal(PositionId.From("delivery-lead"), configuration.Position.ReportsTo);
        Assert.Equal("Bug Triage", configuration.Position.Name);
        Assert.Equal("Europe/Lisbon", configuration.Position.Timezone);
        Assert.Equal(OccupantType.AiAgent, configuration.Occupant.Type);
        Assert.Equal("triage-v1", configuration.Occupant.IdentityPromptRef);
        Assert.NotNull(configuration.Occupant.IdentityPrompt);
        Assert.Equal("triage-v1", configuration.Occupant.IdentityPrompt.Id);
        Assert.Equal("prompts/triage-v1.md", configuration.Occupant.IdentityPrompt.Path);
        Assert.Contains("Example bug triage facts", configuration.Occupant.IdentityPrompt.Content, StringComparison.Ordinal);
        var aiGateway = configuration.Occupant.AiGateway;
        Assert.NotNull(aiGateway);
        Assert.Equal("stub", aiGateway.Primary.ProviderId);
        Assert.Equal("deterministic", aiGateway.Primary.ModelId);
        Assert.Equal(AiProcessingMode.Interactive, aiGateway.ProcessingMode);
        Assert.Equal(["delivery.bug-triage"], configuration.Authority.CanDecide.Select(key => key.Value));
        Assert.Empty(configuration.Authority.Overrides);
        Assert.Empty(configuration.Schedules);
    }

    [Fact]
    public async Task Provider_returns_missing_for_confirmed_absence_in_registry()
    {
        var registry = new InMemoryOrganizationRegistry();
        await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(ImportAt))
            .ImportAsync(ExampleConfiguration());
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(registry);

        var absentOrganization = await provider.LoadAsync(
            PositionEntityId.From(
                OrganizationId.From("missing-org"),
                PositionId.From("delivery-lead")),
            CancellationToken.None);
        var absentPosition = await provider.LoadAsync(
            PositionEntityId.From(
                OrganizationId.From("acme-delivery"),
                PositionId.From("missing-position")),
            CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Missing, absentOrganization.Status);
        Assert.True(absentOrganization.IsBlocking);
        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Missing, absentPosition.Status);
        Assert.True(absentPosition.IsBlocking);
    }

    [Fact]
    public async Task Provider_rejects_partial_registry_projection_as_incomplete()
    {
        var snapshot = await ImportedSnapshotAsync();
        var deliveryLead = PositionId.From("delivery-lead");
        var occupantsWithoutPosition = snapshot.Occupants
            .Where(pair => pair.Key != deliveryLead)
            .ToDictionary(pair => pair.Key, pair => pair.Value);
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => SnapshotWith(snapshot, occupants: occupantsWithoutPosition)));

        var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Incomplete, result.Status);
        Assert.True(result.IsBlocking);
        Assert.Contains("occupant", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_rejects_incoherent_registry_projection_as_incomplete()
    {
        var snapshot = await ImportedSnapshotAsync();
        var deliveryLead = PositionId.From("delivery-lead");
        var original = snapshot.Occupants[deliveryLead];
        var mismatched = new RegistryEntry<RegistryOccupant>(
            original.Value with { PositionId = PositionId.From("ceo") },
            original.Fingerprint,
            original.UpdatedAt);
        var occupants = snapshot.Occupants.ToDictionary(pair => pair.Key, pair => pair.Value);
        occupants[deliveryLead] = mismatched;
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => SnapshotWith(snapshot, occupants: occupants)));

        var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Incomplete, result.Status);
        Assert.True(result.IsBlocking);
        Assert.Contains("occupant", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    public static TheoryData<AiConfiguration, string> InvalidAiGatewayConfigurations => new()
    {
        { new AiConfiguration("stub", "deterministic", processing: "streaming"), "processing" },
        { new AiConfiguration("stub", "deterministic", timeout: "soon"), "timeout" },
        { new AiConfiguration("stub", "deterministic", temperature: 4.5), "temperature" },
        { new AiConfiguration("stub", "deterministic", maxIterations: 0), "iterations" },
        { new AiConfiguration("stub", "deterministic", budget: new BudgetConfiguration(maxCallsPerHour: -1)), "budget" },
    };

    [Theory]
    [MemberData(nameof(InvalidAiGatewayConfigurations))]
    public async Task Provider_rejects_invalid_ai_gateway_projection_as_incomplete(
        AiConfiguration ai,
        string expectedReason)
    {
        var snapshot = await ImportedSnapshotAsync();
        var deliveryLead = PositionId.From("delivery-lead");
        var original = snapshot.Occupants[deliveryLead];
        var invalid = new RegistryEntry<RegistryOccupant>(
            original.Value with { Ai = ai },
            original.Fingerprint,
            original.UpdatedAt);
        var occupants = snapshot.Occupants.ToDictionary(pair => pair.Key, pair => pair.Value);
        occupants[deliveryLead] = invalid;
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => SnapshotWith(snapshot, occupants: occupants)));

        var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Incomplete, result.Status);
        Assert.True(result.IsBlocking);
        Assert.Contains(expectedReason, result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_leaves_gateway_configuration_null_when_ai_block_is_absent()
    {
        var snapshot = await ImportedSnapshotAsync();
        var deliveryLead = PositionId.From("delivery-lead");
        var original = snapshot.Occupants[deliveryLead];
        var withoutAi = new RegistryEntry<RegistryOccupant>(
            original.Value with { Ai = null },
            original.Fingerprint,
            original.UpdatedAt);
        var occupants = snapshot.Occupants.ToDictionary(pair => pair.Key, pair => pair.Value);
        occupants[deliveryLead] = withoutAi;
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => SnapshotWith(snapshot, occupants: occupants)));

        var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Loaded, result.Status);
        var configuration = Assert.IsType<PositionRuntimeConfiguration>(result.Configuration);
        Assert.Null(configuration.Occupant.Ai);
        Assert.Null(configuration.Occupant.AiGateway);
    }

    [Fact]
    public async Task Provider_rejects_ai_occupant_when_identity_prompt_ref_is_orphaned()
    {
        var snapshot = await ImportedSnapshotAsync();
        var deliveryLead = PositionId.From("delivery-lead");
        var original = snapshot.Occupants[deliveryLead];
        var orphaned = new RegistryEntry<RegistryOccupant>(
            original.Value with { IdentityPromptRef = "missing-prompt" },
            original.Fingerprint,
            original.UpdatedAt);
        var occupants = snapshot.Occupants.ToDictionary(pair => pair.Key, pair => pair.Value);
        occupants[deliveryLead] = orphaned;
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => SnapshotWith(snapshot, occupants: occupants)),
            OrganizationsRoot);

        var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Incomplete, result.Status);
        Assert.True(result.IsBlocking);
        Assert.Contains("identity prompt", result.Reason, StringComparison.OrdinalIgnoreCase);
        Assert.Contains("missing-prompt", result.Reason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task Provider_rejects_ai_occupant_when_identity_prompt_file_is_unreadable()
    {
        var snapshot = await ImportedSnapshotAsync();
        var temporaryRoot = Path.Combine(Path.GetTempPath(), $"hive-prompts-{Guid.NewGuid():N}");
        Directory.CreateDirectory(Path.Combine(temporaryRoot, "acme-delivery", "prompts"));

        try
        {
            IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
                new SnapshotReader(_ => snapshot),
                temporaryRoot);

            var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

            Assert.Equal(PositionRuntimeConfigurationLoadStatus.Incomplete, result.Status);
            Assert.True(result.IsBlocking);
            Assert.Contains("identity prompt", result.Reason, StringComparison.OrdinalIgnoreCase);
            Assert.Contains("engineer-v1", result.Reason, StringComparison.Ordinal);
        }
        finally
        {
            Directory.Delete(temporaryRoot, recursive: true);
        }
    }

    [Fact]
    public async Task Provider_rejects_snapshot_without_valid_stamp()
    {
        var snapshot = await ImportedSnapshotAsync();
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => SnapshotWith(snapshot, version: 0)));

        var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.InvalidStamp, result.Status);
        Assert.True(result.IsBlocking);
        Assert.Contains("stamp", result.Reason, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public async Task Provider_returns_technical_failure_when_registry_reader_fails()
    {
        var failure = new InvalidOperationException("registry unavailable");
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => throw failure));

        var result = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.TechnicalFailure, result.Status);
        Assert.True(result.IsTechnicalFailure);
        Assert.Same(failure, result.TechnicalException);
        Assert.Null(result.Reason);
    }

    [Fact]
    public async Task Provider_propagates_cancellation_without_converting_it_to_technical_failure()
    {
        using var cts = new CancellationTokenSource();
        await cts.CancelAsync();
        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(cancellationToken =>
            {
                cancellationToken.ThrowIfCancellationRequested();
                return null;
            }));

        await Assert.ThrowsAsync<OperationCanceledException>(
            () => provider.LoadAsync(DeliveryLeadEntityId(), cts.Token));
    }

    private static OrganizationConfiguration ExampleConfiguration()
    {
        var result = new OrganizationConfigurationParser().ParseFile(
            Path.Combine(RepositoryRoot, "config", "organizations", "acme-delivery", "organization.yaml"));

        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static PositionEntityId DeliveryLeadEntityId() =>
        PositionEntityId.From(
            OrganizationId.From("acme-delivery"),
            PositionId.From("delivery-lead"));

    private static async Task<OrganizationRegistrySnapshot> ImportedSnapshotAsync()
    {
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(ImportAt))
            .ImportAsync(ExampleConfiguration());

        return imported.Snapshot!;
    }

    private static OrganizationRegistrySnapshot SnapshotWith(
        OrganizationRegistrySnapshot snapshot,
        long? version = null,
        IReadOnlyDictionary<PositionId, RegistryEntry<RegistryPosition>>? positions = null,
        IReadOnlyDictionary<PositionId, RegistryEntry<RegistryOccupant>>? occupants = null,
        IReadOnlyDictionary<PositionId, RegistryEntry<RegistryAuthority>>? authorities = null,
        IReadOnlyDictionary<RegistryScheduleKey, RegistryEntry<RegistrySchedule>>? schedules = null)
    {
        var constructor = typeof(OrganizationRegistrySnapshot)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();

        return (OrganizationRegistrySnapshot)constructor.Invoke(
        [
            snapshot.OrganizationId,
            version ?? snapshot.Version,
            snapshot.Fingerprint,
            snapshot.ImportedAt,
            snapshot.Organization,
            snapshot.Units,
            positions ?? snapshot.Positions,
            occupants ?? snapshot.Occupants,
            authorities ?? snapshot.Authorities,
            schedules ?? snapshot.Schedules,
            snapshot.Relations,
        ]);
    }

    private static string RepositoryRoot
    {
        get
        {
            var current = new DirectoryInfo(AppContext.BaseDirectory);
            while (current is not null)
            {
                if (File.Exists(Path.Combine(current.FullName, "Hive.sln")))
                {
                    return current.FullName;
                }

                current = current.Parent;
            }

            throw new InvalidOperationException("Could not locate the Hive repository root.");
        }
    }

    private static string OrganizationsRoot =>
        Path.Combine(RepositoryRoot, "config", "organizations");

    private sealed class ManualTimeProvider(DateTimeOffset utcNow) : TimeProvider
    {
        public override DateTimeOffset GetUtcNow() => utcNow;
    }

    private sealed class SnapshotReader(
        Func<CancellationToken, OrganizationRegistrySnapshot?> find) : IOrganizationRegistryReader
    {
        public ValueTask<OrganizationRegistrySnapshot?> FindSnapshotAsync(
            OrganizationId organizationId,
            CancellationToken cancellationToken = default) =>
            new(find(cancellationToken));
    }
}
