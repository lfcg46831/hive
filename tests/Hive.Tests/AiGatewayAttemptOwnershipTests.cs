using Akka.Actor;
using Hive.Actors.Gateway;
using Hive.Domain.Ai;
using Hive.Domain.Identity;
using Hive.Infrastructure.Ai;
using Hive.Infrastructure.Configuration;
using Microsoft.Extensions.Options;

namespace Hive.Tests;

/// <summary>
/// Verifies BUG-023: every policy-validated attempt — the first candidate, its retries and every
/// declared fallback — is executed by the entity that owns the effective provider's admission and
/// circuit, while policy, chain, retries and auditing stay with the coordinating gateway. The
/// receiving entity never starts another journey, and a failed attempt at the owner never
/// authorizes an alternative local execution.
/// </summary>
public sealed class AiGatewayAttemptOwnershipTests : IDisposable
{
    private readonly List<IDisposable> _disposables = [];

    private static readonly OrganizationId Organization = OrganizationId.From("acme-delivery");
    private static readonly PositionId Position = PositionId.From("triage-agent");
    private static readonly ThreadId Thread =
        ThreadId.From(Guid.Parse("55555555-5555-5555-5555-555555555555"));
    private static readonly MessageId Message =
        MessageId.From(Guid.Parse("66666666-6666-6666-6666-666666666666"));
    private static readonly AiProviderMetadata Primary = new("openai", "gpt-5-mini");
    private static readonly AiProviderMetadata Secondary = new("anthropic", "claude-sonnet");
    private static readonly TimeSpan AskTimeout = TimeSpan.FromSeconds(5);

    [Fact]
    public async Task Every_attempt_including_retries_and_fallback_reaches_its_provider_owner()
    {
        using var system = ActorSystem.Create($"ai-gateway-attempt-{Guid.NewGuid():N}");
        var colocated = new ScriptedProvider(Success);
        var audit = new RecordingAuditPublisher();
        var route = system.ActorOf(RouteActor.Props(request => request.Provider == Primary
            ? Failure(request, AiGatewayErrorCode.Timeout, isRetryable: true)
            : Success(request)));
        var gateway = Gateway(colocated, Routed(route), maxAttempts: 2, audit);

        var response = await gateway.CompleteAsync(Request(Policy(Secondary)));

        var attempts = await RouteActor.AttemptsOf(route);
        Assert.True(response.IsSuccess);
        // The whole chain crossed the cluster: nothing ran on the coordinator's own adapter.
        Assert.Empty(colocated.Requests);
        Assert.Equal(
            new[] { "openai", "openai", "anthropic" },
            attempts.Select(attempt => attempt.ProviderKey));
        // Every attempt, retries included, carries its own transport correlation.
        Assert.Equal(
            3,
            attempts
                .Select(attempt => attempt.Command.CorrelationId)
                .Distinct(StringComparer.Ordinal)
                .Count());
        // The coordinator keeps the single per-attempt and per-journey audit.
        Assert.Equal(
            new[] { "0-1", "0-2", "1-1" },
            audit.Events
                .Where(@event => @event.Scope == AiGatewayCostAuditScope.Attempt)
                .Select(@event => @event.AttemptId));
        Assert.Single(audit.Events.Where(
            @event => @event.Scope == AiGatewayCostAuditScope.Journey));
    }

    [Fact]
    public async Task The_owner_entity_executes_the_attempt_without_starting_another_journey()
    {
        var journeys = new RecordingGateway();
        var provider = new ScriptedProvider(Success);
        using var system = ActorSystem.Create($"ai-gateway-attempt-{Guid.NewGuid():N}");
        var entity = system.ActorOf(
            AiGatewayActor.Props(journeys, logger: null, localAttempts: LocalExecutor(provider)),
            "gateway");

        var reply = await entity.Ask<object>(
            new ExecuteAiGatewayAttempt("attempt-1", Request()),
            AskTimeout);

        var completed = Assert.IsType<AiGatewayAttemptCompleted>(reply);
        Assert.Equal("attempt-1", completed.CorrelationId);
        Assert.True(completed.Result.Response.IsSuccess);
        Assert.True(completed.Result.ReachedProvider);
        Assert.NotNull(completed.Result.QueueDuration);
        Assert.Single(provider.Requests);
        Assert.Equal(0, journeys.Calls);
    }

    [Fact]
    public async Task An_entity_without_a_local_executor_reports_an_attempt_failure()
    {
        var journeys = new RecordingGateway();
        using var system = ActorSystem.Create($"ai-gateway-attempt-{Guid.NewGuid():N}");
        var entity = system.ActorOf(AiGatewayActor.Props(journeys), "gateway");

        var reply = await entity.Ask<object>(
            new ExecuteAiGatewayAttempt("attempt-2", Request()),
            AskTimeout);

        var failed = Assert.IsType<AiGatewayAttemptFailed>(reply);
        Assert.Equal("attempt-2", failed.CorrelationId);
        Assert.Equal(0, journeys.Calls);
    }

