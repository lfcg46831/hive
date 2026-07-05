using System.Reflection;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using Hive.Domain.Scheduling;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Scheduling;

namespace Hive.Tests;

public sealed class RegistryScheduleLoaderTests
{
    private static readonly DateTimeOffset ImportAt =
        new(2026, 7, 4, 9, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Loader_returns_canonical_schedule_definitions_from_registry_snapshot()
    {
        var snapshot = await ImportedSnapshotAsync(ExampleConfiguration());

        var result = RegistryScheduleLoader.Load(snapshot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(error => error.ToString())));
        var loaded = Assert.Single(result.Schedules);
        Assert.Equal(OrganizationId.From("acme-delivery"), loaded.OrganizationId);
        Assert.Equal(PositionId.From("delivery-lead"), loaded.PositionId);
        Assert.True(loaded.IsActive);
        Assert.Equal(new TimeOnly(9, 0), loaded.WorkingHours.Start);
        Assert.Equal(new TimeOnly(18, 0), loaded.WorkingHours.End);

        var definition = loaded.Definition;
        Assert.Equal("relatorio-diario", definition.Id.Value);
        Assert.Equal("0 55 17 ? * MON-FRI", definition.Cron.Value);
        Assert.Equal("Europe/Lisbon", definition.TimeZone);
        Assert.Equal("Compilar e enviar relatorio diario ao superior", definition.Payload);
        Assert.Equal(Priority.Normal, definition.Priority);
        Assert.False(definition.IsCritical);
        Assert.Equal(CatchUpPolicy.Skip, definition.CatchUp);
    }

    [Fact]
    public async Task Loader_preserves_inactive_schedules_but_runtime_position_configuration_omits_them()
    {
        var configuration = WithDeliveryLeadSchedule(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "paused-report",
                "0 0 9 ? * MON-FRI",
                "Keep this paused",
                isActive: false));
        var snapshot = await ImportedSnapshotAsync(configuration);

