using Akka.Actor;
using Hive.Actors.Positions;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Messaging;
using Hive.Domain.Organization.Configuration;
using Hive.Domain.Positions;
using OrgDirective = Hive.Domain.Messaging.Directive;

namespace Hive.Tests;

public sealed class AiDirectiveInterpretationTests
{
    private static readonly DateTimeOffset At =
        new(2026, 7, 3, 10, 0, 0, TimeSpan.Zero);
    private static readonly OrganizationId Organization = OrganizationId.From("acme");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("bbbbbbbb-0000-0000-0000-000000000907"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("aaaaaaaa-0000-0000-0000-000000000907"));

    [Fact]
    public void Interpret_maps_invalid_provider_output_to_escalation_with_parse_errors()
    {
        var invocation = AiAgentGatewayInvocationResult.FromResponse(
            "directive:invalid-output",
            AiGatewayResponse.Succeeded(
                Organization,
                Position,
                Thread,
                Message,
                "{",
                AiFinishReason.Stop));

        var result = AiDirectiveDecisionInterpreter.Interpret(invocation);

        Assert.Equal("directive:invalid-output", result.CorrelationId);
        Assert.Equal(AiDirectiveInterpretationOutcomeKind.EscalationRequired, result.Outcome);
        Assert.True(result.RequiresEscalation);
        Assert.True(result.IsFailure);
        Assert.Null(result.Decision);
        var failure = Assert.IsType<AiDirectiveInterpretationFailure>(result.Failure);
        Assert.Equal("ai-output-invalid", failure.Code);
        Assert.False(failure.IsRetryable);
        Assert.Contains("ai-output-invalid", failure.AuditReason, StringComparison.Ordinal);
        var parseError = Assert.Single(failure.ParseErrors);
        Assert.Equal("invalid-json", parseError.Code);
        Assert.Equal("$", parseError.Path);
        Assert.False(typeof(OrgMessage).IsAssignableFrom(result.GetType()));
    }

    [Fact]
    public async Task AiAgentActor_stores_accepted_interpretation_and_advances_to_result_emitted()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-interpretation-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            actor.Tell(request);

