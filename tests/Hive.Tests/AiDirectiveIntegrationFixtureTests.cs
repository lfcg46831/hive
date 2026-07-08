using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class AiDirectiveIntegrationFixtureTests
{
    [Fact]
    public async Task Fixture_dispatches_directive_through_position_actor_ai_agent_and_configured_gateway_stub()
    {
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(
            AiDirectiveIntegrationScenario.Create(configureStub: options =>
            {
                options.ModelId = "fixture-scenario";
                options.Scenario = "bug-triage-report";
            }));

        var result = await fixture.ProcessDirectiveAsync();
        var snapshot = await result.Agent.Ask<AiDirectiveProcessingSnapshotQueryResult>(
            new GetAiDirectiveProcessingSnapshot(fixture.CorrelationId),
            Timeout());
        var resultMessage = await result.Agent.Ask<AiDirectiveResultMessageQueryResult>(
            new GetAiDirectiveResultMessage(fixture.CorrelationId),
            Timeout());

        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, result.Audit.Status);
        Assert.Equal("result-emitted", result.Audit.TerminalCode);
        Assert.Equal("fixture-scenario", result.GatewayInvocation.Response.Provider!.ModelId);
        Assert.True(snapshot.Found);
        Assert.Equal(fixture.CorrelationId, snapshot.Snapshot!.CorrelationId);
        Assert.Equal(result.Directive.Thread, snapshot.Snapshot.ThreadId);
        Assert.Equal(result.Directive.DirectiveId, snapshot.Snapshot.DirectiveId);
        Assert.Equal(result.Directive.Id, snapshot.Snapshot.MessageId);
        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshot.Snapshot.Status);
        Assert.True(resultMessage.Found);
        Assert.True(resultMessage.Result!.IsSuccess);
        Assert.Contains(result.Directive.Id, result.PositionState.ProcessedMessages);
        Assert.Equal(
            "Report Done: Bug triage complete: checkout confirmation failures are reproducible with high user impact.",
            result.PositionState.ShortMemory[
                $"directive:{result.Directive.DirectiveId.Value:N}:result"]);
    }

    [Fact]
    public async Task Report_path_persists_completion_and_records_audit()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14b-report";
            options.Text = ValidReportOutput();
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var result = await fixture.ProcessDirectiveAsync();
        var events = await WaitForPersistedEventsAsync(
            fixture,
            persisted => persisted.OfType<TaskCompleted>()
                .Any(completed => completed.TaskId == initialTask.TaskId));
        var state = await WaitForPositionStateAsync(
            result.Position,
            snapshot => !snapshot.OpenTasks.ContainsKey(initialTask.TaskId));

        AssertSuccessfulAudit(
            result,
            decisionKind: "Report",
            messageType: "Report",
            [nameof(UpdateShortMemory), nameof(CompleteTask)]);
        Assert.Equal(
            "Report Done: Integration report complete.",
            state.ShortMemory[scenario.ResultMemoryKey]);
        Assert.DoesNotContain(initialTask.TaskId, state.OpenTasks.Keys);
        Assert.Contains(events.OfType<ShortMemoryUpdated>(), updated =>
            updated.Key == scenario.ResultMemoryKey &&
            updated.Value == "Report Done: Integration report complete.");
        Assert.Contains(events.OfType<TaskCompleted>(), completed =>
            completed.TaskId == initialTask.TaskId &&
            completed.Summary == "Integration report complete.");
        Assert.Contains(
            result.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.report.body");
    }

    [Fact]
    public async Task Escalation_path_persists_blocking_task_update_and_records_audit()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14b-escalation";
            options.Text = ValidEscalationOutput();
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var result = await fixture.ProcessDirectiveAsync();
        var events = await WaitForPersistedEventsAsync(
            fixture,
            persisted => persisted.OfType<TaskUpdated>()
                .Any(updated => updated.TaskId == initialTask.TaskId));
        var state = await WaitForPositionStateAsync(
            result.Position,
            snapshot => snapshot.OpenTasks.TryGetValue(initialTask.TaskId, out var task) &&
                task.Priority == Priority.Critical);

        AssertSuccessfulAudit(
            result,
            decisionKind: "Escalation",
            messageType: "Escalation",
            [nameof(UpdateShortMemory), nameof(UpdateTask)]);
        Assert.Equal(
            "Escalation: Missing payment gateway logs. Cannot complete triage without callback logs.",
            state.ShortMemory[scenario.ResultMemoryKey]);
        Assert.Equal(Priority.Critical, state.OpenTasks[initialTask.TaskId].Priority);
        Assert.Contains(events.OfType<TaskUpdated>(), updated =>
            updated.TaskId == initialTask.TaskId &&
            updated.Note == "Escalation: Missing payment gateway logs. Cannot complete triage without callback logs." &&
            updated.Priority == Priority.Critical);
        Assert.Contains(
            result.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.escalation.issue");
        Assert.Contains(
            result.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.escalation.context");
        Assert.Contains(
            result.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.escalation.optionsConsidered");
    }

    [Fact]
    public async Task Child_directive_path_persists_followup_task_and_records_audit()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14b-child-directive";
            options.Text = ValidChildDirectiveOutput();
        });
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var result = await fixture.ProcessDirectiveAsync();
        var events = await WaitForPersistedEventsAsync(
            fixture,
            persisted => persisted.OfType<TaskCreated>()
                .Any(created => created.Title == "Follow delegated directive to engineer"));
        var state = await WaitForPositionStateAsync(
            result.Position,
            snapshot => snapshot.OpenTasks.Values.Any(task =>
                task.Title == "Follow delegated directive to engineer"));

        AssertSuccessfulAudit(
            result,
            decisionKind: "Directive",
            messageType: "Directive",
            [nameof(UpdateShortMemory), nameof(OpenTask)]);
        Assert.Equal(
            "Delegated directive to engineer: Investigate checkout callback failures.",
            state.ShortMemory[scenario.ResultMemoryKey]);
        var createdTask = Assert.Single(events.OfType<TaskCreated>().Where(created =>
            created.Title == "Follow delegated directive to engineer"));
        Assert.Equal(result.Directive.Thread, createdTask.Thread);
        Assert.Equal(result.Directive.Priority, createdTask.Priority);
        Assert.Equal(result.Directive.Deadline, createdTask.Deadline);
        Assert.Equal(result.Directive.Id, createdTask.CausedBy);
        Assert.Contains(
            result.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.directive.objective");
        Assert.Contains(
            result.Audit.Redactions,
            redaction => redaction.Path == "resultMessage.directive.context");
    }

    private static void AssertSuccessfulAudit(
        AiDirectiveIntegrationRun result,
        string decisionKind,
        string messageType,
        IReadOnlyList<string> commandTypes)
    {
        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, result.Audit.Status);
        Assert.Equal("result-emitted", result.Audit.TerminalCode);
        Assert.True(result.Audit.Gateway.WasRequested);
        Assert.Equal(AiGatewayCallResult.Succeeded, result.Audit.Gateway.Result);
        Assert.Equal(AiDirectiveInterpretationOutcomeKind.DecisionAccepted, result.Audit.Decision!.Outcome);
        Assert.Equal(decisionKind, result.Audit.Decision.DecisionKind);
        Assert.Equal(messageType, result.Audit.ResultMessage!.MessageType);
        Assert.Equal(commandTypes, result.Audit.PositionEffects!.CommandTypes);
        Assert.True(result.Audit.IterationAudit!.IsTerminal);
        Assert.Equal("completed", result.Audit.IterationAudit.TerminalCode);
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

        throw new TimeoutException("PositionActor persisted events did not reach the expected state.");
    }

    private static async Task<PositionState> WaitForPositionStateAsync(
        IActorRef position,
        Func<PositionState, bool> isReady)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var state = await position.Ask<PositionState>(GetPositionState.Instance, Timeout());
            if (isReady(state))
            {
                return state;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("PositionActor state did not reach the expected state.");
    }

    private static string ValidReportOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Done",
            "body": "Integration report complete."
          }
        }
        """;

    private static string ValidEscalationOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Escalation",
          "escalation": {
            "issue": "Missing payment gateway logs",
            "context": "Cannot complete triage without callback logs.",
            "options_considered": [
              "Retry with current evidence",
              "Ask engineering for callback logs"
            ]
          }
        }
        """;

    private static string ValidChildDirectiveOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Directive",
          "directive": {
            "target_position_id": "engineer",
            "objective": "Investigate checkout callback failures.",
            "context": "Focus on payment callback logs."
          }
        }
        """;

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);
}
