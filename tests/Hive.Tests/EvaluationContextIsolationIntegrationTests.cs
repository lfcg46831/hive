using System.Text;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class EvaluationContextIsolationIntegrationTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 13, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Context_stays_isolated_across_run_ids_case_order_retries_and_existing_state()
    {
        var foreignThread = ThreadId.From(
            Guid.Parse("bbbbbbbb-0000-0000-0000-000000009901"));
        var scenario = AiDirectiveIntegrationScenario.Create(
            entity: PositionEntityId.From(
                OrganizationId.From("acme-delivery"),
                PositionId.From("bug-triage")),
            openTasks:
            [
                new PersistedTask(
                    PositionTaskId.From(
                        Guid.Parse("dddddddd-0000-0000-0000-000000009901")),
                    foreignThread,
                    "foreign-run-task-must-not-leak",
                    Priority.Critical,
                    At),
            ],
            shortMemory: new Dictionary<string, string>(StringComparer.Ordinal)
            {
                ["foreign-thread-result"] = "foreign-run-memory-must-not-leak",
                ["legacy-result"] = "legacy-memory-must-not-leak",
                ["evaluation-fact"] = "stable-position-fact",
            },
            recentHistory:
            [
                MessageId.From(Guid.Parse("eeeeeeee-0000-0000-0000-000000009901")),
            ],
            shortMemoryContextScopes: new Dictionary<string, ShortMemoryContextScope>(
                StringComparer.Ordinal)
            {
                ["foreign-thread-result"] = ShortMemoryContextScope.ForThread(foreignThread),
                ["evaluation-fact"] = ShortMemoryContextScope.ForPositionFact(),
            });
        await using var fixture = await AiDirectiveIntegrationFixture.StartAsync(scenario);

        var firstCase = EvaluationDirective(
            run: 1,
            caseNumber: 1,
            "First isolated evaluation context.");
        var secondCase = EvaluationDirective(
            run: 1,
            caseNumber: 2,
            "Second isolated evaluation context.");
        var firstRunCaseOne = await fixture.ProcessDirectiveAsync(firstCase);
        var firstRunCaseTwo = await fixture.ProcessDirectiveAsync(secondCase);
        var stateAfterFirstRun = await fixture.GetPositionStateAsync();

        var repeatedCaseOne = await fixture.ProcessDirectiveAsync(firstCase);
        var repeatedCaseTwo = await fixture.ProcessDirectiveAsync(secondCase);
        var stateAfterRepeat = await fixture.GetPositionStateAsync();

        var nextRunCaseOne = await fixture.ProcessDirectiveAsync(EvaluationDirective(
            run: 2,
            caseNumber: 1,
            "First isolated evaluation context."));
        var nextRunCaseTwo = await fixture.ProcessDirectiveAsync(EvaluationDirective(
            run: 2,
            caseNumber: 2,
            "Second isolated evaluation context."));

        var reversedRunCaseTwo = await fixture.ProcessDirectiveAsync(EvaluationDirective(
            run: 3,
            caseNumber: 2,
            "Second isolated evaluation context."));
        var reversedRunCaseOne = await fixture.ProcessDirectiveAsync(EvaluationDirective(
            run: 3,
            caseNumber: 1,
            "First isolated evaluation context."));

        Assert.Equal(firstRunCaseOne.GatewayRequest, repeatedCaseOne.GatewayRequest);
        Assert.Equal(firstRunCaseTwo.GatewayRequest, repeatedCaseTwo.GatewayRequest);
        Assert.Equal(
            stateAfterFirstRun.ProcessedMessages.OrderBy(item => item.Value),
            stateAfterRepeat.ProcessedMessages.OrderBy(item => item.Value));
        Assert.Equal(stateAfterFirstRun.ShortMemory.Count, stateAfterRepeat.ShortMemory.Count);

        Assert.NotEqual(firstRunCaseOne.Directive.Id, nextRunCaseOne.Directive.Id);
        Assert.NotEqual(firstRunCaseOne.Directive.Thread, nextRunCaseOne.Directive.Thread);
        Assert.NotEqual(
            firstRunCaseOne.Directive.DirectiveId,
            nextRunCaseOne.Directive.DirectiveId);
        Assert.Equal(PromptSize(firstRunCaseOne), PromptSize(nextRunCaseOne));
        Assert.Equal(PromptSize(firstRunCaseOne), PromptSize(reversedRunCaseOne));
        Assert.Equal(PromptSize(firstRunCaseTwo), PromptSize(nextRunCaseTwo));
        Assert.Equal(PromptSize(firstRunCaseTwo), PromptSize(reversedRunCaseTwo));

        var runs = new[]
        {
            firstRunCaseOne,
            firstRunCaseTwo,
            nextRunCaseOne,
            nextRunCaseTwo,
            reversedRunCaseOne,
            reversedRunCaseTwo,
        };
        foreach (var run in runs)
        {
            Assert.Contains(run.Directive.Context, run.GatewayRequest.Content, StringComparison.Ordinal);
            Assert.Contains("stable-position-fact", run.GatewayRequest.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("foreign-run-task-must-not-leak", run.GatewayRequest.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("foreign-run-memory-must-not-leak", run.GatewayRequest.Content, StringComparison.Ordinal);
            Assert.DoesNotContain("legacy-memory-must-not-leak", run.GatewayRequest.Content, StringComparison.Ordinal);

            foreach (var other in runs.Where(other =>
                         other.Directive.DirectiveId != run.Directive.DirectiveId))
            {
                Assert.DoesNotContain(
                    AiDirectiveIntegrationScenario.ResultMemoryKeyFor(other.Directive),
                    run.GatewayRequest.Content,
                    StringComparison.Ordinal);
            }
        }
    }

    private static int PromptSize(AiDirectiveIntegrationRun run) =>
        Encoding.UTF8.GetByteCount(string.Concat(
            run.GatewayRequest.SystemInstruction,
            "\n",
            run.GatewayRequest.Content));

    private static OrgDirective EvaluationDirective(
        int run,
        int caseNumber,
        string context) => new(
            EvaluationId('a', run, caseNumber, MessageId.From),
            OrganizationId.From("acme-delivery"),
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(PositionId.From("bug-triage")),
            EvaluationId('b', run, caseNumber, ThreadId.From),
            Priority.High,
            schemaVersion: 1,
            sentAt: At.AddSeconds(caseNumber - 1),
            deadline: null,
            EvaluationId('c', run, caseNumber, DirectiveId.From),
            parentDirectiveId: null,
            objective: "Triage the submitted production issue.",
            context);

    private static T EvaluationId<T>(
        char prefix,
        int run,
        int caseNumber,
        Func<Guid, T> factory) =>
        factory(Guid.Parse(
            $"{new string(prefix, 8)}-0000-0000-0000-{run:0000}{caseNumber:00000000}"));
}
