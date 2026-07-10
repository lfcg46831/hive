using System.Net;
using System.Net.Http.Json;
using System.Text.Json;
using Akka.Actor;
using Hive.Actors.Positions;
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

    [Fact]
    public async Task Restart_and_redelivery_do_not_duplicate_provider_result_effects_or_audit()
    {
        await using var dataSource = postgres.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14c-restart-redelivery";
            options.Scenario = "bug-triage-report";
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
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
        var timelineBeforeRestart = await WaitForTimelineAsync(
            dataSource,
            scenario,
            timeline => timeline.Entries.Any(entry =>
                entry.Stage == JourneyAuditStage.ResultMessageCreated &&
                entry.MessageType == nameof(Report)));
        var eventsBeforeRestart = await WaitForPersistedEventsAsync(
            fixture,
            events => events.OfType<TaskCompleted>()
                .Any(completed => completed.TaskId == initialTask.TaskId));
        var stateBeforeRestart = await WaitForPositionStateAsync(
            fixture,
            snapshot => snapshot.ShortMemory.ContainsKey(scenario.ResultMemoryKey) &&
                !snapshot.OpenTasks.ContainsKey(initialTask.TaskId));

        await fixture.RestartPositionAsync();

        var firstRedelivery = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme/directives",
            RequestFrom(scenario.Directive));
        Assert.Equal(HttpStatusCode.Accepted, firstRedelivery.StatusCode);
        var stateAfterFirstRedelivery = await fixture.GetPositionStateAsync();
        var timelineAfterFirstRedelivery = await WaitForTimelineAsync(
            dataSource,
            scenario,
            timeline => timeline.Entries.Count(entry =>
                entry.Stage == JourneyAuditStage.DuplicateSuppressed) == 1);

        var secondRedelivery = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme/directives",
            RequestFrom(scenario.Directive));
        Assert.Equal(HttpStatusCode.Accepted, secondRedelivery.StatusCode);
        var stateAfterSecondRedelivery = await fixture.GetPositionStateAsync();
        var timelineAfterSecondRedelivery = new PostgreSqlJourneyAuditReadModel(dataSource).ReadTimeline(
            scenario.Directive.OrganizationId,
            scenario.Directive.Thread,
            scenario.Directive.DirectiveId);
        var eventsAfterRedelivery = await fixture.ReadPersistedEventsAsync();

        Assert.Equal(
            stateBeforeRestart.ShortMemory[scenario.ResultMemoryKey],
            stateAfterFirstRedelivery.ShortMemory[scenario.ResultMemoryKey]);
        Assert.Equal(
            stateBeforeRestart.ShortMemory[scenario.ResultMemoryKey],
            stateAfterSecondRedelivery.ShortMemory[scenario.ResultMemoryKey]);
        Assert.DoesNotContain(initialTask.TaskId, stateAfterFirstRedelivery.OpenTasks.Keys);
        Assert.DoesNotContain(initialTask.TaskId, stateAfterSecondRedelivery.OpenTasks.Keys);
        Assert.Equal(
            eventsBeforeRestart.OfType<MessageReceived>().Count(),
            eventsAfterRedelivery.OfType<MessageReceived>().Count());
        Assert.Equal(
            eventsBeforeRestart.OfType<MessageDispatched>().Count(),
            eventsAfterRedelivery.OfType<MessageDispatched>().Count());
        Assert.Equal(
            eventsBeforeRestart.OfType<ShortMemoryUpdated>().Count(),
            eventsAfterRedelivery.OfType<ShortMemoryUpdated>().Count());
        Assert.Equal(
            eventsBeforeRestart.OfType<TaskCompleted>().Count(),
            eventsAfterRedelivery.OfType<TaskCompleted>().Count());

        AssertJourneyStageCountsAfterRedelivery(timelineAfterFirstRedelivery);
        AssertJourneyStageCountsAfterRedelivery(timelineAfterSecondRedelivery);

        var suppression = Assert.Single(timelineAfterSecondRedelivery.Entries.Where(entry =>
            entry.Stage == JourneyAuditStage.DuplicateSuppressed));
        Assert.Equal(JourneyAuditOutcome.Rejected, suppression.Outcome);
        Assert.Equal("terminal-result-already-materialized", suppression.ReasonCode);
        Assert.Equal("ResultMessageCreated", suppression.RedactedPayload["suppressedStage"]);
        var suppressionPayload = string.Join(" ", suppression.RedactedPayload.Values);
        Assert.DoesNotContain(scenario.Directive.Objective, suppressionPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(scenario.Directive.Context, suppressionPayload, StringComparison.Ordinal);
        Assert.DoesNotContain(
            JourneyAuditStage.DuplicateSuppressed,
            timelineBeforeRestart.Entries.Select(entry => entry.Stage));
    }

    [Theory]
    [InlineData(
        "bug-triage-missing-information",
        "t14a-missing-information",
        "Missing bug triage information",
        "The report lacks enough reproduction or environment evidence to complete triage deterministically.",
        "Request reproduction steps and affected environment")]
    [InlineData(
        "bug-triage-external-decision-blocked",
        "t14a-actionable-escalation",
        "External decision required",
        "The next action depends on an external production or customer-impact decision outside the triage position authority.",
        "Escalate for an accountable external decision")]
    public async Task Alternative_escalation_paths_emit_canonical_message_and_correlated_audit(
        string stubScenario,
        string modelId,
        string expectedIssue,
        string expectedContext,
        string expectedOption)
    {
        await using var dataSource = postgres.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = modelId;
            options.Scenario = stubScenario;
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
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
        var timeline = await WaitForTimelineAsync(
            dataSource,
            scenario,
            ready => ready.Entries.Any(entry =>
                entry.Stage == JourneyAuditStage.ResultMessageCreated &&
                entry.MessageType == nameof(Escalation)));
        var agent = await fixture.ResolveAgentAsync();
        var resultMessage = await agent.Ask<AiDirectiveResultMessageQueryResult>(
            new GetAiDirectiveResultMessage(fixture.CorrelationId),
            Timeout());
        var state = await WaitForPositionStateAsync(
            fixture,
            snapshot => snapshot.OpenTasks.TryGetValue(initialTask.TaskId, out var task) &&
                task.Priority == Priority.Critical &&
                snapshot.ShortMemory.ContainsKey(scenario.ResultMemoryKey));

        Assert.True(resultMessage.Found);
        var escalation = Assert.IsType<Escalation>(resultMessage.Result!.Message);
        Assert.Equal(new PositionEndpointRef(PositionId.From("triage-agent")), escalation.From);
        Assert.Equal(new PositionEndpointRef(PositionId.From("delivery-lead")), escalation.To);
        Assert.Equal(scenario.Directive.Thread, escalation.Thread);
        Assert.Equal(scenario.Directive.Priority, escalation.Priority);
        Assert.Equal(scenario.Directive.Deadline, escalation.Deadline);
        Assert.Equal(expectedIssue, escalation.Issue);
        Assert.Equal(expectedContext, escalation.Context);
        Assert.Contains(expectedOption, escalation.OptionsConsidered);

        var expectedNote = $"Escalation: {expectedIssue}. {expectedContext}";
        Assert.Equal(expectedNote, state.ShortMemory[scenario.ResultMemoryKey]);
        Assert.Equal(Priority.Critical, state.OpenTasks[initialTask.TaskId].Priority);

        AssertTimelineCoversEscalationPath(
            timeline,
            scenario,
            modelId);
    }

    [Fact]
    public async Task Provider_controlled_failure_records_structured_error_without_result_message_or_partial_state()
    {
        await using var dataSource = postgres.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14b-provider-failure";
            options.Scenario = "provider-controlled-failure";
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
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
        var timeline = await WaitForTimelineAsync(
            dataSource,
            scenario,
            ready => ready.Entries.Any(entry => entry.Stage == JourneyAuditStage.AgentDecided));
        var persistedEvents = await WaitForPersistedEventsAsync(
            fixture,
            events => events.OfType<MessageProcessingCompleted>()
                .Any(completed => completed.Message == scenario.Directive.Id));
        var state = await WaitForPositionStateAsync(
            fixture,
            snapshot => snapshot.ProcessedMessages.Contains(scenario.Directive.Id));

        AssertTimelineCoversProviderFailure(
            timeline,
            scenario,
            modelId: "t14b-provider-failure");

        Assert.Contains(scenario.Directive.Id, state.ProcessedMessages);
        Assert.DoesNotContain(scenario.ResultMemoryKey, state.ShortMemory.Keys);
        Assert.Contains(initialTask.TaskId, state.OpenTasks.Keys);
        var completed = Assert.Single(persistedEvents.OfType<MessageProcessingCompleted>());
        Assert.Equal(MessageProcessingCompletionStatus.Failed, completed.Status);
        Assert.Equal("ai-gateway-failure", completed.FailureCode);
        Assert.Empty(persistedEvents.OfType<ShortMemoryUpdated>());
        Assert.Empty(persistedEvents.OfType<TaskUpdated>());
        Assert.Empty(persistedEvents.OfType<TaskCompleted>());
        Assert.Empty(persistedEvents.OfType<TaskCreated>());
    }

    [Fact]
    public async Task Invalid_submission_returns_problem_details_without_dispatch_audit_or_position_state()
    {
        await using var dataSource = postgres.CreateDataSource();
        await ResetAuditAsync(dataSource);
        await new PostgreSqlJourneyAuditLogMigrator(dataSource).MigrateAsync();
        var auditLog = new PostgreSqlJourneyAuditLog(dataSource);
        var scenario = AiDirectiveIntegrationScenario.Create();
        var initialTask = Assert.Single(scenario.OpenTasks);
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(
            scenario,
            auditLog);
        var dispatcher = new FixturePositionCommandDispatcher(fixture);
        await using var app = BuildSubmissionApp(
            fixture,
            SampleRelations(),
            auditLog,
            dispatcher);
        await app.StartAsync();
        using var client = app.GetTestClient();

        var response = await client.PostAsJsonAsync(
            $"{DirectiveSubmissionEndpointExtensions.BasePath}/acme/directives",
            InvalidPriorityRequestFrom(scenario.Directive));
        var problem = await response.Content.ReadFromJsonAsync<JsonElement>();
        var timeline = new PostgreSqlJourneyAuditReadModel(dataSource).ReadTimeline(
            scenario.Directive.OrganizationId,
            scenario.Directive.Thread,
            scenario.Directive.DirectiveId);
        var state = await fixture.GetPositionStateAsync();
        var persistedEvents = await fixture.ReadPersistedEventsAsync();

        Assert.Equal(HttpStatusCode.BadRequest, response.StatusCode);
        Assert.Equal("Invalid directive submission", problem.GetProperty("title").GetString());
        Assert.Equal("priority", problem.GetProperty("path").GetString());
        Assert.Equal(0, dispatcher.DispatchCount);
        Assert.Empty(timeline.Entries);
        Assert.Empty(persistedEvents);
        Assert.DoesNotContain(scenario.Directive.Id, state.ProcessedMessages);
        Assert.DoesNotContain(scenario.ResultMemoryKey, state.ShortMemory.Keys);
        Assert.Contains(initialTask.TaskId, state.OpenTasks.Keys);
    }

    private static WebApplication BuildSubmissionApp(
        AiDirectiveIntegrationFixture fixture,
        IOrganizationRelations relations,
        IJourneyAuditLog auditLog,
        IPositionCommandDispatcher? dispatcher = null)
    {
        var builder = WebApplication.CreateBuilder();
        builder.WebHost.UseTestServer();
        builder.Services.AddSingleton(relations);
        builder.Services.AddSingleton<IPositionCommandDispatcher>(
            dispatcher ?? new FixturePositionCommandDispatcher(fixture));
        builder.Services.AddSingleton(auditLog);
        builder.Services.AddHiveDirectiveSubmissionApi();

        var app = builder.Build();
        app.MapHiveDirectiveSubmissionApi();
        return app;
    }

    private static object RequestFrom(Hive.Domain.Messaging.Directive directive)
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

    private static object InvalidPriorityRequestFrom(Hive.Domain.Messaging.Directive directive)
    {
        var from = Assert.IsType<PositionEndpointRef>(directive.From);
        var to = Assert.IsType<PositionEndpointRef>(directive.To);

        return new
        {
            messageId = directive.Id.ToString(),
            from = new { kind = "position", positionId = from.PositionId.Value },
            to = new { kind = "position", positionId = to.PositionId.Value },
            threadId = directive.Thread.ToString(),
            priority = "urgent",
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

    private static async Task<IReadOnlyList<PositionEvent>> WaitForPersistedEventsAsync(
        AiDirectiveIntegrationFixture fixture,
        Func<IReadOnlyList<PositionEvent>, bool> isReady)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var events = await fixture.ReadPersistedEventsAsync();
            if (isReady(events))
            {
                return events;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Bug triage position events did not reach the expected state.");
    }

    private static async Task<PositionState> WaitForPositionStateAsync(
        AiDirectiveIntegrationFixture fixture,
        Func<PositionState, bool> isReady)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await fixture.GetPositionStateAsync();
            if (isReady(state))
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Bug triage position state did not reach the expected state.");
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

    private static void AssertTimelineCoversEscalationPath(
        JourneyAuditTimeline timeline,
        AiDirectiveIntegrationScenario scenario,
        string modelId)
    {
        Assert.Equal(scenario.Directive.OrganizationId, timeline.OrganizationId);
        Assert.Equal(scenario.Directive.Thread, timeline.ThreadId);
        Assert.Equal(scenario.Directive.DirectiveId, timeline.DirectiveId);
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
        {
            Assert.Equal(scenario.Directive.Id, entry.MessageId);
            Assert.Equal(scenario.Directive.DirectiveId, entry.DirectiveId);
            Assert.Equal(PositionId.From("triage-agent"), entry.PositionId);
            Assert.NotEqual(JourneyAuditOutcome.Rejected, entry.Outcome);
        });
        Assert.Contains(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.GatewayCalled &&
            entry.Provider?.ProviderId == "stub" &&
            entry.Provider.ModelId == modelId &&
            entry.Latency is not null);
        Assert.Contains(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.AgentDecided &&
            entry.RedactedPayload["decisionKind"] == nameof(Escalation));
        Assert.Contains(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.ResultMessageCreated &&
            entry.MessageType == nameof(Escalation) &&
            entry.RedactedPayload["resultMessageType"] == nameof(Escalation));

        var resultAudit = Assert.Single(timeline.Entries.Where(entry =>
            entry.Stage == JourneyAuditStage.ResultMessageCreated));
        Assert.Contains(
            "resultMessage.escalation.issue:free-text",
            resultAudit.RedactedPayload["redactions"],
            StringComparison.Ordinal);
        Assert.Contains(
            "resultMessage.escalation.context:free-text",
            resultAudit.RedactedPayload["redactions"],
            StringComparison.Ordinal);
        Assert.Contains(
            "resultMessage.escalation.optionsConsidered:free-text",
            resultAudit.RedactedPayload["redactions"],
            StringComparison.Ordinal);
    }

    private static void AssertTimelineCoversProviderFailure(
        JourneyAuditTimeline timeline,
        AiDirectiveIntegrationScenario scenario,
        string modelId)
    {
        Assert.Equal(scenario.Directive.OrganizationId, timeline.OrganizationId);
        Assert.Equal(scenario.Directive.Thread, timeline.ThreadId);
        Assert.Equal(scenario.Directive.DirectiveId, timeline.DirectiveId);
        Assert.Equal(
            [
                JourneyAuditStage.SubmissionReceived,
                JourneyAuditStage.DirectiveCreated,
                JourneyAuditStage.PositionAccepted,
                JourneyAuditStage.PositionDispatched,
                JourneyAuditStage.GatewayCalled,
                JourneyAuditStage.GatewayCostRecorded,
                JourneyAuditStage.AgentDecided,
            ],
            timeline.Entries.Select(entry => entry.Stage));

        Assert.All(timeline.Entries, entry =>
        {
            Assert.Equal(scenario.Directive.Id, entry.MessageId);
            Assert.Equal(scenario.Directive.DirectiveId, entry.DirectiveId);
            Assert.Equal(PositionId.From("triage-agent"), entry.PositionId);
        });

        var gatewayCalled = Assert.Single(timeline.Entries.Where(entry =>
            entry.Stage == JourneyAuditStage.GatewayCalled));
        Assert.Equal(JourneyAuditOutcome.Failed, gatewayCalled.Outcome);
        Assert.Equal("provider-unavailable", gatewayCalled.ReasonCode);
        Assert.Equal("stub", gatewayCalled.Provider?.ProviderId);
        Assert.Equal(modelId, gatewayCalled.Provider?.ModelId);
        Assert.NotNull(gatewayCalled.Latency);
        Assert.DoesNotContain(
            "AI gateway stub returned a controlled provider failure",
            string.Join(" ", gatewayCalled.RedactedPayload.Values),
            StringComparison.Ordinal);

        var gatewayCost = Assert.Single(timeline.Entries.Where(entry =>
            entry.Stage == JourneyAuditStage.GatewayCostRecorded));
        Assert.Equal(JourneyAuditOutcome.Failed, gatewayCost.Outcome);
        Assert.Equal("provider-unavailable", gatewayCost.ReasonCode);
        Assert.Equal("Failed", gatewayCost.RedactedPayload["result"]);
        Assert.Equal("True", gatewayCost.RedactedPayload["isRetryable"]);

        var agentDecided = Assert.Single(timeline.Entries.Where(entry =>
            entry.Stage == JourneyAuditStage.AgentDecided));
        Assert.Equal(JourneyAuditOutcome.Failed, agentDecided.Outcome);
        Assert.Equal("ai-gateway-failure", agentDecided.ReasonCode);
        Assert.Equal("Failed", agentDecided.RedactedPayload["status"]);
        Assert.Equal("ai-gateway-failure", agentDecided.RedactedPayload["terminalCode"]);
        Assert.Equal("none", agentDecided.RedactedPayload["decisionKind"]);
        Assert.Equal("0", agentDecided.RedactedPayload["parseErrorCount"]);
        Assert.Contains(
            "gateway.error.message:free-text",
            agentDecided.RedactedPayload["redactions"],
            StringComparison.Ordinal);

        Assert.DoesNotContain(timeline.Entries, entry =>
            entry.Stage == JourneyAuditStage.ResultMessageCreated);
    }

    private static void AssertJourneyStageCountsAfterRedelivery(JourneyAuditTimeline timeline)
    {
        Assert.Equal(9, timeline.Entries.Count);
        Assert.Equal(1, Count(JourneyAuditStage.SubmissionReceived));
        Assert.Equal(1, Count(JourneyAuditStage.DirectiveCreated));
        Assert.Equal(1, Count(JourneyAuditStage.PositionAccepted));
        Assert.Equal(1, Count(JourneyAuditStage.PositionDispatched));
        Assert.Equal(1, Count(JourneyAuditStage.GatewayCalled));
        Assert.Equal(1, Count(JourneyAuditStage.GatewayCostRecorded));
        Assert.Equal(1, Count(JourneyAuditStage.AgentDecided));
        Assert.Equal(1, Count(JourneyAuditStage.ResultMessageCreated));
        Assert.Equal(1, Count(JourneyAuditStage.DuplicateSuppressed));

        int Count(JourneyAuditStage stage) =>
            timeline.Entries.Count(entry => entry.Stage == stage);
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
        public int DispatchCount { get; private set; }

        public ValueTask DispatchAsync(
            PositionEnvelope envelope,
            CancellationToken cancellationToken)
        {
            DispatchCount++;
            return fixture.DispatchAsync(envelope, cancellationToken);
        }
    }
}
