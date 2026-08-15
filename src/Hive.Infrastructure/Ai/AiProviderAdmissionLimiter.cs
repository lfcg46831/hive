using System.Collections.Concurrent;
using Hive.Domain.Ai;

namespace Hive.Infrastructure.Ai;

/// <summary>
/// Resolves the immutable resilience policy applied to one effective provider.
/// Operational configuration replaces the default resolver in US-F1-05-T10.
/// </summary>
public interface IAiProviderResiliencePolicyResolver
{
    AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider);
}

/// <summary>
/// Process-local admission seam for provider concurrency and sliding-window limits.
/// US-F1-05-T07 can replace this implementation with cluster-wide actor routing
/// without changing <see cref="IAiGateway"/> callers.
/// </summary>
public interface IAiProviderAdmissionLimiter
{
    ValueTask<AiProviderAdmissionResult> AcquireAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default);
}

public sealed class AiProviderAdmissionResult
{
    private AiProviderAdmissionResult(
        AiProviderAdmissionLease? lease,
        AiGatewayError? error)
    {
        if ((lease is null) == (error is null))
        {
            throw new ArgumentException(
                "Provider admission must contain exactly one lease or error.");
        }

        Lease = lease;
        Error = error;
    }

    public bool IsAdmitted => Lease is not null;

    public AiProviderAdmissionLease? Lease { get; }

    public AiGatewayError? Error { get; }

    internal static AiProviderAdmissionResult Admitted(
        AiProviderAdmissionLease lease) =>
        new(lease ?? throw new ArgumentNullException(nameof(lease)), error: null);

    internal static AiProviderAdmissionResult Rejected(AiGatewayError error) =>
        new(lease: null, error ?? throw new ArgumentNullException(nameof(error)));
}

public sealed class AiProviderAdmissionLease : IDisposable
{
    private ProviderAdmissionState? _owner;

    internal AiProviderAdmissionLease(
        ProviderAdmissionState owner,
        TimeSpan queueDuration)
    {
        _owner = owner ?? throw new ArgumentNullException(nameof(owner));
        if (queueDuration < TimeSpan.Zero)
        {
            throw new ArgumentOutOfRangeException(
                nameof(queueDuration),
                queueDuration,
                "AI provider queue duration cannot be negative.");
        }

        QueueDuration = queueDuration;
    }

    public TimeSpan QueueDuration { get; }

    public void Dispose() =>
        Interlocked.Exchange(ref _owner, null)?.Release();
}

/// <summary>
/// FIFO provider admission controller. Concurrency leases and sliding-window
/// admissions are isolated by provider id and shared by every position/model
/// using that provider in this process.
/// </summary>
public sealed class AiProviderAdmissionLimiter : IAiProviderAdmissionLimiter, IDisposable
{
    private readonly ConcurrentDictionary<ProviderAdmissionKey, ProviderAdmissionState> _states =
        new();
    private readonly IAiProviderResiliencePolicyResolver _policyResolver;
    private readonly TimeProvider _timeProvider;
    private int _disposed;

    public AiProviderAdmissionLimiter(
        IAiProviderResiliencePolicyResolver policyResolver,
        TimeProvider? timeProvider = null)
    {
        _policyResolver = policyResolver ??
            throw new ArgumentNullException(nameof(policyResolver));
        _timeProvider = timeProvider ?? TimeProvider.System;
    }

    public ValueTask<AiProviderAdmissionResult> AcquireAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken = default)
    {
        ArgumentNullException.ThrowIfNull(request);
        cancellationToken.ThrowIfCancellationRequested();
        ObjectDisposedException.ThrowIf(
            Volatile.Read(ref _disposed) != 0,
            this);

        var key = new ProviderAdmissionKey(request.Provider?.ProviderId);
        var state = _states.GetOrAdd(
            key,
            _ => new ProviderAdmissionState(
                _policyResolver.Resolve(request.Provider),
                _timeProvider));

        if (Volatile.Read(ref _disposed) != 0)
        {
            state.Dispose();
            throw new ObjectDisposedException(nameof(AiProviderAdmissionLimiter));
        }

        return state.AcquireAsync(request, cancellationToken);
    }

    public void Dispose()
    {
        if (Interlocked.Exchange(ref _disposed, 1) != 0)
        {
            return;
        }

        foreach (var state in _states.Values)
        {
            state.Dispose();
        }

        _states.Clear();
    }

    private readonly record struct ProviderAdmissionKey(string? ProviderId);
}

internal sealed class DefaultAiProviderResiliencePolicyResolver
    : IAiProviderResiliencePolicyResolver
{
    public static DefaultAiProviderResiliencePolicyResolver Instance { get; } = new();

    private DefaultAiProviderResiliencePolicyResolver()
    {
    }

    public AiProviderResiliencePolicy Resolve(AiProviderMetadata? provider) =>
        AiProviderResiliencePolicy.Default;
}

internal sealed class ProviderAdmissionState : IDisposable
{
    private static readonly TimeSpan Infinite = Timeout.InfiniteTimeSpan;

