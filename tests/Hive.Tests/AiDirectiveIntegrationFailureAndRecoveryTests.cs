using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Tests;

/// <summary>
/// Verifies US-F0-08-T14c: through the full
/// <c>Directive → PositionActor → AiAgentActor → AiGateway stub</c> flow, invalid provider output and
/// rejected routing escalate fail-closed without emitting an organizational message or mutating
/// recoverable position state, and a restart recovers the accepted result without duplicating work —
/// all correlated by <c>ThreadId</c>/<c>DirectiveId</c>.
/// </summary>
public sealed class AiDirectiveIntegrationFailureAndRecoveryTests
{
    [Fact]
    public async Task Invalid_output_escalates_without_result_message_or_position_effects()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14c-invalid-output";
            options.Text = "{ this is not valid json";
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var result = await fixture.ProcessDirectiveAsync();

        Assert.Equal(AiDirectiveProcessingStatus.Escalated, result.Audit.Status);
        Assert.Equal("ai-output-invalid", result.Audit.TerminalCode);
        Assert.True(result.Audit.Gateway.WasRequested);
        Assert.Equal(AiGatewayCallResult.Succeeded, result.Audit.Gateway.Result);
        Assert.Equal(
            AiDirectiveInterpretationOutcomeKind.EscalationRequired,
            result.Audit.Decision!.Outcome);
        Assert.Equal("ai-output-invalid", result.Audit.Decision.FailureCode);
        Assert.Null(result.Audit.ResultMessage);
        Assert.Null(result.Audit.PositionEffects);

        // Correlation is preserved end-to-end by ThreadId/DirectiveId.
        AssertCorrelation(result.Audit, fixture.Directive, fixture.CorrelationId);

        // Fail-closed: the message is accounted for but no recoverable state was mutated.
        Assert.Contains(fixture.Directive.Id, result.PositionState.ProcessedMessages);
        Assert.DoesNotContain(ResultMemoryKey(fixture.Directive), result.PositionState.ShortMemory.Keys);
        Assert.Contains(initialTask.TaskId, result.PositionState.OpenTasks.Keys);

        var events = await fixture.ReadPersistedEventsAsync();
        Assert.Empty(events.OfType<ShortMemoryUpdated>());
        Assert.Empty(events.OfType<TaskCompleted>());
        Assert.Empty(events.OfType<TaskCreated>());
    }

    [Fact]
    public async Task Rejected_routing_escalates_without_emitting_message_or_effects()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14c-rejected-routing";
            // Target is not a permitted direct subordinate (only "engineer" is), so routing/authority
            // is rejected fail-closed before any organizational message is emitted.
            options.Text = ChildDirectiveOutput("auditor");
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var result = await fixture.ProcessDirectiveAsync();

        Assert.Equal(AiDirectiveProcessingStatus.Escalated, result.Audit.Status);
        Assert.Equal("child-directive-target-not-permitted", result.Audit.TerminalCode);
        Assert.Equal(AiGatewayCallResult.Succeeded, result.Audit.Gateway.Result);

        // The provider output parsed to a valid child-directive decision; the rejection happens at the
        // routing/authority boundary, not at interpretation.
        Assert.Equal(
            AiDirectiveInterpretationOutcomeKind.DecisionAccepted,
            result.Audit.Decision!.Outcome);
        Assert.Equal("Directive", result.Audit.Decision.DecisionKind);

        // No organizational message was materialized and no position commands were produced.
        Assert.Null(result.Audit.ResultMessage!.MessageType);
        Assert.Equal("child-directive-target-not-permitted", result.Audit.ResultMessage.FailureCode);
        Assert.Empty(result.Audit.PositionEffects!.CommandTypes);
        Assert.Equal("child-directive-target-not-permitted", result.Audit.PositionEffects.FailureCode);

        AssertCorrelation(result.Audit, fixture.Directive, fixture.CorrelationId);

        Assert.Contains(fixture.Directive.Id, result.PositionState.ProcessedMessages);
        Assert.DoesNotContain(ResultMemoryKey(fixture.Directive), result.PositionState.ShortMemory.Keys);
        Assert.Contains(initialTask.TaskId, result.PositionState.OpenTasks.Keys);

        var events = await fixture.ReadPersistedEventsAsync();
        Assert.Empty(events.OfType<TaskCreated>());
        Assert.Empty(events.OfType<ShortMemoryUpdated>());
    }

    [Fact]
    public async Task Restart_recovers_accepted_result_without_duplicating_work()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t14c-restart";
            options.Text = ReportOutput();
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
        var resultKey = scenario.ResultMemoryKey;
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var result = await fixture.ProcessDirectiveAsync();
        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, result.Audit.Status);
        var eventsBeforeRestart = await WaitForPersistedEventsAsync(
            fixture,
            persisted => persisted.OfType<TaskCompleted>()
                .Any(completed => completed.TaskId == initialTask.TaskId));

        await fixture.RestartPositionAsync();

        // Recoverable state survives the restart, still keyed by DirectiveId/ThreadId.
        var recovered = await fixture.GetPositionStateAsync();
        Assert.Contains(fixture.Directive.Id, recovered.ProcessedMessages);
        Assert.Equal(
            "Report Done: Integration report complete.",
            recovered.ShortMemory[resultKey]);
        Assert.DoesNotContain(initialTask.TaskId, recovered.OpenTasks.Keys);

        // Re-delivering the same directive after restart is suppressed as already processed.
        fixture.RedeliverDirective();
        var afterRedelivery = await fixture.GetPositionStateAsync();
        Assert.Contains(fixture.Directive.Id, afterRedelivery.ProcessedMessages);

        var eventsAfterRedelivery = await fixture.ReadPersistedEventsAsync();
        Assert.Equal(
            eventsBeforeRestart.OfType<MessageReceived>().Count(),
            eventsAfterRedelivery.OfType<MessageReceived>().Count());
        Assert.Single(
            eventsAfterRedelivery.OfType<MessageReceived>()
                .Where(received => received.Message.Id == fixture.Directive.Id));
        Assert.Single(
            eventsAfterRedelivery.OfType<ShortMemoryUpdated>()
                .Where(updated => updated.Key == resultKey));
        Assert.Single(
            eventsAfterRedelivery.OfType<TaskCompleted>()
                .Where(completed => completed.TaskId == initialTask.TaskId));
    }

    private static void AssertCorrelation(
        AiDirectiveAuditSnapshot audit,
        Directive directive,
        string correlationId)
    {
        Assert.Equal(correlationId, audit.CorrelationId);
        Assert.Equal(directive.Thread, audit.Context.ThreadId);
        Assert.Equal(directive.DirectiveId, audit.Context.DirectiveId);
        Assert.Equal(directive.Id, audit.Context.MessageId);
    }

    private static string ResultMemoryKey(Directive directive) =>
        $"directive:{directive.DirectiveId.Value:N}:result";

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

    private static string ReportOutput() =>
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

    private static string ChildDirectiveOutput(string targetPositionId) =>
        $$"""
        {
          "schema_version": 1,
          "intent": "Directive",
          "directive": {
            "target_position_id": "{{targetPositionId}}",
            "objective": "Investigate checkout callback failures.",
            "context": "Focus on payment callback logs."
          }
        }
        """;

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);
}
