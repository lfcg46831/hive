using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Organization.Configuration.Validation;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;

namespace Hive.Tests;

public sealed class EventSubscriptionConfigurationTests
{
    internal const string Deadline = "{ event: directive-deadline-approaching, within: PT1H }";
    internal const string Blocked = "{ event: position-blocked-prolonged, after: P1D, critical: true, priority: high }";
    internal const string Budget = "{ event: budget-threshold-reached, threshold_percent: 80 }";
    private const string EntryPath = "positions[0].occupant.subscriptions[0]";

    [Fact]
    public void All_types_materialize_typed_parameters_and_delivery_defaults()
    {
        var subscriptions = Configuration($"[{Deadline}, {Blocked}, {Budget}]").Positions[0].Occupant.Subscriptions;
        var deadline = subscriptions[0].ToEventSubscription();
        Assert.Equal(TimeSpan.FromHours(1), Assert.IsType<DirectiveDeadlineParameters>(deadline.Parameters).Within);
        Assert.False(deadline.IsCritical);
        Assert.Equal(Priority.Normal, deadline.Priority);
        var blocked = subscriptions[1].ToEventSubscription();
        Assert.Equal(TimeSpan.FromDays(1), Assert.IsType<PositionBlockedParameters>(blocked.Parameters).After);
        Assert.True(blocked.IsCritical);
        Assert.Equal(Priority.High, blocked.Priority);
        Assert.Equal(80, Assert.IsType<BudgetThresholdParameters>(subscriptions[2].ToEventSubscription().Parameters).ThresholdPercent);
    }

