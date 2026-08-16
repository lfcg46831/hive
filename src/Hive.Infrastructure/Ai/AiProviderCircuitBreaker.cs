using System.Collections.Concurrent;
using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Process-local provider circuit seam. US-F1-05-T07 can replace this runtime
/// with cluster-wide actor coordination without changing <see cref="IAiGateway"/>.
/// </summary>
public interface IAiProviderCircuitBreaker
{
    AiProviderCircuitAdmission Acquire(AiGatewayRequest request);
}

public sealed class AiProviderCircuitAdmission
{
    private AiProviderCircuitAdmission(
        AiProviderCircuitLease? lease,
        AiGatewayError? error)
    {
        if ((lease is null) == (error is null))
        {
            throw new ArgumentException(
                "Provider circuit admission must contain exactly one lease or error.");
        }

        Lease = lease;
        Error = error;
    }

    public bool IsAllowed => Lease is not null;

    public AiProviderCircuitLease? Lease { get; }

    public AiGatewayError? Error { get; }

    internal static AiProviderCircuitAdmission Allowed(
        AiProviderCircuitLease lease) =>
        new(lease ?? throw new ArgumentNullException(nameof(lease)), error: null);

    internal static AiProviderCircuitAdmission Rejected(AiGatewayError error) =>
        new(lease: null, error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class AiProviderCircuitLease : IDisposable
{
    private ProviderCircuitLeaseRegistration? _registration;

    internal AiProviderCircuitLease(ProviderCircuitLeaseRegistration registration)
    {
        _registration = registration ?? throw new ArgumentNullException(nameof(registration));
    }

    internal void Observe(AiGatewayResponse response)
    {
        ArgumentNullException.ThrowIfNull(response);
        Complete(ProviderCircuitObservation.FromResponse(response));
    }

    internal void ObserveFailure(AiGatewayErrorCode errorCode) =>
        Complete(ProviderCircuitObservation.Failed(errorCode));

    public void Dispose() => Complete(ProviderCircuitObservation.Neutral);

    private void Complete(ProviderCircuitObservation observation) =>
        Interlocked.Exchange(ref _registration, null)?.Complete(observation);
}

/// <summary>
/// Thread-safe sliding-window circuit breaker isolated by provider id. Models and
/// positions that use the same provider share one state bucket in this process.
/// </summary>
public sealed class AiProviderCircuitBreaker : IAiProviderCircuitBreaker
{
    private readonly ConcurrentDictionary<ProviderCircuitKey, ProviderCircuitState> _states =
        new();
    private readonly IAiProviderResiliencePolicyResolver _policyResolver;
    private readonly TimeProvider _timeProvider;
    private readonly IAiProviderCircuitTransitionPublisher _transitionPublisher;

    public AiProviderCircuitBreaker(
        IAiProviderResiliencePolicyResolver policyResolver,
        TimeProvider? timeProvider = null,
        IAiProviderCircuitTransitionPublisher? transitionPublisher = null)
    {
        _policyResolver = policyResolver ??
            throw new ArgumentNullException(nameof(policyResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
        _transitionPublisher = transitionPublisher ??
            NoopAiProviderCircuitTransitionPublisher.Instance;
    }

    public AiProviderCircuitAdmission Acquire(AiGatewayRequest request)
    {
        ArgumentNullException.ThrowIfNull(request);

        var key = new ProviderCircuitKey(request.Provider?.ProviderId);
        var state = _states.GetOrAdd(
            key,
            _ => new ProviderCircuitState(
                key.ProviderId,
                RequirePolicy(_policyResolver.Resolve(request.Provider)),
                _timeProvider));

        var result = state.Acquire(this, request);
        try
        {
            Publish(result.Transition);
            return result.Admission;
        }
        catch
        {
            result.Admission.Lease?.Dispose();
            throw;
        }
    }

    internal void Complete(
        ProviderCircuitLeaseRegistration registration,
        ProviderCircuitObservation observation)
    {
        var transition = registration.State.Observe(registration, observation);
        Publish(transition);
    }

    private void Publish(AiProviderCircuitTransition? transition)
    {
        if (transition is not null)
        {
            _transitionPublisher.Publish(transition);
        }
    }

    private static AiProviderResiliencePolicy RequirePolicy(
        AiProviderResiliencePolicy? policy) =>
        policy ?? throw new InvalidOperationException(
            "AI provider resilience policy resolver returned no policy.");

    private readonly record struct ProviderCircuitKey(string? ProviderId);
}

internal sealed class ProviderCircuitState
{
    private readonly object _sync = new();
    private readonly string? _providerId;
    private readonly AiProviderCircuitBreakerPolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<long> _failures = new();
    private AiProviderCircuitState _state = AiProviderCircuitState.Closed;
    private long? _openedAt;
    private int _generation;
    private int _activeProbes;

    public ProviderCircuitState(
        string? providerId,
        AiProviderResiliencePolicy policy,
        TimeProvider timeProvider)
    {
        _providerId = providerId;
        _policy = (policy ?? throw new ArgumentNullException(nameof(policy))).CircuitBreaker;
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ProviderCircuitAcquireResult Acquire(
        AiProviderCircuitBreaker owner,
        AiGatewayRequest request)
    {
        lock (_sync)
        {
            var now = _timeProvider.GetTimestamp();
            AiProviderCircuitTransition? transition = null;

            if (_state == AiProviderCircuitState.Closed)
            {
                RemoveExpiredFailures(now);
            }
            else if (_state == AiProviderCircuitState.Open)
            {
                if (Elapsed(_openedAt!.Value, now) < _policy.OpenDuration)
                {
                    return ProviderCircuitAcquireResult.Rejected(
                        AiGatewayResilienceErrorCatalog.CircuitOpen(request));
                }

                transition = TransitionTo(
                    request,
                    AiProviderCircuitState.HalfOpen,
                    AiProviderCircuitTransitionReason.OpenDurationElapsed,
                    errorCode: null,
                    now);
            }

            if (_state == AiProviderCircuitState.HalfOpen)
            {
                if (_activeProbes >= _policy.HalfOpenMaxConcurrentProbes)
                {
                    return ProviderCircuitAcquireResult.Rejected(
                        AiGatewayResilienceErrorCatalog.CircuitOpen(request),
                        transition);
                }

                _activeProbes++;
            }

            var registration = new ProviderCircuitLeaseRegistration(
                owner,
                this,
                request,
                _state,
                _generation);
            return ProviderCircuitAcquireResult.Allowed(
                new AiProviderCircuitLease(registration),
                transition);
        }
    }

    public AiProviderCircuitTransition? Observe(
        ProviderCircuitLeaseRegistration registration,
        ProviderCircuitObservation observation)
    {
        lock (_sync)
        {
            if (registration.Generation != _generation ||
                registration.AcquiredState != _state)
            {
                return null;
            }

            var now = _timeProvider.GetTimestamp();

            if (_state == AiProviderCircuitState.HalfOpen)
            {
                if (_activeProbes <= 0)
                {
                    throw new InvalidOperationException(
                        "AI provider circuit probe completed without an active lease.");
                }

                _activeProbes--;

                if (observation.Outcome == ProviderCircuitOutcome.Succeeded)
                {
                    return TransitionTo(
                        registration.Request,
                        AiProviderCircuitState.Closed,
                        AiProviderCircuitTransitionReason.HalfOpenProbeSucceeded,
                        errorCode: null,
                        now);
                }

                if (observation.Outcome == ProviderCircuitOutcome.Failed)
                {
                    return TransitionTo(
                        registration.Request,
                        AiProviderCircuitState.Open,
                        AiProviderCircuitTransitionReason.HalfOpenProbeFailed,
                        observation.ErrorCode,
                        now);
                }

                return null;
            }

            if (_state != AiProviderCircuitState.Closed ||
                observation.Outcome != ProviderCircuitOutcome.Failed)
            {
                return null;
            }

            RemoveExpiredFailures(now);
            _failures.Enqueue(now);
            if (_failures.Count < _policy.FailureThreshold)
            {
                return null;
            }

            return TransitionTo(
                registration.Request,
                AiProviderCircuitState.Open,
                AiProviderCircuitTransitionReason.FailureThresholdReached,
                observation.ErrorCode,
                now);
        }
    }

    private AiProviderCircuitTransition TransitionTo(
        AiGatewayRequest request,
        AiProviderCircuitState newState,
        AiProviderCircuitTransitionReason reason,
        AiGatewayErrorCode? errorCode,
        long now)
    {
        var previousState = _state;
        _state = newState;
        _generation++;
        _failures.Clear();
        _activeProbes = 0;
        _openedAt = newState == AiProviderCircuitState.Open ? now : null;

        return new AiProviderCircuitTransition(
            request.OrganizationId,
            request.PositionId,
            request.ThreadId,
            request.MessageId,
            _providerId,
            previousState,
            newState,
            _timeProvider.GetUtcNow(),
            reason,
            errorCode);
    }

    private void RemoveExpiredFailures(long now)
    {
        while (_failures.TryPeek(out var failedAt) &&
               Elapsed(failedAt, now) >= _policy.SamplingWindow)
        {
            _failures.Dequeue();
        }
    }

    private TimeSpan Elapsed(long startedAt, long endedAt) =>
        _timeProvider.GetElapsedTime(startedAt, endedAt);
}

internal sealed class ProviderCircuitLeaseRegistration
{
    public ProviderCircuitLeaseRegistration(
        AiProviderCircuitBreaker owner,
        ProviderCircuitState state,
        AiGatewayRequest request,
        AiProviderCircuitState acquiredState,
        int generation)
    {
        Owner = owner ?? throw new ArgumentNullException(nameof(owner));
        State = state ?? throw new ArgumentNullException(nameof(state));
        Request = request ?? throw new ArgumentNullException(nameof(request));
        AcquiredState = acquiredState;
        Generation = generation;
    }

    public ProviderCircuitState State { get; }

    public AiGatewayRequest Request { get; }

    public AiProviderCircuitState AcquiredState { get; }

    public int Generation { get; }

    public void Complete(ProviderCircuitObservation observation) =>
        Owner.Complete(this, observation);

    private AiProviderCircuitBreaker Owner { get; }
}

internal sealed record ProviderCircuitAcquireResult(
    AiProviderCircuitAdmission Admission,
    AiProviderCircuitTransition? Transition)
{
    public static ProviderCircuitAcquireResult Allowed(
        AiProviderCircuitLease lease,
        AiProviderCircuitTransition? transition) =>
        new(AiProviderCircuitAdmission.Allowed(lease), transition);

    public static ProviderCircuitAcquireResult Rejected(
        AiGatewayError error,
        AiProviderCircuitTransition? transition = null) =>
        new(AiProviderCircuitAdmission.Rejected(error), transition);
}

internal readonly record struct ProviderCircuitObservation(
    ProviderCircuitOutcome Outcome,
    AiGatewayErrorCode? ErrorCode)
{
    public static ProviderCircuitObservation Neutral { get; } =
        new(ProviderCircuitOutcome.Neutral, ErrorCode: null);

    public static ProviderCircuitObservation Failed(AiGatewayErrorCode errorCode) =>
        new(ProviderCircuitOutcome.Failed, errorCode);

    public static ProviderCircuitObservation FromResponse(AiGatewayResponse response)
    {
        if (response.IsSuccess)
        {
            return new ProviderCircuitObservation(
                ProviderCircuitOutcome.Succeeded,
                ErrorCode: null);
        }

        var error = response.Error!;
        if (error.Reason is not null)
        {
            return Neutral;
        }

        return error.Code is
            AiGatewayErrorCode.Timeout or
            AiGatewayErrorCode.QuotaExceeded or
            AiGatewayErrorCode.ProviderUnavailable or
            AiGatewayErrorCode.InvalidProviderResponse or
            AiGatewayErrorCode.Unknown
                ? Failed(error.Code)
                : Neutral;
    }
}

internal enum ProviderCircuitOutcome
{
    Neutral = 0,
    Succeeded = 1,
    Failed = 2,
}

internal sealed class NoopAiProviderCircuitTransitionPublisher
    : IAiProviderCircuitTransitionPublisher
{
    public static NoopAiProviderCircuitTransitionPublisher Instance { get; } = new();

    private NoopAiProviderCircuitTransitionPublisher()
    {
    }

    public void Publish(AiProviderCircuitTransition transition)
    {
        ArgumentNullException.ThrowIfNull(transition);
    }
}
