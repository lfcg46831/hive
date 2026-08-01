using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Auditing;
using Hive.Domain.Directives;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class AiDirectiveIntegrationFixtureTests
{
    [Fact]
    public async Task Empty_journal_bootstraps_configured_occupant_and_reaches_terminal_result()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t07c-empty-journal";
            options.Scenario = "bug-triage-report";
        });
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(
            scenario,
            seedInitialSnapshot: false);

        var result = await fixture.ProcessDirectiveAsync();
        var events = await fixture.ReadPersistedEventsAsync();
        var occupantIndex = Array.FindIndex(
            events.ToArray(),
            @event => @event is OccupantChanged);
        var dispatchIndex = Array.FindIndex(
            events.ToArray(),
            @event => @event is MessageDispatched);

        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, result.Audit.Status);
        Assert.True(result.Audit.Gateway.WasRequested);
        Assert.Equal(scenario.Occupant, Assert.Single(events.OfType<OccupantChanged>()).Occupant);
        Assert.InRange(occupantIndex, 0, dispatchIndex - 1);
        Assert.Contains(scenario.Directive.Id, result.PositionState.ProcessedMessages);
    }

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
    [Trait(
        DirectiveExecutionCharacterization.CategoryTrait,
        DirectiveExecutionCharacterization.Category)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.PositionEffects)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.Audit)]
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
    [Trait(
        DirectiveExecutionCharacterization.CategoryTrait,
        DirectiveExecutionCharacterization.Category)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.Audit)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.Projections)]
    [Trait(
        DirectiveExecutionCharacterization.ResponsibilityTrait,
        DirectiveExecutionCharacterization.PositionEffects)]
    public async Task Checkpointable_progress_persists_before_result_and_projects_minimized_audit()
    {
        var audit = new RecordingJourneyAuditLog();
        var scenario = AiDirectiveIntegrationScenario.Create(
            configureStub: options =>
            {
                options.ModelId = "f0-19-t05-progress";
                options.Text = CheckpointProgressOutput();
            },
            directiveExecutionPolicyRequest: new DirectiveExecutionPolicyRequest(
                DirectiveExecutionPolicyContractVersions.V1,
                DirectiveExecutionMode.Checkpointable),
            directiveExecutionPolicyCapability: new DirectiveExecutionPolicyCapability(
                DirectiveExecutionPolicyContractVersions.V1,
                DirectiveExecutionMode.Checkpointable,
                TimeSpan.FromSeconds(5)));
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(
            scenario,
            audit);

        var result = await fixture.ProcessDirectiveAsync();
        var events = await WaitForPersistedEventsAsync(
            fixture,
            persisted => persisted.OfType<DirectiveCheckpointPersisted>().Any());
        var checkpoint = Assert.Single(events.OfType<DirectiveCheckpointPersisted>()).Checkpoint;
        var auditRecord = await WaitForCheckpointAuditAsync(audit);
        var resultMessage = await result.Agent.Ask<AiDirectiveResultMessageQueryResult>(
            new GetAiDirectiveResultMessage(fixture.CorrelationId),
            Timeout());
        var auditRecords = audit.Records.ToArray();
        var checkpointAuditIndex = Array.FindIndex(
            auditRecords,
            record => record.Stage == JourneyAuditStage.DirectiveCheckpointTransition);
        var resultAuditIndex = Array.FindIndex(
            auditRecords,
            record => record.Stage == JourneyAuditStage.ResultMessageCreated);

        Assert.Equal(1, checkpoint.Revision);
        Assert.Equal(["inspect"], checkpoint.CompletedSubtasks.Select(item => item.LocalId));
        Assert.Equal("verify", checkpoint.NextSubtaskId);
        Assert.Equal(
            ReportKind.Progress,
            Assert.IsType<Report>(resultMessage.Result!.Message).Kind);
        Assert.InRange(checkpointAuditIndex, 0, resultAuditIndex - 1);
        Assert.Equal("checkpoint-created", auditRecord.ReasonCode);
        Assert.Equal("inspect", auditRecord.Payload["completedSubtaskIds"]);
        Assert.Equal("verify", auditRecord.Payload["nextSubtaskId"]);
        Assert.DoesNotContain(
            "Inspect private customer evidence",
            string.Join("|", auditRecord.Payload.Values),
            StringComparison.Ordinal);
        Assert.DoesNotContain(
            "directive.context",
            string.Join("|", auditRecord.Payload.Values),
            StringComparison.Ordinal);
    }

    [Fact]
    public async Task External_decision_blocked_stub_scenario_materializes_escalation_without_blocked_report()
    {
        var scenario = AiDirectiveIntegrationScenario.Create(configureStub: options =>
        {
            options.ModelId = "t08-external-decision-blocked";
            options.Scenario = "bug-triage-external-decision-blocked";
        });
        var initialTask = Assert.Single(scenario.OpenTasks);
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var result = await fixture.ProcessDirectiveAsync();
        var resultMessage = await result.Agent.Ask<AiDirectiveResultMessageQueryResult>(
            new GetAiDirectiveResultMessage(fixture.CorrelationId),
            Timeout());
        var parsed = AiDirectiveDecisionParser.Parse(result.GatewayInvocation.Response.Text);
        var events = await WaitForPersistedEventsAsync(
            fixture,
            persisted => persisted.OfType<TaskUpdated>()
                .Any(updated => updated.TaskId == initialTask.TaskId));
        var state = await WaitForPositionStateAsync(
            result.Position,
            snapshot => snapshot.OpenTasks.TryGetValue(initialTask.TaskId, out var task) &&
                task.Priority == Priority.Critical);

        Assert.True(parsed.IsSuccess);
        Assert.IsType<AiDirectiveEscalationDecision>(parsed.Decision);
        Assert.DoesNotContain("\"kind\"", result.GatewayInvocation.Response.Text, StringComparison.Ordinal);
        Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, result.Audit.Status);
        Assert.Equal("result-emitted", result.Audit.TerminalCode);
        Assert.Equal(AiDirectiveInterpretationOutcomeKind.DecisionAccepted, result.Audit.Decision!.Outcome);
        Assert.Equal("Escalation", result.Audit.Decision.DecisionKind);
        Assert.Equal("Escalation", result.Audit.ResultMessage!.MessageType);

        Assert.True(resultMessage.Found);
        var escalation = Assert.IsType<Escalation>(resultMessage.Result!.Message);
        Assert.Equal(new PositionEndpointRef(PositionId.From("triage-agent")), escalation.From);
        Assert.Equal(new PositionEndpointRef(PositionId.From("delivery-lead")), escalation.To);
        Assert.Equal(result.Directive.Thread, escalation.Thread);
        Assert.Equal(result.Directive.Priority, escalation.Priority);
        Assert.Equal("External decision required", escalation.Issue);

        Assert.Equal(Priority.Critical, state.OpenTasks[initialTask.TaskId].Priority);
        Assert.Contains(events.OfType<TaskUpdated>(), updated =>
            updated.TaskId == initialTask.TaskId &&
            updated.Priority == Priority.Critical &&
            updated.Note.StartsWith("Escalation: External decision required.", StringComparison.Ordinal));
        Assert.Empty(events.OfType<TaskCompleted>());
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

    private static string CheckpointProgressOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Progress",
            "body": "Grounded progress is checkpointed.",
            "checkpoint": {
              "contract_version": 1,
              "plan": {
                "contract_version": 1,
                "subtasks": [
                  {
                    "sequence": 1,
                    "local_id": "inspect",
                    "objective": "Inspect private customer evidence",
                    "completion_criteria": ["Failure context is identified"],
                    "estimated_duration_ms": 20000
                  },
                  {
                    "sequence": 2,
                    "local_id": "verify",
                    "objective": "Verify the grounded finding",
                    "completion_criteria": ["Finding references directive input"],
                    "estimated_duration_ms": 20000
                  }
                ]
              },
              "completed_subtasks": [
                {
                  "local_id": "inspect",
                  "evidence_references": [
                    {
                      "source": "DirectiveInput",
                      "reference": "directive.context"
                    }
                  ]
                }
              ],
              "blockers": [],
              "next_subtask_id": "verify"
            }
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

    private static async Task<JourneyAuditRecord> WaitForCheckpointAuditAsync(
        RecordingJourneyAuditLog audit)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var records = audit.Records;
            var record = records.LastOrDefault(candidate =>
                candidate.Stage == JourneyAuditStage.DirectiveCheckpointTransition);
            if (record is not null && records.Any(candidate =>
                    candidate.Stage == JourneyAuditStage.ResultMessageCreated))
            {
                return record;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("Checkpoint audit transition was not recorded.");
    }

    private sealed class RecordingJourneyAuditLog : IJourneyAuditLog
    {
        private readonly object _sync = new();
        private readonly List<JourneyAuditRecord> _records = [];

        public IReadOnlyList<JourneyAuditRecord> Records
        {
            get
            {
                lock (_sync)
                {
                    return _records.ToArray();
                }
            }
        }

        public void Append(JourneyAuditRecord record)
        {
            lock (_sync)
            {
                _records.Add(record);
            }
        }

        public IReadOnlyList<JourneyAuditRecord> ReadByThread(
            ThreadId threadId,
            DirectiveId? directiveId = null)
        {
            lock (_sync)
            {
                return _records
                    .Where(record =>
                        record.ThreadId == threadId &&
                        (directiveId is null || record.DirectiveId == directiveId))
                    .ToArray();
            }
        }
    }
}