    [Fact]
    public async Task A_failed_attempt_at_the_owner_never_falls_back_to_local_execution()
    {
        using var system = ActorSystem.Create($"ai-gateway-attempt-{Guid.NewGuid():N}");
        var colocated = new ScriptedProvider(Success);
        var route = system.ActorOf(RouteActor.PropsFailingEveryAttempt());
        var gateway = Gateway(colocated, Routed(route), maxAttempts: 1);

        await Assert.ThrowsAsync<InvalidOperationException>(
            () => gateway.CompleteAsync(Request()));

        Assert.Empty(colocated.Requests);
        Assert.Single(await RouteActor.AttemptsOf(route));
    }

    [Fact]
    public async Task Attempt_cancellation_is_sent_to_the_same_owner()
    {
        using var system = ActorSystem.Create($"ai-gateway-attempt-{Guid.NewGuid():N}");
        var provider = new ScriptedProvider(Success);
        var route = system.ActorOf(RouteActor.PropsWithoutReplies());
        var executor = new ShardedAiGatewayAttemptExecutor(
            Routed(route),
            LocalExecutor(provider),
            GatewayOptions());
        using var cancellation = new CancellationTokenSource();

        var pending = executor.ExecuteAsync(Request(), cancellation.Token);
        cancellation.CancelAfter(TimeSpan.FromMilliseconds(100));

        await Assert.ThrowsAnyAsync<OperationCanceledException>(() => pending);
        // The cancel command and the canceled ask race by construction, so wait for the owner to
        // observe it rather than assuming an ordering the runtime does not guarantee.
        Assert.True(await RouteActor.EventuallyReceivedAsync(
            route,
            envelope => envelope.Command is CancelAiGatewayCall));

        var envelopes = await RouteActor.EnvelopesOf(route);
        var execute = Assert.IsType<ExecuteAiGatewayAttempt>(envelopes[0].Command);
        var cancel = Assert.IsType<CancelAiGatewayCall>(envelopes[^1].Command);
        Assert.Equal(execute.CorrelationId, cancel.CorrelationId);
        Assert.All(envelopes, envelope => Assert.Equal("openai", envelope.ProviderKey));
        Assert.Empty(provider.Requests);
    }

    [Fact]
    public async Task Without_a_usable_route_the_attempt_stays_colocated()
    {
        var provider = new ScriptedProvider(Success);
        var executor = new ShardedAiGatewayAttemptExecutor(
            new AiGatewayShardRegion(),
            LocalExecutor(provider),
            GatewayOptions());

        var result = await executor.ExecuteAsync(Request());

        Assert.True(result.Response.IsSuccess);
        Assert.True(result.ReachedProvider);
        Assert.NotNull(result.QueueDuration);
        Assert.Single(provider.Requests);
    }

    public void Dispose()
    {
        foreach (var disposable in _disposables)
        {
            disposable.Dispose();
        }
    }

    private AiGateway Gateway(
        IAiGatewayProvider colocatedProvider,
        AiGatewayShardRegion region,
        int maxAttempts,
        IAiGatewayAuditPublisher? auditPublisher = null)
    {
        var resolver = FixedResolver(maxAttempts);
        return new AiGateway(
            colocatedProvider,
            auditPublisher,
            TimeProvider.System,
            detailedAuditPublisher: null,
            admissionLimiter: Track(new AiProviderAdmissionLimiter(resolver)),
            resolver,
            new ImmediateBackoff(),
            circuitBreaker: null,
            fallbackSkipPublisher: null,
            metricsPublisher: null,
            attemptExecutor: new ShardedAiGatewayAttemptExecutor(
                region,
                LocalExecutor(colocatedProvider, resolver),
                GatewayOptions()));
    }

    private LocalAiGatewayAttemptExecutor LocalExecutor(
        IAiGatewayProvider provider,
        IAiProviderResiliencePolicyResolver? resolver = null)
    {
        resolver ??= FixedResolver();
        return new LocalAiGatewayAttemptExecutor(
            provider,
            Track(new AiProviderAdmissionLimiter(resolver)),
            new AiProviderCircuitBreaker(resolver));
    }

    private T Track<T>(T disposable)
        where T : IDisposable
    {
        _disposables.Add(disposable);
        return disposable;
    }

    private static IOptions<HiveOptions> GatewayOptions() =>
        Microsoft.Extensions.Options.Options.Create(new HiveOptions
        {
            Gateway = new GatewayNodeOptions { AskTimeout = AskTimeout },
        });

    private static AiGatewayShardRegion Routed(IActorRef route)
    {
        var region = new AiGatewayShardRegion();
        region.Publish(route, hasGatewayMember: () => true);
        return region;
    }

