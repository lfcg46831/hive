using Hive.Application.Events;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Events;
using Hive.Domain.Identity;
using Hive.Infrastructure.Auditing;
using Hive.Infrastructure.Auditing.PostgreSql;
using Hive.Infrastructure.Events;
using Hive.Infrastructure.Organization.Configuration;
using Hive.Infrastructure.Organization.Registry;
using Hive.Infrastructure.Organization.Registry.PostgreSql;
using Microsoft.Extensions.Configuration;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlBudgetThresholdSourceTests(PostgreSqlFixture fixture)
{
    // 2026-09-15 in Europe/Lisbon starts at 2026-09-14T23:00Z.
    private static readonly DateTimeOffset Now = new(2026, 9, 15, 12, 0, 0, TimeSpan.Zero);
    private static readonly DateTimeOffset LisbonDayStart = new(2026, 9, 14, 23, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Org = OrganizationId.From("budget-test");
    private static readonly PositionId Engineer = PositionId.From("engineer");
    private static readonly PositionId Lead = PositionId.From("lead");

    [Fact]
    public async Task Reads_attempt_costs_by_category_with_registry_caps_and_position_timezone()
    {
        await InitializeAsync();
        var log = await AuditLogAsync();
        var pulse = Call(Engineer);
        var directive = Call(Engineer);
        Accepted(log, pulse, "Pulse");
        Accepted(log, directive, "Directive");
        Attempt(log, pulse, LisbonDayStart, 0.6m);
        Attempt(log, directive, LisbonDayStart.AddHours(1), 2m);
        Attempt(log, directive, LisbonDayStart.AddHours(1), 2m, attempt: 2, currency: "USD");
        Attempt(log, Call(Engineer), LisbonDayStart.AddHours(2), 1m);
        Attempt(log, Call(Engineer), LisbonDayStart.AddTicks(-10), 50m);
        Attempt(log, Call(Engineer), Now.AddSeconds(1), 50m);
        log.Append(Journey(directive, LisbonDayStart.AddHours(1), 20m));

        await using var source = Source();
        var usage = Assert.Single(await source.ReadAsync(Org, [Engineer, Lead, PositionId.From("unknown")], Now));
        Assert.Equal(Engineer, usage.PositionId);
        Assert.Equal("Europe/Lisbon", usage.TimeZone);
        Assert.Equal(LisbonDayStart, usage.Day.StartsAtUtc);
        Assert.Equal((5m, 1m, 6m), (usage.ReactiveLimitEur!.Value, usage.ProactiveLimitEur!.Value, usage.TotalLimitEur!.Value));
        Assert.Equal([BudgetCostCategory.Proactive, BudgetCostCategory.Reactive, BudgetCostCategory.Unclassified],
            usage.Facts.Select(item => item.Category).ToArray());
        Assert.Equal([0.6m, 2m, 1m], usage.Facts.Select(item => item.AmountEur).ToArray());
        Assert.Equal(directive.Directive, usage.Facts[1].Correlation.DirectiveId);

        // 60%: proactive 0.6 of 1.00 and total 3.60 of 6.00 are reached; reactive 2.00 of 5.00 is not.
        var result = await DetectAsync(source, Snapshot(60));
        var payloads = result.Occurrences.Select(item => Assert.IsType<BudgetThresholdReachedPayload>(item.Payload))
            .ToDictionary(item => item.Budget);
        Assert.Equal([DailyBudgetKind.Proactive, DailyBudgetKind.Total], payloads.Keys.Order().ToArray());
        Assert.Equal(pulse.Message, payloads[DailyBudgetKind.Proactive].Correlation!.MessageId);
        Assert.Equal(3.6m, payloads[DailyBudgetKind.Total].ConsumedEur);
        Assert.Equal(new DateOnly(2026, 9, 15), payloads[DailyBudgetKind.Total].CivilDate);
        Assert.Null(result.Cursor);
    }

    [Fact]
    public async Task Duplicate_attempts_count_once_and_late_commits_and_classification_are_reread()
    {
        await InitializeAsync();
        var log = await AuditLogAsync();
        var call = Call(Engineer);
        Attempt(log, call, LisbonDayStart.AddHours(1), 0.5m);
        // The same attempt persisted again with another outcome must not be debited twice.
        log.Append(JourneyAuditRecord.Create(JourneyAuditStage.GatewayCostRecorded, JourneyAuditOutcome.Failed, Org,
            call.Thread, call.Message, positionId: call.Position, cost: new AiCostMetadata(0.5m, "EUR", isEstimated: true),
            payload: new Dictionary<string, string>
            {
                ["scope"] = "attempt", ["attemptId"] = "0-1", ["operation"] = "directive", ["iteration"] = "1",
            },
            occurredAtUtc: LisbonDayStart.AddHours(1), idempotencyDiscriminator: "directive:1:0-1"));
        await using var source = Source();
        var first = await DetectAsync(source, Snapshot(80));
        Assert.Empty(first.Occurrences);
        Assert.Equal(BudgetCostCategory.Unclassified, Assert.Single(Assert.Single(await source.ReadAsync(Org, [Engineer], Now)).Facts).Category);

        Accepted(log, call, "EventTrigger");
        var classified = Assert.Single((await DetectAsync(source, Snapshot(50))).Occurrences);
        Assert.Equal(DailyBudgetKind.Proactive, ((BudgetThresholdReachedPayload)classified.Payload).Budget);

        Attempt(log, Call(Engineer), LisbonDayStart.AddMinutes(5), 4.5m);
        await using var restarted = Source();
        var afterLate = await DetectAsync(restarted, Snapshot(50));
        Assert.Contains(afterLate.Occurrences, item => item.Key == classified.Key);
        Assert.Contains(afterLate.Occurrences, item => ((BudgetThresholdReachedPayload)item.Payload).Budget == DailyBudgetKind.Total);
    }

    [Fact]
    public async Task Undeclared_timezone_uses_utc_and_invalid_data_fails_without_absence()
    {
        await InitializeAsync(engineerTimezone: null);
        var log = await AuditLogAsync();
        await using var source = Source();
        var usage = Assert.Single(await source.ReadAsync(Org, [Engineer], Now));
        Assert.Equal("UTC", usage.TimeZone);
        Assert.Equal(new DateTimeOffset(2026, 9, 15, 0, 0, 0, TimeSpan.Zero), usage.Day.StartsAtUtc);

        await InitializeAsync(engineerTimezone: "Mars/Olympus");
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Org, [Engineer], Now).AsTask());

        await InitializeAsync();
        log = await AuditLogAsync();
        Attempt(log, Call(Engineer), LisbonDayStart.AddHours(1), 1m);
        await using (var dataSource = fixture.CreateDataSource())
        await using (var command = dataSource.CreateCommand("UPDATE audit.journey_events SET payload = payload - 'attemptId'"))
            await command.ExecuteNonQueryAsync();
        await Assert.ThrowsAsync<InvalidOperationException>(() => source.ReadAsync(Org, [Engineer], Now).AsTask());
        Assert.Empty(await source.ReadAsync(OrganizationId.From("unknown"), [Engineer], Now));
        using var cancellation = new CancellationTokenSource();
        cancellation.Cancel();
        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => source.ReadAsync(Org, [Engineer], Now, cancellation.Token).AsTask());
    }

    private async Task InitializeAsync(string? engineerTimezone = "Europe/Lisbon")
    {
        await fixture.ResetRegistryAsync();
        await using var dataSource = fixture.CreateDataSource();
        await new PostgreSqlOrganizationRegistryMigrator(dataSource).MigrateAsync();
        var parsed = new OrganizationConfigurationParser().Parse(Yaml(engineerTimezone), "budget.yaml");
        Assert.True(parsed.IsSuccess, string.Join(Environment.NewLine, parsed.Errors));
        var imported = await new OrganizationConfigurationImporter(new PostgreSqlOrganizationRegistry(dataSource))
            .ImportAsync(parsed.Configuration!);
        Assert.True(imported.Status == OrganizationImportStatus.Applied, string.Join(Environment.NewLine, imported.ValidationErrors));
    }

    private async Task<PostgreSqlJourneyAuditLog> AuditLogAsync()
    {
        var dataSource = fixture.CreateDataSource();
        await using (var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS audit CASCADE;"))
            await command.ExecuteNonQueryAsync();
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        return new PostgreSqlJourneyAuditLog(dataSource);
    }

    private PostgreSqlBudgetThresholdSource Source() => new(new ConfigurationBuilder()
        .AddInMemoryCollection(new Dictionary<string, string?> { ["ConnectionStrings:PostgreSql"] = fixture.ConnectionString }).Build());

    private static Task<DomainEventDetectionResult> DetectAsync(IBudgetThresholdSource source, EventSubscriptionsSnapshot snapshot) =>
        new BudgetThresholdReachedDetector(source).EvaluateAsync(new(
            new(Org, OrganizationEventType.BudgetThresholdReached), snapshot, null, Now)).AsTask();

    private static EventSubscriptionsSnapshot Snapshot(int percent) => EventSubscriptionsSnapshot.CreateBuilder(Org)
        .AddPosition(Engineer, [new(new BudgetThresholdParameters(percent))]).Build();

    private static SourceCall Call(PositionId position) => new(position, ThreadId.New(), MessageId.New(), DirectiveId.New());

    private static void Accepted(IJourneyAuditLog log, SourceCall call, string messageType) =>
        log.Append(JourneyAuditRecord.Create(JourneyAuditStage.PositionAccepted, JourneyAuditOutcome.Accepted, Org,
            call.Thread, call.Message, positionId: call.Position, messageType: messageType, occurredAtUtc: LisbonDayStart));

    private static void Attempt(IJourneyAuditLog log, SourceCall call, DateTimeOffset completedAt, decimal amount,
        int attempt = 1, string currency = "EUR")
    {
        var request = new AiGatewayRequest(Org, call.Position, call.Thread, call.Message, "private prompt",
            metadata: new Dictionary<string, string> { ["hive.operation"] = "directive", ["iteration"] = "1", ["directive_id"] = call.Directive.ToString() });
        var cost = new AiCostMetadata(amount, currency, isEstimated: true);
        var response = AiGatewayResponse.Succeeded(Org, call.Position, call.Thread, call.Message, "private answer",
            AiFinishReason.Stop, new AiProviderMetadata("stub", "model"), cost: cost);
        new JourneyAuditAiGatewayPublisher(log).Publish(AiGatewayCostAuditEvent.FromResponse(request, response,
            completedAt.AddSeconds(-1), completedAt, new AiGatewayCostAuditAttempt(0, attempt, reachedProvider: true)));
    }

    private static JourneyAuditRecord Journey(SourceCall call, DateTimeOffset at, decimal amount) =>
        JourneyAuditRecord.Create(JourneyAuditStage.GatewayCostRecorded, JourneyAuditOutcome.Succeeded, Org, call.Thread,
            call.Message, positionId: call.Position, cost: new AiCostMetadata(amount, "EUR", isEstimated: true),
            payload: new Dictionary<string, string> { ["scope"] = "journey" }, occurredAtUtc: at);

    private static string Yaml(string? engineerTimezone) =>
        $$"""
        organization:
          id: budget-test
          root_unit: root
          owner: { type: human, ref: owner@example.test }
        units:
          - id: root
            parent: null
            leadership: lead
        positions:
          - id: lead
            unit: root
            reports_to: null
            occupant:
              type: human
          - id: engineer
            unit: root
            reports_to: lead
        {{(engineerTimezone is null ? "" : $"    timezone: {engineerTimezone}\n")}}    occupant:
              type: ai-agent
              ai:
                provider: stub
                model: model
                budget:
                  reactive_max_eur_per_day: 5.00
                  proactive_max_eur_per_day: 1.00
                  total_max_eur_per_day: 6.00
                  max_calls_per_hour: 60
        """;

    private sealed record SourceCall(PositionId Position, ThreadId Thread, MessageId Message, DirectiveId Directive);
}