    [Theory]
    [InlineData(null)]
    [InlineData("[]")]
    public async Task Absent_or_empty_subscriptions_import_as_empty(string? value)
    {
        var result = await new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry()).ImportAsync(Configuration(value));
        Assert.Equal(OrganizationImportStatus.Applied, result.Status);
        Assert.Empty(Assert.Single(result.Snapshot!.Occupants).Value.Value.Subscriptions);
    }

    public static IEnumerable<object[]> InvalidSubscriptions()
    {
        foreach (var value in new[] { "null", "{}", "text" })
            yield return [value, "positions[0].occupant.subscriptions"];
        foreach (var value in new[] { "null", "[]", "text" })
            yield return [$"[{value}]", EntryPath];
        foreach (var value in new[] { "null", "''", "unknown", "1", "Directive-deadline-approaching", "'directive-deadline-approaching '", "[]", "{}" })
            yield return [$"[{Deadline.Replace("directive-deadline-approaching", value)}]", $"{EntryPath}.event"];
        yield return ["[{ within: PT1H }]", $"{EntryPath}.event"];
        foreach (var (entry, field, valid) in new[] { (Deadline, "within", "PT1H"), (Blocked, "after", "P1D") })
        {
            yield return [$"[{entry.Replace($", {field}: {valid}", "")}]", $"{EntryPath}.{field}"];
            foreach (var value in new[] { "null", "''", "PT0S", "-PT1H", "00:01:00", "pt1h", "P", "PT", "PT999999999999999999H", "[]", "{}" })
                yield return [$"[{entry.Replace(valid, value)}]", $"{EntryPath}.{field}"];
        }
        yield return ["[{ event: budget-threshold-reached }]", $"{EntryPath}.threshold_percent"];
        foreach (var value in new[] { "null", "0", "101", "-1", "1.5", "2147483648", "true", "[]", "{}" })
            yield return [$"[{Budget.Replace("80", value)}]", $"{EntryPath}.threshold_percent"];
        foreach (var (field, values) in new[]
        {
            ("critical", new[] { "null", "yes", "True", "1", "[]", "{}" }),
            ("priority", new[] { "null", "Normal", "1", "urgent", "'normal '", "[]", "{}" }),
        })
            foreach (var value in values)
                yield return [$"[{Deadline.Replace(" }", $", {field}: {value} }}")}]", $"{EntryPath}.{field}"];
        foreach (var (entry, field) in new[]
        {
            (Deadline, "after"), (Deadline, "threshold_percent"), (Blocked, "within"),
            (Blocked, "threshold_percent"), (Budget, "within"), (Budget, "after"), (Deadline, "unexpected"),
        })
            yield return [$"[{entry.Replace(" }", $", {field}: null }}")}]", $"{EntryPath}.{field}"];
    }

    [Theory]
    [MemberData(nameof(InvalidSubscriptions))]
    public void Invalid_declarations_fail_closed_with_source_locations(string value, string path)
    {
        var result = Parse(value);
        Assert.False(result.IsSuccess);
        Assert.Null(result.Configuration);
        Assert.Contains(result.Errors, error => error.FieldPath == path);
        Assert.All(result.Errors, error =>
        {
            Assert.Equal("subscriptions.yaml", error.FilePath);
            Assert.True(error.Line > 0);
            Assert.True(error.Column > 0);
        });
    }

    [Theory]
    [InlineData("low")]
    [InlineData("normal")]
    [InlineData("high")]
    [InlineData("critical")]
    public void All_priorities_are_independent_of_critical_flag(string priority)
    {
        var subscription = Assert.Single(Configuration($"[{Deadline.Replace(" }", $", priority: {priority} }}")}]").Positions[0].Occupant.Subscriptions);
        Assert.Equal(priority, subscription.Priority);
        Assert.False(subscription.IsCritical);
    }

    [Theory]
    [InlineData(1)]
    [InlineData(100)]
    public void Threshold_boundaries_are_inclusive(int threshold) =>
        Assert.Equal(threshold, Assert.Single(Configuration($"[{Budget.Replace("80", threshold.ToString())}]").Positions[0].Occupant.Subscriptions).ThresholdPercent);

    [Theory]
    [InlineData(Deadline, "{ event: directive-deadline-approaching, within: PT60M, priority: critical, critical: true }")]
    [InlineData(Blocked, "{ event: position-blocked-prolonged, after: PT24H }")]
    [InlineData(Budget, "{ event: budget-threshold-reached, threshold_percent: 80, critical: true }")]
    public async Task Duplicate_normalized_identity_rejects_import_without_mutating_registry(string first, string duplicate)
    {
        var registry = new InMemoryOrganizationRegistry();
        var importer = new OrganizationConfigurationImporter(registry);
        var initial = await importer.ImportAsync(Configuration($"[{first}]"));
        var rejected = await importer.ImportAsync(Configuration($"[{first}, {duplicate}]"));
        Assert.Equal(OrganizationImportStatus.Invalid, rejected.Status);
        var snapshot = await registry.FindSnapshotAsync(initial.Snapshot!.OrganizationId);
        Assert.Equal(initial.Snapshot, snapshot);
        var error = Assert.Single(OrganizationConfigurationUniquenessValidator.Validate(Configuration($"[{first}, {duplicate}]")).Errors);
        Assert.Equal("duplicate-subscription-event", error.Code);
        Assert.Contains("subscriptions[1].event", error.Path);
    }

    [Fact]
    public async Task Different_parameters_are_distinct_and_reorder_or_equivalent_duration_is_a_no_op()
    {
        var importer = new OrganizationConfigurationImporter(new InMemoryOrganizationRegistry());
        var secondWindow = Deadline.Replace("PT1H", "PT2H");
        var first = await importer.ImportAsync(Configuration($"[{Deadline}, {secondWindow}, {Blocked}, {Budget}]"));
        Assert.Equal(OrganizationImportStatus.Applied, first.Status);
        var reordered = await importer.ImportAsync(Configuration($"[{Budget}, {Blocked.Replace("P1D", "PT24H")}, {secondWindow}, {Deadline.Replace("PT1H", "PT60M").Replace(" }", ", priority: normal, critical: false }")}]"));
        Assert.Equal(OrganizationImportStatus.NoChanges, reordered.Status);
        Assert.Equal(first.Snapshot!.Fingerprint, reordered.Snapshot!.Fingerprint);
        var changed = await importer.ImportAsync(Configuration($"[{Deadline}, {secondWindow}, {Blocked.Replace("high", "critical")}, {Budget}]"));
        Assert.Equal(OrganizationImportStatus.Applied, changed.Status);
        Assert.NotEqual(first.Snapshot.Fingerprint, changed.Snapshot!.Fingerprint);
    }

    [Fact]
    public void Registry_json_reads_legacy_defaults_and_round_trips_all_fields()
    {
        var legacy = RegistryJson.Deserialize<SubscriptionConfiguration>("""{"event":"directive-deadline-approaching","within":"PT60M"}""");
        Assert.Equal(new SubscriptionConfiguration("directive-deadline-approaching", "PT1H"), legacy);
        var subscriptions = Configuration($"[{Deadline}, {Blocked}, {Budget}]").Positions[0].Occupant.Subscriptions;
        Assert.Equal(subscriptions, RegistryJson.Deserialize<SubscriptionConfiguration[]>(RegistryJson.Serialize(subscriptions)));
    }

    [Fact]
    public void Programmatic_configuration_cannot_bypass_validation()
    {
        Assert.Throws<ArgumentException>(() => new SubscriptionConfiguration("unknown", "PT1H"));
        Assert.Throws<ArgumentException>(() => new SubscriptionConfiguration("directive-deadline-approaching", "PT1H", after: "PT1H"));
        Assert.Throws<ArgumentException>(() => new SubscriptionConfiguration("directive-deadline-approaching", "invalid"));
        Assert.Throws<ArgumentOutOfRangeException>(() => new SubscriptionConfiguration("budget-threshold-reached", thresholdPercent: 101));
        Assert.Throws<ArgumentNullException>(() => new SubscriptionConfiguration("position-blocked-prolonged"));
        Assert.Throws<ArgumentException>(() => new SubscriptionConfiguration("position-blocked-prolonged", after: "P1D", priority: "Normal"));
    }

    internal static OrganizationConfiguration Configuration(string? subscriptions)
    {
        var result = Parse(subscriptions);
        Assert.True(result.IsSuccess, string.Join(Environment.NewLine, result.Errors));
        return result.Configuration!;
    }

    private static OrganizationConfigurationParseResult Parse(string? subscriptions) => new OrganizationConfigurationParser().Parse(
        """
        organization:
          id: subscriptions-test
          root_unit: root
          owner: { type: human, ref: owner@example.test }
        units:
          - id: root
            parent: null
            leadership: ceo
        positions:
          - id: ceo
            unit: root
            reports_to: null
            occupant:
              type: human
        """ + (subscriptions is null ? "" : $"\n      subscriptions: {subscriptions}"),
        "subscriptions.yaml");
}
