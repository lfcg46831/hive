using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Outcomes;
using Hive.Domain.Positions;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.DependencyInjection;
using Microsoft.Extensions.Hosting;

namespace Hive.Tests;

public sealed class AiGatewayOutcomeVerifierTests
{
    [Fact]
    public async Task Adapter_sends_only_bounded_context_with_no_tools_or_conversation_history()
    {
        var gateway = new CapturingGateway(request => Success(
            request,
            "{\"schema_version\":1,\"classification\":\"Escalation\"}"));
        var verifier = new AiGatewayOutcomeVerifier(gateway);

        var result = await verifier.VerifyAsync(Request());

        Assert.Equal(OutcomeVerifierResultStatus.Classified, result.Status);
        Assert.Equal(OutcomeVerifierClassification.Escalation, result.Classification);
        var sent = Assert.IsType<AiGatewayRequest>(gateway.LastRequest);
        Assert.Empty(sent.Tools);
        Assert.Empty(sent.ContextMessages);
        Assert.Equal(TimeSpan.FromSeconds(7), sent.Timeout);
        Assert.Null(sent.ModelParameters.Temperature);
        Assert.Equal(2048, sent.ModelParameters.MaxOutputTokens);
        Assert.Equal(OutcomeVerifierConstraint.SchemaName, sent.OutputConstraint!.SchemaName);
        Assert.Equal(
            "33333333-3333-3333-3333-333333333333",
            sent.Metadata["directive_id"]);
        Assert.Equal("1", sent.Metadata["iteration"]);
        Assert.Equal("outcome-verification", sent.Metadata["hive.operation"]);
        Assert.Contains("schema_version 1", sent.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains("Report.Done", sent.SystemInstruction, StringComparison.Ordinal);
        Assert.Contains(
            "Report.Progress requires verifiable_progress=true",
            sent.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "pending_actions=true, autonomous_action_available=true",
            sent.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "proposal.semantic_completion_candidate",
            sent.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "proposed_artifact actually completes",
            sent.SystemInstruction,
            StringComparison.Ordinal);
        Assert.Contains(
            "\"semantic_completion_candidate\":false",
            sent.Content,
            StringComparison.Ordinal);
        Assert.Contains("\"proposed_artifact\":", sent.Content, StringComparison.Ordinal);
        Assert.Contains("\"kind\":\"Report.Progress\"", sent.Content, StringComparison.Ordinal);
        Assert.Contains("Bounded progress report.", sent.Content, StringComparison.Ordinal);
        Assert.Contains("directive.objective", sent.Content, StringComparison.Ordinal);
        Assert.Contains("Assess the work item.", sent.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("The work is complete.", sent.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("Continue the work.", sent.Content, StringComparison.Ordinal);
        Assert.DoesNotContain("triage", sent.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("memory", sent.Content, StringComparison.OrdinalIgnoreCase);
        Assert.DoesNotContain("tool_calls", sent.Content, StringComparison.OrdinalIgnoreCase);
    }

    [Fact]
    public void Bootstrap_registers_the_limited_verifier_and_orchestrator_without_external_calls()
    {
        var builder = Host.CreateApplicationBuilder();
        builder.AddHiveBootstrap();
        using var services = builder.Services.BuildServiceProvider();

        Assert.IsType<AiGatewayOutcomeVerifier>(services.GetRequiredService<IOutcomeVerifier>());
        Assert.IsType<OrganizationalOutcomeOrchestrator>(
            services.GetRequiredService<IOrganizationalOutcomeOrchestrator>());
    }

    [Fact]
    public async Task Adapter_marks_the_closed_semantic_completion_candidate_in_payload()
    {
        var gateway = new CapturingGateway(request => Success(
            request,
            "{\"schema_version\":1,\"classification\":\"Report.Done\"}"));
        var verifier = new AiGatewayOutcomeVerifier(gateway);

        var result = await verifier.VerifyAsync(SemanticDoneRequest());

        Assert.Equal(OutcomeVerifierClassification.ReportDone, result.Classification);
        var sent = Assert.IsType<AiGatewayRequest>(gateway.LastRequest);
        Assert.Contains(
            "\"semantic_completion_candidate\":true",
            sent.Content,
            StringComparison.Ordinal);
    }

    [Theory]
    [InlineData(AiGatewayErrorCode.Timeout, OutcomeVerifierResultStatus.TimedOut)]
    [InlineData(AiGatewayErrorCode.ProviderUnavailable, OutcomeVerifierResultStatus.Unavailable)]
    public async Task Gateway_failures_are_mapped_to_closed_verifier_status(
        AiGatewayErrorCode errorCode,
        OutcomeVerifierResultStatus expectedStatus)
    {
        var gateway = new CapturingGateway(request => AiGatewayResponse.Failed(
            new AiGatewayError(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                errorCode,
                "Controlled verifier failure.",
                isRetryable: true)));
        var verifier = new AiGatewayOutcomeVerifier(gateway);

        var result = await verifier.VerifyAsync(Request());

        Assert.Equal(expectedStatus, result.Status);
        Assert.Null(result.Classification);
    }

    [Fact]
    public async Task Missing_position_gateway_configuration_fails_before_the_provider_call()
    {
        var gateway = new CapturingGateway(request => Success(
            request,
            "{\"schema_version\":1,\"classification\":\"Escalation\"}"));
        var verifier = new AiGatewayOutcomeVerifier(
            gateway,
            new MissingPositionConfigurationProvider());

        var result = await verifier.VerifyAsync(Request());

        Assert.Equal(OutcomeVerifierResultStatus.Unavailable, result.Status);
        Assert.Equal(0, gateway.CallCount);
    }

    [Fact]
    public async Task Missing_bounded_artifact_fails_before_the_provider_call()
    {
        var gateway = new CapturingGateway(request => Success(
            request,
            "{\"schema_version\":1,\"classification\":\"Escalation\"}"));
        var verifier = new AiGatewayOutcomeVerifier(gateway);
        var request = Request(includeArtifact: false);

        var result = await verifier.VerifyAsync(request);

        Assert.Equal(OutcomeVerifierResultStatus.Unavailable, result.Status);
        Assert.Equal(0, gateway.CallCount);
    }

    [Theory]
    [InlineData("not-json")]
    [InlineData("{\"schema_version\":1,\"classification\":\"Report\"}")]
    [InlineData("{\"schema_version\":1,\"classification\":\"Escalation\",\"reasoning\":\"not allowed\"}")]
    public async Task Invalid_provider_output_is_reduced_to_one_closed_status(string output)
    {
        var verifier = new AiGatewayOutcomeVerifier(new CapturingGateway(
            request => Success(request, output)));

        var result = await verifier.VerifyAsync(Request());

        Assert.Equal(OutcomeVerifierResultStatus.InvalidOutput, result.Status);
        Assert.Null(result.Classification);
    }

    [Fact]
    public async Task Unexpected_tool_call_or_correlation_mismatch_is_invalid_output()
    {
        var toolCallingVerifier = new AiGatewayOutcomeVerifier(new CapturingGateway(request =>
            AiGatewayResponse.Succeeded(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                text: null,
                AiFinishReason.ToolCalls,
                toolCalls: [new AiToolCall("call-1", "unexpected.tool")])));
        var mismatchingVerifier = new AiGatewayOutcomeVerifier(new CapturingGateway(request =>
            AiGatewayResponse.Succeeded(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                MessageId.From(Guid.Parse("99999999-9999-9999-9999-999999999999")),
                "{\"schema_version\":1,\"classification\":\"Escalation\"}",
                AiFinishReason.Stop)));

        var toolResult = await toolCallingVerifier.VerifyAsync(Request());
        var mismatchResult = await mismatchingVerifier.VerifyAsync(Request());

        Assert.Equal(OutcomeVerifierResultStatus.InvalidOutput, toolResult.Status);
        Assert.Equal(OutcomeVerifierResultStatus.InvalidOutput, mismatchResult.Status);
    }

    private static OutcomeVerificationRequest Request(bool includeArtifact = true) =>
        new(
            new OutcomeVerificationContext(
                OrganizationId.From("org-verifier"),
                PositionId.From("delivery-lead"),
                ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                TimeSpan.FromSeconds(7),
                [new OutcomeVerificationContextEntry(
                    "directive.objective",
                    "Assess the work item.")]),
            new ExecutionFacts(
                iterationCount: 1,
                retryCount: 0,
                deadlineExceeded: false,
                budgetExhausted: false,
                humanApprovalRequired: false,
                approvalPending: false,
                OutcomeDependencyState.Available,
                OutcomeAuthorityState.Authorized,
                OutcomeRoutingState.Available,
                autonomousActionAvailable: false,
                delegationRequired: false,
                pendingActions: false,
                externalInterventionRequired: false,
                verifiableProgress: false,
                responsibilityRetained: true,
                OutcomeCompletionState.NotDeclared),
            new DirectiveExecutionContract(
                completionCriteria: [new("criterion.complete", "The work is complete.")]),
            new OutcomeProposal(
                OutcomeProposedIntent.ContinueWork,
                OutcomeWorkState.InProgress,
                OutcomeRequiredIntervention.None,
                blockers: [],
                "Continue the work.",
                evidenceReferences: []),
            new OutcomePolicySnapshot(
                "outcome-policy-v1",
                "sha256:verifier-adapter",
                maximumIterations: 8,
                maximumRetries: 3,
                verifierEnabled: true),
            includeArtifact
                ? new OutcomeVerificationArtifact(
                    OutcomeKind.ReportProgress,
                    [new("report.body", "Bounded progress report.")])
                : null);

    private static OutcomeVerificationRequest SemanticDoneRequest() =>
        new(
            new OutcomeVerificationContext(
                OrganizationId.From("org-verifier"),
                PositionId.From("delivery-lead"),
                ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111")),
                MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222")),
                DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                TimeSpan.FromSeconds(7),
                [new OutcomeVerificationContextEntry(
                    "directive.objective",
                    "Assess the work item.")]),
            new ExecutionFacts(
                iterationCount: 1,
                retryCount: 0,
                deadlineExceeded: false,
                budgetExhausted: false,
                humanApprovalRequired: false,
                approvalPending: false,
                OutcomeDependencyState.Available,
                OutcomeAuthorityState.Authorized,
                OutcomeRoutingState.Available,
                autonomousActionAvailable: false,
                delegationRequired: false,
                pendingActions: false,
                externalInterventionRequired: false,
                verifiableProgress: false,
                responsibilityRetained: true,
                OutcomeCompletionState.NotDeclared),
            new DirectiveExecutionContract(),
            new OutcomeProposal(
                OutcomeProposedIntent.ReportDone,
                OutcomeWorkState.Completed,
                OutcomeRequiredIntervention.None,
                blockers: [],
                nextAction: null,
                [new OutcomeEvidenceReference(
                    OutcomeEvidenceSource.DirectiveInput,
                    "directive.objective")]),
            new OutcomePolicySnapshot(
                "outcome-policy-v1",
                "sha256:semantic-done",
                maximumIterations: 8,
                maximumRetries: 3,
                verifierEnabled: true),
            new OutcomeVerificationArtifact(
                OutcomeKind.ReportDone,
                [new("report.body", "The requested assessment is complete.")]));

    private static AiGatewayResponse Success(AiGatewayRequest request, string output) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            output,
            AiFinishReason.Stop,
            outputConstraintMode: AiOutputConstraintMode.JsonSchema);

    private sealed class CapturingGateway(
        Func<AiGatewayRequest, AiGatewayResponse> responseFactory) : IAiGateway
    {
        public int CallCount { get; private set; }

        public AiGatewayRequest? LastRequest { get; private set; }

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            CallCount++;
            LastRequest = request;
            return Task.FromResult(responseFactory(request));
        }
    }

    private sealed class MissingPositionConfigurationProvider : IPositionConfigurationProvider
    {
        public Task<PositionRuntimeConfigurationLoadResult> LoadAsync(
            PositionEntityId entityId,
            CancellationToken cancellationToken) =>
            Task.FromResult(PositionRuntimeConfigurationLoadResult.Missing(
                "Position configuration is unavailable."));
    }
}
