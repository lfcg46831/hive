using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Hive.Actors.Sharding;
using Hive.Api.Directives;
using Hive.Domain.Auditing;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization;
using Hive.Domain.Positions;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Auditing.PostgreSql;
using Microsoft.AspNetCore.Builder;
using Microsoft.AspNetCore.TestHost;
using Microsoft.Extensions.DependencyInjection;
using Npgsql;

namespace Hive.Tests.PostgreSql;

[Collection(PostgreSqlCollection.Name)]
public sealed class PostgreSqlBugTriageHappyPathEndToEndTests(PostgreSqlFixture postgres)
{
    [Fact]
    public async Task Local_happy_path_submits_bug_persists_directive_emits_report_and_reads_audit_after_restart()
    {
        await using var dataSource = postgres.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t13-happy-path";
            options.Scenario = "bug-triage-report";
            options.Usage = new StubAiGatewayUsageOptions
            {
                InputTokens = 42,
                OutputTokens = 36,
                TotalTokens = 78,
                IsEstimated = true,
            };
            options.Cost = new StubAiGatewayCostOptions
            {
                Amount = 0.00042m,
                Currency = "EUR",
                IsEstimated = true,
            };
        });
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(
            scenario,
            auditLog);
        await using var app = BuildSubmissionApp(
            fixture,
            SampleRelations(),
            auditLog);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme/directives",
            RequestFrom(scenario.Directive));

        Assert.Equal(HttpStatusCode.Accepted, response.StatusCode);
        var body = await response.Content.ReadFromJsonAsync<JsonElement>();
        Assert.Equal("accepted", body.GetProperty("status").GetString());
        Assert.Equal(scenario.Directive.Id.ToString(), body.GetProperty("messageId").GetString());
        Assert.Equal(scenario.Directive.DirectiveId.ToString(), body.GetProperty("directiveId").GetString());
        Assert.Equal(scenario.Directive.Thread.ToString(), body.GetProperty("threadId").GetString());

        var timelineBeforeRestart = await WaitForTimelineAsync(
            dataSource,
            scenario,
            timeline => timeline.Entries.Any(entry =>
                entry.Stage == JourneyAuditStage.ResultMessageCreated &&
                entry.MessageType == nameof(Report)));
        AssertTimelineCoversHappyPath(timelineBeforeRestart);

        var persistedEvents = await fixture.ReadPersistedEventsAsync();
        Assert.Contains(persistedEvents.OfType<MessageReceived>(), received =>
            received.Message.Id == scenario.Directive.Id);
        Assert.Contains(persistedEvents.OfType<MessageDispatched>(), dispatched =>
            dispatched.Message == scenario.Directive.Id);

        await fixture.RestartPositionAsync();
        var recreatedReadModel = new PostgreSqlJourneyAuditReadModel(dataSource);
        var timelineAfterRestart = recreatedReadModel.ReadTimeline(
            scenario.Directive.OrganizationId,
            scenario.Directive.Thread,
            scenario.Directive.DirectiveId);

        Assert.Equal(
            timelineBeforeRestart.Entries.Select(entry => entry.AuditEventId),
            timelineAfterRestart.Entries.Select(entry => entry.AuditEventId));
        AssertTimelineCoversHappyPath(timelineAfterRestart);
    }

    private static WebApplication BuildSubmissionApp(
        AiDirectiveIntegrationFixture fixture,
        IOrganizationRelations relations,
        IJourneyAuditLog auditLog)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(relations);
        builder.Services.AddSingleton<IPositionCommandDispatcher>(
            new FixturePositionCommandDispatcher(fixture));
        builder.Services.AddSingleton(auditLog);
        builder.Services.AddHiveDirectiveSubmissionApi();

        var app = builder.Build();
        app.MapHiveDirectiveSubmissionApi();
        return app;
    }

    private static object RequestFrom(Directive directive)
    {
        var from = Assert.IsType<PositionEndpointRef>(directive.From);
        var to = Assert.IsType<PositionEndpointRef>(directive.To);

        return new
        {
            messageId = directive.Id.ToString(),
            from = new { kind = "position", positionId = from.PositionId.Value },
            to = new { kind = "position", positionId = to.PositionId.Value },
            threadId = directive.Thread.ToString(),
            priority = PriorityContract.ToWireValue(directive.Priority),
            schemaVersion = directive.SchemaVersion,
            sentAt = directive.SentAt,
            deadline = directive.Deadline,
            directiveId = directive.DirectiveId.ToString(),
            objective = directive.Objective,
            context = directive.Context,
        };
    }

    private static async Task<JourneyAuditTimeline> WaitForTimelineAsync(
        NpgsqlDataSource dataSource,
        AiDirectiveIntegrationScenario scenario,
        Func<JourneyAuditTimeline, bool> isReady)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        var readModel = new PostgreSqlJourneyAuditReadModel(dataSource);

        while (DateTimeOffset.UtcNow < deadline)
        {
            var timeline = readModel.ReadTimeline(
                scenario.Directive.OrganizationId,
                scenario.Directive.Thread,
                scenario.Directive.DirectiveId);
            if (isReady(timeline))
            {
                return timeline;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Bug triage journey audit timeline did not reach the expected state.");
    }

    private static void AssertTimelineCoversHappyPath(JourneyAuditTimeline timeline)
    {
        Assert.Equal(
            [
                JourneyAuditStage.SubmissionReceived,
                JourneyAuditStage.DirectiveCreated,
                JourneyAuditStage.PositionAccepted,
                JourneyAuditStage.PositionDispatched,
                JourneyAuditStage.GatewayCalled,
                JourneyAuditStage.GatewayCostRecorded,
                JourneyAuditStage.AgentDecided,
                JourneyAuditStage.ResultMessageCreated,
            ],
            timeline.Entries.Select(entry => entry.Stage));

        Assert.All(timeline.Entries, entry =>
            Assert.NotEqual(JourneyAuditOutcome.Rejected, entry.Outcome));
        Assert.All(timeline.Entries, entry =>
            Assert.Equal(PositionId.From("triage-agent"), entry.PositionId));
        Assert.All(
            timeline.Entries.Where(entry =>
                entry.Stage is JourneyAuditStage.SubmissionReceived or JourneyAuditStage.DirectiveCreated),
            entry => Assert.Equal(
                "directive.objective,directive.context",
                entry.RedactedPayload["redactions"]));

        Assert.Contains(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.GatewayCalled &&
            entry.Provider?.ProviderId == "stub" &&
            entry.Provider.ModelId == "t13-happy-path" &&
            entry.Latency is not null);
        Assert.Contains(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.GatewayCostRecorded &&
            entry.Usage?.TotalTokens == 78 &&
            entry.Cost?.Amount == 0.00042m);
        Assert.Contains(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.AgentDecided &&
            entry.RedactedPayload["decisionKind"] == "Report");
        Assert.Contains(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.ResultMessageCreated &&
            entry.MessageType == nameof(Report) &&
            entry.RedactedPayload["resultMessageType"] == nameof(Report));
    }

    private static IOrganizationRelations SampleRelations() =>
        new MaterializedOrganizationRelations(
            OrganizationRelationsSnapshot
                .CreateBuilder(OrganizationId.From("acme"), new OrganizationOwnerEndpointRef())
                .AddPosition(PositionId.From("ceo"), UnitId.From("root"))
                .AddPosition(
                    PositionId.From("delivery-lead"),
                    UnitId.From("delivery"),
                    PositionId.From("ceo"))
                .AddPosition(
                    PositionId.From("triage-agent"),
                    UnitId.From("delivery"),
                    PositionId.From("delivery-lead"))
                .Build());

    private static async Task ResetAuditAsync(NpgsqlDataSource dataSource)
    {
        await using var command = dataSource.CreateCommand("DROP SCHEMA IF EXISTS audit CASCADE;");
        await command.ExecuteNonQueryAsync();
    }

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private sealed class FixturePositionCommandDispatcher(
        AiDirectiveIntegrationFixture fixture) : IPositionCommandDispatcher
    {
        public ValueTask DispatchAsync(
            PositionEnvelope envelope,
            CancellationToken cancellationToken) =>
            fixture.DispatchAsync(envelope, cancellationToken);
    }
}