    private readonly object _sync = new();
    private readonly AiProviderResiliencePolicy _policy;
    private readonly TimeProvider _timeProvider;
    private readonly Queue<long> _admissions = new();
    private readonly Queue<ProviderAdmissionWaiter> _waiters = new();
    private ITimer? _timer;
    private int _activeCalls;
    private int _waitingCount;
    private bool _disposed;

    public ProviderAdmissionState(
        AiProviderResiliencePolicy policy,
        TimeProvider timeProvider)
    {
        _policy = policy ?? throw new ArgumentNullException(nameof(policy));
        _timeProvider = timeProvider ?? throw new ArgumentNullException(nameof(timeProvider));
    }

    public ValueTask<AiProviderAdmissionResult> AcquireAsync(
        AiGatewayRequest request,
        CancellationToken cancellationToken)
    {
        ProviderAdmissionWaiter? waiter = null;
        AiProviderAdmissionResult? immediateResult = null;

        lock (_sync)
        {
            ObjectDisposedException.ThrowIf(_disposed, this);

            var now = _timeProvider.GetTimestamp();
            DrainAndSchedule(now);

            if (_waitingCount == 0 && HasAdmissionCapacity())
            {
                immediateResult = Admit(request, now, queuedAt: null);
            }
            else if (!_policy.Queue.IsEnabled ||
                     _waitingCount >= _policy.Queue.MaxDepth)
            {
                immediateResult = AiProviderAdmissionResult.Rejected(
                    AiGatewayResilienceErrorCatalog.GatewayOverloaded(request));
            }
            else
            {
                waiter = new ProviderAdmissionWaiter(request, now);
                _waiters.Enqueue(waiter);
                _waitingCount++;
                ScheduleNextTimer(now);
            }
        }

        if (immediateResult is not null)
        {
            return ValueTask.FromResult(immediateResult);
        }

        RegisterCancellation(waiter!, cancellationToken);
        return AwaitWaiterAsync(waiter!);
    }

    public void Release()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            if (_activeCalls <= 0)
            {
                throw new InvalidOperationException(
                    "AI provider admission lease was released without an active call.");
            }

