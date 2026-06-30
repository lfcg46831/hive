using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiGatewayServiceTests
{
    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));

    [Fact]
    public async Task CompleteAsync_rejects_null_request_without_calling_provider()
    {
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        await Assert.ThrowsAsync<ArgumentNullException>(
            async () => await gateway.CompleteAsync(null!));

        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public void Constructor_rejects_null_provider()
    {
        Assert.Throws<ArgumentNullException>(() => new AiGateway(null!));
    }

    [Fact]
    public async Task CompleteAsync_delegates_request_and_cancellation_to_provider()
    {
        var request = Request();
        var response = SuccessResponse();
        var provider = new RecordingAiGatewayProvider(response);
        var gateway = new AiGateway(provider);
        using var cancellation = new CancellationTokenSource();

        var result = await gateway.CompleteAsync(request, cancellation.Token);

        Assert.Same(response, result);
        Assert.Equal(1, provider.CallCount);
        Assert.Same(request, provider.Request);
        Assert.Equal(cancellation.Token, provider.CancellationToken);
    }

    [Fact]
    public async Task CompleteAsync_publishes_success_cost_audit_event_after_provider_returns()
    {
        var startedAt = new DateTimeOffset(2026, 6, 30, 9, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMilliseconds(125);
        var providerMetadata = new AiProviderMetadata("openai", "gpt-4.1");
        var usage = new AiTokenUsage(12, 8, 20, isEstimated: false);
        var cost = new AiCostMetadata(0.03m, "EUR", isEstimated: true);
        var request = Request(provider: providerMetadata);
        var response = AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The bug is reproducible.",
            AiFinishReason.Stop,
            providerMetadata,
            usage: usage,
            cost: cost);
        var audit = new CapturingAiGatewayAuditPublisher();
        var timeProvider = new SequenceTimeProvider(startedAt, completedAt);
        var provider = new RecordingAiGatewayProvider(response);
        var gateway = new AiGateway(provider, audit, timeProvider);

        var result = await gateway.CompleteAsync(request);

        Assert.Same(response, result);
        var published = Assert.Single(audit.Events);
        Assert.Equal(Organization, published.OrganizationId);
        Assert.Equal(Position, published.PositionId);
        Assert.Equal(Thread, published.ThreadId);
        Assert.Equal(Message, published.MessageId);
        Assert.Equal(startedAt, published.StartedAt);
        Assert.Equal(completedAt, published.CompletedAt);
        Assert.Equal(TimeSpan.FromMilliseconds(125), published.Duration);
        Assert.Equal(AiGatewayCallResult.Succeeded, published.Result);
        Assert.Equal(providerMetadata, published.Provider);
        Assert.Equal(usage, published.Usage);
        Assert.Equal(cost, published.Cost);
        Assert.Null(published.ErrorCode);
    }

    [Fact]
    public async Task CompleteAsync_publishes_provider_failure_cost_audit_event()
    {
        var startedAt = new DateTimeOffset(2026, 6, 30, 10, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMilliseconds(250);
        var providerMetadata = new AiProviderMetadata("openai", "gpt-4.1");
        var error = new AiGatewayError(
            Organization,
            Position,
            Thread,
            Message,
            AiGatewayErrorCode.QuotaExceeded,
            "Provider quota exceeded.",
            isRetryable: true,
            providerMetadata);
        var response = AiGatewayResponse.Failed(error);
        var audit = new CapturingAiGatewayAuditPublisher();
        var timeProvider = new SequenceTimeProvider(startedAt, completedAt);
        var provider = new RecordingAiGatewayProvider(response);
        var gateway = new AiGateway(provider, audit, timeProvider);

        var result = await gateway.CompleteAsync(Request(provider: providerMetadata));

        Assert.Same(response, result);
        var published = Assert.Single(audit.Events);
        Assert.Equal(AiGatewayCallResult.Failed, published.Result);
        Assert.Equal(providerMetadata, published.Provider);
        Assert.Equal(AiGatewayErrorCode.QuotaExceeded, published.ErrorCode);
        Assert.True(published.IsRetryable);
        Assert.Null(published.Usage);
        Assert.Null(published.Cost);
    }

    [Fact]
    public async Task CompleteAsync_publishes_policy_failure_without_calling_provider()
    {
        var startedAt = new DateTimeOffset(2026, 6, 30, 11, 0, 0, TimeSpan.Zero);
        var completedAt = startedAt.AddMilliseconds(5);
        var providerMetadata = new AiProviderMetadata("openai", "gpt-4.1");
        var request = Request(
            provider: providerMetadata,
            policy: Policy(
                authorizedModels: [providerMetadata],
                hasAvailableBudget: false));
        var audit = new CapturingAiGatewayAuditPublisher();
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(
            provider,
            audit,
            new SequenceTimeProvider(startedAt, completedAt));

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsFailure);
        Assert.Equal(0, provider.CallCount);
        var published = Assert.Single(audit.Events);
        Assert.Equal(AiGatewayCallResult.Failed, published.Result);
        Assert.Equal(providerMetadata, published.Provider);
        Assert.Equal(AiGatewayErrorCode.BudgetInsufficient, published.ErrorCode);
        Assert.False(published.IsRetryable);
        Assert.Equal(TimeSpan.FromMilliseconds(5), published.Duration);
    }

    [Fact]
    public async Task CompleteAsync_does_not_publish_audit_event_when_precanceled()
    {
        var audit = new CapturingAiGatewayAuditPublisher();
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider, audit, TimeProvider.System);
        using var cancellation = new CancellationTokenSource();
        await cancellation.CancelAsync();

        await Assert.ThrowsAsync<OperationCanceledException>(
            async () => await gateway.CompleteAsync(Request(), cancellation.Token));

        Assert.Empty(audit.Events);
        Assert.Equal(0, provider.CallCount);
    }

    [Fact]
    public async Task CompleteAsync_applies_valid_policy_and_caps_request_before_calling_provider()
    {
        var requestedProvider = new AiProviderMetadata("openai", "gpt-4.1");
        var request = Request(
            provider: requestedProvider,
            processingMode: AiProcessingMode.Interactive,
            tools:
            [
                new AiToolDefinition("ticket.lookup", "Looks up a ticket."),
            ],
            modelParameters: new AiModelParameters(maxOutputTokens: 512),
            timeout: TimeSpan.FromSeconds(20),
            policy: Policy(
                authorizedModels: [requestedProvider],
                maxOutputTokens: 128,
                maxTimeout: TimeSpan.FromSeconds(5),
                allowedProcessingModes: [AiProcessingMode.Interactive],
                authorizedTools: ["ticket.lookup"]));
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsSuccess);
        Assert.Equal(1, provider.CallCount);
        Assert.NotSame(request, provider.Request);
        Assert.NotNull(provider.Request);
        Assert.Equal(requestedProvider, provider.Request!.Provider);
        Assert.Equal(AiProcessingMode.Interactive, provider.Request.ProcessingMode);
        Assert.Equal(128, provider.Request.ModelParameters.MaxOutputTokens);
        Assert.Equal(TimeSpan.FromSeconds(5), provider.Request.Timeout);
        Assert.Single(provider.Request.Tools);
    }

    [Fact]
    public async Task CompleteAsync_rejects_provider_not_authorized_without_calling_provider()
    {
        var request = Request(
            provider: new AiProviderMetadata("anthropic", "claude-haiku"),
            policy: Policy(
                authorizedModels: [new AiProviderMetadata("openai", "gpt-4.1")]));
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsFailure);
        Assert.Equal(0, provider.CallCount);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.ProviderNotAuthorized, error.Code);
        Assert.False(error.IsRetryable);
        Assert.NotNull(error.Provider);
        Assert.Equal("anthropic", error.Provider.ProviderId);
    }

    [Fact]
    public async Task CompleteAsync_rejects_model_not_authorized_without_calling_provider()
    {
        var request = Request(
            provider: new AiProviderMetadata("openai", "gpt-4.1"),
            policy: Policy(
                authorizedModels: [new AiProviderMetadata("openai", "gpt-4o-mini")]));
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsFailure);
        Assert.Equal(0, provider.CallCount);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.ModelNotAuthorized, error.Code);
        Assert.False(error.IsRetryable);
        Assert.NotNull(error.Provider);
        Assert.Equal("gpt-4.1", error.Provider.ModelId);
    }

    [Fact]
    public async Task CompleteAsync_rejects_unavailable_budget_without_calling_provider()
    {
        var request = Request(
            provider: new AiProviderMetadata("openai", "gpt-4.1"),
            policy: Policy(
                authorizedModels: [new AiProviderMetadata("openai", "gpt-4.1")],
                hasAvailableBudget: false));
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsFailure);
        Assert.Equal(0, provider.CallCount);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.BudgetInsufficient, error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task CompleteAsync_rejects_tool_not_authorized_without_calling_provider()
    {
        var request = Request(
            provider: new AiProviderMetadata("openai", "gpt-4.1"),
            tools: [new AiToolDefinition("ticket.delete", "Deletes a ticket.")],
            policy: Policy(
                authorizedModels: [new AiProviderMetadata("openai", "gpt-4.1")],
                authorizedTools: ["ticket.lookup"]));
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsFailure);
        Assert.Equal(0, provider.CallCount);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.ToolNotAuthorized, error.Code);
        Assert.False(error.IsRetryable);
    }

    [Fact]
    public async Task CompleteAsync_rejects_processing_mode_not_allowed_without_calling_provider()
    {
        var request = Request(
            provider: new AiProviderMetadata("openai", "gpt-4.1"),
            processingMode: AiProcessingMode.Batch,
            policy: Policy(
                authorizedModels: [new AiProviderMetadata("openai", "gpt-4.1")],
                allowedProcessingModes: [AiProcessingMode.Interactive]));
        var provider = new RecordingAiGatewayProvider(SuccessResponse());
        var gateway = new AiGateway(provider);

        var response = await gateway.CompleteAsync(request);

        Assert.True(response.IsFailure);
        Assert.Equal(0, provider.CallCount);
        var error = Assert.IsType<AiGatewayError>(response.Error);
        Assert.Equal(AiGatewayErrorCode.ConfigurationInvalid, error.Code);
        Assert.False(error.IsRetryable);
    }

    private static AiGatewayRequest Request(
        AiProviderMetadata? provider = null,
        AiProcessingMode? processingMode = null,
        IEnumerable<AiToolDefinition>? tools = null,
        AiModelParameters? modelParameters = null,
        TimeSpan? timeout = null,
        AiGatewayPolicy? policy = null) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            tools: tools,
            modelParameters: modelParameters,
            provider: provider,
            processingMode: processingMode,
            timeout: timeout,
            policy: policy);

    private static AiGatewayPolicy Policy(
        IEnumerable<AiProviderMetadata> authorizedModels,
        bool hasAvailableBudget = true,
        int? maxOutputTokens = null,
        TimeSpan? maxTimeout = null,
        IEnumerable<AiProcessingMode>? allowedProcessingModes = null,
        IEnumerable<string>? authorizedTools = null) =>
        new(
            authorizedModels,
            hasAvailableBudget,
            maxOutputTokens,
            maxTimeout,
            allowedProcessingModes,
            authorizedTools);

    private static AiGatewayResponse SuccessResponse() =>
        AiGatewayResponse.Succeeded(
            Organization,
            Position,
            Thread,
            Message,
            "The bug is reproducible.",
            AiFinishReason.Stop);

    private sealed class RecordingAiGatewayProvider(AiGatewayResponse response)
        : IAiGatewayProvider
    {
        public int CallCount { get; private set; }

        public AiGatewayRequest? Request { get; private set; }

        public CancellationToken CancellationToken { get; private set; }

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            CallCount++;
            Request = request;
            CancellationToken = cancellationToken;

            return Task.FromResult(response);
        }
    }

    private sealed class CapturingAiGatewayAuditPublisher : IAiGatewayAuditPublisher
    {
        private readonly List<AiGatewayCostAuditEvent> _events = new();

        public IReadOnlyList<AiGatewayCostAuditEvent> Events => _events;

        public void Publish(AiGatewayCostAuditEvent @event)
        {
            _events.Add(@event);
        }
    }

    private sealed class SequenceTimeProvider(params DateTimeOffset[] timestamps)
        : TimeProvider
    {
        private int _index;

        public override DateTimeOffset GetUtcNow()
        {
            if (_index >= timestamps.Length)
            {
                return timestamps[^1];
            }

            return timestamps[_index++];
        }
    }
}
