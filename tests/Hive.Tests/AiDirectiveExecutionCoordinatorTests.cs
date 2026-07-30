using Hive.Actors.Positions;
using Hive.Application.Directives;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;

namespace Hive.Tests;

public sealed class AiDirectiveExecutionCoordinatorTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 29, 10, 0, 0, TimeSpan.Zero);

    [Fact]
    public async Task Coordinator_owns_primary_budget_and_returns_ordered_dispatch_effects()
    {
        var request = ProcessingRequest();
        var coordinator = new AiDirectiveExecutionCoordinator(
            new StaticResponseInvoker(ValidReportOutput()),
            AiDirectiveResultMessageEmissionGate.Instance,
            AllowingAiAgentActionGate.Instance,
            PassthroughAiDirectiveOutcomeResolutionIntegrator.Instance,
            () => At);

        var execution = await coordinator.ExecuteDetailedAsync(request);

        Assert.Equal(DirectiveExecutionStatus.Completed, execution.Result.Status);
        Assert.Equal(
            [ExecutionBudgetOperation.PrimaryInference],
            execution.Budget.ConsumedOperations);
        Assert.Equal(1, execution.Budget.ConsumedIterations);
        Assert.Collection(
            execution.Result.Effects,
            effect => Assert.IsType<DirectiveAuditExportResultEffect>(effect),
            effect => Assert.IsType<DirectivePositionCommandEffect>(effect),
            effect => Assert.IsType<DirectiveJourneyAuditEffect>(effect),
            effect => Assert.IsType<DirectiveJourneyAuditEffect>(effect));
        Assert.Equal(
            request.CorrelationId,
            execution.Result.CorrelationId);
        Assert.True(execution.Processing.IsTerminal);

        var publicResult = await ((IDirectiveExecutionCoordinator)coordinator)
            .ExecuteAsync(request.ExecutionRequest);
        Assert.Equal(DirectiveExecutionStatus.Completed, publicResult.Status);
        Assert.Equal(request.CorrelationId, publicResult.CorrelationId);
    }

    private static AiDirectiveProcessingRequest ProcessingRequest()
    {
        var organization = OrganizationId.From("acme");
        var position = PositionId.From("bug-triage");
        var entity = PositionEntityId.From(organization, position);
        var superior = PositionId.From("delivery-lead");
        var directive = new Directive(
            MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000815")),
            organization,
            new PositionEndpointRef(superior),
            new PositionEndpointRef(position),
            ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000815")),
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: At.AddMinutes(5),
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000815")),
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(15, "sha256:f0-15-t03"),
            organization,
            position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: superior,
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(30),
                    maxIterations: 3),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You triage incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration());

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Empty,
            OccupantId.From("agent-15"),
            directive);
    }

    private static string ValidReportOutput() =>
        """
        {
          "schema_version": 1,
          "intent": "Report",
          "report": {
            "kind": "Done",
            "body": "Bug triage is complete."
          }
        }
        """;

    private sealed class StaticResponseInvoker(string output) : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Succeeded(
                    invocation.Request.OrganizationId,
                    invocation.Request.PositionId,
                    invocation.Request.ThreadId,
                    invocation.Request.MessageId,
                    output,
                    AiFinishReason.Stop)));
    }
}