        var result = RegistryScheduleLoader.Load(snapshot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(error => error.ToString())));
        var loaded = Assert.Single(result.Schedules);
        Assert.False(loaded.IsActive);

        IPositionConfigurationProvider provider = new RegistryPositionConfigurationProvider(
            new SnapshotReader(_ => snapshot),
            OrganizationsRoot);
        var runtime = await provider.LoadAsync(DeliveryLeadEntityId(), CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Loaded, runtime.Status);
        var positionConfiguration = Assert.IsType<PositionRuntimeConfiguration>(runtime.Configuration);
        Assert.Empty(positionConfiguration.Schedules);
    }

    [Fact]
    public async Task Loader_reports_semantic_schedule_errors_without_loading_invalid_entries()
    {
        var configuration = WithDeliveryLeadSchedule(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "bad-schedule",
                "99 99 99 * * MON-FRI",
                "Run an invalid schedule",
                priority: "urgent",
                catchUp: "replay"),
            timezone: "Mars/Base",
            workingHours: new WorkingHoursConfiguration("18:00", "09:00"));
        var snapshot = await ImportedSnapshotAsync(configuration);

        var result = RegistryScheduleLoader.Load(snapshot);

        Assert.False(result.IsValid);
        Assert.Empty(result.Schedules);
        Assert.Contains(result.Errors, error => error.Code == "schedule-cron-invalid");
        Assert.Contains(result.Errors, error => error.Code == "position-timezone-invalid");
        Assert.Contains(result.Errors, error => error.Code == "working-hours-invalid");
        Assert.Contains(result.Errors, error => error.Code == "schedule-priority-invalid");
        Assert.Contains(result.Errors, error => error.Code == "schedule-catch-up-invalid");
    }

    [Theory]
    [InlineData("0 0 9 ? JAN MON")]
    [InlineData("0 0/15 9-17 ? * MON-FRI")]
    [InlineData("0 0 9 1 * ?")]
    [InlineData("0 30 8 ? * MON-FRI 2026")]
    public async Task Loader_accepts_valid_quartz_cron_variants(string cron)
    {
        var configuration = WithDeliveryLeadSchedule(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "valid-cron",
                cron,
                "Run a valid schedule"));
        var snapshot = await ImportedSnapshotAsync(configuration);

        var result = RegistryScheduleLoader.Load(snapshot);

        Assert.True(result.IsValid, string.Join(Environment.NewLine, result.Errors.Select(error => error.ToString())));
        var loaded = Assert.Single(result.Schedules);
        Assert.Equal(cron, loaded.Definition.Cron.Value);
    }

    [Theory]
    [InlineData("0 0 9 * * MON-FRI")]
    [InlineData("0 0 9 ? * ?")]
    [InlineData("0,,5 0 9 * * MON-FRI")]
    [InlineData("0 0 9 ?/5 * MON-FRI")]
    public async Task Loader_rejects_malformed_cron_fields(string cron)
    {
        var configuration = WithDeliveryLeadSchedule(
            ExampleConfiguration(),
            new ScheduleEntryConfiguration(
                "bad-cron",
                cron,
                "Run a malformed schedule"));
        var snapshot = await ImportedSnapshotAsync(configuration);

        var result = RegistryScheduleLoader.Load(snapshot);

        Assert.False(result.IsValid);
        Assert.Empty(result.Schedules);
        Assert.Contains(result.Errors, error => error.Code == "schedule-cron-invalid");
    }

    [Fact]
    public async Task Loader_reports_registry_schedule_references_that_do_not_resolve_to_positions()
    {
        var snapshot = await ImportedSnapshotAsync(ExampleConfiguration());
        var schedules = snapshot.Schedules.ToDictionary(pair => pair.Key, pair => pair.Value);
        var ghostPosition = PositionId.From("ghost-position");
        schedules.Add(
            new RegistryScheduleKey(ghostPosition, "ghost-schedule"),
            new RegistryEntry<RegistrySchedule>(
                new RegistrySchedule(
                    ghostPosition,
                    "ghost-schedule",
                    true,
                    "0 0 9 ? * MON-FRI",
                    "normal",
                    false,
                    "skip",
                    "Run a ghost schedule"),
                "sha256:ghost",
                ImportAt));
        var incoherent = SnapshotWith(snapshot, schedules: schedules);

        var result = RegistryScheduleLoader.Load(incoherent);

        Assert.False(result.IsValid);
        Assert.Contains(result.Errors, error => error.Code == "schedule-position-not-found");
    }

    private static OrganizationConfiguration WithDeliveryLeadSchedule(
        OrganizationConfiguration configuration,
        ScheduleEntryConfiguration schedule,
        string? timezone = "Europe/Lisbon",
        WorkingHoursConfiguration? workingHours = null) =>
        new(
            configuration.Organization,
            configuration.Units,
            configuration.Positions
                .Select(position => position.Id.Value == "delivery-lead"
                    ? new PositionConfiguration(
                        position.Id,
                        position.Unit,
                        new OccupantConfiguration(
                            position.Occupant.Type,
                            position.Occupant.IdentityPromptRef,
                            position.Occupant.Ai,
                            workingHours ?? new WorkingHoursConfiguration("09:00", "18:00"),
                            position.Occupant.Authority,
                            [schedule],
                            position.Occupant.Subscriptions,
                            position.Occupant.Tools),
                        position.ReportsTo,
                        position.Name,
                        timezone)
                    : position)
                .ToArray(),
            configuration.Prompts);

    private static async Task<OrganizationRegistrySnapshot> ImportedSnapshotAsync(
        OrganizationConfiguration configuration)
    {
        var registry = new InMemoryOrganizationRegistry();
        var imported = await new OrganizationConfigurationImporter(
            registry,
            new ManualTimeProvider(ImportAt))
            .ImportAsync(configuration);

        Assert.Equal(OrganizationImportStatus.Applied, imported.Status);
        return imported.Snapshot!;
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

    private static OrganizationRegistrySnapshot SnapshotWith(
        OrganizationRegistrySnapshot snapshot,
        IReadOnlyDictionary<RegistryScheduleKey, RegistryEntry<RegistrySchedule>>? schedules = null)
    {
        var constructor = typeof(OrganizationRegistrySnapshot)
            .GetConstructors(BindingFlags.Instance | BindingFlags.NonPublic)
            .Single();

        return (OrganizationRegistrySnapshot)constructor.Invoke(
        [
            snapshot.OrganizationId,
            snapshot.Version,
            snapshot.Fingerprint,
            snapshot.ImportedAt,
            snapshot.Organization,
            snapshot.Units,
            snapshot.Positions,
            snapshot.Occupants,
            snapshot.Authorities,
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
