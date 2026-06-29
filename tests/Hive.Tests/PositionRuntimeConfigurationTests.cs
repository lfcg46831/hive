using Hive.Domain.Identity;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-06-T08a: runtime-configuration gate contracts, load outcomes,
/// compatibility decisions and operational states for the PositionActor.
/// </summary>
public sealed class PositionRuntimeConfigurationTests
{
    [Fact]
    public void Configuration_stamp_requires_positive_version_and_fingerprint_content()
    {
        var stamp = new PositionConfigurationStamp(7, "sha256:runtime");

        Assert.Equal(7, stamp.Version);
        Assert.Equal("sha256:runtime", stamp.Fingerprint);
        Assert.Throws<ArgumentOutOfRangeException>(() => new PositionConfigurationStamp(0, "sha256:runtime"));
        Assert.Throws<ArgumentException>(() => new PositionConfigurationStamp(1, " "));
        Assert.Throws<ArgumentException>(() => new PositionConfigurationStamp(1, " padded"));
    }

    [Fact]
    public void Runtime_configuration_rejects_partial_or_mismatched_shape()
    {
        var configuration = RuntimeConfiguration("acme", "bug-triage", new PositionConfigurationStamp(2, "sha256:v2"));

        Assert.Equal(OrganizationId.From("acme"), configuration.OrganizationId);
        Assert.Equal(PositionId.From("bug-triage"), configuration.PositionId);
        Assert.Equal(OccupantType.AiAgent, configuration.Occupant.Type);
        Assert.Null(configuration.Occupant.AiGateway);
        Assert.Equal("daily", Assert.Single(configuration.Schedules).Id);

        Assert.Throws<ArgumentNullException>(
            () => new PositionRuntimeConfiguration(
                null!,
                OrganizationId.From("acme"),
                PositionId.From("bug-triage"),
                RuntimeDescriptor(),
                Occupant(),
                Authority(),
                new[] { Schedule() }));
        Assert.Throws<ArgumentException>(
            () => new PositionRuntimeConfiguration(
                new PositionConfigurationStamp(1, "sha256:v1"),
                OrganizationId.From("acme"),
                PositionId.From("bug-triage"),
                RuntimeDescriptor(),
                Occupant(),
                Authority(),
                new List<PositionScheduleRuntimeConfiguration> { null! }));
    }