    private static IAiProviderResiliencePolicyResolver FixedResolver(int maxAttempts = 1) =>
        new FixedPolicyResolver(new AiProviderResiliencePolicy(
            AiProviderRateLimitPolicy.Default,
            AiProviderQueuePolicy.Default,
            new AiProviderRetryPolicy(
                maxAttempts,
                TimeSpan.FromMilliseconds(1),
                TimeSpan.FromMilliseconds(10),
                jitterRatio: 0m),
            AiProviderCircuitBreakerPolicy.Default));

    private static AiGatewayPolicy Policy(params AiProviderMetadata[] fallback) =>
        new(
            new[] { Primary }.Concat(fallback),
            hasAvailableBudget: true,
            maxOutputTokens: null,
            maxTimeout: null,
            allowedProcessingModes: null,
            authorizedTools: null,
            fallback: fallback);

    private static AiGatewayRequest Request(AiGatewayPolicy? policy = null) =>
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

    private sealed class ScriptedProvider(Func<AiGatewayRequest, AiGatewayResponse> respond)
        : IAiGatewayProvider
    {
        private readonly List<AiGatewayRequest> _requests = [];

        public IReadOnlyList<AiGatewayRequest> Requests => _requests;

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken)
        {
            _requests.Add(request);
            return Task.FromResult(respond(request));
        }
    }

    private sealed class RecordingGateway : IAiGateway
    {
        public int Calls { get; private set; }

        public Task<AiGatewayResponse> CompleteAsync(
            AiGatewayRequest request,
            CancellationToken cancellationToken = default)
        {
            Calls++;
            return Task.FromResult(AiGatewayResponse.Succeeded(
                request.OrganizationId,
                request.PositionId,
                request.ThreadId,
                request.MessageId,
                "journey",
                AiFinishReason.Stop,
                request.Provider));
        }
    }

    private sealed class RecordingAuditPublisher : IAiGatewayAuditPublisher
    {
        private readonly List<AiGatewayCostAuditEvent> _events = [];

        public IReadOnlyList<AiGatewayCostAuditEvent> Events => _events;

        public void Publish(AiGatewayCostAuditEvent auditEvent) => _events.Add(auditEvent);
    }

    /// <summary>Stands in for the shard region: records envelopes and answers attempt commands.</summary>
    private sealed class RouteActor : ReceiveActor
    {
        private readonly List<AiGatewayEnvelope> _envelopes = [];

        public RouteActor(Func<AiGatewayRequest, AiGatewayResponse>? respond, bool failEveryAttempt)
        {
            Receive<AiGatewayEnvelope>(envelope =>
            {
                _envelopes.Add(envelope);
                if (envelope.Command is not ExecuteAiGatewayAttempt attempt)
                {
                    return;
                }

                if (failEveryAttempt)
                {
                    Sender.Tell(new AiGatewayAttemptFailed(attempt.CorrelationId), Self);
                    return;
                }

                if (respond is null)
                {
                    return;
                }

                Sender.Tell(
                    new AiGatewayAttemptCompleted(
                        attempt.CorrelationId,
                        new AiGatewayAttemptResult(
                            respond(attempt.Request),
                            TimeSpan.FromMilliseconds(5),
                            ReachedProvider: true)),
                    Self);
            });
            Receive<GetEnvelopes>(_ => Sender.Tell(_envelopes.ToArray(), Self));
        }

        public static Props Props(Func<AiGatewayRequest, AiGatewayResponse> respond) =>
            Akka.Actor.Props.Create(() => new RouteActor(respond, false));

        public static Props PropsFailingEveryAttempt() =>
            Akka.Actor.Props.Create(() => new RouteActor(null, true));

        public static Props PropsWithoutReplies() =>
            Akka.Actor.Props.Create(() => new RouteActor(null, false));

        public static async Task<IReadOnlyList<AiGatewayEnvelope>> EnvelopesOf(IActorRef route) =>
            await route.Ask<AiGatewayEnvelope[]>(new GetEnvelopes(), TimeSpan.FromSeconds(5));

        public static async Task<IReadOnlyList<RoutedAttempt>> AttemptsOf(IActorRef route) =>
            (await EnvelopesOf(route))
                .Where(envelope => envelope.Command is ExecuteAiGatewayAttempt)
                .Select(envelope => new RoutedAttempt(
                    envelope.ProviderKey,
                    (ExecuteAiGatewayAttempt)envelope.Command))
                .ToArray();

        public static async Task<bool> EventuallyReceivedAsync(
            IActorRef route,
            Func<AiGatewayEnvelope, bool> predicate)
        {
            for (var attempt = 0; attempt < 40; attempt++)
            {
                if ((await EnvelopesOf(route)).Any(predicate))
                {
                    return true;
                }

                await Task.Delay(TimeSpan.FromMilliseconds(50));
            }

            return false;
        }

        public sealed record GetEnvelopes;
    }

    private sealed record RoutedAttempt(string ProviderKey, ExecuteAiGatewayAttempt Command);
}
