using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Domain.Outcomes;
using Hive.Infrastructure.Ai;
using Microsoft.Extensions.AI;
using Microsoft.Extensions.Configuration;
using Microsoft.Extensions.DependencyInjection;

namespace Hive.Tests;

public sealed class AiGatewayIntegrationTests
{
    private const string RealTestApiKeyEnvironmentVariable =
        "HIVE_AI_GATEWAY_REAL_TEST_API_KEY";
    private const string RealTestModelIdEnvironmentVariable =
        "HIVE_AI_GATEWAY_REAL_TEST_MODEL_ID";
    private const string RealTestEndpointEnvironmentVariable =
        "HIVE_AI_GATEWAY_REAL_TEST_ENDPOINT";

    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task Configured_stub_gateway_completes_success_through_di()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Hive:AiGateway:Provider"] = "stub",
            ["Hive:AiGateway:Stub:ProviderId"] = "stub",
            ["Hive:AiGateway:Stub:ModelId"] = "integration-deterministic",
            ["Hive:AiGateway:Stub:Text"] = "Integration stub response.",
            ["Hive:AiGateway:Stub:Usage:InputTokens"] = "13",
            ["Hive:AiGateway:Stub:Usage:OutputTokens"] = "8",
            ["Hive:AiGateway:Stub:Usage:TotalTokens"] = "21",
            ["Hive:AiGateway:Stub:Usage:IsEstimated"] = "true",
            ["Hive:AiGateway:Stub:Cost:Amount"] = "0.04",
            ["Hive:AiGateway:Stub:Cost:Currency"] = "EUR",
            ["Hive:AiGateway:Stub:Cost:IsEstimated"] = "true",
        });

        var gateway = provider.GetRequiredService<IAiGateway>();
        Assert.NotNull(provider.GetRequiredService<IAiGatewayAuditPublisher>());
        Assert.NotNull(provider.GetRequiredService<IAiGatewayDetailedAuditPublisher>());
        Assert.Null(provider.GetService<IChatClient>());

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal("Integration stub response.", response.Text);
        Assert.Equal(AiFinishReason.Stop, response.FinishReason);
        Assert.NotNull(response.Provider);
        Assert.Equal("stub", response.Provider.ProviderId);
        Assert.Equal("integration-deterministic", response.Provider.ModelId);
        Assert.NotNull(response.Usage);
        Assert.Equal(13, response.Usage.InputTokens);
        Assert.Equal(8, response.Usage.OutputTokens);
        Assert.Equal(21, response.Usage.TotalTokens);
        Assert.True(response.Usage.IsEstimated);
        Assert.NotNull(response.Cost);
        Assert.Equal(0.04m, response.Cost.Amount);
        Assert.Equal("EUR", response.Cost.Currency);
        Assert.True(response.Cost.IsEstimated);
    }

    [Fact]
    public async Task Configured_stub_gateway_returns_tool_call_through_di()
    {
        using var provider = BuildProvider(new Dictionary<string, string?>
        {
            ["Hive:AiGateway:Provider"] = "stub",
            ["Hive:AiGateway:Stub:Outcome"] = "tool-call",
            ["Hive:AiGateway:Stub:Text"] = "",
            ["Hive:AiGateway:Stub:ToolCall:Id"] = "call-integration-1",
            ["Hive:AiGateway:Stub:ToolCall:Name"] = "ticket.lookup",
            ["Hive:AiGateway:Stub:ToolCall:Arguments:ticket"] = "HIVE-789",
        });

        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request());

        Assert.True(response.IsSuccess);
        Assert.Equal(AiFinishReason.ToolCalls, response.FinishReason);
        Assert.Null(response.Text);
        var toolCall = Assert.Single(response.ToolCalls);
        Assert.Equal("call-integration-1", toolCall.Id);
        Assert.Equal("ticket.lookup", toolCall.Name);
        Assert.Equal("HIVE-789", toolCall.Arguments["ticket"]);
        Assert.Null(provider.GetService<IChatClient>());
    }

    [Fact]
    public void Optional_real_smoke_settings_are_inert_without_complete_local_configuration()
    {
        var missingAll = OptionalRealSmokeSettings.From(_ => null);
        var missingModel = OptionalRealSmokeSettings.From(name =>
            name == RealTestApiKeyEnvironmentVariable ? "local-secret" : null);
        var missingKey = OptionalRealSmokeSettings.From(name =>
            name == RealTestModelIdEnvironmentVariable ? "local-model" : null);

        Assert.False(missingAll.IsEnabled);
        Assert.Empty(missingAll.ToConfiguration());
        Assert.False(missingModel.IsEnabled);
        Assert.Empty(missingModel.ToConfiguration());
        Assert.False(missingKey.IsEnabled);
        Assert.Empty(missingKey.ToConfiguration());
    }

    [Theory]
    [InlineData(AiGatewayErrorCode.ConfigurationInvalid, null, "configuration-invalid")]
    [InlineData(AiGatewayErrorCode.ProviderUnavailable, null, "provider-unavailable")]
    [InlineData(AiGatewayErrorCode.ProviderRejected, 400, "provider-rejected")]
    public void Real_smoke_gate_fails_closed_with_sanitized_diagnostics(
        AiGatewayErrorCode errorCode,
        int? providerStatusCode,
        string wireCode)
    {
        var settings = new OptionalRealSmokeSettings(
            "local-secret",
            "local-model",
            Endpoint: null);
        var response = AiGatewayResponse.Failed(new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            errorCode,
            "secret=local-secret; prompt=private; output=private; reasoning=private",
            isRetryable: false,
            new AiProviderMetadata("openai", "local-model"),
            providerStatusCode is { } statusCode
                ? new AiGatewayFailureDiagnostics(providerStatusCode: statusCode)
                : null));

        var exception = Assert.Throws<Xunit.Sdk.XunitException>(
            () => RequireSuccessfulRealProviderCall(response, settings));

        var expected = providerStatusCode is { } expectedStatusCode
            ? $"code={wireCode}; status={expectedStatusCode}; provider=openai; model=local-model"
            : $"code={wireCode}; provider=openai; model=local-model";
        Assert.Equal(expected, exception.Message);
    }

    [Fact]
    public void Real_verifier_smoke_gate_does_not_expose_invalid_provider_output()
    {
        var settings = new OptionalRealSmokeSettings(
            "local-secret",
            "local-model",
            Endpoint: null);
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "secret=local-secret; prompt=private; output=private; reasoning=private",
            AiFinishReason.Stop,
            provider: new AiProviderMetadata("openai", "local-model"));

        var exception = Assert.Throws<Xunit.Sdk.XunitException>(
            () => RequireClassifiedVerifierResult(
                OutcomeVerifierResult.Unavailable(),
                response,
                settings));

        Assert.Equal(
            "code=invalid-provider-response; provider=openai; model=local-model",
            exception.Message);
    }

    [Fact]
    public async Task Optional_real_provider_smoke_test_runs_only_with_local_secret_and_model()
    {
        var settings = OptionalRealSmokeSettings.FromEnvironment();
        if (!settings.IsEnabled)
        {
            Assert.Empty(settings.ToConfiguration());
            return;
        }

        using var provider = BuildProvider(settings.ToConfiguration());
        var gateway = provider.GetRequiredService<IAiGateway>();

        var response = await gateway.CompleteAsync(Request(
            provider: new AiProviderMetadata("openai", settings.ModelId!),
            timeout: TimeSpan.FromSeconds(30)));

        RequireSuccessfulRealProviderCall(response, settings);
    }

    [Fact]
    public async Task Optional_real_outcome_verifier_smoke_confirms_semantic_done()
    {
        var settings = OptionalRealSmokeSettings.FromEnvironment();
        if (!settings.IsEnabled)
        {
            Assert.Empty(settings.ToConfiguration());
            return;
        }

        using var provider = BuildProvider(settings.ToConfiguration());
        var gateway = new CapturingResponseGateway(provider.GetRequiredService<IAiGateway>());
        var verifier = new AiGatewayOutcomeVerifier(gateway);

        var result = await verifier.VerifyAsync(OutcomeVerifierRequest());

        var gatewayResponse = Assert.IsType<AiGatewayResponse>(gateway.Response);
        RequireClassifiedVerifierResult(result, gatewayResponse, settings);
        Assert.Equal(OutcomeVerifierClassification.ReportDone, result.Classification);
    }

    private static ServiceProvider BuildProvider(
        IReadOnlyDictionary<string, string?> values)
    {
        var configuration = new ConfigurationBuilder()
            .AddInMemoryCollection(values)
            .Build();
        var services = new ServiceCollection();
        services.AddHiveAiGateway(configuration);

        return services.BuildServiceProvider(new ServiceProviderOptions
        {
            ValidateOnBuild = true,
            ValidateScopes = true,
        });
    }

    private static AiGatewayRequest Request(
        AiProviderMetadata? provider = null,
        TimeSpan? timeout = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this integration smoke request in one short sentence.",
            provider: provider,
            timeout: timeout);

    private static OutcomeVerificationRequest OutcomeVerifierRequest() =>
        new(
            new OutcomeVerificationContext(
                Organization,
                Position,
                Thread,
                Message,
                DirectiveId.From(Guid.Parse("33333333-3333-3333-3333-333333333333")),
                TimeSpan.FromSeconds(45),
                [new OutcomeVerificationContextEntry(
                    "directive.objective",
                    "Assess whether a reproducible non-blocking defect should be reported."),
                new OutcomeVerificationContextEntry(
                    "directive.context",
                    "The defect is reproducible, visible, and does not block the user workflow.")]),
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
                evidenceReferences:
                [
                    new OutcomeEvidenceReference(
                        OutcomeEvidenceSource.DirectiveInput,
                        "directive.objective"),
                    new OutcomeEvidenceReference(
                        OutcomeEvidenceSource.DirectiveInput,
                        "directive.context"),
                ]),
            new OutcomePolicySnapshot(
                "outcome-policy-v1",
                "sha256:real-verifier-smoke",
                maximumIterations: 4,
                maximumRetries: 3,
                verifierEnabled: true),
            new OutcomeVerificationArtifact(
                OutcomeKind.ReportDone,
                [new(
                    "report.body",
                    "The reproducible non-blocking defect was assessed and should be reported.")]));

    private static void RequireClassifiedVerifierResult(
        OutcomeVerifierResult result,
        AiGatewayResponse response,
        OptionalRealSmokeSettings settings)
    {
        RequireSuccessfulRealProviderCall(response, settings);
        if (result.Status != OutcomeVerifierResultStatus.Classified)
        {
            throw Failure(
                AiGatewayErrorCode.InvalidProviderResponse,
                providerStatusCode: null,
                response.Provider,
                settings);
        }
    }

    private static void RequireSuccessfulRealProviderCall(
        AiGatewayResponse response,
        OptionalRealSmokeSettings settings)
    {
        if (response.IsFailure)
        {
            var error = response.Error!;
            throw Failure(
                error.Code,
                error.Diagnostics?.ProviderStatusCode,
                error.Provider,
                settings);
        }

        var provider = response.Provider;
        if (provider is null ||
            !string.Equals(provider.ProviderId, "openai", StringComparison.Ordinal) ||
            !string.Equals(provider.ModelId, settings.ModelId, StringComparison.Ordinal) ||
            ContainsCredential(response.Text, settings.ApiKey))
        {
            throw Failure(
                AiGatewayErrorCode.InvalidProviderResponse,
                providerStatusCode: null,
                provider,
                settings);
        }
    }

    private static Xunit.Sdk.XunitException Failure(
        AiGatewayErrorCode code,
        int? providerStatusCode,
        AiProviderMetadata? provider,
        OptionalRealSmokeSettings settings)
    {
        var wireCode = AiGatewayErrorCodeContract.ToWireValue(code);
        var providerId = provider?.ProviderId ?? "openai";
        var modelId = provider?.ModelId ?? settings.ModelId ?? "unconfigured";
        var diagnostic = providerStatusCode is >= 100 and <= 599
            ? $"code={wireCode}; status={providerStatusCode}; " +
              $"provider={providerId}; model={modelId}"
            : $"code={wireCode}; provider={providerId}; model={modelId}";
        return new Xunit.Sdk.XunitException(diagnostic);
    }

    private static bool ContainsCredential(string? text, string? credential) =>
        !string.IsNullOrEmpty(text) &&
        !string.IsNullOrEmpty(credential) &&
        text.Contains(credential, StringComparison.Ordinal);

    private sealed class CapturingResponseGateway(IAiGateway inner) : IAiGateway
    {
        public AiGatewayResponse? Response { get; private set; }

        public async Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            Response = await inner.CompleteAsync(request, cancellationToken);
            return Response;
        }
    }

    private sealed record OptionalRealSmokeSettings(
        string? ApiKey,
        string? ModelId,
        string? Endpoint)
    {
        public bool IsEnabled =>
            !string.IsNullOrWhiteSpace(ApiKey) &&
            !string.IsNullOrWhiteSpace(ModelId);

        public static OptionalRealSmokeSettings FromEnvironment() =>
            From(Environment.GetEnvironmentVariable);

        public static OptionalRealSmokeSettings From(
            Func<string, string?> readVariable) =>
            new(
                readVariable(RealTestApiKeyEnvironmentVariable),
                readVariable(RealTestModelIdEnvironmentVariable),
                readVariable(RealTestEndpointEnvironmentVariable));

        public IReadOnlyDictionary<string, string?> ToConfiguration()
        {
            if (!IsEnabled)
            {
                return new Dictionary<string, string?>();
            }

            var values = new Dictionary<string, string?>
            {
                ["Hive:AiGateway:Provider"] = "real",
                ["Hive:AiGateway:Real:ProviderId"] = "openai",
                ["Hive:AiGateway:Real:ModelId"] = ModelId,
                ["Hive:AiGateway:Real:ApiKey"] = ApiKey,
            };

            if (!string.IsNullOrWhiteSpace(Endpoint))
            {
                values["Hive:AiGateway:Real:Endpoint"] = Endpoint;
            }

            return values;
        }
    }
}