    [Fact]
    public void Load_result_distinguishes_loaded_blocking_and_technical_outcomes()
    {
        var configuration = RuntimeConfiguration("acme", "bug-triage", new PositionConfigurationStamp(2, "sha256:v2"));
        var loaded = PositionRuntimeConfigurationLoadResult.Loaded(configuration);
        var missing = PositionRuntimeConfigurationLoadResult.Missing("position not found");
        var incomplete = PositionRuntimeConfigurationLoadResult.Incomplete("occupant missing");
        var invalidStamp = PositionRuntimeConfigurationLoadResult.InvalidStamp("version must be positive");
        var unsupported = PositionRuntimeConfigurationLoadResult.UnsupportedRuntimeSchema("schema 3");
        var technical = PositionRuntimeConfigurationLoadResult.TechnicalFailure(
            new InvalidOperationException("registry unavailable"));

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Loaded, loaded.Status);
        Assert.Same(configuration, loaded.Configuration);
        Assert.False(loaded.IsBlocking);
        Assert.True(missing.IsBlocking);
        Assert.True(incomplete.IsBlocking);
        Assert.True(invalidStamp.IsBlocking);
        Assert.True(unsupported.IsBlocking);
        Assert.True(technical.IsTechnicalFailure);
        Assert.NotNull(technical.TechnicalException);
    }

    [Theory]
    [InlineData(null, null, PositionConfigurationCompatibilityDecision.ApplyNewConfiguration)]
    [InlineData(2, "sha256:v2", PositionConfigurationCompatibilityDecision.AlreadyApplied)]
    [InlineData(1, "sha256:v1", PositionConfigurationCompatibilityDecision.ApplyNewConfiguration)]
    [InlineData(2, "sha256:changed", PositionConfigurationCompatibilityDecision.Blocked)]
    [InlineData(3, "sha256:v3", PositionConfigurationCompatibilityDecision.Blocked)]
    public void Compatibility_matrix_compares_recovered_and_current_stamps(
        int? recoveredVersion,
        string? recoveredFingerprint,
        PositionConfigurationCompatibilityDecision expectedDecision)
    {
        var entityId = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));
        PositionConfigurationStamp? recovered = recoveredVersion is null
            ? null
            : new PositionConfigurationStamp(recoveredVersion.Value, recoveredFingerprint!);
        var current = RuntimeConfiguration("acme", "bug-triage", new PositionConfigurationStamp(2, "sha256:v2"));

        var result = PositionConfigurationCompatibility.Evaluate(
            recovered,
            PositionRuntimeConfigurationLoadResult.Loaded(current),
            entityId);

        Assert.Equal(expectedDecision, result.Decision);
    }

    [Fact]
    public void Compatibility_matrix_blocks_missing_invalid_and_mismatched_configuration()
    {
        var entityId = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));
        var mismatched = RuntimeConfiguration("acme", "delivery-lead", new PositionConfigurationStamp(2, "sha256:v2"));

        Assert.Equal(
            PositionConfigurationBlockReason.ConfigurationMissing,
            PositionConfigurationCompatibility.Evaluate(
                null,
                PositionRuntimeConfigurationLoadResult.Missing("not found"),
                entityId).BlockReason);
        Assert.Equal(
            PositionConfigurationBlockReason.ConfigurationIncomplete,
            PositionConfigurationCompatibility.Evaluate(
                null,
                PositionRuntimeConfigurationLoadResult.Incomplete("occupant missing"),
                entityId).BlockReason);
        Assert.Equal(
            PositionConfigurationBlockReason.InvalidStamp,
            PositionConfigurationCompatibility.Evaluate(
                null,
                PositionRuntimeConfigurationLoadResult.InvalidStamp("bad stamp"),
                entityId).BlockReason);
        Assert.Equal(
            PositionConfigurationBlockReason.UnsupportedRuntimeSchema,
            PositionConfigurationCompatibility.Evaluate(
                null,
                PositionRuntimeConfigurationLoadResult.UnsupportedRuntimeSchema("schema 3"),
                entityId).BlockReason);
        Assert.Equal(
            PositionConfigurationBlockReason.EntityMismatch,
            PositionConfigurationCompatibility.Evaluate(
                null,
                PositionRuntimeConfigurationLoadResult.Loaded(mismatched),
                entityId).BlockReason);
    }

    [Fact]
    public void Technical_load_failures_are_not_converted_to_blocking_configuration()
    {
        var entityId = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));
        var exception = new TimeoutException("registry timeout");

        var result = PositionConfigurationCompatibility.Evaluate(
            null,
            PositionRuntimeConfigurationLoadResult.TechnicalFailure(exception),
            entityId);

        Assert.Equal(PositionConfigurationCompatibilityDecision.TechnicalFailure, result.Decision);
        Assert.Same(exception, result.TechnicalException);
        Assert.Null(result.BlockReason);
    }

    [Fact]
    public void Operational_states_cover_recovery_loading_blocked_and_ready()
    {
        var states = Enum.GetValues<PositionOperationalState>();

        Assert.Contains(PositionOperationalState.Recovering, states);
        Assert.Contains(PositionOperationalState.LoadingConfiguration, states);
        Assert.Contains(PositionOperationalState.ConfigurationBlocked, states);
        Assert.Contains(PositionOperationalState.Ready, states);
    }

    [Fact]
    public async Task Configuration_provider_contract_loads_by_entity_id()
    {
        var entityId = PositionEntityId.From(OrganizationId.From("acme"), PositionId.From("bug-triage"));
        var configuration = RuntimeConfiguration("acme", "bug-triage", new PositionConfigurationStamp(1, "sha256:v1"));
        IPositionConfigurationProvider provider = new DelegatePositionConfigurationProvider(
            requested => requested == entityId
                ? PositionRuntimeConfigurationLoadResult.Loaded(configuration)
                : PositionRuntimeConfigurationLoadResult.Missing("not found"));

        var loaded = await provider.LoadAsync(entityId, CancellationToken.None);

        Assert.Equal(PositionRuntimeConfigurationLoadStatus.Loaded, loaded.Status);
        Assert.Same(configuration, loaded.Configuration);
    }

    private static PositionRuntimeConfiguration RuntimeConfiguration(
        string organization,
        string position,
        PositionConfigurationStamp stamp) =>
        new(
            stamp,
            OrganizationId.From(organization),
            PositionId.From(position),
            RuntimeDescriptor(),
            Occupant(),
            Authority(),
            new[] { Schedule() });

    private static PositionRuntimeDescriptor RuntimeDescriptor() =>
        new(
            UnitId.From("engineering"),
            reportsTo: PositionId.From("cto"),
            name: "Bug triage",
            timezone: "Europe/Lisbon");

    private static OccupantRuntimeConfiguration Occupant() =>
        new(
            OccupantType.AiAgent,
            identityPromptRef: "engineer-v1",
            ai: null,
            workingHours: null,
            subscriptions: new[] { new SubscriptionConfiguration("customer.reported", "PT4H") },
            tools: Array.Empty<ToolConfiguration>());

    private static PositionAuthorityRuntimeConfiguration Authority() =>
        new(
            canDecide: new[] { "triage" },
            mustEscalate: new[] { "refund" },
            requiresHumanApproval: new[] { "contract-change" });

    private static PositionScheduleRuntimeConfiguration Schedule() =>
        new("daily", "0 9 * * *", "Review open support threads.");

    private sealed class DelegatePositionConfigurationProvider : IPositionConfigurationProvider
    {
        private readonly Func<PositionEntityId, PositionRuntimeConfigurationLoadResult> _load;

        public DelegatePositionConfigurationProvider(
            Func<PositionEntityId, PositionRuntimeConfigurationLoadResult> load) =>
            _load = load;

        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(_load(entityId));
    }
}
