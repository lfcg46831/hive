using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;

namespace Hive.Tests;

public sealed class AiGatewayFallbackTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private static readonly OrganizationId Organization =
        OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("11111111-1111-1111-1111-111111111111"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("22222222-2222-2222-2222-222222222222"));
    private static readonly AiProviderMetadata Primary = new("openai", "gpt-5-mini");
    private static readonly AiProviderMetadata Secondary = new("anthropic", "claude-sonnet");
    private static readonly AiProviderMetadata Tertiary = new("mistral", "mistral-large");

    [Fact]
    public void Skip_reason_contract_is_closed_and_canonical()
    {
        Assert.Equal(
            "duplicate-candidate",
            AiGatewayFallbackSkipReasonContract.ToWireValue(
                AiGatewayFallbackSkipReason.DuplicateCandidate));
        Assert.Equal(
            AiGatewayFallbackSkipReason.PolicyRevalidationFailed,
            AiGatewayFallbackSkipReasonContract.ParseWireValue("policy-revalidation-failed"));

        Assert.False(
            AiGatewayFallbackSkipReasonContract.TryParseWireValue("DuplicateCandidate", out _));
        Assert.Throws<ArgumentOutOfRangeException>(
            () => AiGatewayFallbackSkipReasonContract.ToWireValue(
                (AiGatewayFallbackSkipReason)99));
        Assert.Throws<ArgumentException>(
            () => AiGatewayFallbackSkipReasonContract.ParseWireValue("policy-failed"));
    }

    [Fact]
    public void Skip_contract_requires_a_chain_index_and_a_matching_causal_error()
    {
        var at = new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero);
        var skip = new AiGatewayFallbackSkip(
            Organization,
            Position,
            Thread,
            Message,
            candidateIndex: 2,
            Secondary.ProviderId,
            Secondary.ModelId,
            at,
            AiGatewayFallbackSkipReason.PolicyRevalidationFailed,
            AiGatewayErrorCode.ModelNotAuthorized);

        Assert.Equal(2, skip.CandidateIndex);
        Assert.Equal(Secondary.ProviderId, skip.ProviderId);
        Assert.Equal(Secondary.ModelId, skip.ModelId);
        Assert.Equal(at, skip.OccurredAt);
        Assert.Equal(AiGatewayErrorCode.ModelNotAuthorized, skip.ErrorCode);

        // Index zero is the primary candidate and can never be skipped.
        Assert.Throws<ArgumentOutOfRangeException>(() => Skip(
            candidateIndex: 0,
            AiGatewayFallbackSkipReason.DuplicateCandidate,
            errorCode: null));
        // A duplicate carries no causal error; a revalidation failure requires one.
        Assert.Throws<ArgumentException>(() => Skip(
            candidateIndex: 1,
            AiGatewayFallbackSkipReason.DuplicateCandidate,
            AiGatewayErrorCode.Timeout));
        Assert.Throws<ArgumentException>(() => Skip(
            candidateIndex: 1,
            AiGatewayFallbackSkipReason.PolicyRevalidationFailed,
            errorCode: null));
        Assert.Throws<ArgumentException>(() => new AiGatewayFallbackSkip(
            Organization,
            Position,
            Thread,
            Message,
            candidateIndex: 1,
            Secondary.ProviderId,
            Secondary.ModelId,
            default,
            AiGatewayFallbackSkipReason.DuplicateCandidate));

        static AiGatewayFallbackSkip Skip(
            int candidateIndex,
            AiGatewayFallbackSkipReason reason,
            AiGatewayErrorCode? errorCode) =>
            new(
                Organization,
                Position,
                Thread,
                Message,
                candidateIndex,
                Secondary.ProviderId,
                Secondary.ModelId,
                new DateTimeOffset(2026, 8, 16, 9, 0, 0, TimeSpan.Zero),
                reason,
                errorCode);
    }

    [Fact]
    public async Task Chain_advances_when_the_previous_candidate_exhausts_its_retries()
    {
        var skips = new RecordingSkipPublisher();
        var audit = new RecordingAuditPublisher();
        var provider = new ScriptedProvider((_, request) =>
            request.Provider == Primary
                ? Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true)
                : Success(request));
        var gateway = Gateway(provider, skipPublisher: skips, auditPublisher: audit);

        var response = await gateway.CompleteAsync(
            Request(Policy(Secondary)),
            CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal(Secondary, response.Provider);
        Assert.Equal(
            new AiProviderMetadata?[] { Primary, Primary, Secondary },
            provider.Requests.Select(request => request.Provider));
        Assert.Empty(skips.Skips);

        // Cost attribution follows the candidate that produced the response.
        var costEvent = Assert.Single(audit.Events);
        Assert.Equal(Secondary.ProviderId, costEvent.Provider?.ProviderId);
        Assert.Equal(Secondary.ModelId, costEvent.Provider?.ModelId);
    }

    [Fact]
    public async Task Chain_advances_on_quota_exceeded_without_retrying_the_same_provider()
    {
        var provider = new ScriptedProvider((_, request) =>
            request.Provider == Primary
                ? Failure(request, AiGatewayErrorCode.QuotaExceeded, isRetryable: false)
                : Success(request));
        var gateway = Gateway(provider);

        var response = await gateway.CompleteAsync(
            Request(Policy(Secondary)),
            CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal(
            new AiProviderMetadata?[] { Primary, Secondary },
            provider.Requests.Select(request => request.Provider));
    }

    [Fact]
    public async Task Chain_advances_on_local_saturation_of_the_previous_candidate()
    {
        var provider = new ScriptedProvider((_, request) => Success(request));
        var limiter = new RejectingAdmissionLimiter(
            Primary.ProviderId,
            Track(new AiProviderAdmissionLimiter(FixedResolver())));
        var gateway = Gateway(provider, limiter: limiter);

        var response = await gateway.CompleteAsync(
            Request(Policy(Secondary)),
            CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal(Secondary, response.Provider);
        Assert.Equal(
            new AiProviderMetadata?[] { Secondary },
            provider.Requests.Select(request => request.Provider));
    }

    [Fact]
    public async Task Chain_advances_on_an_open_circuit_without_calling_that_provider()
    {
        var resolver = FixedResolver(
            maxAttempts: 1,
            circuitBreaker: new AiProviderCircuitBreakerPolicy(
                samplingWindow: TimeSpan.FromMinutes(1),
                failureThreshold: 1,
                openDuration: TimeSpan.FromMinutes(5),
                halfOpenMaxConcurrentProbes: 1));
        var provider = new ScriptedProvider((_, request) =>
            request.Provider == Primary
                ? Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true)
                : Success(request));
        var gateway = Gateway(provider, resolver: resolver);
        var policy = Policy(Secondary);

        // The first call opens the primary circuit and falls back.
        var first = await gateway.CompleteAsync(
            Request(policy),
            CancellationToken.None);
        // The second call never reaches the primary provider at all.
        var second = await gateway.CompleteAsync(
            Request(policy),
            CancellationToken.None);

        Assert.True(first.IsSuccess);
        Assert.True(second.IsSuccess);
        Assert.Equal(
            new AiProviderMetadata?[] { Primary, Secondary, Secondary },
            provider.Requests.Select(request => request.Provider));
    }

    [Theory]
    [InlineData(AiGatewayErrorCode.ProviderRejected)]
    [InlineData(AiGatewayErrorCode.InvalidProviderResponse)]
    [InlineData(AiGatewayErrorCode.CredentialsMissing)]
    [InlineData(AiGatewayErrorCode.OutputConstraintUnsupported)]
    public async Task Terminal_errors_fail_immediately_without_touching_the_chain(
        AiGatewayErrorCode code)
    {
        var provider = new ScriptedProvider((_, request) =>
            Failure(request, code, isRetryable: false));
        var gateway = Gateway(provider);

        var response = await gateway.CompleteAsync(
            Request(Policy(Secondary)),
            CancellationToken.None);

        Assert.False(response.IsSuccess);
        Assert.Equal(code, response.Error!.Code);
        Assert.Null(response.Error.Reason);
        Assert.Equal(
            new AiProviderMetadata?[] { Primary },
            provider.Requests.Select(request => request.Provider));
    }

    [Fact]
    public async Task Exhausted_chain_returns_the_last_error_with_the_terminal_reason()
    {
        var diagnostics = new AiGatewayFailureDiagnostics(providerStatusCode: 503);
        var provider = new ScriptedProvider((_, request) => AiGatewayResponse.Failed(
            new AiGatewayError(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                AiGatewayErrorCode.ProviderUnavailable,
                "AI provider is unavailable.",
                isRetryable: true,
                request.Provider,
                diagnostics)));
        var gateway = Gateway(provider);

        var response = await gateway.CompleteAsync(
            Request(Policy(Secondary)),
            CancellationToken.None);

        Assert.False(response.IsSuccess);
        var error = response.Error!;
        Assert.Equal(AiGatewayErrorReason.FallbackExhausted, error.Reason);
        Assert.Equal(AiGatewayErrorCode.ProviderUnavailable, error.Code);
        Assert.Equal("AI provider is unavailable.", error.Message);
        Assert.True(error.IsRetryable);
        Assert.Same(diagnostics, error.Diagnostics);
        // The terminal error belongs to the last candidate that actually ran.
        Assert.Equal(Secondary, error.Provider);
        Assert.Equal(Thread, error.ThreadId);
        Assert.Equal(Message, error.MessageId);
    }

    [Fact]
    public async Task An_empty_chain_leaves_the_terminal_error_exactly_as_before()
    {
        var provider = new ScriptedProvider((_, request) =>
            Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true));
        var gateway = Gateway(provider);

        var withoutPolicy = await gateway.CompleteAsync(
            Request(policy: null),
            CancellationToken.None);
        var withEmptyChain = await gateway.CompleteAsync(
            Request(Policy()),
            CancellationToken.None);

        Assert.Null(withoutPolicy.Error!.Reason);
        Assert.Equal(AiGatewayErrorCode.Timeout, withoutPolicy.Error.Code);
        Assert.Null(withEmptyChain.Error!.Reason);
        Assert.Equal(AiGatewayErrorCode.Timeout, withEmptyChain.Error.Code);
    }

    [Fact]
    public async Task An_empty_chain_preserves_the_circuit_open_reason()
    {
        var resolver = FixedResolver(
            maxAttempts: 1,
            circuitBreaker: new AiProviderCircuitBreakerPolicy(
                samplingWindow: TimeSpan.FromMinutes(1),
                failureThreshold: 1,
                openDuration: TimeSpan.FromMinutes(5),
                halfOpenMaxConcurrentProbes: 1));
        var provider = new ScriptedProvider((_, request) =>
            Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true));
        var gateway = Gateway(provider, resolver: resolver);

        await gateway.CompleteAsync(Request(Policy()), CancellationToken.None);
        var response = await gateway.CompleteAsync(
            Request(Policy()),
            CancellationToken.None);

        Assert.Equal(AiGatewayErrorReason.CircuitOpen, response.Error!.Reason);
        Assert.Single(provider.Requests);
    }

    [Fact]
    public async Task A_candidate_that_fails_policy_revalidation_is_skipped_and_audited()
    {
        var skips = new RecordingSkipPublisher();
        var provider = new ScriptedProvider((_, request) =>
            request.Provider == Tertiary
                ? Success(request)
                : Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true));
        var gateway = Gateway(provider, skipPublisher: skips);
        // Secondary is declared in the chain but is not an authorized model.
        var policy = new AiGatewayPolicy(
            [Primary, Tertiary],
            hasAvailableBudget: true,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback: [Secondary, Tertiary]);

        var response = await gateway.CompleteAsync(
            Request(policy),
            CancellationToken.None);

        Assert.True(response.IsSuccess);
        Assert.Equal(
            new AiProviderMetadata?[] { Primary, Primary, Tertiary },
            provider.Requests.Select(request => request.Provider));

        var skip = Assert.Single(skips.Skips);
        Assert.Equal(1, skip.CandidateIndex);
        Assert.Equal(Secondary.ProviderId, skip.ProviderId);
        Assert.Equal(Secondary.ModelId, skip.ModelId);
        Assert.Equal(AiGatewayFallbackSkipReason.PolicyRevalidationFailed, skip.Reason);
        Assert.Equal(AiGatewayErrorCode.ProviderNotAuthorized, skip.ErrorCode);
        Assert.Equal(Organization, skip.OrganizationId);
        Assert.Equal(Thread, skip.ThreadId);
        Assert.Equal(Message, skip.MessageId);
    }

    [Fact]
    public async Task A_candidate_that_repeats_an_executed_pair_is_skipped_as_a_duplicate()
    {
        var skips = new RecordingSkipPublisher();
        var provider = new ScriptedProvider((_, request) =>
            Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true));
        var gateway = Gateway(provider, skipPublisher: skips);
        var policy = new AiGatewayPolicy(
            [Primary, Secondary],
            hasAvailableBudget: true,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback: [Primary, Secondary, Secondary]);

        var response = await gateway.CompleteAsync(
            Request(policy),
            CancellationToken.None);

        Assert.Equal(
            AiGatewayErrorReason.FallbackExhausted,
            response.Error!.Reason);
        Assert.Equal(
            new AiProviderMetadata?[] { Primary, Primary, Secondary, Secondary },
            provider.Requests.Select(request => request.Provider));

        Assert.Equal(2, skips.Skips.Count);
        Assert.All(
            skips.Skips,
            skip => Assert.Equal(
                AiGatewayFallbackSkipReason.DuplicateCandidate,
                skip.Reason));
        Assert.All(skips.Skips, skip => Assert.Null(skip.ErrorCode));
        Assert.Equal(new[] { 1, 3 }, skips.Skips.Select(skip => skip.CandidateIndex));
    }

    [Fact]
    public async Task Every_candidate_keeps_the_request_identity_and_correlation()
    {
        var provider = new ScriptedProvider((_, request) =>
            Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true));
        var gateway = Gateway(provider);

        await gateway.CompleteAsync(
            Request(Policy(Secondary)),
            CancellationToken.None);

        Assert.All(provider.Requests, request =>
        {
            Assert.Equal(Organization, request.OrganizationId);
            Assert.Equal(Position, request.PositionId);
            Assert.Equal(Thread, request.ThreadId);
            Assert.Equal(Message, request.MessageId);
            Assert.Equal("Classify this bug.", request.Content);
        });
    }

    [Fact]
    public async Task Cancellation_stops_the_chain_before_the_next_candidate()
    {
        using var cancellation = new CancellationTokenSource();
        var provider = new ScriptedProvider((_, request) =>
        {
            cancellation.Cancel();
            return Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true);
        });
        var gateway = Gateway(provider, resolver: FixedResolver(maxAttempts: 1));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(
            () => gateway.CompleteAsync(Request(Policy(Secondary)), cancellation.Token));

        Assert.Equal(
            new AiProviderMetadata?[] { Primary },
            provider.Requests.Select(request => request.Provider));
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private AiGateway Gateway(
        IAiGatewayProvider provider,
        IAiProviderResiliencePolicyResolver? resolver = null,
        IAiProviderAdmissionLimiter? limiter = null,
        IAiGatewayFallbackSkipPublisher? skipPublisher = null,
        IAiGatewayAuditPublisher? auditPublisher = null)
    {
        resolver ??= FixedResolver();
        return new AiGateway(
            provider,
            auditPublisher,
            TimeProvider.System,
            detailedAuditPublisher: null,
            limiter ?? Track(new AiProviderAdmissionLimiter(resolver)),
            resolver,
            new ImmediateBackoff(),
            circuitBreaker: null,
            fallbackSkipPublisher: skipPublisher);
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private static IAiProviderResiliencePolicyResolver FixedResolver(
        int maxAttempts = 2,
        AiProviderCircuitBreakerPolicy? circuitBreaker = null) =>
        new FixedPolicyResolver(new AiProviderResiliencePolicy(
            AiProviderRateLimitPolicy.Default,
            AiProviderQueuePolicy.Default,
            new AiProviderRetryPolicy(
                maxAttempts,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(10),
                jitterRatio: 0m),
            circuitBreaker ?? AiProviderCircuitBreakerPolicy.Default));

    private static AiGatewayPolicy Policy(params AiProviderMetadata[] fallback) =>
        new(
            new[] { Primary }.Concat(fallback),
            hasAvailableBudget: true,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback: fallback);

    private static AiGatewayRequest Request(AiGatewayPolicy? policy) =>
        new(
            Organization,
            Position,
            Thread,
            Message,
            "Classify this bug.",
            provider: Primary,
            policy: policy);

    private static AiGatewayResponse Success(AiGatewayRequest request) =>
        AiGatewayResponse.Succeeded(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            "Done.",
            AiFinishReason.Stop,
            request.Provider);

    private static AiGatewayResponse Failure(
        AiGatewayRequest request,
        AiGatewayErrorCode code,
        bool isRetryable) =>
        AiGatewayResponse.Failed(new AiGatewayError(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            code,
            "Provider failed.",
            isRetryable,
            request.Provider));

    private sealed class FixedPolicyResolver(AiProviderResiliencePolicy policy)
        : IAiProviderResiliencePolicyResolver
    {
        public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) => policy;
    }

    private sealed class ImmediateBackoff : IAiProviderRetryBackoff
    {
        public Task DelayAsync(
            AiProviderRetryPolicy policy,
            int failedAttemptNumber,
            CancellationToken cancellationToken = default)
        {
            cancellationToken.ThrowIfCancellationRequested();
            return Task.CompletedTask;
        }
    }

    /// <summary>
    /// Saturates one provider deterministically and delegates every other provider to
    /// the real limiter, so overflow drives the chain without timing races.
    /// </summary>
    private sealed class RejectingAdmissionLimiter(
        string rejectedProviderId,
        IAiProviderAdmissionLimiter inner)
        : IAiProviderAdmissionLimiter
    {
        public ValueTask<AiProviderAdmissionResult> AcquireAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default) =>
            string.Equals(
                request.Provider?.ProviderId,
                rejectedProviderId,
                StringComparison.Ordinal)
                ? ValueTask.FromResult(AiProviderAdmissionResult.Rejected(
                    AiGatewayResilienceErrorCatalog.GatewayOverloaded(request)))
                : inner.AcquireAsync(request, cancellationToken);
    }

    private sealed class ScriptedProvider(
        Func<int, AiGatewayRequest, AiGatewayResponse> respond)
        : IAiGatewayProvider
    {
        private readonly List<AiGatewayRequest> _requests = [];

        public IReadOnlyList<AiGatewayRequest> Requests => _requests;

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(respond(_requests.Count, request));
        }
    }

    private sealed class RecordingSkipPublisher : IAiGatewayFallbackSkipPublisher
    {
        private readonly List<AiGatewayFallbackSkip> _skips = [];

        public IReadOnlyList<AiGatewayFallbackSkip> Skips => _skips;

        public void Publish(AiGatewayFallbackSkip skip) => _skips.Add(skip);
    }

    private sealed class RecordingAuditPublisher : IAiGatewayAuditPublisher
    {
        private readonly List<AiGatewayCostAuditEvent> _events = [];

        public IReadOnlyList<AiGatewayCostAuditEvent> Events => _events;

        public void Publish(AiGatewayCostAuditEvent auditEvent) => _events.Add(auditEvent);
    }
}
