using Hive.Infrastructure.Hosting;

namespace Hive.Tests.Fakes;

/// <summary>
/// Test double for <see cref="IRoleWorkload"/>. Records start/stop so tests can assert the
/// role-conditional activation seam without any placeholder actors or real Akka workloads.
/// </summary>
public sealed class FakeRoleWorkload : IRoleWorkload
{
    private readonly List<string> _activationLog;

    public FakeRoleWorkload(string role)
        : this(role, new List<string>())
    {
    }

    public FakeRoleWorkload(string role, List<string> activationLog)
    {
        Role = role;
        _activationLog = activationLog;
    }

    public string Role { get; }

    public int StartCount { get; private set; }

    public int StopCount { get; private set; }

    public bool IsRunning { get; private set; }

    public Task StartAsync(CancellationToken cancellationToken)
    {
        StartCount++;
        IsRunning = true;
        _activationLog.Add($"start:{Role}");
        return Task.CompletedTask;
    }

    public Task StopAsync(CancellationToken cancellationToken)
    {
        StopCount++;
        IsRunning = false;
        _activationLog.Add($"stop:{Role}");
        return Task.CompletedTask;
    }
}