            var interpretation = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult(request.CorrelationId),
                Timeout());
            var snapshot = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                Timeout());

            Assert.True(interpretation.Found);
            Assert.Equal(request.CorrelationId, interpretation.CorrelationId);
            Assert.Equal(AiDirectiveInterpretationOutcomeKind.DecisionAccepted, interpretation.Result!.Outcome);
            var decision = Assert.IsType<AiDirectiveReportDecision>(interpretation.Result.Decision);
            Assert.Equal(ReportKind.Done, decision.Kind);
            Assert.Equal("Bug triage is complete.", decision.Body);
            Assert.Null(interpretation.Result.Failure);
            Assert.True(snapshot.Found);
            Assert.Equal(AiDirectiveProcessingStatus.ResultEmitted, snapshot.Snapshot!.Status);
            Assert.Equal(
                [
                    AiDirectiveProcessingStatus.Received,
                    AiDirectiveProcessingStatus.ContextAssembled,
                    AiDirectiveProcessingStatus.GatewayRequested,
                    AiDirectiveProcessingStatus.ResponseInterpreted,
                    AiDirectiveProcessingStatus.ResultEmitted,
                ],
                snapshot.Snapshot.History.Select(transition => transition.Status).ToArray());
            Assert.False(typeof(OrgMessage).IsAssignableFrom(interpretation.Result.GetType()));
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public void Interpret_maps_gateway_failure_to_structured_error_with_canonical_code()
    {
        var gatewayError = new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.ProviderRejected,
            "Provider rejected the request.",
            isRetryable: false,
            new AiProviderMetadata("stub", "triage"));
        var invocation = AiAgentGatewayInvocationResult.FromResponse(
            "directive:gateway-failure",
            AiGatewayResponse.Failed(gatewayError));

        var result = AiDirectiveDecisionInterpreter.Interpret(invocation);

        Assert.Equal(AiDirectiveInterpretationOutcomeKind.StructuredError, result.Outcome);
        Assert.True(result.IsStructuredError);
        Assert.True(result.IsFailure);
        Assert.Null(result.Decision);
        var failure = Assert.IsType<AiDirectiveInterpretationFailure>(result.Failure);
        Assert.Equal("ai-gateway-failure", failure.Code);
        Assert.False(failure.IsRetryable);
        Assert.Same(gatewayError, failure.GatewayError);
        Assert.Empty(failure.ParseErrors);
        Assert.Contains("provider-rejected", failure.AuditReason, StringComparison.Ordinal);
    }

    [Fact]
    public async Task AiAgentActor_stores_invalid_output_escalation_and_advances_to_escalated()
    {
        var request = Request();
        var system = ActorSystem.Create($"ai-agent-interpretation-invalid-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new StaticResponseInvoker("{"))),
                "agent");

            actor.Tell(request);

            var interpretation = await WaitForInterpretationAsync(actor, request.CorrelationId);
            var snapshot = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                Timeout());

            Assert.Equal(AiDirectiveInterpretationOutcomeKind.EscalationRequired, interpretation.Result!.Outcome);
            var failure = Assert.IsType<AiDirectiveInterpretationFailure>(interpretation.Result.Failure);
            Assert.Equal("ai-output-invalid", failure.Code);
            Assert.Contains(failure.ParseErrors, error => error.Code == "invalid-json" && error.Path == "$");
            Assert.True(snapshot.Found);
            Assert.Equal(AiDirectiveProcessingStatus.Escalated, snapshot.Snapshot!.Status);
            Assert.Contains("ai-output-invalid", snapshot.Snapshot.TerminalReason, StringComparison.Ordinal);
        }
        finally
        {
            await system.Terminate();
        }
    }

    [Fact]
    public async Task AiAgentActor_stores_gateway_error_and_advances_to_failed()
    {
        var request = Request();
        var gatewayError = new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.ProviderUnavailable,
            "Provider unavailable.",
            isRetryable: true,
            new AiProviderMetadata("stub", "triage"));
        var system = ActorSystem.Create($"ai-agent-interpretation-gateway-failed-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    request.Occupant,
                    new FailureResponseInvoker(gatewayError))),
                "agent");

            actor.Tell(request);

            var interpretation = await WaitForInterpretationAsync(actor, request.CorrelationId);
            var snapshot = await actor.Ask<AiDirectiveProcessingSnapshotQueryResult>(
                new GetAiDirectiveProcessingSnapshot(request.CorrelationId),
                Timeout());

            Assert.Equal(AiDirectiveInterpretationOutcomeKind.StructuredError, interpretation.Result!.Outcome);
            var failure = Assert.IsType<AiDirectiveInterpretationFailure>(interpretation.Result.Failure);
            Assert.Equal("ai-gateway-failure", failure.Code);
            Assert.True(failure.IsRetryable);
            Assert.Same(gatewayError, failure.GatewayError);
            Assert.Empty(failure.ParseErrors);
            Assert.True(snapshot.Found);
            Assert.Equal(AiDirectiveProcessingStatus.Failed, snapshot.Snapshot!.Status);
            Assert.Contains("provider-unavailable", snapshot.Snapshot.TerminalReason, StringComparison.Ordinal);
        }
        finally
        {
            await system.Terminate();
        }
    }


    [Fact]
    public async Task AiAgentActor_returns_missing_interpretation_for_unknown_correlation()
    {
        var system = ActorSystem.Create($"ai-agent-interpretation-missing-{Guid.NewGuid():N}");

        try
        {
            var actor = system.ActorOf(
                Props.Create(() => new AiAgentActor(
                    OccupantId.From("agent-7"),
                    new StaticResponseInvoker(ValidReportOutput()))),
                "agent");

            var result = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult("directive:unknown"),
                Timeout());

            Assert.False(result.Found);
            Assert.Equal("directive:unknown", result.CorrelationId);
            Assert.Null(result.Result);
        }
        finally
        {
            await system.Terminate();
        }
    }

    private static AiDirectiveProcessingRequest Request()
    {
        var entity = PositionEntityId.From(Organization, Position);
        var occupant = OccupantId.From("agent-7");
        var directive = new OrgDirective(
            Message,
            Organization,
            new PositionEndpointRef(PositionId.From("delivery-lead")),
            new PositionEndpointRef(Position),
            Thread,
            Priority.High,
            schemaVersion: 1,
            sentAt: At,
            deadline: null,
            DirectiveId.From(Guid.Parse("cccccccc-0000-0000-0000-000000000907")),
            parentDirectiveId: null,
            objective: "Triage checkout regression",
            context: "Customer reports checkout failures.");
        var configuration = new PositionRuntimeConfiguration(
            new PositionConfigurationStamp(12, "sha256:t07c"),
            Organization,
            Position,
            new PositionRuntimeDescriptor(
                UnitId.From("engineering"),
                reportsTo: PositionId.From("delivery-lead"),
                name: "Bug triage",
                timezone: "Europe/Lisbon"),
            new OccupantRuntimeConfiguration(
                OccupantType.AiAgent,
                identityPromptRef: "triage-v1",
                aiGateway: new AiPositionRuntimeConfiguration(
                    new AiProviderMetadata("stub", "triage"),
                    new AiModelParameters(maxOutputTokens: 256),
                    timeout: TimeSpan.FromSeconds(15)),
                identityPrompt: new IdentityPromptRuntimeConfiguration(
                    "triage-v1",
                    "prompts/triage-v1.md",
                    "You are responsible for triaging incoming bugs.")),
            new PositionAuthorityRuntimeConfiguration());

        return AiDirectiveProcessingRequest.Create(
            entity,
            configuration,
            PositionState.Restore(new PositionSnapshot(At)),
            occupant,
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

    private static TimeSpan Timeout() => TimeSpan.FromSeconds(10);

    private static async Task<AiDirectiveInterpretationQueryResult> WaitForInterpretationAsync(
        IActorRef actor,
        string correlationId)
    {
        var deadline = DateTimeOffset.UtcNow.Add(Timeout());
        while (DateTimeOffset.UtcNow < deadline)
        {
            var result = await actor.Ask<AiDirectiveInterpretationQueryResult>(
                new GetAiDirectiveInterpretationResult(correlationId),
                TimeSpan.FromSeconds(1));
            if (result.Found)
            {
                return result;
            }

            await Task.Delay(25);
        }

        throw new TimeoutException("AI directive interpretation result was not recorded.");
    }

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

    private sealed class FailureResponseInvoker(AiGatewayError error) : IAiAgentGatewayInvoker
    {
        public Task<AiAgentGatewayInvocationResult> InvokeAsync(
            AiAgentGatewayInvocation invocation,
            CancellationToken cancellationToken = default) =>
            Task.FromResult(AiAgentGatewayInvocationResult.FromResponse(
                invocation.CorrelationId,
                AiGatewayResponse.Failed(error)));
    }
}