            _activeCalls--;
            DrainAndSchedule(_timeProvider.GetTimestamp());
        }
    }

    public void Dispose()
    {
        List<ProviderAdmissionWaiter> pending = [];

        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            _disposed = true;
            _timer?.Change(Infinite, Infinite);

            while (_waiters.TryDequeue(out var waiter))
            {
                if (waiter.Status != ProviderAdmissionWaiterStatus.Pending)
                {
                    continue;
                }

                waiter.Status = ProviderAdmissionWaiterStatus.Disposed;
                _waitingCount--;
                pending.Add(waiter);
            }
        }

        _timer?.Dispose();
        foreach (var waiter in pending)
        {
            waiter.Completion.TrySetException(new ObjectDisposedException(
                nameof(AiProviderAdmissionLimiter)));
        }
    }

    private void RegisterCancellation(
        ProviderAdmissionWaiter waiter,
        CancellationToken cancellationToken)
    {
        if (!cancellationToken.CanBeCanceled)
        {
            return;
        }

        var registration = cancellationToken.Register(
            static state =>
            {
                var callback = (ProviderAdmissionCancellation)state!;
                callback.Owner.Cancel(callback.Waiter, callback.CancellationToken);
            },
            new ProviderAdmissionCancellation(this, waiter, cancellationToken));
        waiter.SetCancellationRegistration(registration);
    }

    private static async ValueTask<AiProviderAdmissionResult> AwaitWaiterAsync(
        ProviderAdmissionWaiter waiter)
    {
        try
        {
            return await waiter.Completion.Task.ConfigureAwait(false);
        }
        finally
        {
            waiter.DisposeCancellationRegistration();
        }
    }

    private void Cancel(
        ProviderAdmissionWaiter waiter,
        CancellationToken cancellationToken)
    {
        lock (_sync)
        {
            if (_disposed ||
                waiter.Status != ProviderAdmissionWaiterStatus.Pending)
            {
                return;
            }

            waiter.Status = ProviderAdmissionWaiterStatus.Canceled;
            _waitingCount--;
            waiter.Completion.TrySetCanceled(cancellationToken);
            DrainAndSchedule(_timeProvider.GetTimestamp());
        }
    }

    private void OnTimer()
    {
        lock (_sync)
        {
            if (_disposed)
            {
                return;
            }

            DrainAndSchedule(_timeProvider.GetTimestamp());
        }
    }

    private void DrainAndSchedule(long now)
    {
        RemoveExpiredAdmissions(now);
        RemoveInactiveWaiters();

        while (_waiters.TryPeek(out var waiter))
        {
            if (waiter.Status != ProviderAdmissionWaiterStatus.Pending)
            {
                _waiters.Dequeue();
                continue;
            }

            if (Elapsed(waiter.QueuedAt, now) >= _policy.Queue.MaxWait)
            {
                _waiters.Dequeue();
                waiter.Status = ProviderAdmissionWaiterStatus.TimedOut;
                _waitingCount--;
                waiter.Completion.TrySetResult(AiProviderAdmissionResult.Rejected(
                    AiGatewayResilienceErrorCatalog.GatewayOverloaded(waiter.Request)));
                continue;
            }

            if (!HasAdmissionCapacity())
            {
                break;
            }

            _waiters.Dequeue();
            waiter.Status = ProviderAdmissionWaiterStatus.Admitted;
            _waitingCount--;
            waiter.Completion.TrySetResult(Admit(
                waiter.Request,
                now,
                waiter.QueuedAt));
        }

        ScheduleNextTimer(now);
    }

    private void RemoveExpiredAdmissions(long now)
    {
        while (_admissions.TryPeek(out var admittedAt) &&
               Elapsed(admittedAt, now) >= _policy.RateLimit.Window)
        {
            _admissions.Dequeue();
        }
    }

    private void RemoveInactiveWaiters()
    {
        while (_waiters.TryPeek(out var waiter) &&
               waiter.Status != ProviderAdmissionWaiterStatus.Pending)
        {
            _waiters.Dequeue();
        }
    }

    private bool HasAdmissionCapacity() =>
        _activeCalls < _policy.RateLimit.MaxConcurrentCalls &&
        _admissions.Count < _policy.RateLimit.MaxCallsPerWindow;

    private AiProviderAdmissionResult Admit(
        AiGatewayRequest request,
        long admittedAt,
        long? queuedAt)
    {
        _activeCalls++;
        _admissions.Enqueue(admittedAt);

        var queueDuration = queuedAt is { } enqueuedAt
            ? Elapsed(enqueuedAt, admittedAt)
            : TimeSpan.Zero;

        return AiProviderAdmissionResult.Admitted(
            new AiProviderAdmissionLease(this, queueDuration));
    }

    private void ScheduleNextTimer(long now)
    {
        RemoveInactiveWaiters();
        if (!_waiters.TryPeek(out var waiter))
        {
            _timer?.Change(Infinite, Infinite);
            return;
        }

        var due = Remaining(
            waiter.QueuedAt,
            _policy.Queue.MaxWait,
            now);

        if (_activeCalls < _policy.RateLimit.MaxConcurrentCalls &&
            _admissions.Count >= _policy.RateLimit.MaxCallsPerWindow &&
            _admissions.TryPeek(out var oldestAdmission))
        {
            var windowDue = Remaining(
                oldestAdmission,
                _policy.RateLimit.Window,
                now);
            if (windowDue < due)
            {
                due = windowDue;
            }
        }

        _timer ??= _timeProvider.CreateTimer(
            static state => ((ProviderAdmissionState)state!).OnTimer(),
            this,
            Infinite,
            Infinite);
        _timer.Change(due < TimeSpan.Zero ? TimeSpan.Zero : due, Infinite);
    }

    private TimeSpan Remaining(long startedAt, TimeSpan limit, long now) =>
        limit - Elapsed(startedAt, now);

    private TimeSpan Elapsed(long startedAt, long endedAt) =>
        _timeProvider.GetElapsedTime(startedAt, endedAt);

    private sealed record ProviderAdmissionCancellation(
        ProviderAdmissionState Owner,
        ProviderAdmissionWaiter Waiter,
        CancellationToken CancellationToken);
}

internal sealed class ProviderAdmissionWaiter
{
    private readonly object _registrationSync = new();
    private CancellationTokenRegistration _cancellationRegistration;
    private bool _hasCancellationRegistration;
    private bool _disposeCancellationRegistration;

    public ProviderAdmissionWaiter(AiGatewayRequest request, long queuedAt)
    {
        Request = request ?? throw new ArgumentNullException(nameof(request));
        QueuedAt = queuedAt;
        Completion = new TaskCompletionSource<AiProviderAdmissionResult>(
            TaskCreationOptions.RunContinuationsAsynchronously);
    }

    public AiGatewayRequest Request { get; }

    public long QueuedAt { get; }

    public TaskCompletionSource<AiProviderAdmissionResult> Completion { get; }

    public ProviderAdmissionWaiterStatus Status { get; set; }

    public void SetCancellationRegistration(
        CancellationTokenRegistration registration)
    {
        var dispose = false;
        lock (_registrationSync)
        {
            if (_disposeCancellationRegistration)
            {
                dispose = true;
            }
            else
            {
                _cancellationRegistration = registration;
                _hasCancellationRegistration = true;
            }
        }

        if (dispose)
        {
            registration.Dispose();
        }
    }

    public void DisposeCancellationRegistration()
    {
        CancellationTokenRegistration registration = default;
        var dispose = false;

        lock (_registrationSync)
        {
            _disposeCancellationRegistration = true;
            if (_hasCancellationRegistration)
            {
                registration = _cancellationRegistration;
                _hasCancellationRegistration = false;
                dispose = true;
            }
        }

        if (dispose)
        {
            registration.Dispose();
        }
    }
}

internal enum ProviderAdmissionWaiterStatus
{
    Pending = 0,
    Admitted = 1,
    TimedOut = 2,
    Canceled = 3,
    Disposed = 4,
}
